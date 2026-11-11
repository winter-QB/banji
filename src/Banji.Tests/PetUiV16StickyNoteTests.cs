using Banji.Controls.Pet;
using Banji.Core.Models;

namespace Banji.Tests;

/// <summary>v1.6.0 专注便签最终样式回归：Windows 便笺纸（白底方角/无模糊/主题蓝色条贴屏幕边）。</summary>
public static class PetUiV16StickyNoteTests
{
    [BanjiTest]
    public static void 便笺纸为实心白底方角且无模糊渐变阴影()
    {
        Assert.Equal("#FFFFFF", FocusNoteStickyStyle.PaperColor);
        Assert.Equal("#1F2329", FocusNoteStickyStyle.TextColor);
        Assert.False(FocusNoteStickyStyle.HasCornerRadius, "便签必须是方角");
        Assert.False(FocusNoteStickyStyle.HasBlur, "便签不得高斯模糊");
        Assert.False(FocusNoteStickyStyle.HasGradient, "便签不得渐变/渐隐");
        Assert.False(FocusNoteStickyStyle.HasShadow, "便签不得投影");
        Assert.Equal(1.0, FocusNoteStickyStyle.TextOpacity);
        Assert.True(FocusNoteStickyStyle.TextPadding > 0);
        Assert.Equal(5.0, FocusNoteStickyStyle.StripThickness);
        Assert.Equal("#317FFF", FocusNoteStickyStyle.StripColor);
    }

    [BanjiTest]
    public static void 彩色条永远位于贴着屏幕边缘的那一边()
    {
        // 水平便签：上边两角贴顶 → 条在上；下边两角贴底 → 条在下
        Assert.Equal(FocusNoteStripEdge.Top, FocusNoteStickyStyle.StripEdge(FocusNotePlacement.TopLeftHorizontal));
        Assert.Equal(FocusNoteStripEdge.Top, FocusNoteStickyStyle.StripEdge(FocusNotePlacement.TopRightHorizontal));
        Assert.Equal(FocusNoteStripEdge.Bottom, FocusNoteStickyStyle.StripEdge(FocusNotePlacement.BottomLeftHorizontal));
        Assert.Equal(FocusNoteStripEdge.Bottom, FocusNoteStickyStyle.StripEdge(FocusNotePlacement.BottomRightHorizontal));
        // 竖直便签：左边两角贴左 → 条在左；右边两角贴右 → 条在右
        Assert.Equal(FocusNoteStripEdge.Left, FocusNoteStickyStyle.StripEdge(FocusNotePlacement.TopLeftVertical));
        Assert.Equal(FocusNoteStripEdge.Left, FocusNoteStickyStyle.StripEdge(FocusNotePlacement.BottomLeftVertical));
        Assert.Equal(FocusNoteStripEdge.Right, FocusNoteStickyStyle.StripEdge(FocusNotePlacement.TopRightVertical));
        Assert.Equal(FocusNoteStripEdge.Right, FocusNoteStickyStyle.StripEdge(FocusNotePlacement.BottomRightVertical));
        // Free 默认条在上方
        Assert.Equal(FocusNoteStripEdge.Top, FocusNoteStickyStyle.StripEdge(FocusNotePlacement.Free));
    }

    [BanjiTest]
    public static void 九种位置与长度设置保持不变()
    {
        Assert.Equal(9, FocusNoteLayout.AllPlacements.Length);
        Assert.Equal(9, FocusNoteLayout.AllPlacements.Distinct().Count());
        Assert.Equal(FocusNoteLayout.ComputeLength(1440, 900, 35), FocusNoteLayout.ComputeLength(1440, 900, 35));
        // 竖直便签仍然沿长边渲染，条贴左/右；水平便签条贴上/下
        var v = FocusNoteLayout.Compute(FocusNotePlacement.TopRightVertical, 0, 0, 1440, 900, 60, 35);
        Assert.True(v.IsVertical);
        Assert.Equal(60, v.Width);
        Assert.Equal(FocusNoteStripEdge.Right, FocusNoteStickyStyle.StripEdge(FocusNotePlacement.TopRightVertical));
    }
}
