using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Quips;
using Banji.Core.Settings;

namespace Banji.Views;

/// <summary>
/// 独立置顶对话气泡：白底半透明（可调）+ 可选高斯模糊背景 + 2px #3E6BFF 描边 + #2E69FF 文字。
/// 三种用途：摸一摸台词（打字机）、抽签结果（固定版式）、计时显示。
/// 每次显示都重新压到最顶层，且不抢焦点。
/// </summary>
public partial class BubbleWindow : Window
{
    private enum BubbleMode
    {
        None = 0,
        Quip = 1,
        Pick = 2,
        Timer = 3
    }

    private const int QuipReadMs = 2800;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    private readonly DispatcherTimer _typeTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _topmostTimer;
    private readonly List<string> _textElements = new();
    private readonly ScaleTransform _popScale = new(1, 1);
    private readonly TranslateTransform _popOffset = new(0, 0);

    private BubbleStyleSettings _style = new();
    private BubbleMode _mode = BubbleMode.None;
    private string _fullText = "";
    private int _shownChars;
    private bool _timerRunning;
    private bool _closing;

    public BubbleWindow()
    {
        InitializeComponent();

        Opacity = 0;
        var popGroup = new TransformGroup();
        popGroup.Children.Add(_popScale);
        popGroup.Children.Add(_popOffset);
        Outer.RenderTransformOrigin = new Point(0.5, 0.62);
        Outer.RenderTransform = popGroup;

        _typeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(28) };
        _typeTimer.Tick += OnTypeTick;

