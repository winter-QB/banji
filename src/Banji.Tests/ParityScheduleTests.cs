using Banji.Core.Abstractions;
using Banji.Core.Models;
using Banji.Core.Schedule;
using Banji.Core.Settings;
using Banji.Core.WeekParityEngine;

namespace Banji.Tests;

/// <summary>
/// v1.4.0 时间引擎单双周接入测试：课程解析优先级（临时换课 &gt; 单/双专用 &gt; 通用）、
/// 快照/下一节/边界按周解析、关闭单双周时与 v1.3 行为一致。
/// </summary>
public static class ParityScheduleTests
{
    private static readonly DateOnly SemesterMonday = new(2026, 11, 2); // 周一，第 1 周（单周）
    private static readonly DateTime OddMonday = new(2026, 11, 2);
    private static readonly DateTime EvenMonday = new(2026, 11, 9);

    private sealed record Fixture(
        ScheduleService Service,
        FakeTimetableService Timetable,
        ScheduleFakeSettingsService Settings,
        WeekParityService Parity);

    private static Fixture Create(bool enabled = true)
    {
        var timetable = new FakeTimetableService { Current = ScheduleTestData.StandardWeek() };
        var settings = new ScheduleFakeSettingsService();
        settings.Current.EnableWeekParity = enabled;
        settings.Current.SemesterFirstWeekMonday = SemesterMonday;
        settings.Current.FirstWeekIsOdd = true;
        var parity = new WeekParityService(settings, () => new DateTime(2026, 11, 2, 8, 0, 0));
        var service = new ScheduleService(timetable, settings, parity);
        return new Fixture(service, timetable, settings, parity);
    }

    private static void SetWeekCourse(WeekTimetable timetable, DayOfWeek day, int periodIndex, WeekType weeks, string course)
    {
        if (!timetable.Days.TryGetValue(day, out var list) || list is null)
            timetable.Days[day] = list = new List<TimetableEntry>();
        list.RemoveAll(e => e.PeriodIndex == periodIndex && e.Weeks == weeks);
        if (!string.IsNullOrEmpty(course))
            list.Add(new TimetableEntry { PeriodIndex = periodIndex, Course = course, Weeks = weeks });
    }

    private static Fixture WithMondayPeriod1Variants()
    {
        var fx = Create();
        var tt = fx.Timetable.Current;
        SetWeekCourse(tt, DayOfWeek.Monday, 1, WeekType.All, "语文");
        SetWeekCourse(tt, DayOfWeek.Monday, 1, WeekType.Odd, "数学");
        SetWeekCourse(tt, DayOfWeek.Monday, 1, WeekType.Even, "英语");
        return fx;
    }

    [BanjiTest]
    public static void 单周显示专用_双周显示另一半专用()
    {
        var fx = WithMondayPeriod1Variants();

        var odd = fx.Service.SnapshotAt(OddMonday.AddHours(8).AddMinutes(5));
        Assert.Equal(SchedulePhase.InClass, odd.Phase);
        Assert.Equal("数学", odd.Current!.Course);

        var even = fx.Service.SnapshotAt(EvenMonday.AddHours(8).AddMinutes(5));
        Assert.Equal("英语", even.Current!.Course);

        // 第 3 周又回到单周
        var oddAgain = fx.Service.SnapshotAt(new DateTime(2026, 11, 16, 8, 5, 0));
        Assert.Equal("数学", oddAgain.Current!.Course);
    }

    [BanjiTest]
    public static void 没有专用条目时回退通用()
    {
        var fx = Create();
        SetWeekCourse(fx.Timetable.Current, DayOfWeek.Monday, 1, WeekType.All, "语文");

        Assert.Equal("语文", fx.Service.SnapshotAt(OddMonday.AddHours(8).AddMinutes(5)).Current!.Course);
        Assert.Equal("语文", fx.Service.SnapshotAt(EvenMonday.AddHours(8).AddMinutes(5)).Current!.Course);
    }

    [BanjiTest]
    public static void 关闭单双周时按通用解析_行为与v13一致()
    {
        var fx = Create(enabled: false);
        var tt = fx.Timetable.Current;
        SetWeekCourse(tt, DayOfWeek.Monday, 1, WeekType.All, "语文");
        SetWeekCourse(tt, DayOfWeek.Monday, 1, WeekType.Odd, "数学");
        SetWeekCourse(tt, DayOfWeek.Monday, 1, WeekType.Even, "英语");

        Assert.Equal("语文", fx.Service.SnapshotAt(OddMonday.AddHours(8).AddMinutes(5)).Current!.Course);
        Assert.Equal("语文", fx.Service.SnapshotAt(EvenMonday.AddHours(8).AddMinutes(5)).Current!.Course);
        Assert.Equal(WeekParity.Odd, fx.Parity.GetParity(SemesterMonday));
    }

    [BanjiTest]
    public static void 关闭单双周时专用条目被忽略()
    {
        var fx = Create(enabled: false);
        var tt = fx.Timetable.Current;
        // 只有双周专用条目，没有通用：关闭单双周后应显示未设置
        SetWeekCourse(tt, DayOfWeek.Tuesday, 2, WeekType.Even, "物理");
        SetWeekCourse(tt, DayOfWeek.Tuesday, 2, WeekType.All, "");

        var snap = fx.Service.SnapshotAt(new DateTime(2026, 11, 3, 9, 0, 0)); // 周二第 2 节
        Assert.Equal(SchedulePhase.InClass, snap.Phase);
        Assert.Equal("", snap.Current!.Course);
        Assert.Equal("未设置", snap.Current.CourseDisplay);
    }

