using Banji.Core.Models;
using Banji.Core.Quips;

namespace Banji.Tests;

/// <summary>常驻信息框/气泡文案纯函数测试（含倒计时边界）。</summary>
public static class QuipFormatterTests
{
    private static ScheduleSnapshot Snapshot(
        SchedulePhase phase,
        CourseOccurrence? current = null,
        CourseOccurrence? next = null,
        TimeSpan? untilCurrentEnd = null,
        TimeSpan? untilNextStart = null,
        TimeSpan? untilSchoolEnd = null,
        int periodsRemaining = 0)
    {
        return new ScheduleSnapshot
        {
            Now = new DateTime(2026, 10, 30, 10, 0, 0),
            Phase = phase,
            HasTimetable = phase != SchedulePhase.NoTimetable,
            Current = current,
            Next = next,
            UntilCurrentEnd = untilCurrentEnd,
            UntilNextStart = untilNextStart,
            UntilSchoolEnd = untilSchoolEnd,
            PeriodsRemaining = periodsRemaining
        };
    }

    private static CourseOccurrence Course(int index, string name, string periodName = "")
        => new()
        {
            PeriodIndex = index,
            PeriodName = periodName,
            Course = name,
            Start = new DateTime(2026, 10, 30, 8 + index, 0, 0),
            End = new DateTime(2026, 10, 30, 8 + index, 45, 0)
        };

