using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Views;

/// <summary>v1.7.0：临时时间表窗口里的一节课的可编辑快照（便于纯函数测试）。</summary>
public sealed class DayPeriodEdit
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string StartText { get; set; } = "";
    public string EndText { get; set; } = "";
    public bool Cancelled { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"第{Index}节" : Name;
}

/// <summary>
/// v1.7.0 临时时间表的纯函数逻辑：时间解析/格式化、“每节 40 分钟”重排、
/// 由基础时段 + 当天覆盖组装编辑行、校验并生成 PeriodsOverride、清除判断。
/// </summary>
public static class DayPeriodsMath
{
    /// <summary>解析用户输入的时间：支持 08:00 / 8:00 / 08:00:30 / 08点30分 / 全角冒号。</summary>
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim()
            .Replace('：', ':')
            .Replace("点", ":")
            .Replace("分", "")
            .Replace(" ", "");
        if (!TimeSpan.TryParseExact(normalized, new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss", @"h\:mm\:ss" },
                CultureInfo.InvariantCulture, out time))
        {
            if (!TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out time)) return false;
        }
        return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    public static string FormatTime(TimeSpan time)
        => $"{(int)time.TotalHours:00}:{time.Minutes:00}";

    /// <summary>
    /// “每节 40 分钟”一键：保留各节当前开始时间（保持原有课间），结束 = 开始 + 40 分钟；
    /// 若开始时间早于上一节结束时间则顺延到上一节结束（不会重叠）。
    /// </summary>
    public static List<(TimeSpan Start, TimeSpan End)> ApplyFortyMinutes(
        IReadOnlyList<TimeSpan> currentStarts, int minutes = 40)
    {
        var result = new List<(TimeSpan Start, TimeSpan End)>();
        var duration = TimeSpan.FromMinutes(Math.Clamp(minutes, 10, 120));
        TimeSpan? previousEnd = null;
        foreach (var rawStart in currentStarts)
        {
            var start = rawStart;
            if (previousEnd is { } previous && start < previous) start = previous;
            var end = start + duration;
            result.Add((start, end));
            previousEnd = end;
        }
        return result;
    }

    /// <summary>
    /// 组装编辑行：基础时段 + 当天 PeriodsOverride（非空时）。
    /// 覆盖里缺少的节次 = 今天被取消（取消勾选状态），方便用户再点开恢复。
    /// </summary>
    public static List<DayPeriodEdit> BuildEditList(
        IReadOnlyList<ClassPeriod>? basePeriods, IReadOnlyList<ClassPeriod>? periodsOverride)
    {
        var byIndex = new Dictionary<int, ClassPeriod>();
        foreach (var period in basePeriods ?? Array.Empty<ClassPeriod>())
            if (period is not null) byIndex[period.Index] = period;

        var hasOverride = periodsOverride is { Count: > 0 };
        if (hasOverride)
        {
            foreach (var period in periodsOverride!)
                if (period is not null) byIndex[period.Index] = period;
        }

        var result = new List<DayPeriodEdit>();
        foreach (var index in byIndex.Keys.OrderBy(i => i))
        {
            var source = byIndex[index];
            var cancelled = hasOverride && (periodsOverride ?? Array.Empty<ClassPeriod>()).All(p => p.Index != index);
            result.Add(new DayPeriodEdit
            {
                Index = index,
                Name = string.IsNullOrWhiteSpace(source.Name) ? $"第{index}节" : source.Name,
                StartText = FormatTime(source.Start),
                EndText = FormatTime(source.End),
                Cancelled = cancelled
            });
        }
        return result;
    }

    /// <summary>
    /// 校验并生成 PeriodsOverride：跳过勾选“取消”的节次，按开始时间排序并重排 Index=1..N；
    /// 时间格式错误、结束不晚于开始、课节重叠、全部取消 都会返回中文错误。
    /// </summary>
    public static bool TryBuildPeriods(
        IReadOnlyList<DayPeriodEdit>? edits, out List<ClassPeriod>? periods, out string? error)
    {
        periods = null;
        error = null;
        if (edits is null || edits.Count == 0)
        {
            error = "今天没有可编辑的课节。";
            return false;
        }

        var active = new List<(DayPeriodEdit Edit, TimeSpan Start, TimeSpan End)>();
        foreach (var edit in edits.Where(e => e is not null && !e.Cancelled))
        {
            if (!TryParseTime(edit.StartText, out var start) || !TryParseTime(edit.EndText, out var end))
            {
                error = $"“{edit.DisplayName}”的时间格式不对，请用 HH:mm（如 08:00）。";
                return false;
            }
            if (end <= start)
            {
                error = $"“{edit.DisplayName}”的结束时间必须晚于开始时间。";
                return false;
            }
            active.Add((edit, start, end));
        }

        if (active.Count == 0)
        {
            error = "至少要保留一节课；如果今天全天不上课，请在课表管理里取消当天课程。";
            return false;
        }

        active.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < active.Count; i++)
        {
            if (active[i].Start < active[i - 1].End)
            {
                error = $"“{active[i - 1].Edit.DisplayName}”与“{active[i].Edit.DisplayName}”的时间重叠，请调整。";
                return false;
            }
        }

        periods = active.Select((item, i) => new ClassPeriod
        {
            Index = i + 1,
            Name = $"第{i + 1}节",
            Start = item.Start,
            End = item.End
        }).ToList();
        return true;
    }

    /// <summary>只清时段后，如果当天没有其它覆盖（换课/取消/备注）→ 需要 RemoveOverride 整天删掉。</summary>
    public static bool ShouldRemoveOverrideAfterClearingPeriods(DayOverride? existing)
        => existing is null || (existing.CourseReplacements.Count == 0
                                && existing.CancelledPeriods.Count == 0
                                && string.IsNullOrWhiteSpace(existing.Note));
}

