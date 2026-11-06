using Banji.Core.Models;
using Banji.Core.Schedule;

namespace Banji.Tests;

/// <summary>
/// 铃声校准纯计算测试：最近边界匹配、偏差计算、建议误差（含正负语义与 ±60 夹取）、文案。
/// </summary>
public static class RingCalibrationTests
{
    private static readonly DateTime Boundary = new(2026, 11, 2, 8, 0, 0);

    private static List<ClassBoundary> SampleBoundaries() => new()
    {
        new ClassBoundary { Time = Boundary, Kind = ClassBoundaryKind.ClassStart, Title = "语文 上课", PeriodIndex = 1 },
        new ClassBoundary { Time = Boundary.AddMinutes(45), Kind = ClassBoundaryKind.ClassEnd, Title = "语文 下课", PeriodIndex = 1 },
        new ClassBoundary { Time = Boundary.AddMinutes(55), Kind = ClassBoundaryKind.ClassStart, Title = "数学 上课", PeriodIndex = 2 }
    };

    [BanjiTest]
    public static void 选择最近的时间边界()
    {
        var boundaries = SampleBoundaries();

        var beforeBell = RingCalibrationMath.FindNearest(boundaries, Boundary.AddSeconds(-7));
        Assert.NotNull(beforeBell);
        Assert.Equal(Boundary, beforeBell!.Time);

        var afterBell = RingCalibrationMath.FindNearest(boundaries, Boundary.AddSeconds(12));
        Assert.NotNull(afterBell);
        Assert.Equal(Boundary, afterBell!.Time);

        var nearEnd = RingCalibrationMath.FindNearest(boundaries, Boundary.AddMinutes(45).AddSeconds(-10));
        Assert.NotNull(nearEnd);
        Assert.Equal(ClassBoundaryKind.ClassEnd, nearEnd!.Kind);
    }

    [BanjiTest]
    public static void 超出容差返回空()
    {
        var boundaries = SampleBoundaries();
        // 距离最近边界 120 秒以上
        Assert.Null(RingCalibrationMath.FindNearest(boundaries, Boundary.AddSeconds(300)));
        Assert.Null(RingCalibrationMath.FindNearest(boundaries, Boundary.AddSeconds(150)));

        // 自定义容差
        Assert.Null(RingCalibrationMath.FindNearest(boundaries, Boundary.AddSeconds(90), TimeSpan.FromSeconds(30)));
        Assert.NotNull(RingCalibrationMath.FindNearest(boundaries, Boundary.AddSeconds(20), TimeSpan.FromSeconds(30)));

        // 空列表 / null
        Assert.Null(RingCalibrationMath.FindNearest(new List<ClassBoundary>(), Boundary));
        Assert.Null(RingCalibrationMath.FindNearest(null, Boundary));
    }

    [BanjiTest]
    public static void 偏差按四舍五入到秒()
    {
        Assert.Equal(7, RingCalibrationMath.DeltaSeconds(Boundary.AddSeconds(7), Boundary));
        Assert.Equal(-7, RingCalibrationMath.DeltaSeconds(Boundary.AddSeconds(-7), Boundary));
        Assert.Equal(0, RingCalibrationMath.DeltaSeconds(Boundary, Boundary));
        Assert.Equal(8, RingCalibrationMath.DeltaSeconds(Boundary.AddMilliseconds(7600), Boundary));
        Assert.Equal(7, RingCalibrationMath.DeltaSeconds(Boundary.AddMilliseconds(7400), Boundary));
    }

    [BanjiTest]
    public static void 建议误差_铃声晚则误差为正()
    {
        // 当前误差 0，铃声比程序晚 7 秒 -> 建议 +7
        Assert.Equal(7, RingCalibrationMath.SuggestOffsetSeconds(0, Boundary.AddSeconds(7), Boundary));
        // 当前误差 +10，本次偏差 -3 秒 -> 建议 +7
        Assert.Equal(7, RingCalibrationMath.SuggestOffsetSeconds(10, Boundary.AddSeconds(-3), Boundary));
        // 当前误差 +5，本次偏差 +5 -> 建议 +10
        Assert.Equal(10, RingCalibrationMath.SuggestOffsetSeconds(5, Boundary.AddSeconds(5), Boundary));
    }

    [BanjiTest]
    public static void 建议误差_铃声早则误差为负()
    {
        // 学校铃声比标准时间快 7 秒：程序还显示 8:00，实际 7:59:53 就响了 -> 建议 -7
        Assert.Equal(-7, RingCalibrationMath.SuggestOffsetSeconds(0, Boundary.AddSeconds(-7), Boundary));
        Assert.Equal(-4, RingCalibrationMath.SuggestOffsetSeconds(3, Boundary.AddSeconds(-7), Boundary));
    }

    [BanjiTest]
    public static void 建议误差夹在正负60秒()
    {
        Assert.Equal(60, RingCalibrationMath.SuggestOffsetSeconds(55, Boundary.AddSeconds(20), Boundary));
        Assert.Equal(60, RingCalibrationMath.SuggestOffsetSeconds(0, Boundary.AddSeconds(999), Boundary));
        Assert.Equal(-60, RingCalibrationMath.SuggestOffsetSeconds(-55, Boundary.AddSeconds(-20), Boundary));
        Assert.Equal(-60, RingCalibrationMath.SuggestOffsetSeconds(0, Boundary.AddSeconds(-999), Boundary));
        Assert.Equal(60, RingCalibrationMath.ClampOffset(999));
        Assert.Equal(-60, RingCalibrationMath.ClampOffset(-999));
        Assert.Equal(0, RingCalibrationMath.ClampOffset(0));
    }

    [BanjiTest]
    public static void 误差文案与正负号()
    {
        Assert.Equal("与课表时间一致", RingCalibrationMath.DescribeOffset(0));
        Assert.True(RingCalibrationMath.DescribeOffset(7).Contains("晚 7 秒"), RingCalibrationMath.DescribeOffset(7));
        Assert.True(RingCalibrationMath.DescribeOffset(-7).Contains("早 7 秒"), RingCalibrationMath.DescribeOffset(-7));

        Assert.Equal("+7", RingCalibrationMath.FormatSigned(7));
        Assert.Equal("-7", RingCalibrationMath.FormatSigned(-7));
        Assert.Equal("0", RingCalibrationMath.FormatSigned(0));
        Assert.Equal("+60", RingCalibrationMath.FormatSigned(999));
        Assert.Equal("-60", RingCalibrationMath.FormatSigned(-999));
    }

    [BanjiTest]
    public static void 典型场景_铃声快6秒校准后在真实时刻对齐()
    {
        // 场景：课表 8:00 上课，实际铃声在 7:59:54 响（铃声快 6 秒）
        var realBellTime = Boundary.AddSeconds(-6);
        var nearest = RingCalibrationMath.FindNearest(SampleBoundaries(), realBellTime);
        Assert.NotNull(nearest);
        var suggested = RingCalibrationMath.SuggestOffsetSeconds(0, realBellTime, nearest!.Time);
        Assert.Equal(-6, suggested);

        // 应用后：程序显示时间 = 课表时间 + (-6s) = 7:59:54，正好等于真实铃声时刻
        var displayed = nearest.Time.AddSeconds(suggested);
        Assert.Equal(realBellTime, displayed);
    }
}
