using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Views;

/// <summary>
/// 课堂时段设置：每节起止时间/名称可编辑（精确到秒），午休/课间可编辑，支持新增/删除节次、
/// “从当前课表自动推算”，保存前做结束 &gt; 开始、时间不重叠的中文校验。
/// </summary>
public partial class PeriodSettingsWindow : Window
{
    private static readonly Brush SuccessBrush = MakeBrush("#22A06B");
    private static readonly Brush ErrorBrush = MakeBrush("#FF3030");
    private static readonly Brush NormalBrush = MakeBrush("#6B7280");

    private readonly ITimetableService _timetable;
    private readonly ObservableCollection<PeriodEditRow> _periods = new();
    private readonly ObservableCollection<BreakEditRow> _breaks = new();

    public PeriodSettingsWindow()
    {
        InitializeComponent();
        _timetable = AppServices.Current.Timetable;

        PeriodGrid.ItemsSource = _periods;
        BreakGrid.ItemsSource = _breaks;
        Loaded += (_, _) => ReloadFromService();
    }

    // =========================================================================
    // 载入 / 放弃修改
    // =========================================================================

    private void ReloadFromService()
    {
        var timetable = _timetable.Current;
        _periods.Clear();
        _breaks.Clear();

        var source = timetable.Periods.Count > 0 ? timetable.Periods : WeekTimetable.CreateDefault().Periods;
        foreach (var period in source)
        {
            _periods.Add(new PeriodEditRow
            {
                OriginalIndex = period.Index,
                Name = string.IsNullOrWhiteSpace(period.Name) ? $"第{period.Index}节" : period.Name,
                StartText = FormatTime(period.Start),
                EndText = FormatTime(period.End)
            });
        }

        var breaks = timetable.Periods.Count > 0 ? timetable.Breaks : WeekTimetable.CreateDefault().Breaks;
        foreach (var segment in breaks)
        {
            _breaks.Add(new BreakEditRow
            {
                Name = segment.Name,
                StartText = FormatTime(segment.Start),
                EndText = FormatTime(segment.End)
            });
        }

        PeriodHint.Text = $"共 {_periods.Count} 节，最后一节 {(_periods.Count > 0 ? _periods[^1].EndText : "-")} 结束";
        SetStatus("已加载当前课表时段，修改后点击“保存”生效。", false);
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ReloadFromService();
        SetStatus("已放弃未保存的修改。", false);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // =========================================================================
    // 新增 / 删除
    // =========================================================================

    private void OnAddPeriodClick(object sender, RoutedEventArgs e)
    {
        var duration = TimeSpan.FromMinutes(45);
        var gap = TimeSpan.FromMinutes(10);
        TimeSpan start;
        TimeSpan end;

        if (_periods.Count == 0)
        {
            start = new TimeSpan(8, 0, 0);
            end = start + duration;
        }
        else if (TryParseTimeText(_periods[^1].StartText, out var lastStart) &&
                 TryParseTimeText(_periods[^1].EndText, out var lastEnd) && lastEnd > lastStart)
        {
            duration = lastEnd - lastStart;
            start = lastEnd + gap;
            end = start + duration;
        }
        else
        {
            start = new TimeSpan(8, 0, 0);
            end = start + duration;
        }

        var row = new PeriodEditRow
        {
            Name = $"第{_periods.Count + 1}节",
            StartText = FormatTime(start),
            EndText = FormatTime(end)
        };
        _periods.Add(row);
        PeriodHint.Text = $"共 {_periods.Count} 节";
        PeriodGrid.SelectedItem = row;
        PeriodGrid.ScrollIntoView(row);
        SetStatus("已新增一节课，请填写名称与起止时间后保存。", false);
    }

    private void OnDeletePeriodClick(object sender, RoutedEventArgs e)
    {
        if (PeriodGrid.SelectedItem is not PeriodEditRow row)
        {
            SetStatus("请先在左侧表格中选中要删除的节次。", true);
            return;
        }
        _periods.Remove(row);
        RenumberDefaultPeriodNames();
        PeriodHint.Text = $"共 {_periods.Count} 节";
        SetStatus("已删除选中的节次（保存后生效）。", false);
    }

    private void RenumberDefaultPeriodNames()
    {
        for (var i = 0; i < _periods.Count; i++)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(_periods[i].Name ?? "", @"^第\s*\d+\s*节$"))
                _periods[i].Name = $"第{i + 1}节";
        }
    }

    private void OnAddBreakClick(object sender, RoutedEventArgs e)
    {
        var row = new BreakEditRow
        {
            Name = _breaks.Any(b => b.Name.Contains('午')) ? "大课间" : "午休",
            StartText = "09:40",
            EndText = "10:00"
        };
        _breaks.Add(row);
        BreakGrid.SelectedItem = row;
        BreakGrid.ScrollIntoView(row);
        SetStatus("已新增一个午休/课间时间段，请填写名称与起止时间。", false);
    }

    private void OnDeleteBreakClick(object sender, RoutedEventArgs e)
    {
        if (BreakGrid.SelectedItem is not BreakEditRow row)
        {
            SetStatus("请先在右侧表格中选中要删除的午休/课间。", true);
            return;
        }
        _breaks.Remove(row);
        SetStatus("已删除选中的午休/课间（保存后生效）。", false);
    }

    // =========================================================================
    // 保存（中文校验）
    // =========================================================================

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildPeriods(out var periods, out var periodError))
        {
            SetStatus(periodError, true);
            return;
        }
        if (!TryBuildBreaks(out var breaks, out var breakError))
        {
            SetStatus(breakError, true);
            return;
        }

        try
        {
            _timetable.SetPeriods(periods);
            _timetable.SetBreaks(breaks);
            ReloadFromService();
            SetStatus("保存成功！课堂时段与午休/课间已立即生效（含抽签窗口与上下课提示）。", false, success: true);
        }
        catch (Exception ex)
        {
            Log.Error("保存课堂时段失败", ex);
            SetStatus($"保存失败：{ex.Message}", true);
        }
    }

    private bool TryBuildPeriods(out List<ClassPeriod> periods, out string error)
    {
        periods = new List<ClassPeriod>();
        error = "";
        if (_periods.Count == 0)
        {
            error = "至少需要保留一节课，请点击“新增一节”添加。";
            return false;
        }

        var drafts = new List<(PeriodEditRow Row, TimeSpan Start, TimeSpan End)>();
        for (var i = 0; i < _periods.Count; i++)
        {
            var row = _periods[i];
            var label = string.IsNullOrWhiteSpace(row.Name) ? $"第{i + 1}行" : row.Name;
            if (!TryParseTimeText(row.StartText, out var start))
            {
                error = $"{label}：开始时间“{row.StartText}”格式不正确，请填写 HH:mm 或 HH:mm:ss。";
                return false;
            }
            if (!TryParseTimeText(row.EndText, out var end))
            {
                error = $"{label}：结束时间“{row.EndText}”格式不正确，请填写 HH:mm 或 HH:mm:ss。";
                return false;
            }
            if (end <= start)
            {
                error = $"{label}：结束时间（{row.EndText}）必须晚于开始时间（{row.StartText}），请调整。";
                return false;
            }
            drafts.Add((row, start, end));
        }

        drafts.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < drafts.Count; i++)
        {
            var prev = drafts[i - 1];
            var cur = drafts[i];
            if (cur.Start < prev.End)
            {
                error = $"“{Label(prev.Row)}”（{prev.Row.StartText}-{prev.Row.EndText}）与“{Label(cur.Row)}”（{cur.Row.StartText}-{cur.Row.EndText}）时间重叠，请调整。";
                return false;
            }
        }

        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            periods.Add(new ClassPeriod
            {
                Index = draft.Row.OriginalIndex > 0 ? draft.Row.OriginalIndex : i + 1,
                Name = string.IsNullOrWhiteSpace(draft.Row.Name) ? $"第{i + 1}节" : draft.Row.Name.Trim(),
                Start = draft.Start,
                End = draft.End
            });
        }
        return true;
    }

    private bool TryBuildBreaks(out List<BreakSegment> breaks, out string error)
    {
        breaks = new List<BreakSegment>();
        error = "";

        var drafts = new List<(BreakEditRow Row, TimeSpan Start, TimeSpan End)>();
        for (var i = 0; i < _breaks.Count; i++)
        {
            var row = _breaks[i];
            var label = string.IsNullOrWhiteSpace(row.Name) ? $"第{i + 1}行" : row.Name;
            if (!TryParseTimeText(row.StartText, out var start))
            {
                error = $"{label}：开始时间“{row.StartText}”格式不正确，请填写 HH:mm 或 HH:mm:ss。";
                return false;
            }
            if (!TryParseTimeText(row.EndText, out var end))
            {
                error = $"{label}：结束时间“{row.EndText}”格式不正确，请填写 HH:mm 或 HH:mm:ss。";
                return false;
            }
            if (end <= start)
            {
                error = $"{label}：结束时间（{row.EndText}）必须晚于开始时间（{row.StartText}），请调整。";
                return false;
            }
            drafts.Add((row, start, end));
        }

        drafts.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < drafts.Count; i++)
        {
            if (drafts[i].Start < drafts[i - 1].End)
            {
                error = $"午休/课间“{Label(drafts[i - 1].Row)}”与“{Label(drafts[i].Row)}”时间重叠，请调整。";
                return false;
            }
        }

        foreach (var draft in drafts)
        {
            breaks.Add(new BreakSegment
            {
                Name = string.IsNullOrWhiteSpace(draft.Row.Name) ? "课间" : draft.Row.Name.Trim(),
                Start = draft.Start,
                End = draft.End
            });
        }
        return true;
    }

    // =========================================================================
    // 从当前课表自动推算
    // =========================================================================

    private void OnAutoInferClick(object sender, RoutedEventArgs e)
    {
        var timetable = _timetable.Current;
        var count = Math.Max(1, timetable.Periods.Count > 0 ? timetable.Periods.Count : _periods.Count);
        if (_periods.Count > 0) count = _periods.Count;

        // 统计当前表格里的典型节次时长与课间间隔
        var durations = new List<TimeSpan>();
        var gaps = new List<TimeSpan>();
        TimeSpan? previousEnd = null;
        foreach (var row in _periods)
        {
            if (!TryParseTimeText(row.StartText, out var start) || !TryParseTimeText(row.EndText, out var end)) continue;
            if (end > start) durations.Add(end - start);
            if (previousEnd is not null && start > previousEnd.Value)
            {
                var between = start - previousEnd.Value;
                if (between >= TimeSpan.FromMinutes(1) && between <= TimeSpan.FromHours(2)) gaps.Add(between);
            }
            previousEnd = end;
        }

        var duration = Median(durations) ?? TimeSpan.FromMinutes(45);
        var gap = Median(gaps) ?? TimeSpan.FromMinutes(10);
        var firstStart = timetable.Periods.Count > 0 ? timetable.Periods[0].Start : new TimeSpan(8, 0, 0);

        // 有午休时段时，下午的课从午休结束开始排
        var lunch = timetable.Breaks.FirstOrDefault(b => b.Name.Contains('午')) ??
                    _breaks.Select(b => new BreakSegment
                    {
                        Name = b.Name,
                        Start = TryParseTimeText(b.StartText, out var s) ? s : TimeSpan.Zero,
                        End = TryParseTimeText(b.EndText, out var e2) ? e2 : TimeSpan.Zero
                    }).FirstOrDefault(b => b.Name.Contains('午'));

        var morningCount = lunch is not null && count >= 6 ? (count + 1) / 2 : count;
        var rebuilt = new List<(TimeSpan Start, TimeSpan End)>();
        var cursor = firstStart;
        for (var i = 0; i < count; i++)
        {
            if (i == morningCount && lunch is not null && lunch.End > cursor)
                cursor = lunch.End;
            var end = cursor + duration;
            rebuilt.Add((cursor, end));
            cursor = end + gap;
        }

        for (var i = 0; i < _periods.Count && i < rebuilt.Count; i++)
        {
            _periods[i].StartText = FormatTime(rebuilt[i].Start);
            _periods[i].EndText = FormatTime(rebuilt[i].End);
        }
        while (_periods.Count < rebuilt.Count)
        {
            var i = _periods.Count;
            _periods.Add(new PeriodEditRow
            {
                Name = $"第{i + 1}节",
                StartText = FormatTime(rebuilt[i].Start),
                EndText = FormatTime(rebuilt[i].End)
            });
        }

        var lunchNote = lunch is not null
            ? $"；上午 {morningCount} 节，下午 {count - morningCount} 节从 {FormatTime(lunch.End)} 开始"
            : "";
        SetStatus($"已按当前课表推算：每节 {(int)Math.Round(duration.TotalMinutes)} 分钟、课间 {(int)Math.Round(gap.TotalMinutes)} 分钟，共 {count} 节{lunchNote}。请检查后点击“保存”。", false, success: true);
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static TimeSpan? Median(List<TimeSpan> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static string Label(PeriodEditRow row) => string.IsNullOrWhiteSpace(row.Name) ? "未命名" : row.Name;

    private static string Label(BreakEditRow row) => string.IsNullOrWhiteSpace(row.Name) ? "课间" : row.Name;

    private static string FormatTime(TimeSpan time)
        => time.Seconds == 0 && time.Milliseconds == 0
            ? $"{time.Hours:00}:{time.Minutes:00}"
            : $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}";

    private static bool TryParseTimeText(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim()
            .Replace('：', ':')
            .Replace('．', '.')
            .Replace('。', '.')
            .Replace("点", ":")
            .Replace("分", "")
            .Replace("时", ":");
        var hasColon = t.Contains(':');
        if (!hasColon) return false;
        if (!TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var parsed)) return false;
        if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromHours(24)) return false;
        time = parsed;
        return true;
    }

    private void SetStatus(string message, bool isError, bool success = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? ErrorBrush : success ? SuccessBrush : NormalBrush;
    }

    private static Brush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>课堂时段表格行（可编辑）。</summary>
public sealed class PeriodEditRow : INotifyPropertyChanged
{
    private string _name = "";
    private string _startText = "";
    private string _endText = "";

    public int OriginalIndex { get; set; }

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; OnPropertyChanged(); }
    }

    public string StartText
    {
        get => _startText;
        set { if (_startText == value) return; _startText = value; OnPropertyChanged(); }
    }

    public string EndText
    {
        get => _endText;
        set { if (_endText == value) return; _endText = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>午休/课间表格行（可编辑）。</summary>
public sealed class BreakEditRow : INotifyPropertyChanged
{
    private string _name = "";
    private string _startText = "";
    private string _endText = "";

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; OnPropertyChanged(); }
    }

    public string StartText
    {
        get => _startText;
        set { if (_startText == value) return; _startText = value; OnPropertyChanged(); }
    }

    public string EndText
    {
        get => _endText;
        set { if (_endText == value) return; _endText = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
