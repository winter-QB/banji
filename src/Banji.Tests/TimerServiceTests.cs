using Banji.Core.Models;
using Banji.Core.Timers;

namespace Banji.Tests;

/// <summary>可手动推进的假时钟。</summary>
internal sealed class FakeClock
{
    public DateTime Now { get; private set; }

    public FakeClock(DateTime start) => Now = start;

    public void Advance(TimeSpan delta) => Now += delta;
}

/// <summary>可手动触发的假节拍源（模拟 DispatcherTimer 的 200ms Tick）。</summary>
internal sealed class FakeTicker : ITimerTicker
{
    public bool IsRunning { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public event EventHandler? Tick;

    public void Start()
    {
        IsRunning = true;
        StartCount++;
    }

    public void Stop()
    {
        IsRunning = false;
        StopCount++;
    }

    public void Raise() => Tick?.Invoke(this, EventArgs.Empty);

    public void Dispose() => IsRunning = false;
}

/// <summary>计时器服务单测：全部用注入时间 + 假节拍源，不依赖真实时间。</summary>
public static class TimerServiceTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public FakeTicker Ticker { get; } = new();
        public TimerService Service { get; }
        public int TickCount;
        public int StateChangedCount;
        public int ExpiredCount;

        public Harness()
        {
            Service = new TimerService(() => Clock.Now, Ticker);
            Service.Tick += (_, _) => TickCount++;
            Service.StateChanged += (_, _) => StateChangedCount++;
            Service.Expired += (_, _) => ExpiredCount++;
        }
    }

    [BanjiTest(Name = "倒计时归零只触发一次 Expired")]
    public static void 倒计时归零只触发一次Expired()
    {
        var h = new Harness();
        h.Service.StartCountdown(TimeSpan.FromSeconds(10));

        Assert.NotNull(h.Service.Current, "开始后 Current 不应为 null");
        Assert.Equal(TimerStatus.Running, h.Service.Current!.Status);
        Assert.True(h.Ticker.IsRunning, "开始时节拍源应启动");

        // 中途 5 秒：还在跑
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Ticker.Raise();
        Assert.Equal(1, h.TickCount);
        Assert.Equal(0, h.ExpiredCount);
        Assert.Equal(TimeSpan.FromSeconds(5), h.Service.Current!.Remaining!.Value);
        Assert.Equal("00:05", h.Service.Current!.DisplayText);

        // 到点：只发一次 Expired
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Ticker.Raise();
        Assert.Equal(2, h.TickCount);
        Assert.Equal(1, h.ExpiredCount);
        Assert.Equal(TimerStatus.Expired, h.Service.Current!.Status);
        Assert.Equal(TimeSpan.Zero, h.Service.Current!.Remaining!.Value);
        Assert.Equal("00:00", h.Service.Current!.DisplayText);
        Assert.False(h.Ticker.IsRunning, "归零后节拍源应停止");

        // 再手动触发节拍：不得重复 Expired、不得再 Tick
        h.Ticker.Raise();
        h.Ticker.Raise();
        Assert.Equal(2, h.TickCount, "归零后不应再 Tick");
        Assert.Equal(1, h.ExpiredCount, "Expired 必须只触发一次");

        // 确认后回到空闲
        h.Service.AcknowledgeExpiry();
        Assert.Null(h.Service.Current, "确认超时后 Current 应回到 null");
        Assert.Equal(1, h.ExpiredCount);
    }

    [BanjiTest(Name = "取消后不再 Tick/Expired 且可重新开始")]
    public static void 取消后不再Tick或Expired()
    {
        var h = new Harness();
        h.Service.StartCountdown(TimeSpan.FromSeconds(3));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Ticker.Raise();
        Assert.Equal(1, h.TickCount);

        h.Service.Cancel();
        Assert.Null(h.Service.Current, "取消后 Current 应为 null");
        Assert.False(h.Ticker.IsRunning, "取消后节拍源应停止");

        var tickAfterCancel = h.TickCount;
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        h.Ticker.Raise(); // 即使假节拍源被强行触发也不该有事件
        Assert.Equal(tickAfterCancel, h.TickCount, "取消后不得再 Tick");
        Assert.Equal(0, h.ExpiredCount, "取消后不得触发 Expired");
        Assert.Null(h.Service.Current);

        // 取消后可以重新开始
        h.Service.StartCountdown(TimeSpan.FromSeconds(2));
        Assert.True(h.Service.Current!.IsRunning);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Ticker.Raise();
        Assert.Equal(1, h.ExpiredCount);
    }

    [BanjiTest(Name = "正计时 elapsed 随时间增长且无超时")]
    public static void 正计时elapsed随时间增长()
    {
        var h = new Harness();
        h.Service.StartStopwatch();

        Assert.True(h.Service.Current!.IsCountdown == false);
        Assert.Equal("00:00", h.Service.Current!.DisplayText);

        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Ticker.Raise();
        Assert.Equal(TimeSpan.FromSeconds(3), h.Service.Current!.Elapsed);
        Assert.Equal("00:03", h.Service.Current!.DisplayText);
        Assert.Null(h.Service.Current!.Remaining);

        h.Clock.Advance(TimeSpan.FromSeconds(63));
        h.Ticker.Raise();
        Assert.Equal(TimeSpan.FromSeconds(66), h.Service.Current!.Elapsed);
        Assert.Equal("01:06", h.Service.Current!.DisplayText);

        // 正计时即使超过 1 小时也不超时
        h.Clock.Advance(TimeSpan.FromSeconds(3600 + 57));
        h.Ticker.Raise();
        Assert.Equal(0, h.ExpiredCount, "正计时不得触发 Expired");
        Assert.Equal(TimeSpan.FromSeconds(66 + 3657), h.Service.Current!.Elapsed);
        Assert.Equal("1:02:03", h.Service.Current!.DisplayText);

        h.Service.Cancel();
        Assert.Null(h.Service.Current);
    }

    [BanjiTest(Name = "倒计时与正计时互斥（后开始者替换前者）")]
    public static void 倒计时与正计时互斥()
    {
        var h = new Harness();
        h.Service.StartCountdown(TimeSpan.FromSeconds(5));
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Service.StartStopwatch();
        Assert.Equal(TimerKind.Stopwatch, h.Service.Current!.Kind);
        Assert.Equal(TimeSpan.Zero, h.Service.Current!.Elapsed);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Ticker.Raise();
        Assert.Equal(TimeSpan.FromSeconds(1), h.Service.Current!.Elapsed);
        Assert.Equal(0, h.ExpiredCount);
    }

    [BanjiTest(Name = "倒计时不使用剩余时间递减累加（防漂移）")]
    public static void 倒计时按绝对时间计算不漂移()
    {
        var h = new Harness();
        h.Service.StartCountdown(TimeSpan.FromMinutes(10));

        // 真实时间推进 7 分 30 秒，但只触发一次节拍（模拟挂起/卡顿后恢复）
        h.Clock.Advance(TimeSpan.FromMinutes(7.5));
        h.Ticker.Raise();
        Assert.Equal("02:30", h.Service.Current!.DisplayText);
        Assert.NotNull(h.Service.Current!.Remaining, "倒计时应有剩余时间");
        Assert.Equal(TimeSpan.FromSeconds(150), h.Service.Current!.Remaining!.Value);
    }
}

