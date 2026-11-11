using Banji.Controls.Pet;
using Banji.Core.Models;

namespace Banji.Tests;

/// <summary>v1.6.0 专注态便签几何：9 种位置 → 屏幕矩形/方向/渐变端点 + 长度/夹取。</summary>
public static class PetUiV16Tests
{
    private const double L = 0;
    private const double T = 0;
    private const double R = 1440;
    private const double B = 900;

    private static FocusNoteGeometry Geo(FocusNotePlacement p, double thickness = 70, int pct = 35) =>
        FocusNoteLayout.Compute(p, L, T, R, B, thickness, pct);

    [BanjiTest]
    public static void 九种位置齐全()
    {
        Assert.Equal(9, FocusNoteLayout.AllPlacements.Length);
        Assert.Equal(9, FocusNoteLayout.AllPlacements.Distinct().Count());
    }

    [BanjiTest]
    public static void 左右上下四个角的方向判断()
    {
        Assert.True(FocusNoteLayout.IsLeftPlacement(FocusNotePlacement.TopLeftHorizontal));
        Assert.True(FocusNoteLayout.IsTopPlacement(FocusNotePlacement.TopLeftHorizontal));
        Assert.True(FocusNoteLayout.IsLeftPlacement(FocusNotePlacement.BottomLeftVertical));
        Assert.False(FocusNoteLayout.IsTopPlacement(FocusNotePlacement.BottomLeftVertical));
        Assert.False(FocusNoteLayout.IsLeftPlacement(FocusNotePlacement.TopRightHorizontal));
        Assert.True(FocusNoteLayout.IsTopPlacement(FocusNotePlacement.TopRightVertical));
        Assert.True(FocusNoteLayout.IsVerticalPlacement(FocusNotePlacement.BottomRightVertical));
        Assert.False(FocusNoteLayout.IsVerticalPlacement(FocusNotePlacement.BottomRightHorizontal));
    }

    [BanjiTest]
    public static void 水平便签贴对应角且长度按百分比()
    {
        var expected = FocusNoteLayout.ComputeLength(R - L, B - T, 35);
        var tl = Geo(FocusNotePlacement.TopLeftHorizontal);
        Assert.Equal(L, tl.Left);
        Assert.Equal(T, tl.Top);
        Assert.Equal(expected, tl.Width);
        Assert.Equal(70, tl.Height);
        Assert.False(tl.IsVertical);
        Assert.Equal(0, tl.TextRotationDegrees);
        Assert.True(tl.SolidAtStart);

        var tr = Geo(FocusNotePlacement.TopRightHorizontal);
        Assert.Equal(R - expected, tr.Left);
        Assert.Equal(T, tr.Top);
        Assert.False(tr.SolidAtStart);

        var bl = Geo(FocusNotePlacement.BottomLeftHorizontal);
        Assert.Equal(L, bl.Left);
        Assert.Equal(B - 70, bl.Top);

        var br = Geo(FocusNotePlacement.BottomRightHorizontal);
        Assert.Equal(R - expected, br.Left);
        Assert.Equal(B - 70, br.Top);
    }

    [BanjiTest]
    public static void 竖直便签贴对应边且旋转正确()
    {
        var expected = FocusNoteLayout.ComputeLength(R - L, B - T, 35);
        var tlv = Geo(FocusNotePlacement.TopLeftVertical);
        Assert.Equal(L, tlv.Left);
        Assert.Equal(T, tlv.Top);
        Assert.Equal(70, tlv.Width);
        Assert.Equal(expected, tlv.Height);
        Assert.True(tlv.IsVertical);
        Assert.Equal(90, tlv.TextRotationDegrees);

        var blv = Geo(FocusNotePlacement.BottomLeftVertical);
        Assert.Equal(B - expected, blv.Top);
        Assert.Equal(-90, blv.TextRotationDegrees);

        var trv = Geo(FocusNotePlacement.TopRightVertical);
        Assert.Equal(R - 70, trv.Left);
        Assert.Equal(T, trv.Top);
        Assert.Equal(90, trv.TextRotationDegrees);

        var brv = Geo(FocusNotePlacement.BottomRightVertical);
        Assert.Equal(R - 70, brv.Left);
        Assert.Equal(B - expected, brv.Top);
        Assert.Equal(-90, brv.TextRotationDegrees);
    }

