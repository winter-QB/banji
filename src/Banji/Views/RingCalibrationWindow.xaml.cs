using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Schedule;

namespace Banji.Views;

/// <summary>
/// 铃声校准向导（第二轮用户点名功能：「铃声又慢了」智能侦测）：
/// 显示当前误差与最近 6 个时间边界（上课/下课/放学 + 实时倒计时），
/// 在铃声响起时点“我听到铃声了”，自动算出建议误差并一键应用（±60 秒内，立即生效、无需重启）。
/// 保留无参构造（AppServices.ShowRingCalibrationWindow 通过它创建）。
/// </summary>
public partial class RingCalibrationWindow : Window
{
    private static readonly Brush TextPrimary = MakeBrush("#1F2329");
    private static readonly Brush TextSecondary = MakeBrush("#6B7280");
    private static readonly Brush TextTertiary = MakeBrush("#9AA3AF");
    private static readonly Brush Brand = MakeBrush("#317FFF");
    private static readonly Brush BrandBg = MakeBrush("#EAF2FF");
    private static readonly Brush Success = MakeBrush("#22A06B");
    private static readonly Brush SuccessBg = MakeBrush("#E9FBF2");
    private static readonly Brush Warning = MakeBrush("#FF9F1C");
    private static readonly Brush WarningBg = MakeBrush("#FFF4E5");
    private static readonly Brush Error = MakeBrush("#FF3030");

    private readonly IScheduleService _schedule;
    private readonly ISettingsService _settings;
    private readonly IClassClock _clock;
    private readonly List<BoundaryRow> _rows = new();

    private DateTime _lastUiUpdate = DateTime.MinValue;
    private bool _subscribed;

    private int? _suggestedOffset;
    private string _suggestedBoundaryTitle = "";
    private DateTime _suggestedBoundaryTime;

