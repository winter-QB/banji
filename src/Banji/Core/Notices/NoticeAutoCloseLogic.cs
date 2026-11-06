namespace Banji.Core.Notices;

/// <summary>
/// 全屏上下课提示「自动关闭」的纯逻辑（不依赖窗口/计时器，便于单元测试）。
/// <para>
/// 窗口显示底部进度条与“N 秒后自动关闭”小字都基于这里的计算：
/// 真实经过时间（DateTime 差值）而不是计时器累加，因此 UI 卡顿、窗口未激活也不会“白屏卡住不关”。
/// 窗口内的 DispatcherTimer 与保险计时器（System.Threading.Timer）共用同一套判定，构成双保险。
/// </para>
/// </summary>
public static class NoticeAutoCloseLogic
{
    /// <summary>停留时长允许的最小值（秒）。</summary>
    public const int MinDurationSeconds = 1;

    /// <summary>停留时长允许的最大值（秒）。</summary>
    public const int MaxDurationSeconds = 120;

    /// <summary>把设置里的停留秒数收敛到合法范围（默认 4 秒）。</summary>
    public static TimeSpan ResolveDuration(int configuredSeconds)
        => TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, MinDurationSeconds, MaxDurationSeconds));

    /// <summary>从显示到现在经过的真实时间（时钟被往回调时按 0 处理）。</summary>
    public static TimeSpan Elapsed(DateTime shownAt, DateTime now)
    {
        var elapsed = now - shownAt;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    /// <summary>剩余进度比例：1 = 刚开始，0 = 到点。</summary>
    public static double ProgressFraction(TimeSpan duration, TimeSpan elapsed)
    {
        if (duration <= TimeSpan.Zero) return 0;
        var fraction = 1 - elapsed.TotalMilliseconds / duration.TotalMilliseconds;
        if (double.IsNaN(fraction) || double.IsInfinity(fraction)) return 0;
        return Math.Clamp(fraction, 0, 1);
    }

    /// <summary>剩余整秒数（向上取整：还剩 0.2 秒也显示 1 秒）。</summary>
    public static int RemainingSeconds(TimeSpan duration, TimeSpan elapsed)
    {
        var remaining = duration - elapsed;
        if (remaining <= TimeSpan.Zero) return 0;
        return (int)Math.Ceiling(remaining.TotalSeconds);
    }

    /// <summary>是否到达自动关闭时刻。</summary>
    public static bool ShouldClose(TimeSpan duration, TimeSpan elapsed)
        => duration <= TimeSpan.Zero || elapsed >= duration;

    /// <summary>底部倒计时小字：例如“4 秒后自动关闭（点击或按 ESC 立即关闭）”。</summary>
    public static string FormatRemainingText(int remainingSeconds, bool clickable)
    {
        var suffix = clickable ? "（点击或按 ESC 立即关闭）" : "（按 ESC 立即关闭）";
        return remainingSeconds <= 0 ? "正在关闭…" : $"{remainingSeconds} 秒后自动关闭{suffix}";
    }

    /// <summary>计算进度条填充宽度（像素）。</summary>
    public static double ProgressWidth(double trackWidth, TimeSpan duration, TimeSpan elapsed)
    {
        if (trackWidth <= 0 || double.IsNaN(trackWidth) || double.IsInfinity(trackWidth)) return 0;
        return trackWidth * ProgressFraction(duration, elapsed);
    }
}
