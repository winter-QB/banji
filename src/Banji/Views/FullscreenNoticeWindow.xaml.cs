using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Notices;
using Banji.Core.Settings;
using Banji.Core.Boot;

namespace Banji.Views;

/// <summary>
/// 全屏上下课提示（开发文档第 2 章）：
/// <list type="bullet">
///   <item>上课：醒目纯色 / 渐变全屏背景 + 大号“上课” + 课程名/节次信息，停留 NoticeDurationSeconds 秒或点击关闭；</item>
///   <item>下课：透明背景纯文字“下课”（可带下一节信息），点击穿透、自动关闭。</item>
/// </list>
/// 两种提示都只覆盖主屏、置顶（高于 PPT 放映/任务栏）、不抢焦点、不进 Alt-Tab。
/// </summary>
public partial class FullscreenNoticeWindow : Window
{
    private DispatcherTimer? _autoCloseTimer;
    private DispatcherTimer? _topmostTimer;
    private bool _clickThrough;
    private bool _closedOnce;

    public FullscreenNoticeWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // =========================================================================
    // 配置（由 NotificationService 调用；窗口保留无参构造契约）
    // =========================================================================

    /// <summary>配置为上课提示。</summary>
    public void ConfigureClassStart(ClassTransitionEventArgs transition, AppSettings settings)
    {
        _clickThrough = false;
        Title = "上课提示";
        PositionOnPrimaryScreen();

        var target = transition.Current ?? transition.Next;
        var notice = settings.Notice;

        ClassStartPanel.Visibility = Visibility.Visible;
        ClassEndPanel.Visibility = Visibility.Collapsed;
        Root.Background = Brushes.Transparent;
        MouseLeftButtonDown -= OnStartNoticeClick;
        MouseLeftButtonDown += OnStartNoticeClick;

        StartTitle.FontSize = Clamp(notice.ClassStartFontSize, 40, 300);
        var textBrush = BrushFromHex(notice.ClassStartTextColor, Colors.White);
        StartTitle.Foreground = textBrush;
        StartCourse.Foreground = textBrush;

        if (target is not null && target.HasCourse)
        {
            StartCourse.Text = target.CourseDisplay;
            StartCourse.Visibility = Visibility.Visible;
            StartPeriod.Text = $"{target.PeriodName}   {target.TimeText}";
            StartPeriod.Visibility = Visibility.Visible;
        }
        else if (target is not null)
        {
            // 课程名为空时不要显示“未设置”，只显示节次/时间
            StartCourse.Visibility = Visibility.Collapsed;
            StartPeriod.Text = $"{target.PeriodName}   {target.TimeText}";
            StartPeriod.Visibility = Visibility.Visible;
        }
        else
        {
            StartCourse.Text = "新的一节课开始了";
            StartCourse.FontSize = 54;
            StartCourse.Visibility = Visibility.Visible;
            StartPeriod.Visibility = Visibility.Collapsed;
        }

        // 背景：纯色 / 渐变预设；可选模糊桌面打底（更通透好看）
        var colorLayer = CreateStartBackground(settings);
        ColorLayer.Background = colorLayer;

        if (settings.StartNoticeBlurBackdrop)
        {
            var shot = ScreenCapture.CapturePrimary(0.5);
            if (shot is not null)
            {
                BackdropImage.Source = shot;
                BackdropImage.Visibility = Visibility.Visible;
                BackdropImage.Effect = new BlurEffect
                {
                    Radius = 28,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance
                };
                ColorLayer.Opacity = 0.90;
            }
        }
        else
        {
            BackdropImage.Visibility = Visibility.Collapsed;
            ColorLayer.Opacity = 1.0;
        }

        StartAutoClose(settings.NoticeDurationSeconds);
    }

