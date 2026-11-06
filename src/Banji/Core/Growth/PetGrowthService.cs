using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Models;

namespace Banji.Core.Growth;

/// <summary>
/// 宠物陪伴养成服务（第二轮新增）：使用天数/摸头/抽签/分组/提示统计 + 成就解锁，纯本地。
/// 数据文件：%APPDATA%\Banji\pet-growth.json
/// </summary>
public sealed class PetGrowthService : IPetGrowthService
{
    private readonly string _path;
    private readonly Func<DateTime> _now;
    private PetGrowthStats _stats;

    public static readonly IReadOnlyList<(string Id, string Title, string Description, Func<PetGrowthStats, bool> Condition)> Definitions =
        new List<(string, string, string, Func<PetGrowthStats, bool>)>
        {
            ("first_day", "初次见面", "班机第一次陪你上课", s => s.DaysUsed >= 1),
            ("pat_10", "摸头 10 次", "你是不是把它当猫了", s => s.PatCount >= 10),
            ("pat_100", "摸头 100 次", "它已经习惯被你摸了", s => s.PatCount >= 100),
            ("pick_10", "抽签新手", "累计抽签 10 次", s => s.PickCount >= 10),
            ("pick_100", "课堂气氛组", "累计抽签 100 次", s => s.PickCount >= 100),
            ("group_5", "分组小能手", "累计分组 5 次", s => s.GroupCount >= 5),
            ("days_7", "一周陪伴", "连续使用 7 天（累计）", s => s.DaysUsed >= 7),
            ("days_30", "满月纪念", "累计使用 30 天", s => s.DaysUsed >= 30),
            ("days_100", "老朋友", "累计使用 100 天", s => s.DaysUsed >= 100),
            ("notice_50", "上下课喇叭", "见证 50 次上下课", s => s.NoticeCount >= 50)
        };

    public PetGrowthService() : this(null, null) { }

    public PetGrowthService(string? filePath, Func<DateTime>? now)
    {
        _path = filePath ?? Path.Combine(AppPaths.Root, "pet-growth.json");
        _now = now ?? (() => DateTime.Now);
        _stats = SafeJsonStore.Load(_path, () => new PetGrowthStats());
        _stats.Normalize();
    }

    public PetGrowthStats Stats => _stats;
    public event EventHandler? Changed;

    public IReadOnlyList<Achievement> Achievements
    {
        get
        {
            var unlocked = new List<Achievement>();
            foreach (var def in Definitions)
            {
                if (!_stats.Achievements.Contains(def.Id)) continue;
                unlocked.Add(new Achievement { Id = def.Id, Title = def.Title, Description = def.Description });
            }
            return unlocked;
        }
    }

    public void RecordSessionStart()
    {
        var today = DateOnly.FromDateTime(_now());
        if (today > _stats.LastUseDate)
        {
            _stats.DaysUsed += today.DayNumber - _stats.LastUseDate.DayNumber;
            _stats.LastUseDate = today;
        }
        else if (today < _stats.FirstUseDate)
        {
            _stats.FirstUseDate = today;
        }
        _stats.SessionCount++;
        // 第一次启动也应立刻解锁“初次见面”这类按天数的成就。
        Touch();
    }

    public void RecordPat() { _stats.PatCount++; Touch(); }

    public void RecordPick() { _stats.PickCount++; Touch(); }

    public void RecordGroup() { _stats.GroupCount++; Touch(); }

    public void RecordNotice() { _stats.NoticeCount++; Touch(); }

    public void Reset()
    {
        _stats = new PetGrowthStats();
        _stats.Normalize();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Touch()
    {
        var newlyUnlocked = new List<Achievement>();
        foreach (var def in Definitions)
        {
            if (_stats.Achievements.Contains(def.Id)) continue;
            bool ok;
            try { ok = def.Condition(_stats); } catch { ok = false; }
            if (!ok) continue;
            _stats.Achievements.Add(def.Id);
            newlyUnlocked.Add(new Achievement { Id = def.Id, Title = def.Title, Description = def.Description });
        }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        if (newlyUnlocked.Count > 0)
            Log.Info("解锁成就: " + string.Join("、", newlyUnlocked.Select(a => a.Title)));
    }

    private void Save() => SafeJsonStore.Save(_path, _stats);
}
