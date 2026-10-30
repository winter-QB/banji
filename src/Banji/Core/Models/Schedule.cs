namespace Banji.Core.Models;

public enum SchedulePhase
{
    /// <summary>没有课表数据。</summary>
    NoTimetable = 0,
    BeforeSchool = 1,
    InClass = 2,
    Break = 3,
    LunchBreak = 4,
    AfterSchool = 5
}

public enum CourseStatus
{
    Past = 0,
    Active = 1,
    Next = 2,
    Upcoming = 3
}

/// <summary>课程表里的一次“出现”（某天第 N 节 + 课程名 + 起止时间）。</summary>
public sealed class CourseOccurrence
{
    public int PeriodIndex { get; init; }
    public string PeriodName { get; init; } = "";
    public string Course { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public CourseStatus Status { get; set; }

    public bool HasCourse => !string.IsNullOrWhiteSpace(Course);
    public string CourseDisplay => HasCourse ? Course.Trim() : "未设置";
    public string TimeText => $"{Start:HH:mm}-{End:HH:mm}";
    public TimeSpan Duration => End - Start;

    public CourseOccurrence Clone() => new()
    {
        PeriodIndex = PeriodIndex,
        PeriodName = PeriodName,
        Course = Course,
        Start = Start,
        End = End,
        Status = Status
    };
}

/// <summary>某一时刻的完整课表快照。纯数据，UI 直接绑定。</summary>
public sealed class ScheduleSnapshot
{
    public DateTime Now { get; init; }
    public SchedulePhase Phase { get; init; } = SchedulePhase.NoTimetable;
    public bool HasTimetable { get; init; }

    /// <summary>当前正在上的课（Phase == InClass 时有值）。</summary>
    public CourseOccurrence? Current { get; init; }
    /// <summary>下一节要上的课（当天下课后为 null）。</summary>
    public CourseOccurrence? Next { get; init; }
    /// <summary>宠物课表面板里用强调色显示的课程：下一节；若是“正在上且开始未满 10 分钟”则仍强调当前节。</summary>
    public CourseOccurrence? Highlighted { get; init; }

    /// <summary>距本节课下课。</summary>
    public TimeSpan? UntilCurrentEnd { get; init; }
    /// <summary>距下一节课上课。</summary>
    public TimeSpan? UntilNextStart { get; init; }
    /// <summary>距放学。</summary>
    public TimeSpan? UntilSchoolEnd { get; init; }
    /// <summary>今天还剩几节（含正在上的）。</summary>
    public int PeriodsRemaining { get; init; }
    public IReadOnlyList<CourseOccurrence> Today { get; init; } = Array.Empty<CourseOccurrence>();
    public DateTime? FirstStart { get; init; }
    public DateTime? SchoolEnd { get; init; }
    /// <summary>已应用的铃声误差（秒）。</summary>
    public int BellOffsetSeconds { get; init; }

    public static ScheduleSnapshot Empty(DateTime now) => new() { Now = now, Phase = SchedulePhase.NoTimetable };

    /// <summary>“距下课 / 距上课 / 距放学”的统一倒计时文案。</summary>
    public string PrimaryCountdownText
    {
        get
        {
            var span = Phase switch
            {
                SchedulePhase.InClass => UntilCurrentEnd,
                SchedulePhase.BeforeSchool or SchedulePhase.Break or SchedulePhase.LunchBreak => UntilNextStart,
                _ => null
            };
            if (span is null) return "";
            return FormatCountdown(span.Value);
        }
    }

    public static string FormatCountdown(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var totalSeconds = (long)Math.Ceiling(span.TotalSeconds);
        var h = totalSeconds / 3600;
        var m = totalSeconds % 3600 / 60;
        var s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }
}
