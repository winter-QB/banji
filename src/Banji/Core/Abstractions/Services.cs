using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Core.Abstractions;

// ============================================================================
// 服务契约（Lead 冻结）。队友实现自己的服务，但不得修改本文件签名。
// ============================================================================

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler? Changed;
    /// <summary>修改设置（mutate 内改 Current），自动触发 Changed 并防抖保存。</summary>
    void Update(Action<AppSettings> mutate);
    /// <summary>立即落盘。</summary>
    void Save();
    /// <summary>重置为全部默认值（不删除花名册/课表）。</summary>
    void ResetToDefaults();
}

public interface IRosterService
{
    IReadOnlyList<Roster> Rosters { get; }
    event EventHandler? Changed;
    Roster? Get(string? rosterId);
    Roster? Active { get; }
    void SetActive(string? rosterId);

    Roster Add(Roster roster);
    void Update(Roster roster);
    void Remove(string rosterId);
    Roster CreateManual(string name, IEnumerable<Student> students, string? subject = null);
    void Save();

    /// <summary>解析文件，自动识别格式/列，返回预览（不落盘）。</summary>
    RosterImportPreview PreviewImport(string filePath);
    /// <summary>按预览/选项真正导入并保存。</summary>
    Roster Import(string filePath, RosterImportOptions options);
}

public interface IPickService
{
    IReadOnlyList<PickRecord> RecentPicks { get; }
    /// <summary>抽签结果广播；UI 订阅显示气泡。</summary>
    event EventHandler<PickResult>? Picked;

    /// <summary>抽一次签。rosterId 为空时使用当前激活花名册。</summary>
    PickResult Pick(string? rosterId = null);

    /// <summary>清空本节课会话计数（不删历史）。</summary>
    void ResetSession();

    /// <summary>当前抽签窗口时长（若课表可用则用当前课时长，否则用设置值）。</summary>
    TimeSpan CurrentWindow { get; }
}

public interface ITimetableService
{
    WeekTimetable Current { get; }
    IReadOnlyList<DayOverride> Overrides { get; }
    event EventHandler? Changed;

    TimetableImportPreview PreviewImport(string filePath);
    WeekTimetable Import(string filePath, string? sheetName = null);

    void SetTimetable(WeekTimetable timetable);
    void SetPeriods(IEnumerable<ClassPeriod> periods);
    void SetBreaks(IEnumerable<BreakSegment> breaks);
    void SetCourse(DayOfWeek day, int periodIndex, string course);

    DayOverride? GetOverride(DateOnly date);
    void SetOverride(DayOverride overrideData);
    void RemoveOverride(DateOnly date);
    void Save();
}

public interface IScheduleService
{
    /// <summary>纯函数：任意时刻的课表快照（不依赖真实时钟，便于测试）。</summary>
    ScheduleSnapshot SnapshotAt(DateTime now);
    event EventHandler? Changed;
}

public interface IClassClock
{
    ScheduleSnapshot Current { get; }
    /// <summary>约每 250ms 一次。</summary>
    event EventHandler<ScheduleSnapshot>? Tick;
    /// <summary>跨过上课/下课/放学时刻时触发一次。</summary>
    event EventHandler<ClassTransitionEventArgs>? Transition;
    void Start();
    void Stop();
    /// <summary>课表/设置变化后立即重算。</summary>
    void Refresh();
}

public interface ITimerService
{
    TimerSnapshot? Current { get; }
    /// <summary>约每 200ms 一次（运行中）。</summary>
    event EventHandler? Tick;
    event EventHandler? StateChanged;
    /// <summary>倒计时归零时触发一次。</summary>
    event EventHandler? Expired;

    void StartCountdown(TimeSpan total);
    void StartStopwatch();
    void Cancel();
    /// <summary>关闭超时全屏提醒（不改变计时历史）。</summary>
    void AcknowledgeExpiry();
}

public interface IAudioService
{
    /// <summary>“清醒一下”：随机播放一个 0.3 秒以内的单拍音效，应用内最大音量。</summary>
    void PlayRandomSting();
}

public interface INotificationService
{
    void ShowClassStart(ClassTransitionEventArgs transition);
    void ShowClassEnd(ClassTransitionEventArgs transition);
    void ShowTimerTimeout();
    void DismissAll();
}
