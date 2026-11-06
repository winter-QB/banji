using Banji.Core.Models;

namespace Banji.Controls.Pet;

/// <summary>课表折叠态要展示的精简信息（v1.3.0）。</summary>
public readonly record struct FoldedTimetableInfo(bool HasData, string Label, string MainText, string? Secondary)
{
    public static FoldedTimetableInfo None { get; } = new(false, "", "暂无课表", null);
}

/// <summary>
/// 课表“折叠态”文案（v1.3.0）：只显示下节课时间和科目；上课中额外显示“下课 mm:ss”。
/// 纯函数，便于单测。
/// </summary>
public static class FoldedTimetableFormatter
{
    public static FoldedTimetableInfo Fold(ScheduleSnapshot? snapshot, DateTime now)
    {
        if (snapshot is null || !snapshot.HasTimetable)
            return new FoldedTimetableInfo(false, "", "暂无课表", null);

        var next = snapshot.Next;
        var current = snapshot.Current;

        if (snapshot.Phase == SchedulePhase.InClass && current is not null)
        {
            var main = snapshot.UntilCurrentEnd is { } untilEnd
                ? ScheduleSnapshot.FormatCountdown(untilEnd)
                : "正在上课";
            var secondary = next is not null
                ? $"下节 {next.Start:HH:mm} {next.CourseDisplay}"
                : "最后一节课";
            return new FoldedTimetableInfo(true, "下课", main, secondary);
        }

        if (next is not null)
        {
            return new FoldedTimetableInfo(true, "下节", $"{next.Start:HH:mm} {next.CourseDisplay}", null);
        }

        if (snapshot.Phase == SchedulePhase.AfterSchool)
            return new FoldedTimetableInfo(false, "", "今天的课已上完", null);

        return new FoldedTimetableInfo(false, "", "今天没有课", null);
    }
}
