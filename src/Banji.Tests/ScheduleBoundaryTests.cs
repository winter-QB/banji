using Banji.Core.Abstractions;
using Banji.Core.Models;

namespace Banji.Tests;

/// <summary>
/// IScheduleService.UpcomingBoundaries 测试：只返回未来、排序、去重、含放学、
/// 含铃声误差、跨天/周末、临时换课与取消、空课表、count 收敛。
/// </summary>
public static class ScheduleBoundaryTests
{
    [BanjiTest]
    public static void 未来边界按时间排序且只返回未来()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var list = svc.UpcomingBoundaries(ScheduleTestData.At(7, 30), 3);

        Assert.Equal(3, list.Count);
        Assert.Equal(ScheduleTestData.At(8, 0), list[0].Time);
        Assert.Equal(ClassBoundaryKind.ClassStart, list[0].Kind);
        Assert.Equal(1, list[0].PeriodIndex);
        Assert.True(list[0].Title.Contains("语文"), "标题应含课程名：" + list[0].Title);

        Assert.Equal(ScheduleTestData.At(8, 45), list[1].Time);
        Assert.Equal(ClassBoundaryKind.ClassEnd, list[1].Kind);

        Assert.Equal(ScheduleTestData.At(8, 55), list[2].Time);
        Assert.Equal(ClassBoundaryKind.ClassStart, list[2].Kind);
        Assert.Equal(2, list[2].PeriodIndex);
    }

    [BanjiTest]
    public static void 上课中第一个边界是当前课的下课()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var list = svc.UpcomingBoundaries(ScheduleTestData.At(8, 5), 2);

        Assert.Equal(2, list.Count);
        Assert.Equal(ScheduleTestData.At(8, 45), list[0].Time);
        Assert.Equal(ClassBoundaryKind.ClassEnd, list[0].Kind);
        Assert.Equal(1, list[0].PeriodIndex);
        Assert.Equal(ScheduleTestData.At(8, 55), list[1].Time);
        Assert.Equal(ClassBoundaryKind.ClassStart, list[1].Kind);
    }

    [BanjiTest]
    public static void 边界时刻正好等于now时不返回_只返回严格未来()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var list = svc.UpcomingBoundaries(ScheduleTestData.At(8, 0), 1);
        Assert.Equal(ScheduleTestData.At(8, 45), list[0].Time);
        Assert.Equal(ClassBoundaryKind.ClassEnd, list[0].Kind);
    }

    [BanjiTest]
    public static void 铃声误差应用到所有边界()
    {
        var (svc, _, settings) = ScheduleTestData.Create();

        settings.Current.BellOffsetSeconds = 25;
        var plus = svc.UpcomingBoundaries(ScheduleTestData.At(7, 30), 2);
        Assert.Equal(ScheduleTestData.At(8, 0, 25), plus[0].Time);
        Assert.Equal(ScheduleTestData.At(8, 45, 25), plus[1].Time);

        settings.Current.BellOffsetSeconds = -30;
        var minus = svc.UpcomingBoundaries(ScheduleTestData.At(7, 30), 1);
        Assert.Equal(ScheduleTestData.At(7, 59, 30), minus[0].Time);
    }

    [BanjiTest]
    public static void 放学边界包含且与最后一节下课去重()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var list = svc.UpcomingBoundaries(ScheduleTestData.At(17, 29), 2);

        Assert.Equal(2, list.Count);
        Assert.Equal(ScheduleTestData.At(17, 30), list[0].Time);
        Assert.Equal(ClassBoundaryKind.SchoolEnd, list[0].Kind);
        Assert.Equal("放学", list[0].Title);
        // 同一时刻的“最后一节下课”被“放学”去重替换
        Assert.Equal(0, list.Count(b => b.Kind == ClassBoundaryKind.ClassEnd && b.Time == ScheduleTestData.At(17, 30)));

        // 第二天第一节
        Assert.Equal(ScheduleTestData.Monday.AddDays(1).AddHours(8), list[1].Time);
        Assert.Equal(ClassBoundaryKind.ClassStart, list[1].Kind);
    }

    [BanjiTest]
    public static void 周五放学后跨天到周一_周末自动跳过()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var fridayEvening = ScheduleTestData.Monday.AddDays(4).AddHours(18);
        var list = svc.UpcomingBoundaries(fridayEvening, 2);

        Assert.Equal(2, list.Count);
        var monday8 = new DateTime(2026, 11, 9, 8, 0, 0);
        Assert.Equal(monday8, list[0].Time);
        Assert.Equal(DayOfWeek.Monday, list[0].Time.DayOfWeek);
        Assert.Equal(ClassBoundaryKind.ClassStart, list[0].Kind);
        Assert.Equal(monday8.AddMinutes(45), list[1].Time);
    }

    [BanjiTest]
    public static void 周末无课跳过到下一个上课日()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var saturdayMorning = ScheduleTestData.Saturday.AddHours(10); // 周六
        var list = svc.UpcomingBoundaries(saturdayMorning, 1);

        Assert.Equal(1, list.Count);
        Assert.Equal(new DateTime(2026, 11, 2, 8, 0, 0), list[0].Time); // 下周一
        Assert.Equal(ClassBoundaryKind.ClassStart, list[0].Kind);
    }

    [BanjiTest]
    public static void 放学后能跨天补满所需数量()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var list = svc.UpcomingBoundaries(ScheduleTestData.Monday.AddHours(18), 6);

        Assert.Equal(6, list.Count);
        Assert.Equal(ScheduleTestData.Monday.AddDays(1).Date, list[0].Time.Date);
        for (var i = 1; i < list.Count; i++)
            Assert.True(list[i].Time >= list[i - 1].Time, $"边界应按时间排序：{list[i - 1].Time} -> {list[i].Time}");
    }

    [BanjiTest]
    public static void 临时换课与取消影响边界()
    {
        var (svc, tt, _) = ScheduleTestData.Create();
        tt.SetOverride(new DayOverride
        {
            Date = DateOnly.FromDateTime(ScheduleTestData.Monday),
            CourseReplacements = { [2] = "数学竞赛" },
            CancelledPeriods = { 1 }
        });

        var list = svc.UpcomingBoundaries(ScheduleTestData.At(7, 30), 2);
        // 第 1 节被取消：第一个边界直接是第 2 节上课（8:55），课程名已替换
        Assert.Equal(ScheduleTestData.At(8, 55), list[0].Time);
        Assert.Equal(ClassBoundaryKind.ClassStart, list[0].Kind);
        Assert.Equal(2, list[0].PeriodIndex);
        Assert.True(list[0].Title.Contains("数学竞赛"), "应使用临时换课后的课程名：" + list[0].Title);
        Assert.Equal(ScheduleTestData.At(9, 40), list[1].Time);
        Assert.Equal(ClassBoundaryKind.ClassEnd, list[1].Kind);
    }

    [BanjiTest]
    public static void 空课表返回空边界()
    {
        var (svc, tt, _) = ScheduleTestData.Create();
        tt.Current = new WeekTimetable();
        var list = svc.UpcomingBoundaries(ScheduleTestData.At(9, 0), 6);
        Assert.Equal(0, list.Count);
    }

    [BanjiTest]
    public static void count收敛且结果无重复()
    {
        var (svc, _, _) = ScheduleTestData.Create();

        var list = svc.UpcomingBoundaries(ScheduleTestData.At(7, 30), 100);
        Assert.True(list.Count is > 0 and <= 60, $"count 应收敛到 60 以内，实际 {list.Count}");

        var seen = new HashSet<(DateTime Time, ClassBoundaryKind Kind)>();
        foreach (var boundary in list)
            Assert.True(seen.Add((boundary.Time, boundary.Kind)), $"重复边界：{boundary.Time} {boundary.Kind}");

        // count=1 只返回一个；count=0/负数收敛为 1
        Assert.Equal(1, svc.UpcomingBoundaries(ScheduleTestData.At(7, 30), 1).Count);
        Assert.Equal(1, svc.UpcomingBoundaries(ScheduleTestData.At(7, 30), 0).Count);
        Assert.Equal(1, svc.UpcomingBoundaries(ScheduleTestData.At(7, 30), -5).Count);
    }

    [BanjiTest]
    public static void 放学边界在样例数据里只出现一次()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var list = svc.UpcomingBoundaries(ScheduleTestData.At(7, 0), 60);
        var schoolEnds = list.Where(b => b.Kind == ClassBoundaryKind.SchoolEnd).ToList();
        Assert.Equal(1, schoolEnds.Count(b => b.Time.Date == ScheduleTestData.Monday.Date));
        Assert.Equal("放学", schoolEnds.First(b => b.Time.Date == ScheduleTestData.Monday.Date).Title);
    }
}
