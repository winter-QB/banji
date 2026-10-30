using System.Security.Cryptography;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Models;

namespace Banji.Core.Picker;

/// <summary>
/// 公平抽签引擎（开发文档第 1 章 + ARCHITECTURE.md“抽签规则”）。
///
/// 规则：
/// 1. 用 <see cref="RandomNumberGenerator"/>（CSPRNG）在合法候选集中<b>等概率</b>抽取，不使用伪随机玩具算法；
/// 2. 滚动窗口（默认=当前课时长；课表不可用时用 <c>ClassDurationMinutes</c>）内，每人最多被抽 2 次，
///    除非窗口内全员都已至少被抽过 1 次（此时放开 2 次上限，由“次数最少优先”保证公平）；
/// 3. 同一个人两次被抽之间至少间隔 4 次其他人的抽取（即最近 4 次抽取涉及的人暂不可抽）；
/// 4. 约束不可满足时按“窗口内次数最少 → 间隔最久”兜底，<b>绝不死锁</b>；
/// 5. 每节课开始 <see cref="ResetSession"/> 重置“本节课窗口”计数；滚动窗口仍按时间戳判断；
/// 6. 抽签历史持久化到 <c>pick-history.json</c>，只保留最近 30 天。
///
/// 所有时间通过 <c>Func&lt;DateTime&gt;</c> 注入，便于测试；窗口时长通过快照提供器注入。
/// </summary>
public sealed class PickService : IPickService
{
    /// <summary>滚动窗口内每人最多被抽次数（全员都抽过之前生效）。</summary>
    public const int MaxPicksPerWindow = 2;

    /// <summary>同一人两次被抽之间至少间隔的“其他人抽取”次数。</summary>
    public const int MinGapBetweenSameStudent = 4;

    /// <summary>抽签历史保留天数。</summary>
    public const int HistoryRetentionDays = 30;

    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(HistoryRetentionDays);

    private readonly object _gate = new();
    private readonly IRosterService _rosters;
    private readonly ISettingsService _settings;
    private readonly Func<DateTime> _now;
    private readonly Func<ScheduleSnapshot?> _snapshotProvider;
    private readonly string _historyPath;
    private readonly bool _persistHistory;

    /// <summary>按时间升序的抽签历史（内存中只保留最近 30 天）。</summary>
    private List<PickRecord> _history = new();

    /// <summary>本节课开始时刻；<see cref="DateTime.MinValue"/> 表示尚未重置过会话（按滚动时间窗口判断）。</summary>
    private DateTime _sessionStart = DateTime.MinValue;

    /// <summary>本节课内已抽签次数（SessionPickIndex 用）。</summary>
    private int _sessionPickCount;

    /// <summary>宿主（AppServices）使用的构造函数。</summary>
    public PickService(IRosterService rosters, ISettingsService settings)
        : this(rosters, settings, null, null, null)
    {
    }

    /// <summary>可测试构造函数：注入时间、课表快照、历史文件路径。</summary>
    public PickService(
        IRosterService rosters,
        ISettingsService settings,
        Func<DateTime>? now = null,
        Func<ScheduleSnapshot?>? snapshotProvider = null,
        string? historyFilePath = null,
        bool persistHistory = true)
    {
        _rosters = rosters ?? throw new ArgumentNullException(nameof(rosters));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _now = now ?? (() => DateTime.Now);
        _snapshotProvider = snapshotProvider ?? DefaultSnapshot;
        _historyPath = historyFilePath ?? AppPaths.PickHistoryFile;
        _persistHistory = persistHistory;
        LoadHistory();
    }

    // =========================================================================
    // 公开契约
    // =========================================================================

    public IReadOnlyList<PickRecord> RecentPicks
    {
        get
        {
            lock (_gate)
            {
                return _history.OrderByDescending(r => r.PickedAt).Take(50).ToList();
            }
        }
    }

    public event EventHandler<PickResult>? Picked;

    /// <summary>当前抽签窗口时长：优先使用“当前这节课”的真实时长（精确到秒），否则用设置的课时长。</summary>
    public TimeSpan CurrentWindow => CurrentWindowCore();

    public PickResult Pick(string? rosterId = null)
    {
        PickResult result;
        lock (_gate)
        {
            result = PickCore(rosterId);
            if (_persistHistory) SaveHistory();
        }

        try
        {
            Picked?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            Log.Error("抽签结果事件处理失败", ex);
        }
        return result;
    }

    public void ResetSession()
    {
        lock (_gate)
        {
            _sessionPickCount = 0;
            _sessionStart = _now();
        }
        Log.Info("抽签会话已重置（每节课开始）");
    }

    // =========================================================================
    // 核心算法
    // =========================================================================