    public RingCalibrationWindow()
    {
        InitializeComponent();
        _schedule = AppServices.Current.Schedule;
        _settings = AppServices.Current.Settings;
        _clock = AppServices.Current.Clock;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _clock.Tick += OnClockTick;
            _settings.Changed += OnSettingsChanged;
            _subscribed = true;
        }
        UpdateCurrentOffset();
        RefreshBoundaryList();
        HideSuggestion();
        SetStatus("提示：在学校铃声响起的那一刻点“我听到铃声了”，越接近铃声越好。", false);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_subscribed) return;
        _clock.Tick -= OnClockTick;
        _settings.Changed -= OnSettingsChanged;
        _subscribed = false;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        AppServices.RunOnUi(() =>
        {
            UpdateCurrentOffset();
            RefreshBoundaryList();
            UpdateLiveCountdowns(DateTime.Now);
        });
    }

    private void OnClockTick(object? sender, ScheduleSnapshot snapshot)
    {
        AppServices.RunOnUi(UpdateLive);
    }

    /// <summary>实时刷新：倒计时文字每 200ms 更新；最近边界过掉后重建列表。</summary>
    private void UpdateLive()
    {
        if (!IsVisible) return;
        var now = DateTime.Now;
        if ((now - _lastUiUpdate).TotalMilliseconds < 200) return;
        _lastUiUpdate = now;

        if (_rows.Count == 0 || _rows[0].Boundary.Time <= now.AddSeconds(1))
        {
            RefreshBoundaryList();
            return;
        }
        UpdateLiveCountdowns(now);
    }

    // =========================================================================
    // 当前误差
    // =========================================================================

    private void UpdateCurrentOffset()
    {
        var offset = RingCalibrationMath.ClampOffset(_settings.Current.BellOffsetSeconds);
        CurrentOffsetText.Text = offset == 0 ? "0 秒" : $"{RingCalibrationMath.FormatSigned(offset)} 秒";
        CurrentOffsetText.Foreground = offset == 0 ? Brand : offset > 0 ? Warning : Success;
        CurrentOffsetDesc.Text = offset == 0
            ? "与课表时间一致（默认）"
            : RingCalibrationMath.DescribeOffset(offset);
    }

    // =========================================================================
    // 时间边界列表
    // =========================================================================

    private sealed class BoundaryRow
    {
        public ClassBoundary Boundary = null!;
        public Grid Root = null!;
        public TextBlock CountdownText = null!;
    }

    private void RefreshBoundaryList()
    {
        BoundaryList.Children.Clear();
        _rows.Clear();

        IReadOnlyList<ClassBoundary> boundaries;
        try
        {
            boundaries = _schedule.UpcomingBoundaries(DateTime.Now, 6);
        }
        catch (Exception ex)
        {
            Log.Error("铃声校准读取时间边界失败", ex);
            boundaries = Array.Empty<ClassBoundary>();
        }

        if (boundaries.Count == 0)
        {
            BoundaryList.Children.Add(new TextBlock
            {
                Text = "今天没有更多课程边界了（周末或无课表数据）。可先在“课表管理”导入课表。",
                FontSize = 13,
                Foreground = TextTertiary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 12, 2, 12)
            });
            return;
        }

        var now = DateTime.Now;
        foreach (var boundary in boundaries)
        {
            var row = BuildBoundaryRow(boundary);
            UpdateCountdown(row, now);
            BoundaryList.Children.Add(row.Root);
            _rows.Add(row);
        }
    }

    private BoundaryRow BuildBoundaryRow(ClassBoundary boundary)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var timeText = new TextBlock
        {
            Text = boundary.Time.ToString("HH:mm:ss"),
            FontFamily = (FontFamily)FindResource("Font.Medium"),
            FontSize = 16,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(timeText, 0);
        grid.Children.Add(timeText);

        var (kindName, kindForeground, kindBackground) = boundary.Kind switch
        {
            ClassBoundaryKind.ClassStart => ("上课", Brand, BrandBg),
            ClassBoundaryKind.ClassEnd => ("下课", Success, SuccessBg),
            _ => ("放学", Warning, WarningBg)
        };
        var chip = new Border
        {
            Background = kindBackground,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 2, 10, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = kindName, FontSize = 12, Foreground = kindForeground }
        };
        Grid.SetColumn(chip, 1);
        grid.Children.Add(chip);

        var titleText = new TextBlock
        {
            Text = boundary.Title,
            FontSize = 14,
            Foreground = TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(titleText, 2);
        grid.Children.Add(titleText);

        var countdownText = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("Font.Medium"),
            FontSize = 14,
            Foreground = TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0)
        };
        Grid.SetColumn(countdownText, 3);
        grid.Children.Add(countdownText);

        return new BoundaryRow { Boundary = boundary, Root = grid, CountdownText = countdownText };
    }

    private void UpdateLiveCountdowns(DateTime now)
    {
        foreach (var row in _rows) UpdateCountdown(row, now);
    }

    private static void UpdateCountdown(BoundaryRow row, DateTime now)
    {
        var remaining = row.Boundary.Time - now;
        if (remaining <= TimeSpan.Zero)
        {
            row.CountdownText.Text = "即将";
            row.CountdownText.Foreground = BrushFromHex("#FF9F1C");
            return;
        }
        row.CountdownText.Text = $"还有 {ScheduleSnapshot.FormatCountdown(remaining)}";
        row.CountdownText.Foreground = remaining <= TimeSpan.FromSeconds(30)
            ? BrushFromHex("#FF3030")
            : BrushFromHex("#6B7280");
    }

    // =========================================================================
    // 我听到铃声了
    // =========================================================================

    private void OnBellClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var listenedAt = DateTime.Now;
            // 候选包含“刚刚过去”的边界：把时间窗往前推，找到最近一个已过/即将到的边界
            var candidates = _schedule.UpcomingBoundaries(listenedAt - RingCalibrationMath.SearchTolerance, 40);
            var nearest = RingCalibrationMath.FindNearest(candidates, listenedAt);

            if (nearest is null)
            {
                HideSuggestion();
                SetStatus($"没有找到 2 分钟内的时间边界，无法判断这是哪次铃声。请在上下课铃声响起的那一刻再点一次。", true);
                return;
            }

            var currentOffset = RingCalibrationMath.ClampOffset(_settings.Current.BellOffsetSeconds);
            var delta = RingCalibrationMath.DeltaSeconds(listenedAt, nearest.Time);
            var suggested = RingCalibrationMath.SuggestOffsetSeconds(currentOffset, listenedAt, nearest.Time);

            Log.Info($"铃声校准：听到铃声 {listenedAt:HH:mm:ss}，最近边界「{nearest.Title}」" +
                     $"{nearest.Time:HH:mm:ss}（{nearest.Kind}），偏差 {delta} 秒，当前误差 {currentOffset}，建议 {suggested}");

            if (suggested == currentOffset)
            {
                HideSuggestion();
                SetStatus($"✓ 当前误差已经很准确（{RingCalibrationMath.FormatSigned(currentOffset)} 秒，" +
                          $"{RingCalibrationMath.DescribeOffset(currentOffset)}），本次偏差 {FormatDelta(delta)} 秒，无需调整。",
                    false, success: true);
                return;
            }

            _suggestedOffset = suggested;
            _suggestedBoundaryTitle = nearest.Title;
            _suggestedBoundaryTime = nearest.Time;
            ShowSuggestion(nearest, listenedAt, currentOffset, delta, suggested);
            SetStatus($"已根据这次铃声算出建议：误差 {FormatSigned(currentOffset)} 秒 → {FormatSigned(suggested)} 秒。", false, success: true);
        }
        catch (Exception ex)
        {
            Log.Error("铃声校准失败", ex);
            SetStatus($"校准失败：{ex.Message}", true);
        }
    }

    private void ShowSuggestion(
        ClassBoundary boundary, DateTime listenedAt, int currentOffset, int delta, int suggested)
    {
        SuggestionPanel.Visibility = Visibility.Visible;
        SuggestionTitle.Text = $"建议把铃声误差调整为 {FormatSigned(suggested)} 秒（当前 {FormatSigned(currentOffset)} 秒）";
        SuggestionDetail.Text =
            $"你听到铃声的时刻：{listenedAt:HH:mm:ss}\n" +
            $"最近边界：{boundary.Title}（显示时间 {boundary.Time:HH:mm:ss}）\n" +
            $"偏差：{FormatDelta(delta)} 秒 → {RingCalibrationMath.DescribeOffset(suggested)}";
        ApplySuggestionButton.Content = $"应用建议（{FormatSigned(suggested)} 秒）";
    }

    private void OnApplySuggestionClick(object sender, RoutedEventArgs e)
    {
        if (_suggestedOffset is not int value) return;
        try
        {
            _settings.Update(x => x.BellOffsetSeconds = value);
            HideSuggestion();
            UpdateCurrentOffset();
            RefreshBoundaryList();
            SetStatus($"已应用：铃声误差 {FormatSigned(value)} 秒（{RingCalibrationMath.DescribeOffset(value)}）。" +
                      "时钟已立即刷新，无需重启程序。", false, success: true);
            Log.Info($"铃声校准已应用：BellOffsetSeconds={value}（最近边界 {_suggestedBoundaryTitle} {_suggestedBoundaryTime:HH:mm:ss}）");
        }
        catch (Exception ex)
        {
            Log.Error("应用铃声校准建议失败", ex);
            SetStatus($"应用失败：{ex.Message}", true);
        }
    }

    private void OnIgnoreSuggestionClick(object sender, RoutedEventArgs e)
    {
        HideSuggestion();
        SetStatus("已忽略本次校准建议（误差保持不变）。可以再试一次。", false);
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.Update(x => x.BellOffsetSeconds = 0);
            HideSuggestion();
            UpdateCurrentOffset();
            RefreshBoundaryList();
            SetStatus("已重置为 0 秒（与课表时间一致）。", false, success: true);
            Log.Info("铃声校准已重置为 0 秒");
        }
        catch (Exception ex)
        {
            Log.Error("重置铃声误差失败", ex);
            SetStatus($"重置失败：{ex.Message}", true);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void HideSuggestion()
    {
        _suggestedOffset = null;
        SuggestionPanel.Visibility = Visibility.Collapsed;
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private void SetStatus(string message, bool isError, bool success = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? Error : success ? Success : TextSecondary;
    }

    private static string FormatSigned(int seconds) => RingCalibrationMath.FormatSigned(seconds);

    private static string FormatDelta(int deltaSeconds) => deltaSeconds > 0 ? $"+{deltaSeconds}" : deltaSeconds.ToString();

    private static Brush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Brush BrushFromHex(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
