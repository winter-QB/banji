using System.Runtime.InteropServices;
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

namespace Banji.Views;

/// <summary>
/// 全屏上下课提示（开发文档第 2 章 + 第二轮用户反馈「上课提醒必须能自动关闭」）：
/// <list type="bullet">
///   <item>上课：醒目纯色 / 渐变全屏背景 + 大号“上课” + 课程名/节次信息；</item>
///   <item>下课：透明背景纯文字“下课”（可带下一节信息），点击穿透；</item>
///   <item>两种提示都在底部显示“N 秒后自动关闭”小字 + 细进度条（随停留时间收缩到 0），
///         DispatcherTimer（刷新 UI/关闭）+ 保险计时器（真实 DateTime 兜底）双保险自动关闭，
///         点击任意位置 / 按 ESC 可立即关闭。</item>
/// </list>
/// 提示都只覆盖主屏、置顶（高于 PPT 放映/任务栏）、不抢焦点、不进 Alt-Tab。
/// </summary>
public partial class FullscreenNoticeWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int EscapeHotkeyId = 0x424A; // "BJ"
    private const uint ModNoRepeat = 0x4000;
    private const int VkEscape = 0x1B;

    private readonly Func<DateTime> _nowProvider = () => DateTime.Now;

    private DispatcherTimer? _uiTimer;
    private System.Threading.Timer? _safetyTimer;
    private DispatcherTimer? _topmostTimer;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd = IntPtr.Zero;

    private bool _clickThrough;
    private bool _clickable;
    private bool _escapeRegistered;
    private bool _closedOnce;
    private bool _closeLogged;

    private string _kindName = "全屏提示";
    private TimeSpan _duration;
    private DateTime _shownAt;

    public FullscreenNoticeWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        ProgressTrack.SizeChanged += (_, _) => UpdateAutoCloseUi();
    }

    // =========================================================================
    // 配置（由 NotificationService 调用；窗口保留无参构造契约）
    // =========================================================================

    /// <summary>配置为上课提示。</summary>
    public void ConfigureClassStart(ClassTransitionEventArgs transition, AppSettings settings)
    {
        _clickThrough = false;
        _clickable = true;
        _kindName = "上课提示";
        Title = "上课提示";
        PositionOnPrimaryScreen();

        var target = transition.Current ?? transition.Next;
        var notice = settings.Notice;

        ClassStartPanel.Visibility = Visibility.Visible;
        ClassEndPanel.Visibility = Visibility.Collapsed;
        Root.Background = Brushes.Transparent;
        MouseLeftButtonDown -= OnNoticeClick;
        MouseLeftButtonDown += OnNoticeClick;

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
        ColorLayer.Background = CreateStartBackground(settings);
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

        ApplyAutoCloseColors(lightMode: true, textColor: Colors.White);
        StartAutoClose(settings.NoticeDurationSeconds);
    }

    /// <summary>配置为下课提示（透明纯文字，点击穿透）。</summary>
    public void ConfigureClassEnd(ClassTransitionEventArgs transition, AppSettings settings)
    {
        _clickThrough = true;
        _clickable = false;
        _kindName = "下课提示";
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

        ApplyAutoCloseColors(lightMode: false, textColor: textColor);
        StartAutoClose(settings.NoticeDurationSeconds);
    }

    // =========================================================================
    // 自动关闭：底部进度条 + 倒计时小字 + DispatcherTimer/保险计时器双保险
    // =========================================================================

    private void StartAutoClose(int seconds)
    {
        StopAutoClose();

        _duration = NoticeAutoCloseLogic.ResolveDuration(seconds);
        _shownAt = _nowProvider();
        _closeLogged = false;

        // 1) UI 计时器：每 100ms 用“真实经过时间”刷新进度条与倒计时文字，到点关闭
        _uiTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _uiTimer.Tick += (_, _) =>
        {
            UpdateAutoCloseUi();
            if (NoticeAutoCloseLogic.ShouldClose(_duration, CurrentElapsed()))
                CloseBy("自动关闭");
        };
        _uiTimer.Start();
        UpdateAutoCloseUi();

        // 2) 保险计时器：不依赖 Dispatcher 队列；即使 UI 线程繁忙/窗口未激活，
        //    恢复后也会立刻按真实 DateTime 判定并关闭（双保险，不会永远残留）。
        var safetyDelay = _duration + TimeSpan.FromMilliseconds(250);
        _safetyTimer = new System.Threading.Timer(_ => EnsureAutoCloseOnUiThread(), null, safetyDelay, Timeout.InfiniteTimeSpan);

        Log.Info($"全屏提示显示：{_kindName}，停留 {_duration.TotalSeconds:0.###} 秒后自动关闭（进度条+双保险）");
    }

    private TimeSpan CurrentElapsed() => NoticeAutoCloseLogic.Elapsed(_shownAt, _nowProvider());

    private void UpdateAutoCloseUi()
    {
        if (_duration <= TimeSpan.Zero) return;
        var elapsed = CurrentElapsed();
        var width = NoticeAutoCloseLogic.ProgressWidth(ProgressTrack.ActualWidth, _duration, elapsed);
        ProgressFill.Width = width < 0 ? 0 : width;
        var remaining = NoticeAutoCloseLogic.RemainingSeconds(_duration, elapsed);
        AutoCloseText.Text = NoticeAutoCloseLogic.FormatRemainingText(remaining, _clickable);
    }

    /// <summary>保险计时器回调（线程池线程）：判定到点后切回 UI 线程关闭。</summary>
    private void EnsureAutoCloseOnUiThread()
    {
        try
        {
            if (!NoticeAutoCloseLogic.ShouldClose(_duration, CurrentElapsed())) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                CloseBy("自动关闭(保险)");
            }
            else if (dispatcher.CheckAccess())
            {
                CloseBy("自动关闭(保险)");
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() => CloseBy("自动关闭(保险)")), DispatcherPriority.Send);
            }
        }
        catch (Exception ex)
        {
            Log.Error("全屏提示保险关闭失败", ex);
        }
    }

    private void StopAutoClose()
    {
        _uiTimer?.Stop();
        _uiTimer = null;
        _safetyTimer?.Dispose();
        _safetyTimer = null;
    }

    /// <summary>关闭窗口并记录实际停留时长（QA 实测用：出现→消失）。</summary>
    private void CloseBy(string reason)
    {
        if (_closedOnce) return;
        if (!_closeLogged)
        {
            _closeLogged = true;
            var elapsed = CurrentElapsed();
            Log.Info($"全屏提示关闭：{_kindName}，方式={reason}，实际停留 {elapsed.TotalSeconds:0.00} 秒（设置 {_duration.TotalSeconds:0.###} 秒）");
        }
        StopAutoClose();
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("关闭全屏提示窗口失败", ex);
        }
    }

    private void OnNoticeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_clickThrough) return;
        CloseBy("点击关闭");
    }

    /// <summary>进度条/倒计时文字的配色：上课用白色系，下课用文字色系。</summary>
    private void ApplyAutoCloseColors(bool lightMode, Color textColor)
    {
        if (lightMode)
        {
            ProgressTrack.Background = MakeBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            ProgressFill.Background = MakeBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            AutoCloseText.Foreground = MakeBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            ProgressTrack.Background = new SolidColorBrush(Color.FromArgb(0x30, textColor.R, textColor.G, textColor.B));
            ProgressFill.Background = new SolidColorBrush(Color.FromArgb(0xA6, textColor.R, textColor.G, textColor.B));
            AutoCloseText.Foreground = new SolidColorBrush(Color.FromArgb(0xD9, textColor.R, textColor.G, textColor.B));
        }
    }

    private static SolidColorBrush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetToolWindow(_hwnd, true);   // 不出现在 Alt-Tab
            NativeMethods.SetNoActivate(_hwnd, true);   // 不抢焦点
            if (_clickThrough) NativeMethods.SetClickThrough(_hwnd, true); // 下课提示不遮挡点击
            NativeMethods.ReassertTopmost(_hwnd);

            // ESC 立即关闭：用全局热键，无需抢焦点（不进 PPT/全屏应用也不会被激活）
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            _hwndSource?.AddHook(WndProc);
            _escapeRegistered = RegisterHotKey(_hwnd, EscapeHotkeyId, ModNoRepeat, VkEscape);
            if (!_escapeRegistered)
                Log.Warn("全屏提示注册 ESC 快捷键失败（仍会自动关闭/点击关闭）");
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
            NativeMethods.ReassertTopmost(_hwnd);

            // 每次显示后定期重新压顶，避免被 PPT 放映 / 任务栏抢层
            _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _topmostTimer.Tick += (_, _) => NativeMethods.ReassertTopmost(_hwnd);
            _topmostTimer.Start();

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { NativeMethods.ReassertTopmost(_hwnd); } catch { /* ignore */ }
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
        StopAutoClose();
        _topmostTimer?.Stop();
        _topmostTimer = null;

        if (!_closeLogged)
        {
            _closeLogged = true;
            Log.Info($"全屏提示关闭：{_kindName}，方式=外部关闭，实际停留 {CurrentElapsed().TotalSeconds:0.00} 秒");
        }

        try
        {
            if (_hwndSource is not null) _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
            if (_escapeRegistered) UnregisterHotKey(_hwnd, EscapeHotkeyId);
            _escapeRegistered = false;
        }
        catch (Exception ex)
        {
            Log.Error("全屏提示资源清理失败", ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == EscapeHotkeyId)
        {
            handled = true;
            CloseBy("ESC");
        }
        return IntPtr.Zero;
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
                return new LinearGradientBrush(ColorFromHex("#1240C4", Color.FromRgb(0x12, 0x40, 0xC4)),
                    ColorFromHex("#2E6BFF", Color.FromRgb(0x2E, 0x6B, 0xFF)), new Point(0, 0), new Point(1, 1));
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

    // =========================================================================
    // Win32
    // =========================================================================

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
