namespace Banji.Core.Models;

public enum TimerKind
{
    Countdown = 0,
    Stopwatch = 1
}

public enum TimerStatus
{
    Idle = 0,
    Running = 1,
    Expired = 2,
    Finished = 3
}

/// <summary>计时器快照（不可变）。</summary>
public sealed class TimerSnapshot
{
    public TimerKind Kind { get; init; }
    public TimerStatus Status { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan? Remaining { get; init; }
    public TimeSpan? Total { get; init; }

    public bool IsRunning => Status == TimerStatus.Running;
    public bool IsCountdown => Kind == TimerKind.Countdown;

    /// <summary>显示文本：倒计时显示剩余，正计时显示已用。超过 1 小时用 h:mm:ss。</summary>
    public string DisplayText
    {
        get
        {
            var span = Kind == TimerKind.Countdown ? Remaining ?? Total ?? TimeSpan.Zero : Elapsed;
            return Format(span);
        }
    }

    public double Progress
    {
        get
        {
            if (Total is null || Total.Value <= TimeSpan.Zero) return 0;
            return Kind == TimerKind.Countdown
                ? Math.Clamp(1.0 - (Remaining ?? TimeSpan.Zero).TotalMilliseconds / Total.Value.TotalMilliseconds, 0, 1)
                : Math.Clamp(Elapsed.TotalMilliseconds / Total.Value.TotalMilliseconds, 0, 1);
        }
    }

    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var totalSeconds = (long)Math.Floor(span.TotalSeconds + 1e-6);
        var h = totalSeconds / 3600;
        var m = totalSeconds % 3600 / 60;
        var s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }
}