/// <summary>
/// v1.7.0 临时修改今天的课表时间（提前放学 / 缩短课时）：
/// 编辑各节起止、每节 40 分钟一键、每节可取消；应用写 DayOverride.PeriodsOverride（仅今天生效），
/// 清除只移除时段、保留当天临时换课/取消。
/// </summary>
public partial class DayPeriodsWindow : Window
{
    private static readonly Brush ErrorBrush = MakeBrush("#FF3030");
    private static readonly Brush OkBrush = MakeBrush("#22A06B");
    private static readonly Brush NormalBrush = MakeBrush("#6B7280");

    private readonly ITimetableService _timetable;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private readonly List<RowControls> _rows = new();

    private sealed record RowControls(int Index, string Name, TextBox StartBox, TextBox EndBox, CheckBox CancelBox);

    public DayPeriodsWindow()
    {
        InitializeComponent();
        _timetable = AppServices.Current.Timetable;
        Loaded += (_, _) => LoadRows();
    }

    private void LoadRows()
    {
        _rows.Clear();
        RowsPanel.Children.Clear();

        var timetable = _timetable.Current;
        var basePeriods = timetable.Periods.Count > 0
            ? timetable.Periods
            : WeekTimetable.CreateDefault().Periods;
        var dayOverride = _timetable.GetOverride(_today);
        var edits = DayPeriodsMath.BuildEditList(basePeriods, dayOverride?.PeriodsOverride);

        DateText.Text = $"今天 · {_today:yyyy 年 M 月 d 日}（{WeekdayText(_today.DayOfWeek)}）" +
                        "　仅今天生效，明天自动恢复";

        foreach (var edit in edits) AddRow(edit);

        if (edits.Count == 0)
        {
            ShowMessage("课表里还没有课堂时段：请先在「课表管理 → 课堂时段」设置好，再来调整今天的时间。", NormalBrush);
            FortyMinuteButton.IsEnabled = false;
            return;
        }

        FortyMinuteButton.IsEnabled = true;
        var extras = new List<string>();
        if (dayOverride is not null)
        {
            if (dayOverride.CourseReplacements.Count > 0) extras.Add($"{dayOverride.CourseReplacements.Count} 处换课");
            if (dayOverride.CancelledPeriods.Count > 0) extras.Add($"{dayOverride.CancelledPeriods.Count} 节取消");
        }
        var suffix = extras.Count > 0
            ? $" 今天已有临时换课/取消（{string.Join("、", extras)}），应用或清除时段都不会影响它们。"
            : "";
        ShowMessage("时间格式 HH:mm（如 08:00）；点“每节 40 分钟”可一键缩短课时；每节可单独勾选“取消”。" + suffix,
            NormalBrush);
    }

