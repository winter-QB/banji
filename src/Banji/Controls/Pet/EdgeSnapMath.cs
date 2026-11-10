namespace Banji.Controls.Pet;

/// <summary>
/// v1.5.0 侧边把手吸附纯逻辑：隐藏/收起时窗口只剩弧线宽，右缘精确贴工作区右缘（DIP，Math.Round），
/// 垂直位置夹在工作区内，避免 1~2px 缝/越界。
/// </summary>
public static class EdgeSnapMath
{
    /// <summary>把手贴右缘时的窗口 Left：工作区右缘 − 把手宽（四舍五入到整数 DIP）。</summary>
    public static double SnapLeftToWorkRight(double workRight, double handleWidth)
    {
        var width = Math.Max(1, handleWidth);
        return Math.Round(workRight - width);
    }

    /// <summary>把手窗口垂直位置夹取：保证整个把手在工作区上下边内。</summary>
    public static double ClampTop(double desiredTop, double handleHeight, double workTop, double workBottom)
    {
        var height = Math.Max(1, handleHeight);
        var maxTop = workBottom - height;
        if (maxTop < workTop) return workTop; // 工作区比把手还矮：贴顶即可
        return Math.Round(Math.Clamp(desiredTop, workTop, maxTop));
    }

    /// <summary>窗口右缘是否与工作区右缘对齐（默认容差半像素，吸收 DPI 舍入）。</summary>
    public static bool IsFlushRight(double windowLeft, double windowWidth, double workRight, double tolerance = 0.5)
        => Math.Abs(windowLeft + windowWidth - workRight) <= tolerance;
}
