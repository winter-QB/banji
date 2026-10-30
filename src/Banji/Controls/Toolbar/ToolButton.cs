using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Banji.Core.Settings;

namespace Banji.Controls.Toolbar;

/// <summary>按钮预览态（仅设置窗口预览用，不影响真实交互状态）。</summary>
public enum ToolButtonPreviewState
{
    Live = 0,
    Hover = 1,
    Pressed = 2,
    Accent = 3
}

/// <summary>
/// 功能区自绘按钮（开发文档第 4 章附录样式）：
/// 透明底 + 圆角矩形边框 + 右下 45° 阴影；常态/悬浮/按下/强调四态。
/// 颜色、透明度、边框宽度、圆角、阴影、字号全部由 <see cref="ButtonStyleSettings"/> 注入。
/// 右键不产生任何视觉反馈（保持悬浮样式）。
/// </summary>
public class ToolButton : Button
{
    public ToolButton()
    {
        Focusable = false;
        IsTabStop = false;
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;
        // 样式定义在 Controls/Toolbar/ToolButtonStyles.xaml，由宿主窗口/控件 merge 后按资源键解析。
        SetResourceReference(StyleProperty, "ToolButton.Default");
    }

    // ------------------------------------------------------------------ 依赖属性

    public static readonly DependencyProperty IdleBrushProperty = DependencyProperty.Register(
        nameof(IdleBrush), typeof(Brush), typeof(ToolButton),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(
        nameof(HoverBrush), typeof(Brush), typeof(ToolButton),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty PressedBrushProperty = DependencyProperty.Register(
        nameof(PressedBrush), typeof(Brush), typeof(ToolButton),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(ToolButton),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty IsAccentProperty = DependencyProperty.Register(
        nameof(IsAccent), typeof(bool), typeof(ToolButton),
        new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty ForcedStateProperty = DependencyProperty.Register(
        nameof(ForcedState), typeof(ToolButtonPreviewState), typeof(ToolButton),
        new PropertyMetadata(ToolButtonPreviewState.Live, OnVisualChanged));

    public static readonly DependencyProperty CornerRadiusValueProperty = DependencyProperty.Register(
        nameof(CornerRadiusValue), typeof(CornerRadius), typeof(ToolButton),
        new PropertyMetadata(new CornerRadius(8)));

    public static readonly DependencyProperty ShadowColorProperty = DependencyProperty.Register(
        nameof(ShadowColor), typeof(Color), typeof(ToolButton),
        new PropertyMetadata(Colors.Black));

    public static readonly DependencyProperty ShadowOpacityProperty = DependencyProperty.Register(
        nameof(ShadowOpacity), typeof(double), typeof(ToolButton),
        new PropertyMetadata(0.12));

    public static readonly DependencyProperty ShadowDepthProperty = DependencyProperty.Register(
        nameof(ShadowDepth), typeof(double), typeof(ToolButton),
        new PropertyMetadata(4.0));

    /// <summary>常态色（含透明度）。</summary>
    public Brush? IdleBrush
    {
        get => (Brush?)GetValue(IdleBrushProperty);
        set => SetValue(IdleBrushProperty, value);
    }

    /// <summary>悬浮色。</summary>
    public Brush? HoverBrush
    {
        get => (Brush?)GetValue(HoverBrushProperty);
        set => SetValue(HoverBrushProperty, value);
    }

    /// <summary>按下色。</summary>
    public Brush? PressedBrush
    {
        get => (Brush?)GetValue(PressedBrushProperty);
        set => SetValue(PressedBrushProperty, value);
    }

    /// <summary>强调色（如“取消计时”）。</summary>
    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>是否处于强调态（计时进行中）。强调态下悬浮/按下不改变颜色。</summary>
    public bool IsAccent
    {
        get => (bool)GetValue(IsAccentProperty);
        set => SetValue(IsAccentProperty, value);
    }

    /// <summary>强制预览状态（设置窗口四态预览用）。</summary>
    public ToolButtonPreviewState ForcedState
    {
        get => (ToolButtonPreviewState)GetValue(ForcedStateProperty);
        set => SetValue(ForcedStateProperty, value);
    }

    /// <summary>圆角 R（默认 8，与 Win11 输入法预览框一致）。</summary>
    public CornerRadius CornerRadiusValue
    {
        get => (CornerRadius)GetValue(CornerRadiusValueProperty);
        set => SetValue(CornerRadiusValueProperty, value);
    }

    public Color ShadowColor
    {
        get => (Color)GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }

    public double ShadowOpacity
    {
        get => (double)GetValue(ShadowOpacityProperty);
        set => SetValue(ShadowOpacityProperty, value);
    }

    /// <summary>阴影距离（沿右下 45°）。</summary>
    public double ShadowDepth
    {
        get => (double)GetValue(ShadowDepthProperty);
        set => SetValue(ShadowDepthProperty, value);
    }

    // ------------------------------------------------------------------ 四态

    private Border? _frame;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _frame = GetTemplateChild("Frame") as Border;
        UpdateShadowEffect();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (ToolButton)d;
        button.ApplyVisualState();
        button.UpdateShadowEffect();
    }

    /// <summary>按依赖属性重建右下 45° 阴影（模板里的 Frame 边框）。</summary>
    private void UpdateShadowEffect()
    {
        if (_frame is null) return;
        if (ShadowOpacity <= 0)
        {
            _frame.Effect = null;
            return;
        }

        _frame.Effect = new DropShadowEffect
        {
            Direction = 315, // 右下 45°
            ShadowDepth = Math.Max(0, ShadowDepth),
            BlurRadius = 3,
            Opacity = Math.Clamp(ShadowOpacity, 0, 1),
            Color = ShadowColor,
            RenderingBias = RenderingBias.Performance
        };
    }

    /// <summary>按当前状态（强调 / 按下 / 悬浮 / 常态）刷新字与边框颜色。</summary>
    public void ApplyVisualState()
    {
        var brush = ResolveBrush();
        if (brush is null) return;
        Foreground = brush;
        BorderBrush = brush;
    }

    private Brush? ResolveBrush()
    {
        switch (ForcedState)
        {
            case ToolButtonPreviewState.Hover: return HoverBrush;
            case ToolButtonPreviewState.Pressed: return PressedBrush;
            case ToolButtonPreviewState.Accent: return AccentBrush;
        }
        if (IsAccent) return AccentBrush;
        if (IsMouseOver && IsPressed) return PressedBrush;
        if (IsMouseOver) return HoverBrush;
        return IdleBrush;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        ApplyVisualState();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ApplyVisualState();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        ApplyVisualState();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ApplyVisualState();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        ApplyVisualState();
    }

    // 右键：不产生任何视觉反馈（也不弹菜单、不抢焦点），仅保持当前悬浮样式。
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) => e.Handled = true;
}

/// <summary>把 <see cref="ButtonStyleSettings"/> 应用到 <see cref="ToolButton"/>（功能区与设置预览共用）。</summary>
public static class ToolButtonStyler
{
    /// <summary>应用整套样式：四态颜色/透明度、边框、圆角、阴影、字号。</summary>
    public static void Apply(ToolButton button, ButtonStyleSettings style, bool accent = false)
    {
        if (button is null || style is null) return;

        button.IdleBrush = CreateBrush(style.IdleColor, style.IdleOpacityPercent);
        button.HoverBrush = CreateBrush(style.HoverColor, style.HoverOpacityPercent);
        button.PressedBrush = CreateBrush(style.PressedColor, style.PressedOpacityPercent);
        button.AccentBrush = CreateBrush(style.AccentColor, style.AccentOpacityPercent);

        button.ShadowColor = ParseColor(style.ShadowColor, Colors.Black);
        button.ShadowOpacity = Math.Clamp(style.ShadowOpacityPercent / 100.0, 0, 1);
        button.ShadowDepth = Math.Clamp(style.ShadowOffset, 0, 24);

        button.CornerRadiusValue = new CornerRadius(Math.Clamp(style.CornerRadius, 0, 40));
        button.BorderThickness = new Thickness(Math.Clamp(style.BorderThickness, 0, 8));
        button.FontSize = Math.Clamp(style.FontSize, 8, 40);
        button.IsAccent = accent;
        button.ApplyVisualState();
    }

    /// <summary>
    /// 颜色 + 透明度百分比 → 已冻结的纯色画刷（透明度作用在字和边框上）。
    /// 注意：开发文档里的“透明度”是**透明程度**——0% 表示完全不透明，
    /// 默认值 10% 表示常态轻微半透明；不能当不透明度用，否则悬浮/按下会完全看不见。
    /// </summary>
    public static SolidColorBrush CreateBrush(string? hex, int transparencyPercent)
    {
        var color = ParseColor(hex, Colors.Transparent);
        var percent = Math.Clamp(transparencyPercent, 0, 100);
        color.A = (byte)Math.Round((100 - percent) * 255.0 / 100.0);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>解析 #RRGGBB / #AARRGGBB，非法时回退。</summary>
    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var text = hex.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            var obj = ColorConverter.ConvertFromString(text);
            if (obj is Color color) return color;
        }
        catch
        {
            // 非法颜色：回退
        }
        return fallback;
    }
}
