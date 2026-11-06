using Banji.Core.Models;

namespace Banji.Core.Quips;

/// <summary>
/// 气泡排版纯函数（不依赖 WPF，便于单测）。
/// 字号随字数自适应，保证长名字/长台词不溢出、短名字足够醒目。
/// </summary>
public static class BubbleTypography
{
    /// <summary>抽签气泡中“学号”一行的固定字号（文档要求“中不溜”的固定字号）。</summary>
    public const double PickIdFontSize = 19;

    /// <summary>抽签气泡姓名行的字号下限 / 上限。</summary>
    public const double MinPickNameFontSize = 16;
    public const double MaxPickNameFontSize = 60;

    public static bool PickShowsId(PickDisplayMode mode) => mode != PickDisplayMode.Name;

    public static bool PickShowsName(PickDisplayMode mode) => mode != PickDisplayMode.StudentId;

    /// <summary>抽中的学号文案；只显示姓名时返回空串。</summary>
    public static string PickIdText(PickResult? result)
    {
        if (result is null || !PickShowsId(result.DisplayMode)) return "";
        return result.Student.DisplayId;
    }

    /// <summary>抽中的姓名文案；只显示学号时返回空串。</summary>
    public static string PickNameText(PickResult? result)
    {
        if (result is null || !PickShowsName(result.DisplayMode)) return "";
        return result.Student.DisplayName;
    }

    /// <summary>估算文本宽度（以“一个汉字”为单位，数字/字母约半个字宽）。</summary>
    public static double EstimateTextUnits(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double units = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) units += 0.35;
            else if (ch < 128) units += 0.56;   // ASCII 字母/数字/符号
            else units += 1.0;                   // 汉字 / 全角标点
        }
        return units;
    }

    /// <summary>
    /// 姓名行自适应字号：字数越多字号越小，并按可用宽度兜底，最终落在
    /// [<see cref="MinPickNameFontSize"/>, <see cref="MaxPickNameFontSize"/>]。
    /// </summary>
    public static double PickNameFontSize(string? name, double maxTextWidth = 300)
    {
        var units = EstimateTextUnits(name);
        if (units <= 0) return 34;

        double byLength = units switch
        {
            <= 2.2 => 58,
            <= 3.2 => 48,
            <= 4.2 => 40,
            <= 5.2 => 34,
            <= 6.2 => 29,
            <= 8.2 => 25,
            _ => 22
        };
        var byWidth = maxTextWidth / Math.Max(units * 1.02, 1);
        var size = Math.Min(byLength, byWidth);
        size = Math.Round(size);
        return Math.Clamp(size, MinPickNameFontSize, MaxPickNameFontSize);
    }

    /// <summary>台词气泡字号：越短越大，读完不累。</summary>
    public static double QuipFontSize(string? text)
    {
        var units = EstimateTextUnits(text);
        return units switch
        {
            <= 8 => 21,
            <= 14 => 19,
            <= 22 => 17.5,
            <= 32 => 16,
            _ => 15
        };
    }

    /// <summary>计时气泡大字号：数字越长字号越小（h:mm:ss）。</summary>
    public static double TimerFontSize(TimerSnapshot? snapshot)
    {
        var text = snapshot?.DisplayText ?? "00:00";
        return text.Length switch
        {
            <= 5 => 46,
            <= 7 => 40,
            _ => 34
        };
    }

    /// <summary>计时气泡顶部小标签：“倒计时”/“正计时”。</summary>
    public static string TimerLabel(TimerSnapshot? snapshot)
        => snapshot is { Kind: TimerKind.Stopwatch } ? "正计时" : "倒计时";

    /// <summary>
    /// 气泡文字行高（第二轮修复多行叠字 bug）。
    /// 注意：WPF 的 TextBlock.LineHeight 是**绝对像素**，之前写 1.45 会被当成 1.45px 行高导致多行叠在一起；
    /// 这里统一按字号倍数换算（1.45 倍字号），即 15px 字 → 约 22px 行高。
    /// </summary>
    public static double LineHeightFor(double fontSize)
    {
        if (double.IsNaN(fontSize) || fontSize <= 0) fontSize = 16;
        return Math.Round(fontSize * 1.45, 1);
    }

    /// <summary>估算多行文字大概占几行（测试/排版用，不依赖 WPF 度量）。</summary>
    public static int EstimateLineCount(string? text, double maxWidth, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        if (maxWidth <= 0 || fontSize <= 0) return 1;

        var perLine = Math.Max(1.0, Math.Floor(maxWidth / fontSize));
        var units = EstimateTextUnits(text);
        return Math.Max(1, (int)Math.Ceiling(units / perLine));
    }
}
