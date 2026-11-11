using Banji.Core.Models;

namespace Banji.Controls.Pet;

/// <summary>便签彩色细条贴在哪条屏幕边（Windows 便笺纸样式：条永远位于便签贴近屏幕边缘的那一边）。</summary>
public enum FocusNoteStripEdge
{
    Top,
    Bottom,
    Left,
    Right
}

/// <summary>
/// v1.6.0 专注便签最终样式（Windows 便笺纸）：实心白底、方角、无模糊/渐变/压暗/阴影，
/// 贴边侧一条主题蓝细条；深色文字清晰显示 1~2 行。
/// 纯函数便于回归测试（避免再退回“清晰层+模糊层/整条渐隐”的旧实现）。
/// </summary>
public static class FocusNoteStickyStyle
{
    /// <summary>便签纸彩色边条厚度（DIP）。</summary>
    public const double StripThickness = 5;

    /// <summary>主题蓝（与主品牌蓝一致）。</summary>
    public const string StripColor = "#317FFF";

    /// <summary>正文深色，无描边/投影。</summary>
    public const string TextColor = "#1F2329";

    /// <summary>文字始终不透明（不随背景淡化）。</summary>
    public const double TextOpacity = 1.0;

    /// <summary>纸张实心白底。</summary>
    public const string PaperColor = "#FFFFFF";

    /// <summary>便签不得使用圆角（方角矩形）。</summary>
    public static bool HasCornerRadius => false;

    /// <summary>便签不得使用高斯模糊（旧实现错误地模糊了文字阴影）。</summary>
    public static bool HasBlur => false;

    /// <summary>便签不得使用渐变/渐隐压暗。</summary>
    public static bool HasGradient => false;

    /// <summary>便签不得使用投影。</summary>
    public static bool HasShadow => false;

    /// <summary>第一行（下课时间/下节科目）字号，比折叠卡略紧凑。</summary>
    public const double Line1FontSize = 13.5;

    /// <summary>第二行（下节/值日/放学信息）字号。</summary>
    public const double Line2FontSize = 10.5;

    /// <summary>紧凑内边距（左右/上下一致，条 5 DIP 不会压到文字）。</summary>
    public const double TextPadding = 12;

    /// <summary>
    /// 彩色条所在边：水平便签贴上/下边（按 Top/Bottom），竖直便签贴左/右边（按 Left/Right）；
    /// Free 默认放上方（便笺纸标题条习惯位置）。
    /// </summary>
    public static FocusNoteStripEdge StripEdge(FocusNotePlacement placement) => placement switch
    {
        FocusNotePlacement.TopLeftHorizontal => FocusNoteStripEdge.Top,
        FocusNotePlacement.TopRightHorizontal => FocusNoteStripEdge.Top,
        FocusNotePlacement.BottomLeftHorizontal => FocusNoteStripEdge.Bottom,
        FocusNotePlacement.BottomRightHorizontal => FocusNoteStripEdge.Bottom,
        FocusNotePlacement.TopLeftVertical => FocusNoteStripEdge.Left,
        FocusNotePlacement.BottomLeftVertical => FocusNoteStripEdge.Left,
        FocusNotePlacement.TopRightVertical => FocusNoteStripEdge.Right,
        FocusNotePlacement.BottomRightVertical => FocusNoteStripEdge.Right,
        FocusNotePlacement.Free => FocusNoteStripEdge.Top,
        _ => FocusNoteStripEdge.Top
    };
}