/// <summary>计时对话框“分:秒”输入解析单测。</summary>
public static class TimerDialogInputTests
{
    [BanjiTest(Name = "计时器对话框分:秒输入解析")]
    public static void 分秒输入解析()
    {
        Assert.Equal(TimeSpan.FromSeconds(330), Parse("5:30"));
        Assert.Equal(TimeSpan.FromMinutes(90), Parse("90"));
        Assert.Equal(TimeSpan.FromSeconds(1), Parse("0:01"));
        Assert.Equal(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3), Parse("1:02:03"));
        Assert.Equal(TimeSpan.FromSeconds(330), Parse("5分30秒"));
        Assert.Equal(TimeSpan.FromMinutes(2), Parse("2分"));
        Assert.Equal(TimeSpan.FromSeconds(90), Parse(" 1 : 30 "));
        Assert.Equal(TimeSpan.FromMinutes(24), Parse("24:00"));

        Assert.False(Banji.Views.TimerDialog.TryParseDuration("", out _));
        Assert.False(Banji.Views.TimerDialog.TryParseDuration("abc", out _));
        Assert.False(Banji.Views.TimerDialog.TryParseDuration("0:00", out _));
        Assert.False(Banji.Views.TimerDialog.TryParseDuration("5:60", out _));
        Assert.False(Banji.Views.TimerDialog.TryParseDuration("1:2:3:4", out _));
    }

    private static TimeSpan Parse(string text)
    {
        Assert.True(Banji.Views.TimerDialog.TryParseDuration(text, out var duration), $"应能解析：{text}");
        return duration;
    }
}

/// <summary>TimerSnapshot.DisplayText / Format 的格式单测（mm:ss 与 h:mm:ss）。</summary>
public static class TimerDisplayTextTests
{
    private static TimerSnapshot Countdown(TimeSpan remaining) => new()
    {
        Kind = TimerKind.Countdown,
        Status = TimerStatus.Running,
        Elapsed = TimeSpan.Zero,
        Remaining = remaining,
        Total = TimeSpan.FromHours(2)
    };

    [BanjiTest(Name = "DisplayText mm:ss 格式")]
    public static void 显示格式分秒()
    {
        Assert.Equal("00:00", Countdown(TimeSpan.Zero).DisplayText);
        Assert.Equal("00:09", Countdown(TimeSpan.FromSeconds(9)).DisplayText);
        Assert.Equal("01:00", Countdown(TimeSpan.FromSeconds(60)).DisplayText);
        Assert.Equal("09:59", Countdown(TimeSpan.FromSeconds(599)).DisplayText);
        Assert.Equal("59:59", Countdown(TimeSpan.FromSeconds(3599)).DisplayText);
        // 不足整秒向下取整，符合倒计时视觉习惯
        Assert.Equal("00:01", Countdown(TimeSpan.FromMilliseconds(1500)).DisplayText);
        Assert.Equal("00:00", Countdown(TimeSpan.FromMilliseconds(400)).DisplayText);
        // 负数兜底为 0
        Assert.Equal("00:00", Countdown(TimeSpan.FromSeconds(-5)).DisplayText);
    }

    [BanjiTest(Name = "DisplayText 超过 1 小时用 h:mm:ss")]
    public static void 显示格式小时()
    {
        Assert.Equal("1:00:00", Countdown(TimeSpan.FromHours(1)).DisplayText);
        Assert.Equal("1:02:03", Countdown(TimeSpan.FromSeconds(3723)).DisplayText);
        Assert.Equal("10:00:00", Countdown(TimeSpan.FromHours(10)).DisplayText);
        Assert.Equal("1:02:03", TimerSnapshot.Format(TimeSpan.FromSeconds(3723)));
    }

    [BanjiTest(Name = "正计时显示已用时间")]
    public static void 正计时显示已用时间()
    {
        var snapshot = new TimerSnapshot
        {
            Kind = TimerKind.Stopwatch,
            Status = TimerStatus.Running,
            Elapsed = TimeSpan.FromSeconds(3723)
        };
        Assert.Equal("1:02:03", snapshot.DisplayText);
        Assert.Null(snapshot.Remaining);
        Assert.False(snapshot.IsCountdown);
    }
}
