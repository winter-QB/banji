using Banji.Core.Models;
using Banji.Core.Quips;

namespace Banji.Tests;

/// <summary>气泡字号自适应/抽签版式纯函数测试。</summary>
public static class QuipTypographyTests
{
    [BanjiTest]
    public static void 姓名行字号随字数单调下降且不越界()
    {
        string[] names =
        {
            "王", "张三", "王浩然", "欧阳娜娜", "买买提·艾力", "阿卜杜热合曼·买买提",
            "中华人民共和国学生代表", "这是一个非常非常非常长的测试姓名"
        };
        var previous = double.MaxValue;
        foreach (var name in names)
        {
            var size = BubbleTypography.PickNameFontSize(name);
            Assert.True(size >= BubbleTypography.MinPickNameFontSize,
                $"字号 {size} 低于下限（{name}）");
            Assert.True(size <= BubbleTypography.MaxPickNameFontSize,
                $"字号 {size} 高于上限（{name}）");
            Assert.True(size <= previous + 0.001,
                $"字数变多字号反而变大：{name} -> {size}（上一个 {previous}）");
            previous = size;
        }
    }

    [BanjiTest]
    public static void 姓名行按可用宽度兜底()
    {
        var name = "阿卜杜热合曼"; // 6 个汉字
        var units = BubbleTypography.EstimateTextUnits(name);
        var narrow = BubbleTypography.PickNameFontSize(name, maxTextWidth: 120);
        var wide = BubbleTypography.PickNameFontSize(name, maxTextWidth: 400);
        Assert.True(narrow <= wide, "可用宽度更小，字号不应更大");
        Assert.True(narrow * units <= 120 * 1.05, $"窄宽度下姓名仍然会溢出：{narrow} * {units}");
        Assert.True(narrow >= BubbleTypography.MinPickNameFontSize, "字号不应低于下限");
    }

    [BanjiTest]
    public static void 空姓名也有兜底字号()
    {
        Assert.True(BubbleTypography.PickNameFontSize(null) > 0);
        Assert.True(BubbleTypography.PickNameFontSize("") > 0);
        Assert.Equal(0.0, BubbleTypography.EstimateTextUnits(""));
    }

    [BanjiTest]
    public static void 台词字号随长度下降且落在合理区间()
    {
        var shortText = "数学好难";
        var mediumText = "这道函数题，我只会画个坐标轴表示尊重。";
        var longText = "今天也是被三角函数和文言文包围的一天，但我还是想先睡个午觉，然后再认真把题做完。";
        var a = BubbleTypography.QuipFontSize(shortText);
        var b = BubbleTypography.QuipFontSize(mediumText);
        var c = BubbleTypography.QuipFontSize(longText);
        Assert.True(a >= b && b >= c, $"台词越长字号应越小：{a},{b},{c}");
        Assert.InRange(a, 15, 21);
        Assert.InRange(c, 15, 21);
    }

    [BanjiTest]
    public static void 计时字号随时长位数变化()
    {
        var shortTimer = new TimerSnapshot
        {
            Kind = TimerKind.Countdown,
            Status = TimerStatus.Running,
            Remaining = TimeSpan.FromSeconds(90),
            Total = TimeSpan.FromMinutes(5)
        };
        var longTimer = new TimerSnapshot
        {
            Kind = TimerKind.Stopwatch,
            Status = TimerStatus.Running,
            Elapsed = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2)
        };
        Assert.Equal("01:30", shortTimer.DisplayText);
        Assert.Equal("1:02:00", longTimer.DisplayText);
        Assert.Equal(46.0, BubbleTypography.TimerFontSize(shortTimer));
        Assert.Equal(40.0, BubbleTypography.TimerFontSize(longTimer));
        Assert.Equal("倒计时", BubbleTypography.TimerLabel(shortTimer));
        Assert.Equal("正计时", BubbleTypography.TimerLabel(longTimer));
        Assert.Equal(46.0, BubbleTypography.TimerFontSize(null));
    }

    [BanjiTest]
    public static void 抽签版式按显示模式取字段()
    {
        var student = new Student { Id = "20260101", Name = "王浩然" };
        var both = new PickResult { Student = student, DisplayMode = PickDisplayMode.Both };
        var idOnly = new PickResult { Student = student, DisplayMode = PickDisplayMode.StudentId };
        var nameOnly = new PickResult { Student = student, DisplayMode = PickDisplayMode.Name };

        Assert.True(BubbleTypography.PickShowsId(both.DisplayMode));
        Assert.True(BubbleTypography.PickShowsName(both.DisplayMode));
        Assert.Equal("20260101", BubbleTypography.PickIdText(both));
        Assert.Equal("王浩然", BubbleTypography.PickNameText(both));

        Assert.True(BubbleTypography.PickShowsId(idOnly.DisplayMode));
        Assert.False(BubbleTypography.PickShowsName(idOnly.DisplayMode));
        Assert.Equal("", BubbleTypography.PickNameText(idOnly));

        Assert.False(BubbleTypography.PickShowsId(nameOnly.DisplayMode));
        Assert.Equal("", BubbleTypography.PickIdText(nameOnly));
        Assert.Equal("王浩然", BubbleTypography.PickNameText(nameOnly));

        Assert.Equal("", BubbleTypography.PickIdText(null));
        Assert.Equal("", BubbleTypography.PickNameText(null));
    }

    [BanjiTest]
    public static void 文本宽度估算()
    {
        Assert.True(Math.Abs(BubbleTypography.EstimateTextUnits("数学") - 2.0) < 1e-9, "两个汉字应为 2 个字宽");
        Assert.True(Math.Abs(BubbleTypography.EstimateTextUnits("abc") - 1.68) < 1e-9, "三个字母应约 1.68 个字宽");
        Assert.True(BubbleTypography.EstimateTextUnits("王浩然") > BubbleTypography.EstimateTextUnits("abc"));
    }
}