    [BanjiTest]
    public static void 渐变方向靠边端实向中央渐弱()
    {
        var tl = Geo(FocusNotePlacement.TopLeftHorizontal);
        Assert.Equal(0, tl.MaskStartX); // 左端实
        Assert.Equal(1, tl.MaskEndX);   // 向右渐弱
        var tr = Geo(FocusNotePlacement.TopRightHorizontal);
        Assert.Equal(1, tr.MaskStartX); // 右端实
        Assert.Equal(0, tr.MaskEndX);
        var tv = Geo(FocusNotePlacement.TopLeftVertical);
        Assert.Equal(0, tv.MaskStartY);
        Assert.Equal(1, tv.MaskEndY);
        var bv = Geo(FocusNotePlacement.BottomLeftVertical);
        Assert.Equal(1, bv.MaskStartY);
        Assert.Equal(0, bv.MaskEndY);
    }

    [BanjiTest]
    public static void 长度百分比夹取在二十到八十()
    {
        Assert.True(FocusNoteLayout.ComputeLength(1440, 900, 120) <= 1440 * 0.8 + 0.01, "超上限应夹到 80%");
        Assert.Equal(FocusNoteLayout.ComputeLength(1440, 900, 80), FocusNoteLayout.ComputeLength(1440, 900, 200));
        Assert.Equal(FocusNoteLayout.ComputeLength(1440, 900, 20), FocusNoteLayout.ComputeLength(1440, 900, 1));
        // 工作区小于 MinLength 时退化为工作区短边（不能超出屏幕），不强行 120
        Assert.Equal(100, FocusNoteLayout.ComputeLength(200, 100, 20));
        Assert.Equal(1280.0, FocusNoteLayout.ComputeLength(1600, 1600, 80), "80% 上限 = 短边 × 80%");
    }

    [BanjiTest]
    public static void 便签厚度夹取()
    {
        Assert.Equal(FocusNoteLayout.MinThickness, Geo(FocusNotePlacement.TopLeftHorizontal, thickness: 1).Height);
        Assert.Equal(FocusNoteLayout.MaxThickness, Geo(FocusNotePlacement.TopLeftHorizontal, thickness: 999).Height);
    }

    [BanjiTest]
    public static void 自由便签可任意位置并夹在工作区()
    {
        var expected = FocusNoteLayout.ComputeLength(R - L, B - T, 35);
        var free = FocusNoteLayout.Compute(FocusNotePlacement.Free, L, T, R, B, 70, 35, 300, 200);
        Assert.True(free.IsFree);
        Assert.Equal(300, free.Left);
        Assert.Equal(200, free.Top);
        Assert.False(free.IsVertical);
        Assert.Equal(expected, free.Width);

        var clamped = FocusNoteLayout.Compute(FocusNotePlacement.Free, L, T, R, B, 70, 35, 5000, 5000);
        Assert.Equal(R - expected, clamped.Left);
        Assert.Equal(B - 70, clamped.Top);
        Assert.True(clamped.Left + clamped.Width <= R);
        Assert.True(clamped.Top + clamped.Height <= B);

        var defaultFree = FocusNoteLayout.Compute(FocusNotePlacement.Free, L, T, R, B, 70, 35);
        Assert.Equal(R - expected, defaultFree.Left);
        Assert.Equal(B - 70, defaultFree.Top);
        Assert.True(FocusNoteLayout.IsDraggable(FocusNotePlacement.Free));
        Assert.False(FocusNoteLayout.IsDraggable(FocusNotePlacement.TopLeftHorizontal));
    }

    [BanjiTest]
    public static void 自由拖动位置夹取()
    {
        var (x1, y1) = FocusNoteLayout.ClampFreePosition(100, 100, 300, 70, 0, 0, 1440, 900);
        Assert.Equal(100, x1);
        Assert.Equal(100, y1);

        var (x2, y2) = FocusNoteLayout.ClampFreePosition(9999, -50, 300, 70, 0, 0, 1440, 900);
        Assert.Equal(1140, x2);
        Assert.Equal(0, y2);
    }
}