    [BanjiTest]
    public static void 倒计时格式边界()
    {
        Assert.Equal("00:00", ScheduleSnapshot.FormatCountdown(TimeSpan.Zero));
        Assert.Equal("00:00", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(-5)));
        Assert.Equal("00:01", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(0.2)));
        Assert.Equal("00:59", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(59)));
        Assert.Equal("01:00", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(59.2)));
        Assert.Equal("59:59", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(3599)));
        Assert.Equal("1:00:00", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(3600)));
        Assert.Equal("1:01:02", ScheduleSnapshot.FormatCountdown(TimeSpan.FromSeconds(3661.2)));
        Assert.Equal("2:13:05", ScheduleSnapshot.FormatCountdown(new TimeSpan(2, 13, 5)));
    }

    [BanjiTest]
    public static void 下课超时文案边界()
    {
        Assert.Equal("00:00", PetUiFormatter.FormatOverrun(TimeSpan.FromSeconds(-1)));
        Assert.Equal("00:00", PetUiFormatter.FormatOverrun(TimeSpan.Zero));
        Assert.Equal("00:00", PetUiFormatter.FormatOverrun(TimeSpan.FromSeconds(0.9)));
        Assert.Equal("00:07", PetUiFormatter.FormatOverrun(TimeSpan.FromSeconds(7.9)));
        Assert.Equal("01:05", PetUiFormatter.FormatOverrun(TimeSpan.FromSeconds(65)));
        Assert.Equal("02:00", PetUiFormatter.FormatOverrun(TimeSpan.FromSeconds(120)));
    }

    [BanjiTest]
    public static void 第一行按阶段切换文案()
    {
        var inClass = Snapshot(SchedulePhase.InClass,
            current: Course(2, "数学"), untilCurrentEnd: TimeSpan.FromSeconds(754));
        var line = PetUiFormatter.Primary(inClass, null);
        Assert.Equal("距下课", line.Label);
        Assert.Equal("12:34", line.Value);
        Assert.False(line.IsOverrun);

        var breakLine = PetUiFormatter.Primary(
            Snapshot(SchedulePhase.Break, next: Course(3, "英语"), untilNextStart: TimeSpan.FromSeconds(65)), null);
        Assert.Equal("距上课", breakLine.Label);
        Assert.Equal("01:05", breakLine.Value);

        var before = PetUiFormatter.Primary(
            Snapshot(SchedulePhase.BeforeSchool, next: Course(1, "语文"), untilNextStart: TimeSpan.FromSeconds(30)), null);
        Assert.Equal("距上课", before.Label);
        Assert.Equal("00:30", before.Value);

        var after = PetUiFormatter.Primary(Snapshot(SchedulePhase.AfterSchool), null);
        Assert.Equal("已放学", after.Value);
        Assert.Equal("", after.Label);

        var none = PetUiFormatter.Primary(Snapshot(SchedulePhase.NoTimetable), null);
        Assert.Equal("暂无课表", none.Value);
    }

    [BanjiTest]
    public static void 下课后超时优先显示在第一行()
    {
        var snapshot = Snapshot(SchedulePhase.Break, next: Course(3, "英语"), untilNextStart: TimeSpan.FromSeconds(200));
        var line = PetUiFormatter.Primary(snapshot, TimeSpan.FromSeconds(12.4));
        Assert.True(line.IsOverrun);
        Assert.Equal("下课超时", line.Label);
        Assert.Equal("00:12", line.Value);
    }

    [BanjiTest]
    public static void 第一行缺数据时用短横线兜底()
    {
        var line = PetUiFormatter.Primary(Snapshot(SchedulePhase.InClass, current: Course(2, "数学")), null);
        Assert.Equal("距下课", line.Label);
        Assert.Equal("—", line.Value);
    }

    [BanjiTest]
    public static void 第二行距放学与剩余节数()
    {
        var line = PetUiFormatter.School(Snapshot(SchedulePhase.InClass,
            current: Course(2, "数学"),
            untilSchoolEnd: new TimeSpan(2, 13, 5),
            periodsRemaining: 3));
        Assert.Equal("距放学", line.Label);
        Assert.Equal("2:13:05", line.Value);
        Assert.Equal(" · 还剩 3 节", line.Suffix);

        var last = PetUiFormatter.School(Snapshot(SchedulePhase.InClass, untilSchoolEnd: TimeSpan.FromSeconds(60), periodsRemaining: 1));
        Assert.Equal(" · 还剩 1 节", last.Suffix);

        var after = PetUiFormatter.School(Snapshot(SchedulePhase.AfterSchool));
        Assert.Equal("今天辛苦啦", after.Value);

        var none = PetUiFormatter.School(Snapshot(SchedulePhase.NoTimetable));
        Assert.Equal("—", none.Value);
    }

    [BanjiTest]
    public static void 第三行现在与下节课程名()
    {
        var line = PetUiFormatter.Course(Snapshot(SchedulePhase.InClass,
            current: Course(2, "数学"), next: Course(3, "体育")));
        Assert.Equal("现在", line.Prefix1);
        Assert.Equal("数学", line.Value1);
        Assert.Equal(" / 下节", line.Prefix2);
        Assert.Equal("体育", line.Value2);

        var nextOnly = PetUiFormatter.Course(Snapshot(SchedulePhase.Break, next: Course(3, "体育")));
        Assert.Equal("", nextOnly.Value1);
        Assert.Equal("下节", nextOnly.Prefix2);
        Assert.Equal("体育", nextOnly.Value2);

        var lastPeriod = PetUiFormatter.Course(Snapshot(SchedulePhase.InClass, current: Course(8, "历史")));
        Assert.Equal("", lastPeriod.Prefix2);
        Assert.Equal("", lastPeriod.Value2);

        Assert.Equal("今天课已上完", PetUiFormatter.Course(Snapshot(SchedulePhase.AfterSchool)).Value1);
        Assert.Equal("暂无课表", PetUiFormatter.Course(Snapshot(SchedulePhase.NoTimetable)).Value1);
    }

    [BanjiTest]
    public static void 课程行辅助文案()
    {
        var course = Course(3, "英语");
        Assert.Equal("3", PetUiFormatter.PeriodTag(course));
        Assert.Equal("11:00", PetUiFormatter.CourseTime(course));
        Assert.Equal("今天 · 周五", PetUiFormatter.TimetableTitle(new DateTime(2026, 10, 30)));
        Assert.Equal("今天 · 周日", PetUiFormatter.TimetableTitle(new DateTime(2026, 11, 1)));
    }
}