    /// <summary>配置为下课提示（透明纯文字，点击穿透）。</summary>
    public void ConfigureClassEnd(ClassTransitionEventArgs transition, AppSettings settings)
    {
        _clickThrough = true;
        Title = "下课提示";
        PositionOnPrimaryScreen();

        var notice = settings.Notice;
        ClassStartPanel.Visibility = Visibility.Collapsed;
        ClassEndPanel.Visibility = Visibility.Visible;
        BackdropImage.Visibility = Visibility.Collapsed;
        ColorLayer.Background = Brushes.Transparent;
        ColorLayer.Opacity = 0;

        EndTitle.FontSize = Clamp(notice.ClassEndFontSize, 40, 300);
        var textColor = ColorFromHex(notice.ClassEndTextColor, Colors.White);
        var opacity = Math.Clamp(notice.ClassEndTextOpacityPercent, 0, 100) / 100.0;
        var brush = new SolidColorBrush(textColor) { Opacity = opacity };

        // 下课提示是“纯文字”，必须保证在任何 PPT / 桌面背景上都看得清：
        // 浅色文字配深色描边光晕，深色文字配白色描边光晕（自动对比）。
        var luminance = (0.299 * textColor.R + 0.587 * textColor.G + 0.114 * textColor.B) / 255.0;
        var glowColor = luminance > 0.55 ? Colors.Black : Colors.White;
        var glow = new DropShadowEffect
        {
            Color = glowColor,
            ShadowDepth = 0,
            BlurRadius = 26,
            Opacity = 0.85
        };

        EndTitle.Foreground = brush;
        EndTitle.Effect = glow;
        EndNext.Foreground = brush;
        EndNext.Effect = glow.Clone();
        EndHint.Foreground = new SolidColorBrush(textColor) { Opacity = opacity * 0.7 };

        var next = transition.Next;
        if (next is not null)
        {
            EndNext.Text = $"下一节：{next.CourseDisplay}   {next.PeriodName} {next.Start:HH:mm}";
            EndNext.Visibility = Visibility.Visible;
            EndHint.Text = $"距离上课还有 {ScheduleSnapshot.FormatCountdown(next.Start - transition.Time)}";
            EndHint.Visibility = Visibility.Visible;
        }
        else
        {
            EndNext.Text = "今天的课结束了，休息一下吧";
            EndNext.Visibility = Visibility.Visible;
            EndHint.Visibility = Visibility.Collapsed;
        }

        StartAutoClose(settings.NoticeDurationSeconds);
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void PositionOnPrimaryScreen()
    {
        WindowState = WindowState.Normal;
        Left = 0;
        Top = 0;
        Width = Math.Max(1, SystemParameters.PrimaryScreenWidth);
        Height = Math.Max(1, SystemParameters.PrimaryScreenHeight);
    }

    private void StartAutoClose(int seconds)
    {
        var duration = Math.Clamp(seconds, 1, 120);
        _autoCloseTimer?.Stop();
        _autoCloseTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(duration)
        };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer?.Stop();
            Close();
        };
        _autoCloseTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetToolWindow(hwnd, true);   // 不出现在 Alt-Tab
            NativeMethods.SetNoActivate(hwnd, true);   // 不抢焦点
            if (_clickThrough) NativeMethods.SetClickThrough(hwnd, true); // 下课提示不遮挡点击
            NativeMethods.ReassertTopmost(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("全屏提示窗口样式设置失败", ex);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.ReassertTopmost(hwnd);

            // 每次显示后定期重新压顶，避免被 PPT 放映 / 任务栏抢层
            _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _topmostTimer.Tick += (_, _) => NativeMethods.ReassertTopmost(hwnd);
            _topmostTimer.Start();

            // 等一帧再压一次（某些全屏应用会在窗口显示后抢层）
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { NativeMethods.ReassertTopmost(hwnd); } catch { /* ignore */ }
            }));
        }
        catch (Exception ex)
        {
            Log.Error("全屏提示窗口显示失败", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closedOnce) return;
        _closedOnce = true;
        _autoCloseTimer?.Stop();
        _autoCloseTimer = null;
        _topmostTimer?.Stop();
        _topmostTimer = null;
    }

    private void OnStartNoticeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_clickThrough) return;
        Close();
    }

    // =========================================================================
    // 背景
    // =========================================================================

    /// <summary>上课提示背景：blue / gradient / orange / custom（custom 取 settings.StartNoticeBackground）。</summary>
    private static Brush CreateStartBackground(AppSettings settings)
    {
        var preset = (settings.StartNoticePreset ?? "gradient").Trim().ToLowerInvariant();
        switch (preset)
        {
            case "blue":
            {
                var brush = new LinearGradientBrush(ColorFromHex("#1240C4", Color.FromRgb(0x12, 0x40, 0xC4)),
                    ColorFromHex("#2E6BFF", Color.FromRgb(0x2E, 0x6B, 0xFF)), new Point(0, 0), new Point(1, 1));
                return brush;
            }
            case "orange":
                return new LinearGradientBrush(
                    ColorFromHex("#FF6A00", Color.FromRgb(0xFF, 0x6A, 0x00)),
                    ColorFromHex("#FFB300", Color.FromRgb(0xFF, 0xB3, 0x00)),
                    new Point(0, 0), new Point(1, 1));
            case "custom":
            {
                var color = ColorFromHex(settings.StartNoticeBackground, Color.FromRgb(0x31, 0x7F, 0xFF));
                return new SolidColorBrush(color);
            }
            default: // gradient
                return new LinearGradientBrush(
                    ColorFromHex("#1D4ED8", Color.FromRgb(0x1D, 0x4E, 0xD8)),
                    ColorFromHex("#7C3AED", Color.FromRgb(0x7C, 0x3A, 0xED)),
                    new Point(0, 0), new Point(1, 1));
        }
    }

    private static Brush BrushFromHex(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                var color = (Color)ColorConverter.ConvertFromString(hex.Trim());
                return new SolidColorBrush(color);
            }
        }
        catch
        {
            // 非法颜色回退默认
        }
        return new SolidColorBrush(fallback);
    }

    private static Color ColorFromHex(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (Color)ColorConverter.ConvertFromString(hex.Trim());
        }
        catch
        {
            // ignore
        }
        return fallback;
    }

    private static double Clamp(double value, double min, double max)
        => double.IsNaN(value) || double.IsInfinity(value) ? min : Math.Clamp(value, min, max);
}
