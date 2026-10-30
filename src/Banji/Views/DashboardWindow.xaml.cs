using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Views;

/// <summary>
/// 仪表盘：当前状态（现在/下一节/距下课/距上课/距放学/剩余节数，订阅 ClassClock.Tick 实时刷新）、
/// 今日抽签统计、花名册规模与快捷操作。
/// </summary>
public partial class DashboardWindow : Window
{
    private bool _subscribed;
    private string _todaySignature = "";

    public DashboardWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateAll();
        if (_subscribed || !AppServices.IsInitialized) return;
        _subscribed = true;
        var svc = AppServices.Current;
        svc.Clock.Tick += OnClockTick;
        svc.Picker.Picked += OnPicked;
        svc.Rosters.Changed += OnSimpleChanged;
        svc.Settings.Changed += OnSimpleChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        try
        {
            var svc = AppServices.Current;
            svc.Clock.Tick -= OnClockTick;
            svc.Picker.Picked -= OnPicked;
            svc.Rosters.Changed -= OnSimpleChanged;
            svc.Settings.Changed -= OnSimpleChanged;
        }
        catch (Exception ex)
        {
            Log.Error("仪表盘取消订阅失败", ex);
        }
    }

    private void OnClockTick(object? sender, ScheduleSnapshot snapshot) => RunOnUi(UpdateClock);

    private void OnPicked(object? sender, PickResult result) => RunOnUi(UpdatePickStats);

    private void OnSimpleChanged(object? sender, EventArgs e) => RunOnUi(UpdateAll);

    private static void RunOnUi(Action action)
    {
        try { AppServices.RunOnUi(action); }
        catch (Exception ex) { Log.Error("仪表盘刷新失败", ex); }
    }

    // =========================================================================
    // 刷新
    // =========================================================================

    private void UpdateAll()
    {
        UpdateClock();
        UpdatePickStats();
        UpdateRosterStats();
    }

    private void UpdateClock()
    {
        if (!AppServices.IsInitialized) return;
        try
        {
            var svc = AppServices.Current;
            var snapshot = svc.Clock.Current;
            var now = DateTime.Now;

            HeaderDateText.Text = $"{now:yyyy年M月d日} {WeekdayText(now.DayOfWeek)} · {now:HH:mm:ss}";
            BellOffsetText.Text = FormatBellOffset(svc.Settings.Current.BellOffsetSeconds);

            // 现在
            if (snapshot.Phase == SchedulePhase.InClass && snapshot.Current is not null)
            {
                CurrentCourseText.Text = snapshot.Current.CourseDisplay;
                CurrentCourseText.Foreground = BrushRes("Brush.Brand", Colors.RoyalBlue);
                CurrentTimeText.Text = $"{snapshot.Current.TimeText} · {snapshot.Current.PeriodName}";
            }
            else
            {
                CurrentCourseText.Text = PhaseText(snapshot.Phase);
                CurrentCourseText.Foreground = BrushRes("Brush.TextPrimary", Colors.Black);
                CurrentTimeText.Text = snapshot.FirstStart is { } first ? $"今天第一节课 {first:HH:mm}" : "";
            }

            // 下一节
            if (snapshot.Next is not null)
            {
                NextCourseText.Text = snapshot.Next.CourseDisplay;
                NextCourseText.Foreground = BrushRes("Brush.Accent", Colors.Red);
                NextTimeText.Text = $"{snapshot.Next.TimeText} · {snapshot.Next.PeriodName}";
            }
            else
            {
                NextCourseText.Text = snapshot.HasTimetable ? "今天没有下一节课" : "—";
                NextCourseText.Foreground = snapshot.HasTimetable
                    ? BrushRes("Brush.Accent", Colors.Red)
                    : BrushRes("Brush.TextTertiary", Colors.Gray);
                NextTimeText.Text = "";
            }

            UntilEndText.Text = snapshot.Phase == SchedulePhase.InClass && snapshot.UntilCurrentEnd is { } untilEnd
                ? ScheduleSnapshot.FormatCountdown(untilEnd)
                : "—";
            UntilNextText.Text = snapshot.UntilNextStart is { } untilNext
                ? ScheduleSnapshot.FormatCountdown(untilNext)
                : "—";
            UntilSchoolEndText.Text = snapshot.UntilSchoolEnd is { } untilSchoolEnd
                ? ScheduleSnapshot.FormatCountdown(untilSchoolEnd)
                : "—";
            PeriodsRemainingText.Text = snapshot.HasTimetable ? $"{snapshot.PeriodsRemaining} 节" : "—";

            UpdateTodayList(snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("仪表盘刷新当前状态失败", ex);
        }
    }

    private void UpdateTodayList(ScheduleSnapshot snapshot)
    {
        var signature = $"{snapshot.Now:yyyyMMdd}|{snapshot.Phase}|" +
                        string.Join(";", snapshot.Today.Select(c => $"{c.PeriodIndex}:{c.Course}:{c.Status}"));
        if (signature == _todaySignature) return;
        _todaySignature = signature;

        TodayListPanel.Children.Clear();
        TodayListEmptyText.Visibility = snapshot.Today.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot.Today.Count == 0) return;

        foreach (var course in snapshot.Today)
        {
            var row = new Grid { Margin = new Thickness(0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var time = new TextBlock
            {
                Text = course.TimeText,
                FontFamily = FontRes("Font.Regular", "Segoe UI"),
                FontSize = 11.5,
                Foreground = BrushRes("Brush.TextTertiary", Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(time, 0);
            row.Children.Add(time);

            var (courseBrush, weight) = course.Status switch
            {
                CourseStatus.Active => (BrushRes("Brush.Brand", Colors.RoyalBlue), "Font.Bold"),
                CourseStatus.Next => (BrushRes("Brush.Accent", Colors.Red), "Font.Bold"),
                CourseStatus.Past => (BrushRes("Brush.TextTertiary", Colors.Gray), "Font.Regular"),
                _ => (BrushRes("Brush.TextPrimary", Colors.Black), "Font.Medium")
            };
            var name = new TextBlock
            {
                Text = course.CourseDisplay,
                FontFamily = FontRes(weight, "Segoe UI"),
                FontWeight = weight == "Font.Bold" ? FontWeights.Bold : FontWeights.Normal,
                FontSize = 13,
                Foreground = courseBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var statusText = course.Status switch
            {
                CourseStatus.Active => "进行中",
                CourseStatus.Next => "下一节",
                CourseStatus.Past => "已结束",
                _ => ""
            };
            var status = new TextBlock
            {
                Text = statusText,
                FontFamily = FontRes("Font.Regular", "Segoe UI"),
                FontSize = 11,
                Foreground = courseBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(status, 2);
            row.Children.Add(status);

            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 1, 0, 1),
                Child = row
            };
            if (course.Status == CourseStatus.Active)
                border.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xFF));
            TodayListPanel.Children.Add(border);
        }
    }

    private void UpdatePickStats()
    {
        if (!AppServices.IsInitialized) return;
        try
        {
            var svc = AppServices.Current;
            var today = svc.Picker.RecentPicks
                .Where(p => p.PickedAt.Date == DateTime.Today)
                .OrderBy(p => p.PickedAt)
                .ToList();

            PickCountText.Text = today.Count.ToString();
            if (today.Count == 0)
            {
                LastPickText.Text = "今天还没有抽过签";
                return;
            }

            var last = today[^1];
            var display = !string.IsNullOrWhiteSpace(last.StudentName) && !string.IsNullOrWhiteSpace(last.StudentId)
                ? $"{last.StudentName}（{last.StudentId}）"
                : !string.IsNullOrWhiteSpace(last.StudentName) ? last.StudentName : last.StudentId;
            LastPickText.Text = $"{display} · {last.PickedAt:HH:mm}";
        }
        catch (Exception ex)
        {
            Log.Error("仪表盘刷新抽签统计失败", ex);
        }
    }

    private void UpdateRosterStats()
    {
        if (!AppServices.IsInitialized) return;
        try
        {
            var rosters = AppServices.Current.Rosters.Rosters;
            RosterCountText.Text = rosters.Count.ToString();
            RosterStudentCountText.Text = rosters.Sum(r => r.Students.Count).ToString();

            var active = AppServices.Current.Rosters.Active;
            ActiveRosterText.Text = active is not null
                ? $"当前激活：{active.Name}（{active.Students.Count} 人）"
                : rosters.Count == 0
                    ? "还没有花名册，点“花名册管理”导入或手动录入吧"
                    : "尚未激活花名册";
        }
        catch (Exception ex)
        {
            Log.Error("仪表盘刷新花名册统计失败", ex);
        }
    }

    // =========================================================================
    // 快捷操作
    // =========================================================================

    private void OnPickClick(object sender, RoutedEventArgs e) => Guard("抽签", () =>
    {
        var svc = AppServices.Current;
        if (svc.Rosters.Rosters.Count == 0 || svc.Rosters.Active is null)
        {
            MessageBox.Show("还没有可用的花名册，先导入或手动录入一份吧。", "班机",
                MessageBoxButton.OK, MessageBoxImage.Information);
            svc.ShowRosterManager();
            return;
        }
        svc.Picker.Pick();
    });

    private void OnTimerClick(object sender, RoutedEventArgs e) => Guard("倒计时",
        () => AppServices.Current.ShowTimerDialog(TimerKind.Countdown));

    private void OnSettingsClick(object sender, RoutedEventArgs e) => Guard("打开设置",
        () => AppServices.Current.ShowSettings());

    private void OnTimetableClick(object sender, RoutedEventArgs e) => Guard("打开课表管理",
        () => AppServices.Current.ShowTimetableEditor());

    private void OnRosterClick(object sender, RoutedEventArgs e) => Guard("打开花名册管理",
        () => AppServices.Current.ShowRosterManager());

    private static void Guard(string action, Action body)
    {
        try
        {
            if (!AppServices.IsInitialized) return;
            body();
        }
        catch (Exception ex)
        {
            Log.Error($"仪表盘「{action}」失败", ex);
            MessageBox.Show($"{action}失败：{ex.Message}", "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // =========================================================================
    // 展示辅助
    // =========================================================================

    private static string PhaseText(SchedulePhase phase) => phase switch
    {
        SchedulePhase.InClass => "上课中",
        SchedulePhase.Break => "课间休息",
        SchedulePhase.LunchBreak => "午休",
        SchedulePhase.BeforeSchool => "还未上课",
        SchedulePhase.AfterSchool => "已放学",
        _ => "无课表数据"
    };

    private static string WeekdayText(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日"
    };

    private static string FormatBellOffset(int seconds)
    {
        if (seconds == 0) return "铃声误差：0 秒（与铃声一致）";
        return seconds > 0
            ? $"铃声误差：本程序比铃声慢 {seconds} 秒"
            : $"铃声误差：本程序比铃声快 {-seconds} 秒";
    }

    private FontFamily FontRes(string key, string fallback) =>
        TryFindResource(key) as FontFamily ?? new FontFamily(fallback);

    private Brush BrushRes(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
