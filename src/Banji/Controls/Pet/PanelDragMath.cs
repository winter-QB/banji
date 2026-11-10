namespace Banji.Controls.Pet;

/// <summary>
/// v1.5.0 课表拖动纯逻辑：4px 阈值区分点击/拖动，偏移夹在 ±800 DIP 内。
/// </summary>
public static class PanelDragMath
{
    /// <summary>点击 vs 拖动的位移阈值（DIP）。</summary>
    public const double DragThreshold = 4;

    /// <summary>面板偏移上限（与 AppSettings 的 PanelOffsetX/Y 限制一致）。</summary>
    public const double OffsetLimit = 800;

    /// <summary>位移是否已超过拖动阈值。</summary>
    public static bool ExceedsThreshold(double dx, double dy)
        => Math.Abs(dx) >= DragThreshold || Math.Abs(dy) >= DragThreshold;

    /// <summary>把“基准偏移 + 拖动位移”夹到允许范围。</summary>
    public static (double X, double Y) ResolveOffsets(double baseX, double baseY, double dx, double dy, double limit = OffsetLimit)
    {
        if (limit < 0) limit = 0;
        var x = Math.Clamp(baseX + dx, -limit, limit);
        var y = Math.Clamp(baseY + dy, -limit, limit);
        return (x, y);
    }

    /// <summary>单个偏移值夹取。</summary>
    public static double ClampOffset(double value, double limit = OffsetLimit)
    {
        if (limit < 0) limit = 0;
        return Math.Clamp(value, -limit, limit);
    }
}
