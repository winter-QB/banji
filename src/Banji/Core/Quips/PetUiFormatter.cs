using Banji.Core.Models;

namespace Banji.Core.Quips;

/// <summary>常驻信息框第一行：左侧小字标签 + 主数值。</summary>
public readonly record struct PrimaryLine(string Label, string Value, bool IsOverrun);

/// <summary>常驻信息框第二行：距放学 + 剩余节数。</summary>
public readonly record struct SchoolLine(string Label, string Value, string Suffix);

/// <summary>常驻信息框第三行：当前课 / 下一节（两段“小字标签 + 课程名”）。</summary>
public readonly record struct CourseLine(string Prefix1, string Value1, string Prefix2, string Value2);

/// <summary>
/// 宠物常驻信息框的纯文案逻辑（不依赖 WPF，便于单测）。
/// 所有时间格式统一走 <see cref="ScheduleSnapshot.FormatCountdown"/>：
/// 1 小时以内是 mm:ss，超过 1 小时是 h:mm:ss。
/// </summary>
public static class PetUiFormatter
{
    /// <summary>“下课超时 mm:ss”（mm:ss 递增，向下取整）。</summary>
    public static string FormatOverrun(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var totalSeconds = (long)Math.Floor(elapsed.TotalSeconds);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes:00}:{seconds:00}";
    }

    /// <summary>
    /// 第一行：上课中显示“距下课 mm:ss”，课间/课前显示“距上课 mm:ss”，放学后显示“已放学”；
    /// 下课后 <c>EndOverrunSeconds</c> 秒内优先显示“下课超时 mm:ss”。
    /// </summary>
    public static PrimaryLine Primary(ScheduleSnapshot snapshot, TimeSpan? overrunElapsed)
    {
        if (overrunElapsed is not null)
            return new PrimaryLine("下课超时", FormatOverrun(overrunElapsed.Value), true);

        switch (snapshot.Phase)
        {
            case SchedulePhase.NoTimetable:
                return new PrimaryLine("", "暂无课表", false);
            case SchedulePhase.AfterSchool:
                return new PrimaryLine("", "已放学", false);
            case SchedulePhase.InClass:
                return new PrimaryLine("距下课", Format(snapshot.UntilCurrentEnd), false);
            case SchedulePhase.BeforeSchool:
            case SchedulePhase.Break:
            case SchedulePhase.LunchBreak:
                return new PrimaryLine("距上课", Format(snapshot.UntilNextStart), false);
            default:
                return new PrimaryLine("", "—", false);
        }
    }

    /// <summary>第二行：距放学 h:mm:ss + 剩余节数。</summary>
    public static SchoolLine School(ScheduleSnapshot snapshot)
    {
        if (snapshot.Phase == SchedulePhase.NoTimetable)
            return new SchoolLine("", "—", "");
        if (snapshot.Phase == SchedulePhase.AfterSchool)
            return new SchoolLine("", "今天辛苦啦", "");
        if (snapshot.UntilSchoolEnd is not { } untilEnd)
            return new SchoolLine("", "—", "");
        var suffix = snapshot.PeriodsRemaining > 0 ? $" · 还剩 {snapshot.PeriodsRemaining} 节" : "";
        return new SchoolLine("距放学", ScheduleSnapshot.FormatCountdown(untilEnd), suffix);
    }

    /// <summary>第三行：现在/下节课程名，如“现在 数学 / 下节 体育”。</summary>
    public static CourseLine Course(ScheduleSnapshot snapshot)
    {
        var current = snapshot.Current;
        var next = snapshot.Next;
        if (current is null && next is null)
        {
            var text = snapshot.Phase switch
            {
                SchedulePhase.NoTimetable => "暂无课表",
                SchedulePhase.AfterSchool => "今天课已上完",
                _ => "—"
            };
            return new CourseLine("", text, "", "");
        }

        var prefix1 = current is null ? "" : "现在";
        var value1 = current?.CourseDisplay ?? "";
        string prefix2 = "", value2 = "";
        if (next is not null)
        {
            prefix2 = current is null ? "下节" : " / 下节";
            value2 = next.CourseDisplay;
        }
        return new CourseLine(prefix1, value1, prefix2, value2);
    }

    /// <summary>课表面板标题，如“今天 · 周五”。</summary>
    public static string TimetableTitle(DateTime now) => $"今天 · {WeekdayZh(now.DayOfWeek)}";

    public static string WeekdayZh(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    /// <summary>第 N 节的短序号，如“3”。</summary>
    public static string PeriodTag(CourseOccurrence course) => course.PeriodIndex > 0 ? course.PeriodIndex.ToString() : "";

    /// <summary>课程行右侧的时间小字，如“08:55”。</summary>
    public static string CourseTime(CourseOccurrence course) => $"{course.Start:HH:mm}";

    private static string Format(TimeSpan? span)
        => span is { } value ? ScheduleSnapshot.FormatCountdown(value) : "—";
}

