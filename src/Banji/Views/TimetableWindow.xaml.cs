using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Views;

/// <summary>
/// 课表管理窗口：周视图网格（行 = 节次+时间，列 = 周一到周日）、点格编辑课程、
/// 导入课表 / 课堂时段设置 / 临时换课（日期 + 按节替换/取消 + 备注 + 已有覆盖列表）、今天/本周切换。
/// </summary>
public partial class TimetableWindow : Window
{
    private static readonly Brush TextPrimary = MakeBrush("#1F2329");
    private static readonly Brush TextSecondary = MakeBrush("#6B7280");
    private static readonly Brush TextTertiary = MakeBrush("#9AA3AF");
    private static readonly Brush Divider = MakeBrush("#E5E9F2");
    private static readonly Brush Accent = MakeBrush("#FF3030");
    private static readonly Brush Success = MakeBrush("#22A06B");
    private static readonly Brush Warm = MakeBrush("#FF9F1C");

    private readonly ITimetableService _timetable;
    private readonly IScheduleService _schedule;
    private readonly Dictionary<int, TextBox> _replacementBoxes = new();
    private readonly Dictionary<int, CheckBox> _cancelChecks = new();

    private DayOfWeek[] _days = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
    private bool _weekMode = true;
    private bool _panelOpen;
    private bool _loadingOverride;
    private bool _suppressRefresh;
    private bool _committing;

    // 内联编辑状态
    private (DayOfWeek Day, int PeriodIndex)? _editingTarget;
    private TextBox? _editingBox;
    private TextBlock? _editingTextBlock;
    private string _editingOriginal = "";
    private string _editingDisplay = "";

    public TimetableWindow()
    {
        InitializeComponent();
        _timetable = AppServices.Current.Timetable;
        _schedule = AppServices.Current.Schedule;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timetable.Changed += OnTimetableChanged;
        UpdateViewButtons();
        RefreshAll();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timetable.Changed -= OnTimetableChanged;
    }

    private void OnTimetableChanged(object? sender, EventArgs e)
    {
        if (_suppressRefresh) return;
        AppServices.RunOnUi(() =>
        {
            if (_committing) return;
            CancelEdit();
            RefreshAll();
        });
    }

    private void RefreshAll()
    {
        var timetable = _timetable.Current;
        _days = ActiveDays(timetable);

        if (_weekMode)
        {
            WeekScroll.Visibility = Visibility.Visible;
            TodayScroll.Visibility = Visibility.Collapsed;
            BuildWeekGrid(timetable);
        }
        else
        {
            WeekScroll.Visibility = Visibility.Collapsed;
            TodayScroll.Visibility = Visibility.Visible;
            BuildTodayView();
        }

        RefreshSourceInfo();
        RefreshOverrideList(keepSelection: true);
        if (_panelOpen) LoadOverrideForm();
    }