    private void AddRow(DayPeriodEdit edit)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });

        var label = new TextBlock
        {
            Text = $"第{edit.Index}节",
            FontFamily = (FontFamily)FindResource("Font.Medium"),
            FontSize = 13.5,
            Foreground = (Brush)FindResource("Brush.TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var startBox = new TextBox
        {
            Text = edit.StartText,
            Style = (Style)FindResource("Input.TextBox"),
            Width = 112,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        Grid.SetColumn(startBox, 1);
        grid.Children.Add(startBox);

        var endBox = new TextBox
        {
            Text = edit.EndText,
            Style = (Style)FindResource("Input.TextBox"),
            Width = 112,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        Grid.SetColumn(endBox, 2);
        grid.Children.Add(endBox);

        var note = new TextBlock
        {
            Text = "第 " + edit.Index + " 节",
            FontFamily = (FontFamily)FindResource("Font.Regular"),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("Brush.TextTertiary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(note, 3);
        grid.Children.Add(note);

        var cancelBox = new CheckBox
        {
            Content = "取消",
            IsChecked = edit.Cancelled,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        Grid.SetColumn(cancelBox, 4);
        grid.Children.Add(cancelBox);

        void UpdateEnabled()
        {
            var cancelled = cancelBox.IsChecked == true;
            startBox.IsEnabled = !cancelled;
            endBox.IsEnabled = !cancelled;
            note.Text = cancelled ? "今天取消（仍可随时恢复）" : "第 " + edit.Index + " 节";
        }
        cancelBox.Checked += (_, _) => UpdateEnabled();
        cancelBox.Unchecked += (_, _) => UpdateEnabled();
        UpdateEnabled();

        RowsPanel.Children.Add(grid);
        _rows.Add(new RowControls(edit.Index, edit.Name, startBox, endBox, cancelBox));
    }

    // =========================================================================
    // 按钮
    // =========================================================================

    private void OnFortyMinuteClick(object sender, RoutedEventArgs e)
    {
        var active = _rows.Where(r => r.CancelBox.IsChecked != true).ToList();
        if (active.Count == 0)
        {
            ShowMessage("没有可调整的课节：请先取消勾选“取消”。", ErrorBrush);
            return;
        }

        var starts = new List<TimeSpan>();
        foreach (var row in active)
        {
            if (!DayPeriodsMath.TryParseTime(row.StartBox.Text, out var start))
            {
                ShowMessage($"“第{row.Index}节”的开始时间格式不对，请用 HH:mm（如 08:00）。", ErrorBrush);
                return;
            }
            starts.Add(start);
        }

        var adjusted = DayPeriodsMath.ApplyFortyMinutes(starts);
        for (var i = 0; i < active.Count; i++)
        {
            active[i].StartBox.Text = DayPeriodsMath.FormatTime(adjusted[i].Start);
            active[i].EndBox.Text = DayPeriodsMath.FormatTime(adjusted[i].End);
        }
        ShowMessage("已按各节当前开始时间重排为每节 40 分钟（如与上一节重叠会自动顺延），点“应用今天”生效。", OkBrush);
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var edits = _rows.Select(row => new DayPeriodEdit
            {
                Index = row.Index,
                Name = row.Name,
                StartText = row.StartBox.Text.Trim(),
                EndText = row.EndBox.Text.Trim(),
                Cancelled = row.CancelBox.IsChecked == true
            }).ToList();

            if (!DayPeriodsMath.TryBuildPeriods(edits, out var periods, out var error))
            {
                ShowMessage(error ?? "时间设置不合法。", ErrorBrush);
                return;
            }

            var existing = _timetable.GetOverride(_today)?.Clone() ?? new DayOverride { Date = _today };
            existing.Date = _today;
            existing.UpdatedAt = DateTime.Now;
            existing.PeriodsOverride = periods;
            _timetable.SetOverride(existing);
            AppServices.Current.Clock.Refresh();

            LoadRows();
            ShowMessage($"已应用今天的临时时间表：{periods!.Count} 节课。仅今天生效，明天自动恢复。", OkBrush);
        }
        catch (Exception ex)
        {
            Log.Error("应用今天的临时时间表失败", ex);
            ShowMessage("应用失败：" + ex.Message, ErrorBrush);
        }
    }

    private void OnClearTodayClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var existing = _timetable.GetOverride(_today);
            if (existing?.PeriodsOverride is null or { Count: 0 })
            {
                ShowMessage("今天没有时间段调整可清除。", NormalBrush);
                return;
            }

            existing.PeriodsOverride = null;
            if (DayPeriodsMath.ShouldRemoveOverrideAfterClearingPeriods(existing))
                _timetable.RemoveOverride(_today);
            else
                _timetable.SetOverride(existing);
            AppServices.Current.Clock.Refresh();

            LoadRows();
            ShowMessage("已清除今天的时段调整；当天的临时换课/取消会保留。", OkBrush);
        }
        catch (Exception ex)
        {
            Log.Error("清除今天的临时时间表失败", ex);
            ShowMessage("清除失败：" + ex.Message, ErrorBrush);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // =========================================================================
    // 小工具
    // =========================================================================

    private void ShowMessage(string text, Brush brush)
    {
        MessageText.Text = text;
        MessageText.Foreground = brush;
    }

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

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