        _hideTimer = new DispatcherTimer(DispatcherPriority.Background);
        _hideTimer.Tick += (_, _) => OnHideTimerElapsed();

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1200) };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();

        SizeChanged += (_, _) =>
        {
            UpdateCardClip();
            if (IsVisible && _mode != BubbleMode.None) RepositionToPet();
        };
        PreviewMouseLeftButtonDown += (_, _) => CompleteTyping();
        Loaded += (_, _) => UpdateCardClip();
        Closed += (_, _) => Cleanup();

        try
        {
            _style = AppServices.Current.Settings.Current.Bubble;
            ApplyStyle();
            AppServices.Current.Settings.Changed += OnSettingsChanged;
        }
        catch (Exception ex)
        {
            Log.Error("初始化气泡样式失败", ex);
        }
    }

    /// <summary>气泡当前是否正在显示内容。</summary>
    public bool IsShowing => IsVisible && _mode != BubbleMode.None;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            // 不进 Alt-Tab、不抢焦点，但仍然可以点（点一下跳过打字机）。
            NativeMethods.SetToolWindow(handle, true);
            NativeMethods.SetNoActivate(handle, true);
            NativeMethods.ReassertTopmost(handle);
        }
        catch (Exception ex)
        {
            Log.Error("设置气泡窗口扩展样式失败", ex);
        }
    }

    // =========================================================================
    // 对外接口
    // =========================================================================

    /// <summary>摸一摸台词（打字机效果，点击气泡可立即显示全文）。</summary>
    public void ShowQuip(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_mode == BubbleMode.Pick) return; // 抽签结果优先，不被打断

        try
        {
            ResetTimers();
            _mode = BubbleMode.Quip;
            ShowOnly(QuipPanel);
            ApplyContentBrushes();

            _fullText = text.Trim();
            _textElements.Clear();
            var enumerator = StringInfo.GetTextElementEnumerator(_fullText);
            while (enumerator.MoveNext()) _textElements.Add((string)enumerator.Current);

            var fontSize = BubbleTypography.QuipFontSize(_fullText);
            QuipText.FontSize = fontSize;
            QuipMeasure.FontSize = fontSize;
            QuipMeasure.Text = _fullText;
            QuipText.Text = "";
            QuipText.MaxWidth = Math.Max(120, _style.MaxWidth - 48);
            QuipMeasure.MaxWidth = QuipText.MaxWidth;
            _shownChars = 0;

            ShowPrepared();
            StartTypewriter();
        }
        catch (Exception ex)
        {
            Log.Error("显示台词气泡失败", ex);
        }
    }

    /// <summary>抽签结果（版式：顶部左对齐 Thin 小字 + 固定中字号 Bold 学号 + 大字号 Bold 姓名）。</summary>
    public void ShowPick(PickResult result)
    {
        if (result is null) return;

        try
        {
            ResetTimers();
            _mode = BubbleMode.Pick;
            RefreshTimerRunning();
            ShowOnly(PickPanel);
            ApplyContentBrushes();

            var id = BubbleTypography.PickIdText(result);
            var name = BubbleTypography.PickNameText(result);
            var maxWidth = Math.Max(120, _style.MaxWidth - 80);

            PickHeader.Text = "抽中的是";
            PickId.Text = id;
            PickId.Visibility = string.IsNullOrEmpty(id) ? Visibility.Collapsed : Visibility.Visible;
            PickName.Text = name;
            PickName.Visibility = string.IsNullOrEmpty(name) ? Visibility.Collapsed : Visibility.Visible;

            if (!string.IsNullOrEmpty(name))
            {
                PickId.FontSize = BubbleTypography.PickIdFontSize;
                PickName.FontSize = BubbleTypography.PickNameFontSize(name, maxWidth);
            }
            else
            {
                // 只显示学号时，学号行就是“大字号”。
                PickId.FontSize = BubbleTypography.PickNameFontSize(id, maxWidth);
            }

            ShowPrepared();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, _style.PickBubbleDurationMs));
            _hideTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("显示抽签气泡失败", ex);
        }
    }

    /// <summary>计时显示：运行中显示“倒计时/正计时 + mm:ss”，结束/取消后自行淡出。</summary>
    public void UpdateTimer(TimerSnapshot? snapshot)
    {
        try
        {
            _timerRunning = snapshot?.IsRunning == true;
            if (!_timerRunning)
            {
                if (_mode == BubbleMode.Timer) FadeOutAndHide();
                return;
            }
            if (_mode is BubbleMode.Pick or BubbleMode.Quip) return; // 等气泡空出来再显示计时

            if (_mode != BubbleMode.Timer)
            {
                ResetTimers();
                _mode = BubbleMode.Timer;
                ShowOnly(TimerPanel);
                ApplyContentBrushes();
                SetTimerText(snapshot!);
                ShowPrepared();
            }
            else
            {
                SetTimerText(snapshot!);
                ApplyContentSize();
            }
        }
        catch (Exception ex)
        {
            Log.Error("显示计时气泡失败", ex);
        }
    }

    private void SetTimerText(TimerSnapshot snapshot)
    {
        TimerLabel.Text = BubbleTypography.TimerLabel(snapshot);
        TimerTime.FontSize = BubbleTypography.TimerFontSize(snapshot);
        TimerTime.Text = snapshot.DisplayText;
    }

    /// <summary>宠物移动/缩放后重新贴到宠物旁边。</summary>
    public void RepositionAndRefresh()
    {
        if (!IsVisible) return;
        try
        {
            ApplyContentSize();
            UpdateLayout();
            RepositionToPet();
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新气泡位置失败：{ex.Message}");
        }
    }

    /// <summary>关闭并释放（应用退出/宠物窗关闭时调用）。</summary>
    public void CloseCleanup()
    {
        try
        {
            _closing = true;
            Cleanup();
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("关闭气泡窗口失败", ex);
        }
    }

    // =========================================================================
    // 显示 / 定位 / 动画
    // =========================================================================

    private void ShowPrepared()
    {
        ApplyContentSize();
        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            Opacity = 0;
            Show();
        }
        UpdateLayout();
        RepositionToPet();
        // 只在“从隐藏到显示”的那一刻截背景，避免把正在淡出的气泡自己也截进去。
        if (!wasVisible) CaptureBackdrop();
        UpdateCardClip();
        AnimateIn();
        ReassertTopmost();
        StartTopmostWatch();
    }

    /// <summary>
    /// 根据当前内容手动计算气泡窗口大小（不用 SizeToContent，避免内容变短后窗口不回收）。
    /// 常数 94/79 = 气泡左右/上下留白（阴影 46/42 + 内边距 44/33 + 描边 4/4）。
    /// </summary>
    private void ApplyContentSize()
    {
        try
        {
            var maxContent = Math.Max(140, _style.MaxWidth);
            double contentWidth;
            double contentHeight;
            switch (_mode)
            {
                case BubbleMode.Quip:
                    QuipText.MaxWidth = maxContent;
                    QuipMeasure.MaxWidth = maxContent;
                    QuipMeasure.Measure(new Size(maxContent, double.PositiveInfinity));
                    contentWidth = Math.Max(112, Math.Min(maxContent, QuipMeasure.DesiredSize.Width));
                    contentHeight = Math.Max(24, QuipMeasure.DesiredSize.Height);
                    break;
                case BubbleMode.Pick:
                    PickPanel.Measure(new Size(maxContent, double.PositiveInfinity));
                    contentWidth = Math.Max(176, Math.Min(maxContent, PickPanel.DesiredSize.Width));
                    contentHeight = Math.Max(52, PickPanel.DesiredSize.Height);
                    break;
                case BubbleMode.Timer:
                    TimerPanel.Measure(new Size(maxContent, double.PositiveInfinity));
                    contentWidth = Math.Max(176, Math.Min(maxContent, TimerPanel.DesiredSize.Width));
                    contentHeight = Math.Max(48, TimerPanel.DesiredSize.Height);
                    break;
                default:
                    return;
            }

            Width = Math.Ceiling(contentWidth) + 94;
            Height = Math.Ceiling(contentHeight) + 79;
        }
        catch (Exception ex)
        {
            Log.Warn($"计算气泡尺寸失败：{ex.Message}");
        }
    }

    private void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        _popScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease, From = 0.94 });
        _popScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease, From = 0.94 });
        _popOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease, From = 7 });
    }

    private void FadeOutAndHide()
    {
        ResetTimers();
        _mode = BubbleMode.None;
        StopTopmostWatch();
        if (!IsVisible) return;

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (_closing) return;
            if (_mode == BubbleMode.None && Opacity < 0.1)
            {
                try { Hide(); } catch { /* ignore */ }
            }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void RepositionToPet()
    {
        try
        {
            var pet = AppServices.Current.PetWindow;
            if (pet is null || !pet.IsVisible) return;
            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            var work = SystemParameters.WorkArea;
            var body = pet.GetPetBodyRect();
            var anchor = pet.GetBubbleAnchor();
            // 功能区展开时让到功能区左侧，避免盖住“取消计时”等按钮。
            var rightBound = pet.GetBubbleRightBoundary();
            double w = ActualWidth, h = ActualHeight;
            const double gap = 12, edge = 8;

            // 首选：贴在宠物（或功能区）左侧，气泡中心对齐宠物眼睛高度。
            var left = rightBound - gap - w;
            var top = anchor.Y - h * 0.52;

            if (left < work.Left + edge)
            {
                // 左侧空间不足：放到宠物窗上方，右边缘尽量对齐宠物右边缘。
                left = Math.Min(body.Right, rightBound) - w;
                top = pet.Top - h - gap;
                if (top < work.Top + edge) top = body.Bottom + gap; // 上方也不够就放下方
            }

            left = Math.Clamp(left, work.Left + edge, work.Right - edge - w);
            top = Math.Clamp(top, work.Top + edge, work.Bottom - edge - h);
            Left = Math.Round(left);
            Top = Math.Round(top);
        }
        catch (Exception ex)
        {
            Log.Warn($"定位气泡失败：{ex.Message}");
        }
    }

    private void ReassertTopmost()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.ReassertTopmost(handle);
        }
        catch
        {
            // 置顶失败不影响内容
        }
    }

    private void StartTopmostWatch()
    {
        if (!_topmostTimer.IsEnabled) _topmostTimer.Start();
    }

    private void StopTopmostWatch() => _topmostTimer.Stop();

    // =========================================================================
    // 打字机
    // =========================================================================

    private void StartTypewriter()
    {
        var interval = _style.TypewriterMsPerChar;
        if (interval <= 0 || _textElements.Count <= 3)
        {
            CompleteTyping();
            return;
        }
        _typeTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(8, interval));
        _typeTimer.Start();
    }

    private void OnTypeTick(object? sender, EventArgs e)
    {
        if (_mode != BubbleMode.Quip)
        {
            _typeTimer.Stop();
            return;
        }

        _shownChars++;
        if (_shownChars >= _textElements.Count)
        {
            CompleteTyping();
            return;
        }
        QuipText.Text = string.Concat(_textElements.Take(_shownChars));
    }

    private void CompleteTyping()
    {
        if (_mode != BubbleMode.Quip) return;
        _typeTimer.Stop();
        QuipText.Text = _fullText;
        _shownChars = _textElements.Count;
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(QuipReadMs);
        _hideTimer.Start();
    }

    // =========================================================================
    // 生命周期衔接
    // =========================================================================

    private void OnHideTimerElapsed()
    {
        _hideTimer.Stop();
        if (_mode == BubbleMode.Timer) return;
        RefreshTimerRunning();
        if (_timerRunning)
        {
            var snapshot = SafeTimers()?.Current;
            if (snapshot is { IsRunning: true }) ShowTimerNow(snapshot);
            else FadeOutAndHide();
        }
        else
        {
            FadeOutAndHide();
        }
    }

    private void ShowTimerNow(TimerSnapshot snapshot)
    {
        ResetTimers();
        _mode = BubbleMode.Timer;
        _timerRunning = true;
        ShowOnly(TimerPanel);
        ApplyContentBrushes();
        TimerLabel.Text = BubbleTypography.TimerLabel(snapshot);
        TimerTime.FontSize = BubbleTypography.TimerFontSize(snapshot);
        TimerTime.Text = snapshot.DisplayText;
        ShowPrepared();
    }

    private void RefreshTimerRunning()
    {
        try { _timerRunning = SafeTimers()?.Current?.IsRunning == true; }
        catch { _timerRunning = false; }
    }

    private static ITimerService? SafeTimers()
    {
        try { return AppServices.Current.Timers; }
        catch { return null; }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            _style = AppServices.Current.Settings.Current.Bubble;
            ApplyStyle();
            if (IsVisible) RepositionAndRefresh();
        }
        catch (Exception ex)
        {
            Log.Error("响应气泡设置变化失败", ex);
        }
    }

    private void Cleanup()
    {
        _typeTimer.Stop();
        _hideTimer.Stop();
        _topmostTimer.Stop();
        try { AppServices.Current.Settings.Changed -= OnSettingsChanged; } catch { /* ignore */ }
    }

    // =========================================================================
    // 样式
    // =========================================================================

    private void ApplyStyle()
    {
        var textBrush = BrushOf(_style.TextColor, Color.FromRgb(0x2E, 0x69, 0xFF));
        var borderBrush = BrushOf(_style.BorderColor, Color.FromRgb(0x3E, 0x6B, 0xFF));
        var background = ColorOf(_style.BackgroundColor, Colors.White);
        var alpha = (byte)Math.Clamp(_style.OpacityPercent * 255 / 100, 0, 255);
        var tint = new SolidColorBrush(Color.FromArgb(alpha, background.R, background.G, background.B));
        tint.Freeze();

        Card.BorderBrush = borderBrush;
        Card.BorderThickness = new Thickness(Math.Max(0, _style.BorderThickness));
        Card.MaxWidth = _style.MaxWidth + 48;
        Card.CornerRadius = new CornerRadius(22);
        Tint.CornerRadius = new CornerRadius(22 - _style.BorderThickness);
        Tint.Background = tint;
        BackdropBlur.Radius = _style.BlurRadius;

        if (_style.UseBlur)
        {
            Backdrop.Visibility = Visibility.Visible;
            Tint.Visibility = Visibility.Visible;
            Card.Background = Brushes.Transparent;
        }
        else
        {
            Backdrop.Visibility = Visibility.Collapsed;
            Tint.Visibility = Visibility.Collapsed;
            Card.Background = tint;
        }

        _textBrush = textBrush;
        ApplyContentBrushes();
        UpdateCardClip();
    }

    private Brush _textBrush = BrushOf("#2E69FF", Color.FromRgb(0x2E, 0x69, 0xFF));

    private void ApplyContentBrushes()
    {
        var brush = _textBrush;
        QuipText.Foreground = brush;
        PickHeader.Foreground = brush;
        PickHeader.Opacity = 0.72;
        PickId.Foreground = brush;
        PickId.Opacity = 0.94;
        PickName.Foreground = brush;
        TimerLabel.Foreground = brush;
        TimerLabel.Opacity = 0.72;
        TimerTime.Foreground = brush;
    }

    private void ShowOnly(FrameworkElement panel)
    {
        QuipPanel.Visibility = panel == QuipPanel ? Visibility.Visible : Visibility.Collapsed;
        PickPanel.Visibility = panel == PickPanel ? Visibility.Visible : Visibility.Collapsed;
        TimerPanel.Visibility = panel == TimerPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>让模糊背景/白色底严格跟随圆角内边界。</summary>
    private void UpdateCardClip()
    {
        try
        {
            if (CardInner.ActualWidth <= 0 || CardInner.ActualHeight <= 0) return;
            var radius = Math.Max(0, 22 - _style.BorderThickness);
            CardInner.Clip = new RectangleGeometry(
                new Rect(0, 0, CardInner.ActualWidth, CardInner.ActualHeight), radius, radius);
        }
        catch
        {
            // 裁剪失败不影响显示
        }
    }

    private void ResetTimers()
    {
        _typeTimer.Stop();
        _hideTimer.Stop();
    }

    // =========================================================================
    // 高斯模糊背景（截取气泡所在屏幕区域并模糊）
    // =========================================================================

    private void CaptureBackdrop()
    {
        if (!_style.UseBlur) return;
        try
        {
            var source = CaptureScreenRegion(Card);
            if (source is not null) Backdrop.Source = source;
        }
        catch (Exception ex)
        {
            Log.Warn($"截取气泡背景失败：{ex.Message}");
        }
    }

    private static BitmapSource? CaptureScreenRegion(FrameworkElement element)
    {
        if (element.ActualWidth <= 1 || element.ActualHeight <= 1) return null;
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        var width = (int)Math.Round(bottomRight.X - topLeft.X);
        var height = (int)Math.Round(bottomRight.Y - topLeft.Y);
        if (width <= 0 || height <= 0 || width > 4000 || height > 4000) return null;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        IntPtr memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, oldObject = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
            oldObject = SelectObject(memoryDc, bitmap);
            var ok = BitBlt(memoryDc, 0, 0, width, height,
                screenDc, (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), SrcCopy | CaptureBlt);
            if (!ok) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && oldObject != IntPtr.Zero) SelectObject(memoryDc, oldObject);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int width, int height,
        IntPtr hdcSrc, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);

    // =========================================================================
    // 颜色
    // =========================================================================

    private static SolidColorBrush BrushOf(string? hex, Color fallback)
    {
        var color = ColorOf(hex, fallback);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color ColorOf(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex.Trim()); }
        catch { return fallback; }
    }
}