    [BanjiTest]
    public static void 临时换课优先级最高_覆盖单双周专用()
    {
        var fx = WithMondayPeriod1Variants();
        fx.Timetable.SetOverride(new DayOverride
        {
            Date = DateOnly.FromDateTime(OddMonday),
            CourseReplacements = { [1] = "数学竞赛" }
        });
        var snap = fx.Service.SnapshotAt(OddMonday.AddHours(8).AddMinutes(5));
        Assert.Equal("数学竞赛", snap.Current!.Course);

        // 换成双周日期时临时换课仍生效
        var fxEven = WithMondayPeriod1Variants();
        fxEven.Timetable.SetOverride(new DayOverride
        {
            Date = DateOnly.FromDateTime(EvenMonday),
            CourseReplacements = { [1] = "自习" }
        });
        Assert.Equal("自习", fxEven.Service.SnapshotAt(EvenMonday.AddHours(8).AddMinutes(5)).Current!.Course);
    }

    [BanjiTest]
    public static void 下一节与当天列表按周解析()
    {
        var fx = WithMondayPeriod1Variants();
        var tt = fx.Timetable.Current;
        SetWeekCourse(tt, DayOfWeek.Monday, 2, WeekType.All, "通用数学");
        SetWeekCourse(tt, DayOfWeek.Monday, 2, WeekType.Even, "双周英语");

        var odd = fx.Service.SnapshotAt(OddMonday.AddHours(8).AddMinutes(50)); // 第 1 节后
        Assert.Equal("通用数学", odd.Next!.Course);

        var even = fx.Service.SnapshotAt(EvenMonday.AddHours(8).AddMinutes(50));
        Assert.Equal("双周英语", even.Next!.Course);

        Assert.Equal("英语", even.Today[0].Course);
        Assert.Equal("双周英语", even.Today[1].Course);
    }

    [BanjiTest]
    public static void 上下课边界标题按周解析()
    {
        var fx = WithMondayPeriod1Variants();
        var evenBoundaries = fx.Service.UpcomingBoundaries(EvenMonday.AddHours(7).AddMinutes(30), 2);
        Assert.True(evenBoundaries.Count >= 1);
        Assert.True(evenBoundaries[0].Title.Contains("英语"), evenBoundaries[0].Title);
        Assert.Equal(ClassBoundaryKind.ClassStart, evenBoundaries[0].Kind);

        var oddBoundaries = fx.Service.UpcomingBoundaries(OddMonday.AddHours(7).AddMinutes(30), 1);
        Assert.True(oddBoundaries[0].Title.Contains("数学"), oddBoundaries[0].Title);
    }

    [BanjiTest]
    public static void 周末排课按单双周筛选()
    {
        var fx = Create();
        var tt = fx.Timetable.Current;
        // 周六只有单周课：单周周六应上课，双周周六应无课
        tt.Days[DayOfWeek.Saturday] = new List<TimetableEntry>
        {
            new() { PeriodIndex = 1, Course = "周六单周课", Weeks = WeekType.Odd }
        };

        var oddSaturday = fx.Service.SnapshotAt(new DateTime(2026, 11, 7, 8, 5, 0)); // 第 1 周周六
        Assert.Equal(SchedulePhase.InClass, oddSaturday.Phase);
        Assert.Equal("周六单周课", oddSaturday.Current!.Course);

        var evenSaturday = fx.Service.SnapshotAt(new DateTime(2026, 11, 14, 8, 5, 0)); // 第 2 周周六
        Assert.Equal(SchedulePhase.NoTimetable, evenSaturday.Phase);
    }

    [BanjiTest]
    public static void 未注入单双周服务时仍走旧逻辑()
    {
        var timetable = new FakeTimetableService { Current = ScheduleTestData.StandardWeek() };
        var settings = new ScheduleFakeSettingsService();
        SetWeekCourse(timetable.Current, DayOfWeek.Monday, 1, WeekType.All, "语文");
        SetWeekCourse(timetable.Current, DayOfWeek.Monday, 1, WeekType.Odd, "数学");
        var service = new ScheduleService(timetable, settings); // 不注入 parity

        Assert.Equal("语文", service.SnapshotAt(OddMonday.AddHours(8).AddMinutes(5)).Current!.Course);
        Assert.Equal("语文", service.SnapshotAt(EvenMonday.AddHours(8).AddMinutes(5)).Current!.Course);
    }

    [BanjiTest]
    public static void 取消的节次仍最高优先级()
    {
        var fx = WithMondayPeriod1Variants();
        fx.Timetable.SetOverride(new DayOverride
        {
            Date = DateOnly.FromDateTime(OddMonday),
            CancelledPeriods = { 1 }
        });
        var snap = fx.Service.SnapshotAt(OddMonday.AddHours(8).AddMinutes(5));
        Assert.Null(snap.Current);
        Assert.Equal(7, snap.Today.Count);
    }
}
