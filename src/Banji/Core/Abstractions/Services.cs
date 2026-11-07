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

    /// <summary>给学生加减积分（抽中答题 +1 等），并立即保存。</summary>
    void AddPoints(string rosterId, string studentKey, int delta);
    /// <summary>清空某份花名册所有学生的积分。</summary>
    void ResetPoints(string rosterId);
    /// <summary>积分排行（降序，含 0 分的也返回）。</summary>
    IReadOnlyList<StudentPoints> TopPoints(string rosterId, int count = 10);
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

    /// <summary>从 now 起接下来的时间边界（上课/下课/放学，已应用铃声误差），供铃声校准向导使用。</summary>
    IReadOnlyList<ClassBoundary> UpcomingBoundaries(DateTime now, int count = 6);
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
    /// <summary>非全屏气泡提醒请求（v1.3：上课/下课/其它提醒统一走气泡，计时器除外）。</summary>
    event EventHandler<NoticeToast>? ToastRequested;

    /// <summary>要求立即关闭当前气泡提醒（DismissAll 时触发）。</summary>
    event EventHandler? ToastDismissed;

    /// <summary>显示一个宠物气泡提醒；PetWindow 订阅 ToastRequested 后弹出气泡窗。</summary>
    void ShowToast(NoticeToast toast);

    void ShowClassStart(ClassTransitionEventArgs transition);
    void ShowClassEnd(ClassTransitionEventArgs transition);
    void ShowTimerTimeout();
    void DismissAll();
}

/// <summary>气泡提醒种类（v1.3.0）。</summary>
public enum NoticeToastKind
{
    Generic = 0,
    ClassStart = 1,
    ClassEnd = 2,
    Preview = 3
}

/// <summary>宠物气泡提醒内容（v1.3.0）：灵动冒出、轻柔回弹、定住 N 秒、收回。</summary>
public sealed class NoticeToast
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public NoticeToastKind Kind { get; init; } = NoticeToastKind.Generic;
    /// <summary>保持毫秒数（默认取设置 NoticeBubbleHoldSeconds*1000）。</summary>
    public int HoldMilliseconds { get; init; } = 7000;
}

// ============================================================================
// v1.4.0：单双周 + 值周/值日（Lead 冻结契约）
// ============================================================================

/// <summary>单双周解析服务：按锚点周自动推算，支持本周手动覆盖。</summary>
public interface IWeekParityService
{
    bool Enabled { get; }
    /// <summary>解析后的锚点周一（WeekParityAnchorDate，缺省用 SemesterFirstWeekMonday）。</summary>
    DateOnly? AnchorMonday { get; }
    WeekParity CurrentParity { get; }
    WeekParityOverrideMode OverrideMode { get; }
    /// <summary>本周是否处于手动覆盖（跨周自动恢复 Auto）。</summary>
    bool IsCurrentWeekOverridden { get; }
    WeekParity GetParity(DateOnly date);
    /// <summary>第几周（锚点周 = 第 1 周）；锚点之前为 0 或负数。</summary>
    int GetWeekNumber(DateOnly date);
    event EventHandler? Changed;
    void Refresh();
}

/// <summary>某一周的轮换信息（展示/打印预览用）。</summary>
public sealed class DutyWeekInfo
{
    public DateOnly WeekMonday { get; init; }
    public string? GroupId { get; init; }
    public string? GroupName { get; init; }
    public bool Paused { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// 值周/值日服务：分组轮换 + 固定任职 + 提醒计算 + 展示文案。
/// 配置持久化到 %APPDATA%\Banji\duty.json，并纳入备份。
/// </summary>
public interface IDutyService
{
    DutyConfig Config { get; }
    /// <summary>是否完成了首次配置（岗位/分组/开学第一周等）。</summary>
    bool IsConfigured { get; }
    /// <summary>第一次配置未完成时，UI 需要强制引导用户设置开学第一周（用户确认稿要求）。</summary>
    bool NeedsFirstTimeSetup { get; }
    event EventHandler? Changed;

    void Save(DutyConfig config);
    void Reload();

    /// <summary>解析某一天的值日/值周安排（自动轮换 + 手动覆盖 + 黑板类置顶排序）。</summary>
    DutyScheduleView GetSchedule(DateOnly date);

    /// <summary>未来 N 周的轮换预览（值周模式；值日模式返回未来 N 天以周汇总也可）。</summary>
    IReadOnlyList<DutyWeekInfo> GetRotationPreview(DateOnly fromAnyDay, int weeks);

    /// <summary>手动覆盖某一周：换组 / 暂停（groupId=null 且 paused=false 表示恢复自动）。</summary>
    void SetWeekOverride(DateOnly weekMonday, string? groupId, bool paused, string? note);

    /// <summary>值日打卡（可选；AwardPointsOnCheckIn 时给参与学生加积分）。</summary>
    void CheckIn(DateOnly date);

    /// <summary>宠物面板展示行（含黑板类置顶、放学后轮值信息替换逻辑由 PetWindow 配合）。</summary>
    IReadOnlyList<string> GetPetPanelLines(DateOnly date);

    /// <summary>复制为文本（发班级群用；v1.4 不做打印）。</summary>
    string BuildCopyText(DateOnly date);
}