    private PickResult PickCore(string? rosterId)
    {
        var roster = ResolveRoster(rosterId);
        var students = roster.Students.Where(s => s.IsValid).ToList();
        if (students.Count == 0)
            throw new InvalidOperationException("当前花名册还没有学生，请先导入或手动录入花名册。");

        var now = _now();
        var windowStart = now - CurrentWindowCore();
        if (_sessionStart != DateTime.MinValue && _sessionStart > windowStart)
            windowStart = _sessionStart; // 本节课开始后重新计数；滚动窗口仍按时间戳/会话起点判断

        // 只统计当前花名册的抽签历史（不同花名册互不影响）
        var rosterPicks = _history
            .Where(r => string.Equals(r.RosterId, roster.Id, StringComparison.Ordinal))
            .ToList();

        var counts = CountInWindow(rosterPicks, windowStart);
        var allCovered = students.All(st => counts.GetValueOrDefault(st.Key) > 0);

        // 间隔约束：最近 MinGap 次抽取涉及的人暂不可抽（同一人两次之间至少隔 4 次其他人）
        var recentKeys = RecentStudentKeys(rosterPicks);
        var gapOk = students.Where(st => !recentKeys.Contains(st.Key)).ToList();
        var gapSatisfiable = gapOk.Count > 0;
        if (!gapSatisfiable) gapOk = students; // 花名册人数 ≤4 时该约束本身不可满足，交给兜底

        List<Student> candidates;
        if (allCovered)
        {
            // 窗口内全员至少抽过 1 次：放开“最多 2 次”的上限，只保留间隔约束
            candidates = gapOk;
        }
        else
        {
            var underLimit = gapOk.Where(st => counts.GetValueOrDefault(st.Key) < MaxPicksPerWindow).ToList();
            if (underLimit.Count > 0)
            {
                candidates = underLimit;
            }
            else if (gapSatisfiable)
            {
                // 兜底 A：间隔满足但窗口内都达到上限（规则本身不可满足）→ 选窗口内次数最少的一档
                candidates = MinCountTier(gapOk, counts);
            }
            else
            {
                // 兜底 B：间隔与窗口上限都无法满足（花名册太小）→ 间隔最久优先，再取次数最少
                var oldest = MinLastPickTier(students, rosterPicks);
                candidates = MinCountTier(oldest, counts);
            }
        }

        if (candidates.Count == 0) candidates = students; // 理论上不会走到，保证绝不死锁

        var chosen = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
        _sessionPickCount++;

        _history.Add(new PickRecord
        {
            RosterId = roster.Id,
            StudentKey = chosen.Key,
            StudentId = chosen.DisplayId,
            StudentName = chosen.DisplayName,
            PickedAt = now
        });
        TrimHistory(now);

        return new PickResult
        {
            Roster = roster,
            Student = chosen,
            DisplayMode = roster.DisplayModeOverride ?? _settings.Current.PickDisplay,
            PickedAt = now,
            SessionPickIndex = _sessionPickCount
        };
    }

    private static Dictionary<string, int> CountInWindow(IEnumerable<PickRecord> records, DateTime windowStart)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rec in records)
        {
            if (rec.PickedAt <= windowStart) continue;
            counts[rec.StudentKey] = counts.GetValueOrDefault(rec.StudentKey) + 1;
        }
        return counts;
    }

    private static HashSet<string> RecentStudentKeys(IReadOnlyList<PickRecord> records)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = records.Count - 1; i >= 0 && keys.Count < MinGapBetweenSameStudent; i--)
            keys.Add(records[i].StudentKey);
        return keys;
    }

    private static List<Student> MinCountTier(IEnumerable<Student> pool, IReadOnlyDictionary<string, int> counts)
    {
        var list = pool.ToList();
        if (list.Count == 0) return list;
        var min = list.Min(st => counts.GetValueOrDefault(st.Key));
        return list.Where(st => counts.GetValueOrDefault(st.Key) == min).ToList();
    }

    private static List<Student> MinLastPickTier(IReadOnlyList<Student> students, IReadOnlyList<PickRecord> rosterPicks)
    {
        var lastAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var rec in rosterPicks)
            lastAt[rec.StudentKey] = rec.PickedAt; // 升序，最后一次覆盖

        var list = students.ToList();
        if (list.Count == 0) return list;
        var oldest = list.Min(st => lastAt.TryGetValue(st.Key, out var t) ? t : DateTime.MinValue);
        return list.Where(st => (lastAt.TryGetValue(st.Key, out var t) ? t : DateTime.MinValue) == oldest).ToList();
    }

    private TimeSpan CurrentWindowCore()
    {
        var settings = _settings.Current;
        var fallback = TimeSpan.FromMinutes(Math.Max(1, settings.ClassDurationMinutes));
        if (!settings.PickWindowFollowsCurrentPeriod) return fallback;

        try
        {
            var snapshot = _snapshotProvider();
            if (snapshot?.Current is { } current && current.Duration > TimeSpan.Zero)
                return current.Duration; // 当天课表当前节真实时长，精确到秒
        }
        catch (Exception ex)
        {
            Log.Error("读取当前课时长失败，回退到设置课时长", ex);
        }
        return fallback;
    }

    private Roster ResolveRoster(string? rosterId)
    {
        var roster = string.IsNullOrWhiteSpace(rosterId) ? _rosters.Active : _rosters.Get(rosterId);
        if (roster is null)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(rosterId)
                ? "还没有可抽签的花名册，请先在“花名册管理”中导入或新建一份花名册。"
                : "找不到指定的花名册，请重新选择一份。");
        }
        return roster;
    }

    // =========================================================================
    // 历史持久化
    // =========================================================================

    private void LoadHistory()
    {
        try
        {
            var loaded = SafeJsonStore.Load(_historyPath, () => new List<PickRecord>());
            var cutoff = _now() - HistoryRetention;
            _history = loaded
                .Where(r => !string.IsNullOrWhiteSpace(r.RosterId) && r.PickedAt > cutoff)
                .OrderBy(r => r.PickedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("读取抽签历史失败，已从空历史开始", ex);
            _history = new List<PickRecord>();
        }
    }

    private void TrimHistory(DateTime now)
    {
        var cutoff = now - HistoryRetention;
        _history.RemoveAll(r => r.PickedAt <= cutoff);
    }

    private void SaveHistory()
    {
        try
        {
            SafeJsonStore.Save(_historyPath, _history);
        }
        catch (Exception ex)
        {
            Log.Error("保存抽签历史失败", ex);
        }
    }

    private static ScheduleSnapshot? DefaultSnapshot()
    {
        try
        {
            return AppServices.IsInitialized ? AppServices.Current.Clock.Current : null;
        }
        catch
        {
            return null;
        }
    }
}
