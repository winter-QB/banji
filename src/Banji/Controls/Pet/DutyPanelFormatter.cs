using Banji.Core.Models;

namespace Banji.Controls.Pet;

/// <summary>面板上的一行值日任务（岗位名 + 成员文本），按“黑板类置顶”规则排好序。</summary>
public readonly record struct DutyTaskLine(string TaskName, string MembersText, bool IsTopTask)
{
    public string Text => $"{TaskName}：{MembersText}";
}

/// <summary>
/// v1.4.0 宠物面板的「值周/值日 + 单双周」纯展示逻辑：
/// <list type="bullet">
///   <item>顶部周次标题：`第 9 周 · 单周`（折叠态用短文本）；</item>
///   <item>值周/值日行：`本周值周：第 1 组` / `今日值日：第 1 组`；</item>
///   <item>黑板类岗位置顶行：`擦黑板：张三`（IsTopTask 的岗位排最前）；</item>
///   <item>折叠态短文案（主行 + 副行）；</item>
///   <item>放学后 N 分钟内的“轮值信息替换课表”时间区间与任务行。</item>
/// </list>
/// 全部为纯函数，便于单元测试。
/// </summary>
public static class DutyPanelFormatter
{
    public const string WeeklyLabel = "本周值周";
    public const string DailyLabel = "今日值日";

    public static string DutyLabel(DutyMode mode) => mode == DutyMode.Weekly ? WeeklyLabel : DailyLabel;

    public static string DutyPrefix(DutyMode mode) => DutyLabel(mode) + "：";

    public static string ParityLabel(WeekParity parity) => parity == WeekParity.Odd ? "单周" : "双周";

    /// <summary>面板顶部周次标题；单双周未启用或关闭“显示周次”时返回空串（不显示）。</summary>
    public static string WeekHeaderText(int weekNumber, WeekParity parity, bool parityEnabled, bool showWeekNumberOnPet)
    {
        if (!parityEnabled || !showWeekNumberOnPet || weekNumber <= 0) return "";
        return $"第 {weekNumber} 周 · {ParityLabel(parity)}";
    }

    /// <summary>折叠态/窄屏用的周次短文本，如 `第9周·单周`；不可显示时返回 fallback。</summary>
    public static string WeekShortText(int weekNumber, WeekParity parity, bool parityEnabled, bool showWeekNumberOnPet,
        string fallback = "")
    {
        if (!parityEnabled || !showWeekNumberOnPet || weekNumber <= 0) return fallback;
        return $"第{weekNumber}周·{ParityLabel(parity)}";
    }

    /// <summary>值周/值日行：`本周值周：第 1 组` / `今日值日：第 1 组`；暂停 / 未分组也会给出可读文案。</summary>
    public static string DutySummaryText(DutyMode mode, DutyScheduleView? schedule)
    {
        if (schedule is null) return "";
        var prefix = DutyPrefix(mode);
        if (schedule.IsPaused) return prefix + (mode == DutyMode.Weekly ? "本周暂停" : "今日暂停");
        if (string.IsNullOrWhiteSpace(schedule.GroupName)) return prefix + "未分组";
        return prefix + schedule.GroupName!.Trim();
    }

    /// <summary>全部任务行，黑板类（IsTopTask）置顶，其余保持原顺序。</summary>
    public static IReadOnlyList<DutyTaskLine> TaskLines(IEnumerable<DutyTaskView>? tasks)
    {
        if (tasks is null) return Array.Empty<DutyTaskLine>();
        var list = tasks.Where(t => !string.IsNullOrWhiteSpace(t.TaskName) || t.StudentNames.Count > 0).ToList();
        return list.Where(t => t.IsTopTask).Concat(list.Where(t => !t.IsTopTask)).Select(ToLine).ToList();
    }

    /// <summary>黑板类岗位行（课表最顶端常驻）。</summary>
    public static IReadOnlyList<DutyTaskLine> TopTaskLines(IEnumerable<DutyTaskView>? tasks)
        => tasks is null
            ? Array.Empty<DutyTaskLine>()
            : tasks.Where(t => t.IsTopTask).Select(ToLine).ToList();

    /// <summary>非置顶任务行（置顶岗位已经单独显示在顶部，避免重复）。</summary>
    public static IReadOnlyList<DutyTaskLine> NonTopTaskLines(IEnumerable<DutyTaskView>? tasks)
        => tasks is null
            ? Array.Empty<DutyTaskLine>()
            : tasks.Where(t => !t.IsTopTask).Select(ToLine).ToList();

    /// <summary>
    /// 折叠态短文案（主行 / 副行）：优先把黑板类岗位压成一行（如 `擦黑板：张三`），
    /// 其次用值周/值日行；两者都有时主行=岗位、副行=值周信息。
    /// </summary>
    public static (string Main, string? Secondary) FoldedDutyLines(DutyMode mode, DutyScheduleView? schedule)
    {
        if (schedule is null) return ("", null);
        var top = TopTaskLines(schedule.Tasks);
        var summary = DutySummaryText(mode, schedule);
        if (top.Count > 0)
        {
            var main = string.Join(" · ", top.Take(2).Select(l => l.Text));
            return (main, string.IsNullOrEmpty(summary) ? null : summary);
        }
        if (!string.IsNullOrEmpty(summary))
        {
            var rest = NonTopTaskLines(schedule.Tasks);
            return (summary, rest.Count > 0 ? rest[0].Text : null);
        }
        return ("", null);
    }

    /// <summary>
    /// 放学后“值日信息替换课表”的时间窗口：星期几由快照判断，
    /// 从 <see cref="ScheduleSnapshot.SchoolEnd"/> 起算 <paramref name="afterSchoolMinutes"/> 分钟内为 true。
    /// </summary>
    public static bool IsAfterSchoolDutyWindow(ScheduleSnapshot? snapshot, int afterSchoolMinutes, bool displayEnabled)
    {
        if (snapshot is null || !displayEnabled || afterSchoolMinutes <= 0) return false;
        if (snapshot.Phase != SchedulePhase.AfterSchool) return false;
        if (snapshot.SchoolEnd is not { } schoolEnd) return false;
        var now = snapshot.Now;
        return now >= schoolEnd && now <= schoolEnd.AddMinutes(afterSchoolMinutes);
    }

    /// <summary>
    /// 放学后替换课表的任务行（不含黑板类置顶岗位，它们仍显示在面板最顶端）。
    /// 值周/值日摘要由面板里可点击的轮值行单独显示，避免重复。
    /// </summary>
    public static IReadOnlyList<DutyTaskLine> AfterSchoolLines(DutyMode mode, DutyScheduleView? schedule)
        => schedule is null ? Array.Empty<DutyTaskLine>() : NonTopTaskLines(schedule.Tasks);

    private static DutyTaskLine ToLine(DutyTaskView task)
    {
        var name = string.IsNullOrWhiteSpace(task.TaskName) ? "岗位" : task.TaskName.Trim();
        var members = string.IsNullOrWhiteSpace(task.MembersText) ? "未安排" : task.MembersText.Trim();
        return new DutyTaskLine(name, members, task.IsTopTask);
    }
}

