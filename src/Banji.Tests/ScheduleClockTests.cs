using Banji.Core.Abstractions;
using Banji.Core.Models;
using Banji.Core.Schedule;

namespace Banji.Tests;

/// <summary>
/// ClassClock 边界测试：通过 PulseAt(假时间) 直接驱动，不依赖真实计时器。
/// 断言：跨过上课/下课/放学/大课间时刻只触发一次；跳变不漏触；同一时刻重复采样不重复；
/// 休眠恢复只处理最近状态；Refresh 不补弹提示。
/// </summary>
public static class ScheduleClockTests
{
    private static (ClassClock Clock, FakeTimetableService Timetable, ScheduleFakeSettingsService Settings, List<ClassTransitionEventArgs> Events)
        Create(DateTime? startNow = null)
    {
        var timetable = new FakeTimetableService { Current = ScheduleTestData.StandardWeek() };
        var settings = new ScheduleFakeSettingsService();
        var schedule = new ScheduleService(timetable, settings);
        var now = startNow ?? ScheduleTestData.At(7, 0);
        var clock = new ClassClock(schedule, () => now);
        var events = new List<ClassTransitionEventArgs>();
        clock.Transition += (_, e) => events.Add(e);
        return (clock, timetable, settings, events);
    }

    private static int Count(List<ClassTransitionEventArgs> events, ClassTransitionKind kind)
        => events.Count(e => e.Kind == kind);

    [BanjiTest]
    public static void 上课边界前后一秒各只触发一次()
    {
        var (clock, _, _, events) = Create();

        clock.PulseAt(ScheduleTestData.At(7, 59, 59)); // 初始化，不触发
        Assert.Equal(0, events.Count);

        clock.PulseAt(ScheduleTestData.At(8, 0, 0)); // 正点触发上课
        clock.PulseAt(ScheduleTestData.At(8, 0, 1)); // 之后不再重复
        clock.PulseAt(ScheduleTestData.At(8, 0, 2));

        Assert.Equal(1, Count(events, ClassTransitionKind.ClassStarted));
        Assert.Equal(1, Count(events, ClassTransitionKind.SchoolStarted));
        var started = events.First(e => e.Kind == ClassTransitionKind.ClassStarted);
        Assert.Equal(1, started.Current!.PeriodIndex);
        Assert.Equal("语文", started.Current.Course);
        Assert.Equal(ScheduleTestData.At(8, 0, 0), started.Time);
    }

    [BanjiTest]
    public static void 下课边界前后一秒各只触发一次()
    {
        var (clock, _, _, events) = Create();

        clock.PulseAt(ScheduleTestData.At(8, 44, 59));
        Assert.Equal(0, events.Count);

        clock.PulseAt(ScheduleTestData.At(8, 45, 0));
        clock.PulseAt(ScheduleTestData.At(8, 45, 1));
        clock.PulseAt(ScheduleTestData.At(8, 45, 2));

        Assert.Equal(1, Count(events, ClassTransitionKind.ClassEnded));
        var ended = events.First(e => e.Kind == ClassTransitionKind.ClassEnded);
        Assert.Equal(1, ended.Current!.PeriodIndex);
        Assert.Equal(2, ended.Next!.PeriodIndex);
        Assert.Equal(ScheduleTestData.At(8, 45, 0), ended.Time);
        Assert.Equal(SchedulePhase.Break, ended.Snapshot.Phase);
    }

    [BanjiTest]
    public static void 同一时刻重复采样不会重复触发()
    {
        var (clock, _, _, events) = Create();
        clock.PulseAt(ScheduleTestData.At(7, 59, 59));
        clock.PulseAt(ScheduleTestData.At(8, 0, 0));
        clock.PulseAt(ScheduleTestData.At(8, 0, 0)); // 相同时间
        clock.PulseAt(ScheduleTestData.At(8, 0, 0));
        Assert.Equal(1, Count(events, ClassTransitionKind.ClassStarted));
    }

    [BanjiTest]
    public static void 跨秒跳变不漏触()
    {
        var (clock, _, _, events) = Create();
        clock.PulseAt(ScheduleTestData.At(7, 59, 59));
        // 中间隔了 3 秒（仍在 5 秒容差内）：边界 8:00:00 落在窗口里，仍应触发一次
        clock.PulseAt(ScheduleTestData.At(8, 0, 2));
        Assert.Equal(1, Count(events, ClassTransitionKind.ClassStarted));
    }

    [BanjiTest]
    public static void 休眠恢复只处理最近状态不补放积压()
    {
        var (clock, _, _, events) = Create();
        clock.PulseAt(ScheduleTestData.At(7, 30)); // 课前
        // 直接跳到 10:30（此时在上第 3 节）：只触发“最近”的一次上课提示，不补放第 1/2 节
        clock.PulseAt(ScheduleTestData.At(10, 30));

        Assert.Equal(1, events.Count);
        Assert.Equal(ClassTransitionKind.ClassStarted, events[0].Kind);
        Assert.Equal(3, events[0].Current!.PeriodIndex);
        Assert.Equal(3, clock.Current.Current!.PeriodIndex);
    }

