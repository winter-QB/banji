using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Banji.Controls.Toolbar;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Views;

/// <summary>
/// 仪表盘：当前状态（现在/下一节/距下课/距上课/距放学/剩余节数，订阅 ClassClock.Tick 实时刷新）、
/// 今日抽签统计、考试 D-Day、积分排行、宠物养成统计、花名册规模与快捷操作。
/// </summary>
public partial class DashboardWindow : Window
{
    private bool _subscribed;
    private string _todaySignature = "";
    private string _pointsSignature = "";
    private string _growthSignature = "";
    private string _countdownSignature = "";

    public DashboardWindow()
    {
        InitializeComponent();
        // 高窗口在小屏幕上自动收敛到工作区，避免超出屏幕（内容本身可滚动）
        try
        {
            var work = SystemParameters.WorkArea;
            MaxHeight = Math.Max(MinHeight, work.Height - 24);
            MaxWidth = Math.Max(MinWidth, work.Width - 24);
        }
        catch
        {
            // 极端环境取默认值
        }
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
        svc.Growth.Changed += OnSimpleChanged;
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
            svc.Growth.Changed -= OnSimpleChanged;
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
        UpdatePointsRanking();
        UpdateGrowthStats();
        UpdateCountdownCard();
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
    // 积分排行 / 宠物养成 / 考试 D-Day（第二轮新增）
    // =========================================================================

    private void UpdatePointsRanking()
    {
        if (!AppServices.IsInitialized) return;
        try
        {
            var svc = AppServices.Current;
            var active = svc.Rosters.Active;
            var top = active is null ? Array.Empty<StudentPoints>() : svc.Rosters.TopPoints(active.Id, 5).ToArray();

            var signature = $"{active?.Id}|{active?.Name}|" + string.Join(";", top.Select(p => $"{p.StudentKey}:{p.Points}"));
            if (signature == _pointsSignature) return;
            _pointsSignature = signature;

            PointsTitleText.Text = active is null ? "" : $"{active.Name} · 前 5";
            PointsPanel.Children.Clear();

            if (active is null)
            {
                PointsEmptyText.Text = svc.Rosters.Rosters.Count == 0
                    ? "还没有花名册，点“花名册管理”导入或手动录入吧"
                    : "还没有激活花名册";
                PointsEmptyText.Visibility = Visibility.Visible;
                return;
            }
            if (top.Length == 0)
            {
                PointsEmptyText.Text = "名单里还没有学生";
                PointsEmptyText.Visibility = Visibility.Visible;
                return;
            }

            PointsEmptyText.Visibility = Visibility.Collapsed;
            for (var i = 0; i < top.Length; i++)
            {
                var item = top[i];
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var rank = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontFamily = FontRes(i == 0 ? "Font.Bold" : "Font.Medium", "Segoe UI"),
                    FontWeight = i == 0 ? FontWeights.Bold : FontWeights.Normal,
                    FontSize = 13,
                    Foreground = i == 0 ? BrushRes("Brush.Accent", Colors.Red) : BrushRes("Brush.TextTertiary", Colors.Gray),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(rank, 0);
                row.Children.Add(rank);

                var name = new TextBlock
                {
                    Text = DisplayStudent(item),
                    FontFamily = FontRes("Font.Medium", "Segoe UI"),
                    FontSize = 13,
                    Foreground = BrushRes("Brush.TextPrimary", Colors.Black),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);

                var points = new TextBlock
                {
                    Text = item.Points > 0 ? $"+{item.Points} 分" : $"{item.Points} 分",
                    FontFamily = FontRes("Font.Bold", "Segoe UI"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    Foreground = item.Points > 0 ? BrushRes("Brush.Brand", Colors.RoyalBlue) : BrushRes("Brush.TextTertiary", Colors.Gray),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                Grid.SetColumn(points, 2);
                row.Children.Add(points);

                PointsPanel.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            Log.Error("仪表盘刷新积分排行失败", ex);
        }
    }

    private static string DisplayStudent(StudentPoints item)
    {
        var name = item.StudentName?.Trim() ?? "";
        var id = item.StudentId?.Trim() ?? "";
        if (name.Length > 0 && id.Length > 0) return $"{name}（{id}）";
        if (name.Length > 0) return name;
        return id.Length > 0 ? id : item.StudentKey;
    }

    private void UpdateGrowthStats()
    {
        if (!AppServices.IsInitialized) return;
        try
        {
            var growth = AppServices.Current.Growth;
            var stats = growth.Stats;
            var achievements = growth.Achievements.Count;
            var signature = $"{stats.DaysUsed}|{stats.PatCount}|{stats.PickCount}|{stats.GroupCount}|{stats.NoticeCount}|{achievements}";
            if (signature == _growthSignature) return;
            _growthSignature = signature;

            GrowthDaysText.Text = stats.DaysUsed.ToString();
            GrowthPatText.Text = stats.PatCount.ToString();
            GrowthPickText.Text = stats.PickCount.ToString();
            GrowthExtraText.Text = $"分组 {stats.GroupCount} 次 · 上下课提示 {stats.NoticeCount} 次" +
                                   (stats.FirstUseDate != default ? $" · 从 {stats.FirstUseDate:yyyy-MM-dd} 开始陪伴" : "");
            GrowthAchievementText.Text = achievements > 0
                ? $"已解锁成就 {achievements} 个：{string.Join("、", growth.Achievements.Take(4).Select(a => a.Title))}" +
                  (achievements > 4 ? "…" : "")
                : "还没有解锁成就，多摸摸它试试～";
        }
        catch (Exception ex)
        {
            Log.Error("仪表盘刷新养成统计失败", ex);
        }
    }

    private void UpdateCountdownCard()
    {
        if (!AppServices.IsInitialized) return;
        try
        {
            var svc = AppServices.Current;
            var events = svc.Settings.Current.CountdownEvents;
            var today = DateOnly.FromDateTime(DateTime.Now);
            var next = NextCountdownEvent(events, today);
            var enabled = (events ?? new List<CountdownEventItem>())
                .Where(e => e is { Enabled: true } && !string.IsNullOrWhiteSpace(e.Name))
                .OrderBy(e => e.DaysLeft(today) < 0 ? int.MaxValue : e.DaysLeft(today))
                .ThenByDescending(e => e.DaysLeft(today))
                .ToList();

            var signature = next is null
                ? "none"
                : $"{next.Id}|{next.DaysLeft(today)}|" + string.Join(";", enabled.Select(e => $"{e.Id}:{e.DaysLeft(today)}"));
            if (signature == _countdownSignature) return;
            _countdownSignature = signature;

            DdayListPanel.Children.Clear();
            if (next is null)
            {
                DdayNameText.Text = "还没有考试倒计时";
                DdayNameText.Foreground = BrushRes("Brush.TextTertiary", Colors.Gray);
                DdayDaysText.Text = "—";
                DdayDaysText.Foreground = BrushRes("Brush.TextTertiary", Colors.Gray);
                DdayDateText.Text = "在“设置 → 考试倒计时”里添加考试/重要日期";
                DdayDot.Background = new SolidColorBrush(Colors.Gray);
                DdaySpacer.Visibility = Visibility.Collapsed;
                return;
            }

            var days = next.DaysLeft(today);
            var color = ToolButtonStyler.ParseColor(next.Color, Colors.OrangeRed);
            DdayDot.Background = new SolidColorBrush(color);
            DdayNameText.Text = next.Name;
            DdayNameText.Foreground = BrushRes("Brush.TextPrimary", Colors.Black);
            DdayDaysText.Text = days == 0 ? "就是今天！" : days > 0 ? $"还有 {days} 天" : $"已过去 {-days} 天";
            DdayDaysText.Foreground = new SolidColorBrush(color);
            DdayDateText.Text = $"{next.Date:yyyy年M月d日} {WeekdayText(next.Date.DayOfWeek)}";

            var others = enabled.Where(e => !ReferenceEquals(e, next) && e.DaysLeft(today) >= 0)
                .OrderBy(e => e.DaysLeft(today))
                .Take(3)
                .ToList();
            DdaySpacer.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in others)
            {
                var itemDays = item.DaysLeft(today);
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var left = new TextBlock
                {
                    Text = item.Name,
                    FontFamily = FontRes("Font.Regular", "Segoe UI"),
                    FontSize = 12,
                    Foreground = BrushRes("Brush.TextSecondary", Colors.Gray),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var right = new TextBlock
                {
                    Text = itemDays == 0 ? "今天" : $"{itemDays} 天",
                    FontFamily = FontRes("Font.Medium", "Segoe UI"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(ToolButtonStyler.ParseColor(item.Color, color)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
                row.Children.Add(left);
                DdayListPanel.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            Log.Error("仪表盘刷新考试倒计时失败", ex);
        }
    }

    /// <summary>
    /// 最近的启用倒计时事件：优先取“今天及未来”里天数最小的，若全部已过期则取最近的过去事件；没有启用事件返回 null。
    /// （纯函数，便于单测。）
    /// </summary>
    public static CountdownEventItem? NextCountdownEvent(IEnumerable<CountdownEventItem>? events, DateOnly today)
    {
        if (events is null) return null;
        var enabled = events
            .Where(e => e is not null && e.Enabled && !string.IsNullOrWhiteSpace(e.Name))
            .ToList();
        if (enabled.Count == 0) return null;

        var upcoming = enabled
            .Where(e => e.DaysLeft(today) >= 0)
            .OrderBy(e => e.DaysLeft(today))
            .FirstOrDefault();
        if (upcoming is not null) return upcoming;

        return enabled.OrderByDescending(e => e.DaysLeft(today)).First();
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

    private void OnGroupingClick(object sender, RoutedEventArgs e) => Guard("打开随机分组",
        () => AppServices.Current.ShowGroupingWindow());

    private void OnCalibrationClick(object sender, RoutedEventArgs e) => Guard("打开铃声校准向导",
        () => AppServices.Current.ShowRingCalibrationWindow());

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