    private static DayOfWeek[] ActiveDays(WeekTimetable timetable)
    {
        var days = new List<DayOfWeek>
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
        };
        if (timetable.Days.ContainsKey(DayOfWeek.Saturday) || timetable.Days.ContainsKey(DayOfWeek.Sunday) ||
            (timetable.Days.TryGetValue(DayOfWeek.Saturday, out var sat) && sat.Count > 0) ||
            (timetable.Days.TryGetValue(DayOfWeek.Sunday, out var sun) && sun.Count > 0))
        {
            days.Add(DayOfWeek.Saturday);
            days.Add(DayOfWeek.Sunday);
        }
        return days.ToArray();
    }

    // =========================================================================
    // 本周视图
    // =========================================================================

    private void BuildWeekGrid(WeekTimetable timetable)
    {
        WeekHost.Children.Clear();
        WeekHost.RowDefinitions.Clear();
        WeekHost.ColumnDefinitions.Clear();

        WeekHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        foreach (var _ in _days)
            WeekHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 112 });

        WeekHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < timetable.Periods.Count; i++)
            WeekHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddHeaderCell(0, 0, "节次 / 时间", HorizontalAlignment.Left);
        for (var i = 0; i < _days.Length; i++)
            AddHeaderCell(0, i + 1, DayName(_days[i]), HorizontalAlignment.Center);

        var snapshot = SafeSnapshot();
        var todayOverride = TryGetTodayOverride();
        var today = DateTime.Today.DayOfWeek;

        for (var row = 0; row < timetable.Periods.Count; row++)
        {
            var period = timetable.Periods[row];
            AddPeriodLabelCell(row + 1, period);

            for (var i = 0; i < _days.Length; i++)
            {
                var day = _days[i];
                var weeklyCourse = timetable.CourseAt(day, period.Index) ?? "";
                var displayCourse = weeklyCourse;
                var cancelled = false;
                var replaced = false;

                if (todayOverride is not null && day == today)
                {
                    if (todayOverride.CancelledPeriods.Contains(period.Index))
                    {
                        cancelled = true;
                        displayCourse = "已取消";
                    }
                    else if (todayOverride.CourseReplacements.TryGetValue(period.Index, out var replacedValue))
                    {
                        replaced = true;
                        displayCourse = string.IsNullOrWhiteSpace(replacedValue) ? "未设置" : replacedValue!;
                    }
                }

                var highlighted = snapshot.Highlighted is not null
                                  && snapshot.Now.DayOfWeek == day
                                  && snapshot.Highlighted.PeriodIndex == period.Index;
                var active = snapshot.Current is not null
                             && snapshot.Now.DayOfWeek == day
                             && snapshot.Current.PeriodIndex == period.Index;

                var cell = BuildCourseCell(day, period.Index, displayCourse, cancelled, replaced, highlighted, active);
                Grid.SetRow(cell, row + 1);
                Grid.SetColumn(cell, i + 1);
                WeekHost.Children.Add(cell);
            }
        }
    }

    private void AddHeaderCell(int row, int column, string text, HorizontalAlignment alignment)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = (FontFamily)FindResource("Font.Medium"),
            FontSize = 13,
            Foreground = TextSecondary,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center
        };
        var border = new Border
        {
            Background = MakeBrush("#EFF3FA"),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(3),
            Padding = new Thickness(12, 8, 12, 8),
            Child = block
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        WeekHost.Children.Add(border);
    }

    private void AddPeriodLabelCell(int row, ClassPeriod period)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = period.Name,
            FontFamily = (FontFamily)FindResource("Font.Medium"),
            FontSize = 14,
            Foreground = TextPrimary
        });
        stack.Children.Add(new TextBlock
        {
            Text = period.TimeText,
            FontSize = 11,
            Foreground = TextTertiary,
            Margin = new Thickness(0, 3, 0, 0)
        });
        var border = new Border
        {
            Background = MakeBrush("#FBFCFE"),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(3),
            Padding = new Thickness(12, 8, 12, 8),
            Child = stack
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, 0);
        WeekHost.Children.Add(border);
    }

    private Border BuildCourseCell(
        DayOfWeek day, int periodIndex, string course, bool cancelled, bool replaced, bool highlighted, bool active)
    {
        var textBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(course) ? "—" : course,
            FontFamily = (FontFamily)FindResource(cancelled || replaced || highlighted ? "Font.Medium" : "Font.Regular"),
            FontSize = 14,
            Foreground = cancelled ? TextTertiary
                : replaced ? Warm
                : highlighted ? Accent
                : string.IsNullOrWhiteSpace(course) ? MakeBrush("#C8D2E0")
                : TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        if (cancelled) textBlock.TextDecorations = TextDecorations.Strikethrough;

        var grid = new Grid();
        grid.Children.Add(textBlock);

        var border = new Border
        {
            Background = active ? MakeBrush("#F3FFF8") : Brushes.White,
            BorderBrush = active ? Success : highlighted ? Accent : Divider,
            BorderThickness = new Thickness(highlighted || active ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(3),
            Padding = new Thickness(8),
            MinHeight = 54,
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = "点击编辑课程名（回车保存，Esc 取消）"
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            BeginEdit(border, grid, textBlock, day, periodIndex, course);
        };
        return border;
    }

    // =========================================================================
    // 内联编辑课程
    // =========================================================================

    private void BeginEdit(Border cell, Grid grid, TextBlock textBlock, DayOfWeek day, int periodIndex, string course)
    {
        CommitEdit();
        if (_editingBox is not null) return; // 上一个提交失败时不继续
        var box = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(course) || course == "已取消" ? "" : course,
            Style = (Style)FindResource("Input.TextBox"),
            FontSize = 13,
            Margin = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Height = 32
        };
        grid.Children.Add(box);
        textBlock.Visibility = Visibility.Collapsed;

        _editingTarget = (day, periodIndex);
        _editingBox = box;
        _editingTextBlock = textBlock;
        _editingOriginal = string.IsNullOrWhiteSpace(course) || course == "已取消" ? "" : course;
        _editingDisplay = textBlock.Text;

        box.KeyDown += OnEditorKeyDown;
        box.LostKeyboardFocus += (_, _) =>
        {
            if (!_committing) CommitEdit();
        };
        box.Focus();
        box.SelectAll();
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitEdit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelEdit();
        }
    }

    private void CommitEdit()
    {
        if (_committing) return;
        var box = _editingBox;
        var target = _editingTarget;
        var textBlock = _editingTextBlock;
        if (box is null || target is null) return;

        var text = (box.Text ?? "").Trim();
        var original = _editingOriginal;
        _committing = true;
        try
        {
            _editingBox = null;
            _editingTarget = null;
            _editingTextBlock = null;
            RemoveEditor(box);
            if (textBlock is not null)
            {
                textBlock.Text = text.Length == 0 ? "—" : text;
                textBlock.Visibility = Visibility.Visible;
            }

            if (text != original)
            {
                _suppressRefresh = true;
                try
                {
                    _timetable.SetCourse(target.Value.Day, target.Value.PeriodIndex, text);
                }
                catch (Exception ex)
                {
                    Log.Error("保存课程失败", ex);
                    MessageBox.Show(this, $"保存课程失败：{ex.Message}", "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    _suppressRefresh = false;
                }
            }
        }
        finally
        {
            _committing = false;
        }
    }

    private void CancelEdit()
    {
        var box = _editingBox;
        var textBlock = _editingTextBlock;
        _committing = true;
        try
        {
            _editingBox = null;
            _editingTarget = null;
            _editingTextBlock = null;
            if (box is not null) RemoveEditor(box);
            if (textBlock is not null)
            {
                textBlock.Text = _editingDisplay;
                textBlock.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _committing = false;
        }
    }

    private static void RemoveEditor(TextBox box)
    {
        if (box.Parent is Panel panel) panel.Children.Remove(box);
    }

    // =========================================================================
    // 今天视图
    // =========================================================================

    private void BuildTodayView()
    {
        TodayHost.Children.Clear();
        var now = DateTime.Now;
        var snapshot = SafeSnapshot();

        var header = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            BorderBrush = Divider,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock
        {
            Text = $"今天 · {DayName(now.DayOfWeek)}  {now:MM 月 dd 日}",
            FontFamily = (FontFamily)FindResource("Font.Bold"),
            FontWeight = FontWeights.Bold,
            FontSize = 19,
            Foreground = TextPrimary
        });
        var summary = new List<string>();
        if (snapshot.Current is not null)
            summary.Add($"正在上：{snapshot.Current.PeriodName} {snapshot.Current.CourseDisplay}");
        else if (snapshot.Next is not null)
            summary.Add($"下一节：{snapshot.Next.PeriodName} {snapshot.Next.CourseDisplay}（{snapshot.Next.Start:HH:mm}）");
        if (snapshot.SchoolEnd is not null && now < snapshot.SchoolEnd)
            summary.Add($"距放学 {ScheduleSnapshot.FormatCountdown(snapshot.SchoolEnd.Value - now)} · 还剩 {snapshot.PeriodsRemaining} 节");
        if (summary.Count == 0) summary.Add(PhaseText(snapshot));
        headerStack.Children.Add(new TextBlock
        {
            Text = string.Join("    ", summary),
            FontSize = 13,
            Foreground = TextSecondary,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        header.Child = headerStack;
        TodayHost.Children.Add(header);

        var offset = AppServices.Current.Settings.Current.BellOffsetSeconds;
        if (offset != 0)
        {
            TodayHost.Children.Add(new TextBlock
            {
                Text = $"提示：时间已按铃声误差 {(offset > 0 ? "+" : "")}{offset} 秒调整（设置中可改）",
                FontSize = 12,
                Foreground = Warm,
                Margin = new Thickness(4, 0, 0, 10)
            });
        }

        if (snapshot.Today.Count == 0)
        {
            TodayHost.Children.Add(new TextBlock
            {
                Text = "今天没有课（周末或无课表数据）。可点击右上角“导入课表”导入 Excel/CSV 课表。",
                FontSize = 14,
                Foreground = TextSecondary,
                Margin = new Thickness(6, 20, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var occurrence in snapshot.Today)
        {
            var row = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(10),
                BorderBrush = occurrence.Status switch
                {
                    CourseStatus.Active => Success,
                    CourseStatus.Next => Accent,
                    _ => Divider
                },
                BorderThickness = new Thickness(occurrence.Status is CourseStatus.Active or CourseStatus.Next ? 1.5 : 1),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var time = new TextBlock
            {
                Text = occurrence.TimeText,
                FontFamily = (FontFamily)FindResource("Font.Medium"),
                FontSize = 14,
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(time, 0);
            grid.Children.Add(time);

            var periodName = new TextBlock
            {
                Text = occurrence.PeriodName,
                FontSize = 13,
                Foreground = TextTertiary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(periodName, 1);
            grid.Children.Add(periodName);

            var course = new TextBlock
            {
                Text = occurrence.CourseDisplay,
                FontFamily = (FontFamily)FindResource(
                    occurrence.Status is CourseStatus.Active or CourseStatus.Next ? "Font.Bold" : "Font.Regular"),
                FontWeight = occurrence.Status is CourseStatus.Active or CourseStatus.Next ? FontWeights.Bold : FontWeights.Normal,
                FontSize = 16,
                Foreground = occurrence.Status switch
                {
                    CourseStatus.Active => Success,
                    CourseStatus.Next => Accent,
                    CourseStatus.Past => TextTertiary,
                    _ => TextPrimary
                },
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(course, 2);
            grid.Children.Add(course);

            var status = new TextBlock
            {
                Text = occurrence.Status switch
                {
                    CourseStatus.Active => "上课中",
                    CourseStatus.Next => "下一节",
                    CourseStatus.Past => "已上",
                    _ => "未开始"
                },
                FontSize = 12,
                Foreground = occurrence.Status switch
                {
                    CourseStatus.Active => Success,
                    CourseStatus.Next => Accent,
                    _ => TextTertiary
                },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 4, 0)
            };
            Grid.SetColumn(status, 3);
            grid.Children.Add(status);

            row.Child = grid;
            TodayHost.Children.Add(row);
        }
    }

    // =========================================================================
    // 顶部按钮
    // =========================================================================

    private void UpdateViewButtons()
    {
        BtnWeek.Style = (Style)FindResource(_weekMode ? "Button.Form" : "Button.FormGhost");
        BtnToday.Style = (Style)FindResource(_weekMode ? "Button.FormGhost" : "Button.Form");
        ViewHint.Text = _weekMode ? "本周视图" : "今天视图";
        ViewChip.Background = MakeBrush(_weekMode ? "#E8F0FF" : "#E9FBF2");
    }

    private void OnWeekClick(object sender, RoutedEventArgs e)
    {
        if (_weekMode) return;
        _weekMode = true;
        UpdateViewButtons();
        RefreshAll();
    }

    private void OnTodayClick(object sender, RoutedEventArgs e)
    {
        if (!_weekMode) return;
        _weekMode = false;
        UpdateViewButtons();
        RefreshAll();
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppServices.Current.ShowTimetableImport();
        }
        catch (Exception ex)
        {
            Log.Error("打开课表导入窗口失败", ex);
            MessageBox.Show(this, $"打开导入窗口失败：{ex.Message}", "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPeriodsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppServices.Current.ShowPeriodSettings();
        }
        catch (Exception ex)
        {
            Log.Error("打开课堂时段设置失败", ex);
            MessageBox.Show(this, $"打开课堂时段设置失败：{ex.Message}", "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // =========================================================================
    // 临时换课面板
    // =========================================================================

    private void OnOverrideClick(object sender, RoutedEventArgs e)
    {
        _panelOpen = !_panelOpen;
        if (_panelOpen)
        {
            OverridePanel.Visibility = Visibility.Visible;
            BtnOverride.Style = (Style)FindResource("Button.Form");
            BuildOverrideEditors();
            LoadOverrideForm();
            RefreshOverrideList(keepSelection: false);
            if (OverrideDate.SelectedDate is null) OverrideDate.SelectedDate = DateTime.Today;
        }
        else
        {
            OverridePanel.Visibility = Visibility.Collapsed;
            BtnOverride.Style = (Style)FindResource("Button.FormGhost");
        }
    }

    private void OnCloseOverrideClick(object sender, RoutedEventArgs e)
    {
        _panelOpen = false;
        OverridePanel.Visibility = Visibility.Collapsed;
        BtnOverride.Style = (Style)FindResource("Button.FormGhost");
    }

    private void BuildOverrideEditors()
    {
        OverridePeriodsHost.Children.Clear();
        _replacementBoxes.Clear();
        _cancelChecks.Clear();

        var timetable = _timetable.Current;
        var date = OverrideDate.SelectedDate?.Date ?? DateTime.Today;
        var day = date.DayOfWeek;

        foreach (var period in timetable.Periods.OrderBy(p => p.Start))
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = period.Name,
                FontSize = 13,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var box = new TextBox
            {
                Style = (Style)FindResource("Input.TextBox"),
                Height = 30,
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = $"原课程：{DisplayCourse(timetable.CourseAt(day, period.Index))}（留空表示不修改）"
            };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            _replacementBoxes[period.Index] = box;

            var check = new CheckBox
            {
                Content = "取消",
                FontSize = 13,
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                ToolTip = "勾选表示这一节停上"
            };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
            _cancelChecks[period.Index] = check;

            OverridePeriodsHost.Children.Add(grid);
        }
    }

    private void LoadOverrideForm()
    {
        if (!_panelOpen) return;
        _loadingOverride = true;
        try
        {
            var date = OverrideDate.SelectedDate?.Date ?? DateTime.Today;
            var day = date.DayOfWeek;
            var timetable = _timetable.Current;
            var dayOverride = _timetable.GetOverride(DateOnly.FromDateTime(date));

            foreach (var period in timetable.Periods)
            {
                if (!_replacementBoxes.TryGetValue(period.Index, out var box)) continue;
                var toolTipBase = DisplayCourse(timetable.CourseAt(day, period.Index));
                box.ToolTip = $"原课程：{toolTipBase}（留空表示不修改）";
                box.Text = dayOverride is not null &&
                           dayOverride.CourseReplacements.TryGetValue(period.Index, out var replaced) &&
                           !string.IsNullOrWhiteSpace(replaced)
                    ? replaced!
                    : "";
            }

            foreach (var (index, check) in _cancelChecks)
                check.IsChecked = dayOverride?.CancelledPeriods.Contains(index) == true;

            OverrideNote.Text = dayOverride?.Note ?? "";
            OverrideStatus.Text = dayOverride is null
                ? "这一天没有临时换课。"
                : "已加载这一天的临时换课，可直接修改后保存。";
            OverrideStatus.Foreground = dayOverride is null ? TextSecondary : Success;
        }
        finally
        {
            _loadingOverride = false;
        }
    }

    private void OnOverrideDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_panelOpen) return;
        BuildOverrideEditors();
        LoadOverrideForm();
    }

    private void OnSaveOverrideClick(object sender, RoutedEventArgs e)
    {
        if (_loadingOverride) return;
        try
        {
            var date = OverrideDate.SelectedDate?.Date ?? DateTime.Today;
            var dayOverride = new DayOverride
            {
                Date = DateOnly.FromDateTime(date),
                Note = string.IsNullOrWhiteSpace(OverrideNote.Text) ? null : OverrideNote.Text.Trim()
            };
            foreach (var (index, box) in _replacementBoxes)
            {
                var text = (box.Text ?? "").Trim();
                if (text.Length == 0) continue;
                if (_cancelChecks.TryGetValue(index, out var check) && check.IsChecked == true) continue;
                dayOverride.CourseReplacements[index] = text;
            }
            foreach (var (index, check) in _cancelChecks)
                if (check.IsChecked == true) dayOverride.CancelledPeriods.Add(index);

            var empty = dayOverride.IsEmpty;
            _timetable.SetOverride(dayOverride);
            RefreshAll();
            OverrideStatus.Foreground = Success;
            OverrideStatus.Text = empty
                ? $"已清除 {date:MM 月 dd 日} 的临时换课。"
                : $"已保存 {date:MM 月 dd 日} 的临时换课（优先于周课表生效）。";
        }
        catch (Exception ex)
        {
            Log.Error("保存临时换课失败", ex);
            OverrideStatus.Foreground = Accent;
            OverrideStatus.Text = $"保存失败：{ex.Message}";
        }
    }

    private void OnDeleteOverrideClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var date = OverrideDate.SelectedDate?.Date ?? DateTime.Today;
            _timetable.RemoveOverride(DateOnly.FromDateTime(date));
            RefreshAll();
            OverrideStatus.Foreground = TextSecondary;
            OverrideStatus.Text = $"已删除 {date:MM 月 dd 日} 的临时换课。";
        }
        catch (Exception ex)
        {
            Log.Error("删除临时换课失败", ex);
            OverrideStatus.Foreground = Accent;
            OverrideStatus.Text = $"删除失败：{ex.Message}";
        }
    }

    private void RefreshOverrideList(bool keepSelection)
    {
        if (!_panelOpen) return;
        var selectedDate = (OverrideList.SelectedItem as ListBoxItem)?.Tag as DateOnly?;
        _loadingOverride = true;
        try
        {
            OverrideList.Items.Clear();
            foreach (var dayOverride in _timetable.Overrides)
            {
                var summary = new List<string>();
                if (dayOverride.CancelledPeriods.Count > 0)
                    summary.Add("取消第 " + string.Join("、", dayOverride.CancelledPeriods.OrderBy(i => i)) + " 节");
                if (dayOverride.CourseReplacements.Count > 0)
                    summary.Add(string.Join("；", dayOverride.CourseReplacements
                        .OrderBy(kv => kv.Key)
                        .Select(kv => $"第 {kv.Key} 节→{(string.IsNullOrWhiteSpace(kv.Value) ? "空" : kv.Value)}")));
                if (!string.IsNullOrWhiteSpace(dayOverride.Note)) summary.Add("备注：" + dayOverride.Note);

                var item = new ListBoxItem
                {
                    Content = $"{dayOverride.Date:MM 月 dd 日}（{DayName(dayOverride.Date.DayOfWeek)}）  {string.Join("；", summary)}",
                    Tag = dayOverride.Date,
                    FontSize = 12,
                    Padding = new Thickness(6, 4, 6, 4)
                };
                OverrideList.Items.Add(item);
                if (keepSelection && selectedDate is not null && selectedDate.Value == dayOverride.Date)
                    OverrideList.SelectedItem = item;
            }
            if (OverrideList.Items.Count == 0)
            {
                OverrideList.Items.Add(new ListBoxItem
                {
                    Content = "暂无临时换课",
                    FontSize = 12,
                    Foreground = TextTertiary,
                    IsEnabled = false,
                    Padding = new Thickness(6, 4, 6, 4)
                });
            }
        }
        finally
        {
            _loadingOverride = false;
        }
    }

    private void OnOverrideListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOverride || !_panelOpen) return;
        if (OverrideList.SelectedItem is not ListBoxItem { Tag: DateOnly date }) return;
        _loadingOverride = true;
        try
        {
            OverrideDate.SelectedDate = date.ToDateTime(TimeOnly.MinValue);
        }
        finally
        {
            _loadingOverride = false;
        }
        BuildOverrideEditors();
        LoadOverrideForm();
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private void RefreshSourceInfo()
    {
        var timetable = _timetable.Current;
        var source = string.IsNullOrWhiteSpace(timetable.SourceFile)
            ? "数据来源：默认课表（尚未导入，可点击“导入课表”选择 Excel / CSV 文件）"
            : $"数据来源：{Path.GetFileName(timetable.SourceFile)}（{timetable.SourceFile}）";
        if (timetable.SourceFile is not null)
            source += $" · 更新于 {timetable.UpdatedAt:yyyy-MM-dd HH:mm}";
        SourceInfo.Text = source;

        var settings = AppServices.Current.Settings.Current;
        var offset = settings.BellOffsetSeconds;
        OffsetInfo.Text = $"铃声误差：{(offset > 0 ? "+" : "")}{offset} 秒";

        var snapshot = SafeSnapshot();
        PhaseInfo.Text = PhaseText(snapshot);
    }

    private ScheduleSnapshot SafeSnapshot()
    {
        try
        {
            return _schedule.SnapshotAt(DateTime.Now);
        }
        catch (Exception ex)
        {
            Log.Error("课表窗口计算快照失败", ex);
            return ScheduleSnapshot.Empty(DateTime.Now);
        }
    }

    private DayOverride? TryGetTodayOverride()
    {
        try
        {
            return _timetable.GetOverride(DateOnly.FromDateTime(DateTime.Now));
        }
        catch (Exception ex)
        {
            Log.Error("读取今日临时换课失败", ex);
            return null;
        }
    }

    private static string PhaseText(ScheduleSnapshot snapshot) => snapshot.Phase switch
    {
        SchedulePhase.InClass => snapshot.Current is null
            ? "上课中"
            : $"正在上：{snapshot.Current.PeriodName} {snapshot.Current.CourseDisplay} · 距下课 {ScheduleSnapshot.FormatCountdown(snapshot.UntilCurrentEnd ?? TimeSpan.Zero)}",
        SchedulePhase.BeforeSchool => $"距上课 {ScheduleSnapshot.FormatCountdown(snapshot.UntilNextStart ?? TimeSpan.Zero)}",
        SchedulePhase.Break => $"课间休息 · 距上课 {ScheduleSnapshot.FormatCountdown(snapshot.UntilNextStart ?? TimeSpan.Zero)}",
        SchedulePhase.LunchBreak => $"午休中 · 距上课 {ScheduleSnapshot.FormatCountdown(snapshot.UntilNextStart ?? TimeSpan.Zero)}",
        SchedulePhase.AfterSchool => "已放学",
        _ => "今天无课"
    };

    private static string DisplayCourse(string? course) => string.IsNullOrWhiteSpace(course) ? "未设置" : course!;

    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    private static Brush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
