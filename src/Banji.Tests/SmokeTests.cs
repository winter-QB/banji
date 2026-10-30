using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Tests;

public static class SmokeTests
{
    [BanjiTest]
    public static void 设置默认值符合开发文档()
    {
        var s = new AppSettings();
        s.Normalize();
        Assert.Equal(45, s.ClassDurationMinutes);
        Assert.Equal(0, s.BellOffsetSeconds);
        Assert.Equal(10, s.Buttons.IdleOpacityPercent);
        Assert.Equal("#317FFF", s.Buttons.IdleColor);
        Assert.Equal("#5998FF", s.Buttons.HoverColor);
        Assert.Equal("#0051D6", s.Buttons.PressedColor);
        Assert.Equal("#FF3030", s.Buttons.AccentColor);
        Assert.Equal("#3E6BFF", s.Bubble.BorderColor);
        Assert.Equal("#2E69FF", s.Bubble.TextColor);
        Assert.Equal(50, s.Bubble.OpacityPercent);
        Assert.Equal(2, s.Bubble.BorderThickness);
    }

    [BanjiTest]
    public static void 默认课表有8节课和午休()
    {
        var tt = WeekTimetable.CreateDefault();
        Assert.Equal(8, tt.Periods.Count);
        Assert.Equal(1, tt.Breaks.Count);
        Assert.Equal(TimeSpan.FromMinutes(45), tt.Periods[0].Duration);
    }

    [BanjiTest]
    public static void 设置越界会被收敛()
    {
        var s = new AppSettings { BellOffsetSeconds = 999, UiScale = -3, ClassDurationMinutes = 0 };
        s.Normalize();
        Assert.Equal(60, s.BellOffsetSeconds);
        Assert.True(s.UiScale > 0);
        Assert.Equal(1, s.ClassDurationMinutes);
    }
}