    [BanjiTest]
    public static void 大课间边界触发一次()
    {
        var (clock, _, _, events) = Create();
        clock.PulseAt(ScheduleTestData.At(9, 39, 59));
        clock.PulseAt(ScheduleTestData.At(9, 40, 0)); // 第 2 节下课 + 大课间开始
        clock.PulseAt(ScheduleTestData.At(9, 40, 1));

        Assert.Equal(1, Count(events, ClassTransitionKind.ClassEnded));
        Assert.Equal(1, Count(events, ClassTransitionKind.BreakStarted));
        Assert.Equal(SchedulePhase.Break, clock.Current.Phase);
    }

    [BanjiTest]
    public static void 午休边界进入LunchBreak()
    {
        var (clock, _, _, events) = Create();
        clock.PulseAt(ScheduleTestData.At(11, 39, 59));
        clock.PulseAt(ScheduleTestData.At(11, 40, 0));
        clock.PulseAt(ScheduleTestData.At(11, 40, 1));

        Assert.Equal(SchedulePhase.LunchBreak, clock.Current.Phase);
        Assert.Equal(1, Count(events, ClassTransitionKind.ClassEnded));
        Assert.Equal(1, Count(events, ClassTransitionKind.BreakStarted));
    }

    [BanjiTest]
    public static void 放学边界同时触发下课与放学()
    {
        var (clock, _, _, events) = Create();
        clock.PulseAt(ScheduleTestData.At(17, 29, 59));
        clock.PulseAt(ScheduleTestData.At(17, 30, 0));
        clock.PulseAt(ScheduleTestData.At(17, 30, 1));

        Assert.Equal(1, Count(events, ClassTransitionKind.ClassEnded));
        Assert.Equal(1, Count(events, ClassTransitionKind.SchoolEnded));
        Assert.Equal(SchedulePhase.AfterSchool, clock.Current.Phase);
        var ended = events.First(e => e.Kind == ClassTransitionKind.ClassEnded);
        Assert.Equal(8, ended.Current!.PeriodIndex);
    }

    [BanjiTest]
    public static void 铃声误差改变后边界随偏移移动()
    {
        var (clock, _, settings, events) = Create();

        // 误差 +60 秒：8:00:00 不再上课，8:01:00 才触发
        settings.Current.BellOffsetSeconds = 60;
        clock.PulseAt(ScheduleTestData.At(8, 0, 0));
        clock.PulseAt(ScheduleTestData.At(8, 0, 59));
        Assert.Equal(0, events.Count);
        clock.PulseAt(ScheduleTestData.At(8, 1, 0));
        Assert.Equal(1, Count(events, ClassTransitionKind.ClassStarted));

        // 误差 -60 秒：下课边界从 8:45 提前到 8:44
        settings.Current.BellOffsetSeconds = -60;
        clock.PulseAt(ScheduleTestData.At(8, 43, 59));
        clock.PulseAt(ScheduleTestData.At(8, 44, 0));
        clock.PulseAt(ScheduleTestData.At(8, 44, 1));
        Assert.Equal(1, Count(events, ClassTransitionKind.ClassEnded));
    }

    [BanjiTest]
    public static void Refresh立即重算但不补弹提示()
    {
        var now = ScheduleTestData.At(7, 59, 0);
        var timetable = new FakeTimetableService { Current = ScheduleTestData.StandardWeek() };
        var settings = new ScheduleFakeSettingsService();
        var schedule = new ScheduleService(timetable, settings);
        var clock = new ClassClock(schedule, () => now);
        var events = new List<ClassTransitionEventArgs>();
        clock.Transition += (_, e) => events.Add(e);

        clock.PulseAt(now);
        Assert.Equal(SchedulePhase.BeforeSchool, clock.Current.Phase);

        // 时间来到 8:00，但走 Refresh()：应立即重算成 InClass，但不弹上课提示
        now = ScheduleTestData.At(8, 0, 0);
        clock.Refresh();
        Assert.Equal(SchedulePhase.InClass, clock.Current.Phase);
        Assert.Equal(0, events.Count);

        // 紧接着的正常 Tick 也不应重复触发（Refresh 已重新同步基准）
        clock.PulseAt(ScheduleTestData.At(8, 0, 1));
        Assert.Equal(0, events.Count);
    }

    [BanjiTest]
    public static void 时钟回拨不触发事件()
    {
        var (clock, _, _, events) = Create();
        clock.PulseAt(ScheduleTestData.At(8, 5, 0)); // 初始化（在上第 1 节，不触发）
        clock.PulseAt(ScheduleTestData.At(7, 0, 0)); // 系统时间被往回调
        Assert.Equal(0, events.Count);
    }

    [BanjiTest]
    public static void 周末不触发任何过渡()
    {
        var (clock, _, _, events) = Create();
        clock.PulseAt(ScheduleTestData.AtSat(7, 0));
        clock.PulseAt(ScheduleTestData.AtSat(8, 0));
        clock.PulseAt(ScheduleTestData.AtSat(10, 0));
        Assert.Equal(0, events.Count);
        Assert.Equal(SchedulePhase.NoTimetable, clock.Current.Phase);
    }

    [BanjiTest]
    public static void Start与Stop可安全重复调用()
    {
        // 该测试不启动 Dispatcher 消息循环，只验证不抛异常、Current 有值
        var (clock, _, _, _) = Create();
        clock.Refresh();
        Assert.NotNull(clock.Current);
        clock.Stop();
        clock.Stop();
    }
}

