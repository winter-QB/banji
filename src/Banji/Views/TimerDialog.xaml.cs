using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Views;

/// <summary>
/// 计时器对话框：倒计时（1/3/5/7/10 分钟快捷 + 自定义分:秒）与正计时。
/// 窗口非模态，关闭后计时继续；超时全屏提醒由 core-schedule 的 NotificationService 负责。
/// </summary>
public partial class TimerDialog : Window
{
    private TimerKind _initialKind = TimerKind.Countdown;
    private bool _subscribed;
    private bool _applyingKind;

    /// <summary>打开时默认选中的计时类型（AppServices.ShowTimerDialog 在 Show 前赋值）。</summary>
    public TimerKind InitialKind
    {
        get => _initialKind;
        set
        {
            if (_initialKind == value) return;
            _initialKind = value;
            if (IsLoaded) ApplyKind();
        }
    }

    public TimerDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyKind();
        UpdateStatus();
        if (_subscribed || !AppServices.IsInitialized) return;
        _subscribed = true;
        var timers = AppServices.Current.Timers;
        timers.Tick += OnTimerChanged;
        timers.StateChanged += OnTimerChanged;
        timers.Expired += OnTimerChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        try
        {
            var timers = AppServices.Current.Timers;
            timers.Tick -= OnTimerChanged;
            timers.StateChanged -= OnTimerChanged;
            timers.Expired -= OnTimerChanged;
        }
        catch { /* 退出过程中服务可能已不可用 */ }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // =========================================================================
    // 模式
    // =========================================================================

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingKind) return;
        _initialKind = StopwatchModeButton.IsChecked == true ? TimerKind.Stopwatch : TimerKind.Countdown;
        ApplyKind();
        UpdateStatus();
    }

    private void ApplyKind()
    {
        _applyingKind = true;
        try
        {
            var stopwatch = _initialKind == TimerKind.Stopwatch;
            CountdownPanel.Visibility = stopwatch ? Visibility.Collapsed : Visibility.Visible;
            StopwatchPanel.Visibility = stopwatch ? Visibility.Visible : Visibility.Collapsed;
            CountdownModeButton.IsChecked = !stopwatch;
            StopwatchModeButton.IsChecked = stopwatch;
            SubtitleText.Text = stopwatch
                ? "正计时从 0 开始累计，没有超时提醒，关闭本窗口后继续计时。"
                : "倒计时到点会全屏模糊提醒；关闭本窗口后继续计时。";
        }
        finally
        {
            _applyingKind = false;
        }
    }

    // =========================================================================
    // 开始 / 取消
    // =========================================================================

    private void OnQuickStartClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag || !int.TryParse(tag, out var minutes)) return;
        StartCountdown(TimeSpan.FromMinutes(minutes));
    }

    private void OnCustomStartClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseDuration(CustomInput.Text, out var duration))
        {
            ShowInputError("格式不对哦，请输入如 5:30 表示 5 分 30 秒（秒数 0~59）。");
            CustomInput.Focus();
            CustomInput.SelectAll();
            return;
        }

        HideInputError();
        StartCountdown(duration);
    }

    private void OnCustomInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnCustomStartClick(sender, e);
    }

    private void OnStartStopwatchClick(object sender, RoutedEventArgs e) => StartStopwatch();

    private void OnCancelTimerClick(object sender, RoutedEventArgs e) => Guard("取消计时", () =>
    {
        AppServices.Current.Timers.Cancel();
        UpdateStatus();
    });

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void StartCountdown(TimeSpan duration) => Guard("开始倒计时", () =>
    {
        AppServices.Current.Timers.StartCountdown(duration);
        UpdateStatus();
    });

    private void StartStopwatch() => Guard("开始正计时", () =>
    {
        AppServices.Current.Timers.StartStopwatch();
        UpdateStatus();
    });

    private static void Guard(string action, Action body)
    {
        try
        {
            if (!AppServices.IsInitialized) return;
            body();
        }
        catch (Exception ex)
        {
            Log.Error($"计时器「{action}」失败", ex);
            MessageBox.Show($"{action}失败：{ex.Message}", "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // =========================================================================
    // 状态显示
    // =========================================================================

    private void OnTimerChanged(object? sender, EventArgs e) => AppServices.RunOnUi(UpdateStatus);

    private void UpdateStatus()
    {
        if (!AppServices.IsInitialized) return;
        var current = AppServices.Current.Timers.Current;

        if (current is null)
        {
            TimeText.Text = "00:00";
            TimeText.Foreground = (Brush)FindResource("Brush.TextPrimary");
            StatusLabel.Text = "未开始";
            StatusLabel.Foreground = (Brush)FindResource("Brush.TextSecondary");
            CancelTimerButton.Visibility = Visibility.Collapsed;
            return;
        }

        TimeText.Text = current.DisplayText;
        CancelTimerButton.Visibility = current.Status == TimerStatus.Running ? Visibility.Visible : Visibility.Collapsed;

        if (current.Status == TimerStatus.Running)
        {
            TimeText.Foreground = current.Kind == TimerKind.Countdown
                ? (Brush)FindResource("Brush.Brand")
                : (Brush)FindResource("Brush.TextPrimary");
            StatusLabel.Text = current.Kind == TimerKind.Countdown
                ? $"倒计时进行中 · 共 {FormatTotal(current.Total)}"
                : "正计时进行中";
            StatusLabel.Foreground = (Brush)FindResource("Brush.TextSecondary");
        }
        else if (current.Status == TimerStatus.Expired)
        {
            TimeText.Foreground = (Brush)FindResource("Brush.Accent");
            StatusLabel.Text = "计时结束 · 已全屏提醒";
            StatusLabel.Foreground = (Brush)FindResource("Brush.Accent");
        }
        else
        {
            TimeText.Foreground = (Brush)FindResource("Brush.TextPrimary");
            StatusLabel.Text = "未开始";
            StatusLabel.Foreground = (Brush)FindResource("Brush.TextSecondary");
        }
    }

    private static string FormatTotal(TimeSpan? total)
    {
        if (total is null) return "";
        var span = total.Value;
        if (span.TotalSeconds < 60) return $"{span.TotalSeconds:0} 秒";
        if (span.TotalMinutes < 60 && span.Seconds == 0) return $"{span.TotalMinutes:0} 分钟";
        return TimerFormat(span);
    }

    private static string TimerFormat(TimeSpan span)
    {
        var totalSeconds = (long)Math.Floor(span.TotalSeconds + 1e-6);
        var h = totalSeconds / 3600;
        var m = totalSeconds % 3600 / 60;
        var s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    // =========================================================================
    // 输入解析
    // =========================================================================

    /// <summary>解析“分:秒”/“时:分:秒”/纯分钟（支持中文“分/秒”）。</summary>
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var cleaned = text.Trim()
            .Replace('：', ':')
            .Replace("分钟", "")
            .Replace("分", ":")
            .Replace("秒", "")
            .Replace(" ", "")
            .Replace("\t", "");
        if (cleaned.EndsWith(':')) cleaned = cleaned.TrimEnd(':');
        if (cleaned.Length == 0) return false;

        var parts = cleaned.Split(':');
        foreach (var part in parts)
        {
            if (part.Length == 0) return false;
            foreach (var ch in part)
                if (!char.IsDigit(ch)) return false;
        }

        long hours = 0, minutes = 0, seconds = 0;
        switch (parts.Length)
        {
            case 1:
                // 纯数字按“分钟”处理
                if (!long.TryParse(parts[0], out minutes)) return false;
                break;
            case 2:
                if (!long.TryParse(parts[0], out minutes)) return false;
                if (!long.TryParse(parts[1], out seconds)) return false;
                break;
            case 3:
                if (!long.TryParse(parts[0], out hours)) return false;
                if (!long.TryParse(parts[1], out minutes)) return false;
                if (!long.TryParse(parts[2], out seconds)) return false;
                break;
            default:
                return false;
        }

        if (seconds is > 59 or < 0) return false;
        if (minutes is > 599 or < 0) return false;
        if (hours is > 23 or < 0) return false;

        duration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return duration > TimeSpan.Zero && duration <= TimeSpan.FromHours(24);
    }

    private void ShowInputError(string message)
    {
        InputErrorText.Text = message;
        InputErrorText.Visibility = Visibility.Visible;
    }

    private void HideInputError() => InputErrorText.Visibility = Visibility.Collapsed;
}
