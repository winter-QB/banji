using Banji.Core.Abstractions;
using Banji.Core.Models;
using Banji.Core.Notices;

namespace Banji.Tests;

/// <summary>
/// 全屏提示「自动关闭」纯逻辑测试：停留时长收敛、真实时间进度、剩余秒数、到点判定、
/// 底部倒计时文案、进度条宽度。窗口内的 DispatcherTimer 与保险计时器都复用这套判定，双保险。
/// </summary>
public static class ScheduleNoticeTests
{
    [BanjiTest]
    public static void 停留时长收敛到合法范围()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), NoticeAutoCloseLogic.ResolveDuration(0));
        Assert.Equal(TimeSpan.FromSeconds(1), NoticeAutoCloseLogic.ResolveDuration(-99));
        Assert.Equal(TimeSpan.FromSeconds(4), NoticeAutoCloseLogic.ResolveDuration(4));
        Assert.Equal(TimeSpan.FromSeconds(60), NoticeAutoCloseLogic.ResolveDuration(60));
        Assert.Equal(TimeSpan.FromSeconds(120), NoticeAutoCloseLogic.ResolveDuration(999));
    }

    [BanjiTest]
    public static void 进度按真实经过时间收缩()
    {
        var duration = TimeSpan.FromSeconds(4);
        Assert.Equal(1.0, NoticeAutoCloseLogic.ProgressFraction(duration, TimeSpan.Zero));
        Assert.Equal(0.5, NoticeAutoCloseLogic.ProgressFraction(duration, TimeSpan.FromSeconds(2)));
        Assert.Equal(0.0, NoticeAutoCloseLogic.ProgressFraction(duration, TimeSpan.FromSeconds(4)));
        Assert.Equal(0.0, NoticeAutoCloseLogic.ProgressFraction(duration, TimeSpan.FromSeconds(8)));
        // 时钟回拨（负 elapsed）夹到满进度
        Assert.Equal(1.0, NoticeAutoCloseLogic.ProgressFraction(duration, TimeSpan.FromSeconds(-1)));
        // 时长为 0 时不崩，进度为 0
        Assert.Equal(0.0, NoticeAutoCloseLogic.ProgressFraction(TimeSpan.Zero, TimeSpan.Zero));
    }

    [BanjiTest]
    public static void 剩余秒数向上取整()
    {
        var duration = TimeSpan.FromSeconds(4);
        Assert.Equal(4, NoticeAutoCloseLogic.RemainingSeconds(duration, TimeSpan.Zero));
        Assert.Equal(4, NoticeAutoCloseLogic.RemainingSeconds(duration, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(3, NoticeAutoCloseLogic.RemainingSeconds(duration, TimeSpan.FromSeconds(1.01)));
        Assert.Equal(1, NoticeAutoCloseLogic.RemainingSeconds(duration, TimeSpan.FromSeconds(3.01)));
        Assert.Equal(1, NoticeAutoCloseLogic.RemainingSeconds(duration, TimeSpan.FromSeconds(3.99)));
        Assert.Equal(0, NoticeAutoCloseLogic.RemainingSeconds(duration, TimeSpan.FromSeconds(4)));
    }

    [BanjiTest]
    public static void 到点判定边界()
    {
        var duration = TimeSpan.FromSeconds(4);
        Assert.False(NoticeAutoCloseLogic.ShouldClose(duration, TimeSpan.FromSeconds(3.999)));
        Assert.True(NoticeAutoCloseLogic.ShouldClose(duration, TimeSpan.FromSeconds(4)));
        Assert.True(NoticeAutoCloseLogic.ShouldClose(duration, TimeSpan.FromSeconds(4.001)));
        Assert.True(NoticeAutoCloseLogic.ShouldClose(duration, TimeSpan.FromSeconds(30)));
        Assert.True(NoticeAutoCloseLogic.ShouldClose(TimeSpan.Zero, TimeSpan.Zero));
    }

    [BanjiTest]
    public static void 倒计时小字包含关闭方式说明()
    {
        var start = NoticeAutoCloseLogic.FormatRemainingText(4, clickable: true);
        Assert.True(start.Contains("4 秒后自动关闭"), start);
        Assert.True(start.Contains("点击"), start);
        Assert.True(start.Contains("ESC"), start);

        var end = NoticeAutoCloseLogic.FormatRemainingText(3, clickable: false);
        Assert.True(end.Contains("3 秒后自动关闭"), end);
        Assert.False(end.Contains("点击"), end);
        Assert.True(end.Contains("ESC"), end);

        Assert.Equal("正在关闭…", NoticeAutoCloseLogic.FormatRemainingText(0, clickable: true));
    }

    [BanjiTest]
    public static void 进度条宽度计算()
    {
        var duration = TimeSpan.FromSeconds(4);
        Assert.Equal(1000.0, NoticeAutoCloseLogic.ProgressWidth(1000, duration, TimeSpan.Zero));
        Assert.Equal(750.0, NoticeAutoCloseLogic.ProgressWidth(1000, duration, TimeSpan.FromSeconds(1)));
        Assert.Equal(500.0, NoticeAutoCloseLogic.ProgressWidth(1000, duration, TimeSpan.FromSeconds(2)));
        Assert.Equal(0.0, NoticeAutoCloseLogic.ProgressWidth(1000, duration, TimeSpan.FromSeconds(4)));
        Assert.Equal(0.0, NoticeAutoCloseLogic.ProgressWidth(0, duration, TimeSpan.FromSeconds(1)));
        Assert.Equal(0.0, NoticeAutoCloseLogic.ProgressWidth(double.NaN, duration, TimeSpan.FromSeconds(1)));
    }

    [BanjiTest]
    public static void 真实经过时间处理时钟回拨()
    {
        var shown = new DateTime(2026, 11, 2, 8, 0, 0);
        Assert.Equal(TimeSpan.FromSeconds(2), NoticeAutoCloseLogic.Elapsed(shown, shown.AddSeconds(2)));
        Assert.Equal(TimeSpan.Zero, NoticeAutoCloseLogic.Elapsed(shown, shown.AddSeconds(-3)));
        Assert.True(NoticeAutoCloseLogic.Elapsed(shown, shown.AddSeconds(-3)) >= TimeSpan.Zero);
    }

    [BanjiTest]
    public static void 默认4秒的设置可被立即关闭()
    {
        // 模拟默认 4 秒：0~4 秒内都应保持显示，4 秒整到点
        var duration = NoticeAutoCloseLogic.ResolveDuration(new Banji.Core.Settings.AppSettings().NoticeDurationSeconds);
        Assert.Equal(TimeSpan.FromSeconds(4), duration);
        for (var sec = 0; sec < 4; sec++)
            Assert.False(NoticeAutoCloseLogic.ShouldClose(duration, TimeSpan.FromSeconds(sec)), $"第 {sec} 秒不应关闭");
        Assert.True(NoticeAutoCloseLogic.ShouldClose(duration, TimeSpan.FromSeconds(4)));
    }
}


