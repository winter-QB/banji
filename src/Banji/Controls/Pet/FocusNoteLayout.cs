using Banji.Core.Models;

namespace Banji.Controls.Pet;

/// <summary>
/// v1.6.0 专注态便签几何（纯函数）：9 种贴边/自由位置 → 屏幕 DIP 矩形、方向、旋转、渐变端点。
/// 便签是沿屏幕边的一条“横杠”：长度 = min(工作区宽, 高) × 百分比（20~80），厚度按内容自适应；
/// 靠边端实、向屏幕中央端渐弱（由窗口用 OpacityMask/模糊层按 <see cref="MaskStartX"/>→<see cref="MaskEndX"/> 实现）。
/// </summary>
public readonly record struct FocusNoteGeometry(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsVertical,
    int TextRotationDegrees,
    double MaskStartX,
    double MaskStartY,
    double MaskEndX,
    double MaskEndY,
    bool IsFree)
{
    /// <summary>便签靠边的那一端在“起点侧”（true=贴左/上，false=贴右/下）。</summary>
    public bool SolidAtStart { get; init; }
}

public static class FocusNoteLayout
{
    public const double MinLength = 120;
    public const double MinThickness = 40;
    public const double MaxThickness = 220;
    public const int MinLengthPercent = 20;
    public const int MaxLengthPercent = 80;

    /// <summary>9 种位置（供设置页/QA 枚举）。</summary>
    public static readonly FocusNotePlacement[] AllPlacements =
    {
        FocusNotePlacement.TopLeftHorizontal,
        FocusNotePlacement.TopLeftVertical,
        FocusNotePlacement.BottomLeftHorizontal,
        FocusNotePlacement.BottomLeftVertical,
        FocusNotePlacement.TopRightHorizontal,
        FocusNotePlacement.TopRightVertical,
        FocusNotePlacement.BottomRightHorizontal,
        FocusNotePlacement.BottomRightVertical,
        FocusNotePlacement.Free
    };

    /// <summary>该位置是否是竖直方向（文字旋转 90°）。</summary>
    public static bool IsVerticalPlacement(FocusNotePlacement placement)
        => placement is FocusNotePlacement.TopLeftVertical or FocusNotePlacement.BottomLeftVertical
            or FocusNotePlacement.TopRightVertical or FocusNotePlacement.BottomRightVertical;

    /// <summary>该位置是否贴屏幕左侧（否则贴右侧）。Free 视为水平自由。</summary>
    public static bool IsLeftPlacement(FocusNotePlacement placement)
        => placement is FocusNotePlacement.TopLeftHorizontal or FocusNotePlacement.TopLeftVertical
            or FocusNotePlacement.BottomLeftHorizontal or FocusNotePlacement.BottomLeftVertical
            or FocusNotePlacement.Free;

    /// <summary>该位置是否贴屏幕顶部（否则贴底部）。Free 视为底部。</summary>
    public static bool IsTopPlacement(FocusNotePlacement placement)
        => placement is FocusNotePlacement.TopLeftHorizontal or FocusNotePlacement.TopLeftVertical
            or FocusNotePlacement.TopRightHorizontal or FocusNotePlacement.TopRightVertical;

    /// <summary>便签长度：min(工作区宽,高) × 百分比（夹在 20~80%，并保证不超过可用边长）。</summary>
    public static double ComputeLength(double workWidth, double workHeight, int lengthPercent)
    {
        var percent = Math.Clamp(lengthPercent, MinLengthPercent, MaxLengthPercent);
        var seed = Math.Max(1, Math.Min(workWidth, workHeight));
        var raw = seed * percent / 100.0;
        var minLength = Math.Min(MinLength, seed);
        return Math.Min(seed, Math.Max(minLength, raw));
    }

    /// <summary>
    /// 计算便签窗口位置/尺寸与渐变方向。
    /// <paramref name="contentThickness"/> = 便签厚度（窗口按文字内容量出后传入）。
    /// Free 时使用 <paramref name="freeLeft/Top"/>（null = 默认右下），并夹在工作区内。
    /// </summary>
    public static FocusNoteGeometry Compute(
        FocusNotePlacement placement,
        double workLeft,
        double workTop,
        double workRight,
        double workBottom,
        double contentThickness,
        int lengthPercent,
        double? freeLeft = null,
        double? freeTop = null)
    {
        var vertical = IsVerticalPlacement(placement);
        var left = IsLeftPlacement(placement);
        var top = IsTopPlacement(placement);
        var thickness = Math.Clamp(contentThickness, MinThickness, MaxThickness);
        var length = ComputeLength(Math.Max(1, workRight - workLeft), Math.Max(1, workBottom - workTop), lengthPercent);

        if (placement == FocusNotePlacement.Free)
        {
            var width = Math.Min(length, Math.Max(1, workRight - workLeft));
            var height = thickness;
            var dx = freeLeft ?? (workRight - width);
            var dy = freeTop ?? (workBottom - height);
            var clampedX = Math.Clamp(dx, workLeft, Math.Max(workLeft, workRight - width));
            var clampedY = Math.Clamp(dy, workTop, Math.Max(workTop, workBottom - height));
            return new FocusNoteGeometry(clampedX, clampedY, width, height, false, 0,
                0, 0.5, 1, 0.5, true) { SolidAtStart = true };
        }

        double x;
        double y;
        double w;
        double h;
        if (vertical)
        {
            w = thickness;
            h = Math.Min(length, Math.Max(1, workBottom - workTop));
            x = left ? workLeft : workRight - w;
            y = top ? workTop : workBottom - h;
        }
        else
        {
            w = Math.Min(length, Math.Max(1, workRight - workLeft));
            h = thickness;
            x = left ? workLeft : workRight - w;
            y = top ? workTop : workBottom - h;
        }

        // 渐变：靠边端不透明（起点），向屏幕中央端透明（终点）
        double sx;
        double sy;
        double ex;
        double ey;
        if (vertical)
        {
            sx = 0.5;
            sy = top ? 0 : 1;
            ex = 0.5;
            ey = top ? 1 : 0;
        }
        else
        {
            sx = left ? 0 : 1;
            sy = 0.5;
            ex = left ? 1 : 0;
            ey = 0.5;
        }

        var rotation = vertical ? (top ? 90 : -90) : 0;
        return new FocusNoteGeometry(x, y, w, h, vertical, rotation, sx, sy, ex, ey, false)
        {
            // 水平：贴左=起点实；竖直：贴顶=起点实
            SolidAtStart = vertical ? top : left
        };
    }

    /// <summary>该放置是否要求窗口可自由拖动（仅 Free）。</summary>
    public static bool IsDraggable(FocusNotePlacement placement) => placement == FocusNotePlacement.Free;

    /// <summary>把自由便签位置夹在工作区内（拖动结束后调用）。</summary>
    public static (double Left, double Top) ClampFreePosition(
        double left, double top, double width, double height,
        double workLeft, double workTop, double workRight, double workBottom)
    {
        var x = Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - Math.Max(1, width)));
        var y = Math.Clamp(top, workTop, Math.Max(workTop, workBottom - Math.Max(1, height)));
        return (Math.Round(x), Math.Round(y));
    }
}

