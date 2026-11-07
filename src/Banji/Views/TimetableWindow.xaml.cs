using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Timetable;

namespace Banji.Views;

/// <summary>
/// 课表管理窗口：周视图网格（行 = 节次+时间，列 = 周一到周日）、点格编辑课程、
/// 导入课表 / 课堂时段设置 / 临时换课（日期 + 按节替换/取消 + 备注 + 已有覆盖列表）、今天/本周切换。
/// v1.4.0：启用单双周后顶部出现「通用 / 单周 / 双周」三页签与复制/清空/恢复工具。
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
    private static readonly Brush ParityOdd = MakeBrush("#E07B00");
    private static readonly Brush ParityEven = MakeBrush("#7C3AED");

    private readonly ITimetableService _timetable;
    private readonly IScheduleService _schedule;
    private readonly IWeekParityService _weekParity;
    private readonly TimetableService? _concreteTimetable;
    private readonly Dictionary<int, TextBox> _replacementBoxes = new();
    private readonly Dictionary<int, CheckBox> _cancelChecks = new();

    private DayOfWeek[] _days = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
    private bool _weekMode = true;
    private bool _panelOpen;
    private bool _loadingOverride;
    private bool _suppressRefresh;
    private bool _committing;
    private bool _settingsSubscribed;

    /// <summary>当前编辑页签（v1.4.0）：通用 / 单周 / 双周。</summary>
    private WeekType _parityPage = WeekType.All;

    // 内联编辑状态
    private (DayOfWeek Day, int PeriodIndex)? _editingTarget;
    private TextBox? _editingBox;
    private TextBlock? _editingTextBlock;
    private StackPanel? _editingStack;
    private TextBlock? _editingBadge;
    private string _editingOriginal = "";
    private string _editingDisplay = "";
    private WeekType _editingPage = WeekType.All;

    public TimetableWindow()
    {
        InitializeComponent();
        _timetable = AppServices.Current.Timetable;
        _schedule = AppServices.Current.Schedule;
        _weekParity = AppServices.Current.WeekParity;
        _concreteTimetable = _timetable as TimetableService;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timetable.Changed += OnTimetableChanged;
        _weekParity.Changed += OnParityChanged;
        AppServices.Current.Settings.Changed += OnSettingsChanged;
        _settingsSubscribed = true;
        UpdateViewButtons();
        RefreshAll();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timetable.Changed -= OnTimetableChanged;
        _weekParity.Changed -= OnParityChanged;
        if (_settingsSubscribed) AppServices.Current.Settings.Changed -= OnSettingsChanged;
    }

    private void OnParityChanged(object? sender, EventArgs e) => RefreshSafely();

    private void OnSettingsChanged(object? sender, EventArgs e) => RefreshSafely();

    private void RefreshSafely()
    {
        AppServices.RunOnUi(() =>
        {
            if (_committing) return;
            CancelEdit();
            RefreshAll();
        });
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
        UpdateParityBar(timetable);

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
        // 周末只有实际排了课才显示对应列
        if (timetable.EntriesFor(DayOfWeek.Saturday).Any(e => !string.IsNullOrWhiteSpace(e.Course)))
            days.Add(DayOfWeek.Saturday);
        if (timetable.EntriesFor(DayOfWeek.Sunday).Any(e => !string.IsNullOrWhiteSpace(e.Course)))
            days.Add(DayOfWeek.Sunday);
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
        var parityEnabled = IsParityEnabled();
        var page = parityEnabled ? _parityPage : WeekType.All;

        for (var row = 0; row < timetable.Periods.Count; row++)
        {
            var period = timetable.Periods[row];
            AddPeriodLabelCell(row + 1, period);

            for (var i = 0; i < _days.Length; i++)
            {
                var day = _days[i];
                var pageCourse = ResolvePageCourse(timetable, day, period.Index, page, parityEnabled, out var weekSpecific);
                var displayCourse = pageCourse;
                var cancelled = false;
                var replaced = false;
                var displaySpecific = weekSpecific;

                if (todayOverride is not null && day == today)
                {
                    if (todayOverride.CancelledPeriods.Contains(period.Index))
                    {
                        cancelled = true;
                        displayCourse = "已取消";
                        displaySpecific = false;
                    }
                    else if (todayOverride.CourseReplacements.TryGetValue(period.Index, out var replacedValue))
                    {
                        replaced = true;
                        displayCourse = string.IsNullOrWhiteSpace(replacedValue) ? "未设置" : replacedValue!;
                        displaySpecific = false;
                    }
                }

                var highlighted = snapshot.Highlighted is not null
                                  && snapshot.Now.DayOfWeek == day
                                  && snapshot.Highlighted.PeriodIndex == period.Index;
                var active = snapshot.Current is not null
                             && snapshot.Now.DayOfWeek == day
                             && snapshot.Current.PeriodIndex == period.Index;

                var cell = BuildCourseCell(
                    day, period.Index, displayCourse, cancelled, replaced, highlighted, active,
                    parityEnabled && page != WeekType.All, displaySpecific, page);
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
        DayOfWeek day, int periodIndex, string course, bool cancelled, bool replaced, bool highlighted, bool active,
        bool parityPage, bool weekSpecific, WeekType page)
    {
        var textBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(course) ? "—" : course,
            FontFamily = (FontFamily)FindResource(cancelled || replaced || highlighted || weekSpecific ? "Font.Medium" : "Font.Regular"),
            FontSize = 14,
            Foreground = cancelled ? TextTertiary
                : replaced ? Warm
                : highlighted ? Accent
                : weekSpecific ? (page == WeekType.Odd ? ParityOdd : ParityEven)
                : string.IsNullOrWhiteSpace(course) ? MakeBrush("#C8D2E0")
                : TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        if (cancelled) textBlock.TextDecorations = TextDecorations.Strikethrough;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(textBlock);

        TextBlock? badge = null;
        if (parityPage)
        {
            badge = new TextBlock
            {
                Text = page == WeekType.Odd ? "单周专用" : "双周专用",
                FontSize = 10,
                Foreground = page == WeekType.Odd ? ParityOdd : ParityEven,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
                Visibility = weekSpecific ? Visibility.Visible : Visibility.Collapsed
            };
            stack.Children.Add(badge);
        }

        var grid = new Grid();
        grid.Children.Add(stack);

        var border = new Border
        {
            Background = active ? MakeBrush("#F3FFF8") : Brushes.White,
            BorderBrush = active ? Success : highlighted ? Accent : weekSpecific ? (page == WeekType.Odd ? ParityOdd : ParityEven) : Divider,
            BorderThickness = new Thickness(highlighted || active || weekSpecific ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(3),
            Padding = new Thickness(8),
            MinHeight = 54,
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = parityPage
                ? "点击编辑本页课程（回车保存，Esc 取消）；右键可“恢复为通用”"
                : "点击编辑课程名（回车保存，Esc 取消）"
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            BeginEdit(border, grid, textBlock, day, periodIndex, course, page, stack, badge);
        };

        // v1.4.0：右键菜单（单/双页签可把本格恢复为通用）
        var menu = new ContextMenu();
        var restore = new MenuItem { Header = "恢复为通用（删除本页专用）", IsEnabled = parityPage && weekSpecific };
        restore.Click += (_, _) => RestoreCellToAll(day, periodIndex);
        var clear = new MenuItem { Header = "清空本格" };
        clear.Click += (_, _) => ClearCell(day, periodIndex, page);
        menu.Items.Add(restore);
        menu.Items.Add(clear);
        border.ContextMenu = menu;
        return border;
    }

    // =========================================================================
    // 内联编辑课程
    // =========================================================================

    private void BeginEdit(
        Border cell, Grid grid, TextBlock textBlock, DayOfWeek day, int periodIndex, string course,
        WeekType page, StackPanel? stack = null, TextBlock? badge = null)
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
        if (stack is not null) stack.Visibility = Visibility.Collapsed;

        _editingTarget = (day, periodIndex);
        _editingBox = box;
        _editingTextBlock = textBlock;
        _editingStack = stack;
        _editingBadge = badge;
        _editingPage = page;
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
        var page = _editingPage;
        var stack = _editingStack;
        var badge = _editingBadge;
        _committing = true;
        try
        {
            _editingBox = null;
            _editingTarget = null;
            _editingTextBlock = null;
            _editingStack = null;
            _editingBadge = null;
            RemoveEditor(box);

            if (text != original)
            {
                _suppressRefresh = true;
                try
                {
                    SaveCell(target.Value.Day, target.Value.PeriodIndex, page, text);
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

            // 编辑结束后按当前页签规则刷新该格（专用/通用、标记、颜色）
            if (textBlock is not null)
            {
                var enabled = IsParityEnabled();
                var effectivePage = enabled ? page : WeekType.All;
                var display = ResolvePageCourse(_timetable.Current, target.Value.Day, target.Value.PeriodIndex,
                    effectivePage, enabled, out var specific);
                if (stack is not null) stack.Visibility = Visibility.Visible;
                textBlock.Text = string.IsNullOrEmpty(display) ? "—" : display;
                if (badge is not null) badge.Visibility = specific ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally
        {
            _committing = false;
        }
    }

    /// <summary>按页签保存：通用页写 All；单/双页写专用（与通用相同或清空则删除专用条目恢复通用）。</summary>
    private void SaveCell(DayOfWeek day, int periodIndex, WeekType page, string text)
    {
        var parityEnabled = IsParityEnabled();
        var effectivePage = parityEnabled ? page : WeekType.All;

        if (effectivePage == WeekType.All || _concreteTimetable is null)
        {
            _timetable.SetCourse(day, periodIndex, text);
            return;
        }

        var allCourse = (_timetable.Current.EntryAt(day, periodIndex, WeekType.All)?.Course ?? "").Trim();
        if (text.Length == 0 || string.Equals(text, allCourse, StringComparison.Ordinal))
        {
            _concreteTimetable.RestoreCellToAll(day, periodIndex);
            return;
        }
        _concreteTimetable.SetEntry(day, periodIndex, effectivePage, text);
    }

    /// <summary>清空某格（当前页签语义）。</summary>
    private void ClearCell(DayOfWeek day, int periodIndex, WeekType page)
    {
        try
        {
            SaveCell(day, periodIndex, IsParityEnabled() ? page : WeekType.All, "");
            RefreshAll();
        }
        catch (Exception ex)
        {
            Log.Error("清空课程格失败", ex);
        }
    }

    /// <summary>某格“恢复为通用”：删除单/双专用条目。</summary>
    private void RestoreCellToAll(DayOfWeek day, int periodIndex)
    {
        try
        {
            if (_concreteTimetable?.RestoreCellToAll(day, periodIndex) == true) RefreshAll();
        }
        catch (Exception ex)
        {
            Log.Error("恢复为通用失败", ex);
        }
    }

    private void CancelEdit()
    {
        var box = _editingBox;
        var textBlock = _editingTextBlock;
        var stack = _editingStack;
        _committing = true;
        try
        {
            _editingBox = null;
            _editingTarget = null;
            _editingTextBlock = null;
            _editingStack = null;
            _editingBadge = null;
            if (box is not null) RemoveEditor(box);
            if (stack is not null) stack.Visibility = Visibility.Visible;
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
    // v1.4.0：单双周页签 / 工具
    // =========================================================================

    private bool IsParityEnabled()
    {
        try { return _weekParity.Enabled; }
        catch (Exception ex)
        {
            Log.Error("读取单双周开关失败", ex);
            return false;
        }
    }

    /// <summary>某页签某格显示的课程（未启用单双周时按 v1.3 逻辑）。</summary>
    private static string ResolvePageCourse(
        WeekTimetable timetable, DayOfWeek day, int periodIndex, WeekType page, bool parityEnabled, out bool specific)
    {
        specific = false;
        if (!parityEnabled)
            return timetable.CourseAt(day, periodIndex) ?? "";

        if (page == WeekType.All)
            return timetable.EntryAt(day, periodIndex, WeekType.All)?.Course ?? "";

        specific = timetable.EntryAt(day, periodIndex, page) is not null;
        return TimetableParityEditor.DisplayCourse(timetable, day, periodIndex, page);
    }

    private void UpdateParityBar(WeekTimetable timetable)
    {
        var enabled = IsParityEnabled();
        ParityBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled)
        {
            _parityPage = WeekType.All;
            WeekParityInfo.Text = "";
            return;
        }

        // 页签按钮状态
        BtnPageAll.Style = (Style)FindResource(_parityPage == WeekType.All ? "Button.Form" : "Button.FormGhost");
        BtnPageOdd.Style = (Style)FindResource(_parityPage == WeekType.Odd ? "Button.Form" : "Button.FormGhost");
        BtnPageEven.Style = (Style)FindResource(_parityPage == WeekType.Even ? "Button.Form" : "Button.FormGhost");

        var currentPage = _parityPage;
        BtnCopyAll.IsEnabled = currentPage != WeekType.All;
        BtnClearPage.IsEnabled = currentPage != WeekType.All;
        BtnCopyAll.Content = currentPage == WeekType.Odd ? "把通用复制到单周" : currentPage == WeekType.Even ? "把通用复制到双周" : "把通用复制到本页";
        BtnClearPage.Content = currentPage == WeekType.Odd ? "清空单周专用" : currentPage == WeekType.Even ? "清空双周专用" : "清空本页专用";
        BtnRestoreAll.IsEnabled = true;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var weekNo = _weekParity.GetWeekNumber(today);
        var parity = _weekParity.CurrentParity;
        var parityText = parity == WeekParity.Odd ? "单周" : "双周";
        var overrideText = _weekParity.IsCurrentWeekOverridden ? "（手动覆盖）" : "";
        var weekText = weekNo > 0
            ? $"本周：第 {weekNo} 周 · {parityText}{overrideText}"
            : $"开学前（第 {weekNo} 周）· {parityText}";
        var (allCount, oddCount, evenCount) = TimetableParityEditor.CountByWeekType(timetable);
        ParityHint.Text = $"{weekText} · 当前页：{PageName(_parityPage)} · 通用 {allCount} / 单周 {oddCount} / 双周 {evenCount}";
        ParityHint.Foreground = currentPage == WeekType.All ? TextSecondary
            : currentPage == WeekType.Odd ? ParityOdd : ParityEven;

        // 同格重复检测（数据修复在服务层，这里只提示；按 星期+节次+周类型 判重）
        var duplicates = timetable.Days
            .SelectMany(kv => kv.Value.Select(entry => (Day: kv.Key, Entry: entry)))
            .GroupBy(x => (x.Day, x.Entry.PeriodIndex, x.Entry.Weeks))
            .Count(g => g.Count() > 1);
        if (duplicates > 0)
            ParityHint.Text += $" · ⚠ 检测到 {duplicates} 处重复条目（已按最后一条显示，可用“恢复为通用”清理）";

        WeekParityInfo.Text = weekText;
        WeekParityInfo.Visibility = Visibility.Visible;
    }

    private static string PageName(WeekType page) => page switch
    {
        WeekType.Odd => "单周",
        WeekType.Even => "双周",
        _ => "通用"
    };

    private void OnParityPageAllClick(object sender, RoutedEventArgs e) => SwitchParityPage(WeekType.All);

    private void OnParityPageOddClick(object sender, RoutedEventArgs e) => SwitchParityPage(WeekType.Odd);

    private void OnParityPageEvenClick(object sender, RoutedEventArgs e) => SwitchParityPage(WeekType.Even);

    private void SwitchParityPage(WeekType page)
    {
        if (_parityPage == page) return;
        CommitEdit();
        _parityPage = page;
        RefreshAll();
    }

    private void OnCopyAllToPageClick(object sender, RoutedEventArgs e)
    {
        if (_parityPage == WeekType.All) return;
        try
        {
            var count = _concreteTimetable?.CopyAllTo(_parityPage) ?? 0;
            RefreshAll();
            ParityHint.Text = $"已把通用课程复制到{PageName(_parityPage)}：{count} 格";
            ParityHint.Foreground = Success;
            Log.Info($"单双周：复制通用到{PageName(_parityPage)}，{count} 格");
        }
        catch (Exception ex)
        {
            Log.Error("复制通用课程失败", ex);
        }
    }

    private void OnClearPageClick(object sender, RoutedEventArgs e)
    {
        if (_parityPage == WeekType.All) return;
        try
        {
            var removed = _concreteTimetable?.ClearWeek(_parityPage) ?? 0;
            RefreshAll();
            ParityHint.Text = $"已清空{PageName(_parityPage)}专用课程：{removed} 格（这些格子恢复显示通用课程）";
            ParityHint.Foreground = Success;
            Log.Info($"单双周：清空{PageName(_parityPage)}专用条目 {removed} 格");
        }
        catch (Exception ex)
        {
            Log.Error("清空单双周专用项失败", ex);
        }
    }

    private void OnRestoreAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var removed = _concreteTimetable?.RestoreAllToAll() ?? 0;
            RefreshAll();
            ParityHint.Text = removed > 0
                ? $"已恢复为通用：删除单/双专用条目 {removed} 格"
                : "当前没有单/双专用条目，全部使用通用课程";
            ParityHint.Foreground = Success;
            Log.Info($"单双周：恢复为通用，删除专用条目 {removed} 格");
        }
        catch (Exception ex)
        {
            Log.Error("恢复为通用失败", ex);
        }
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
        var paritySuffix = "";
        if (IsParityEnabled())
        {
            var weekNo = _weekParity.GetWeekNumber(DateOnly.FromDateTime(now));
            var parityText = _weekParity.CurrentParity == WeekParity.Odd ? "单周" : "双周";
            paritySuffix = weekNo > 0
                ? $"  |  第 {weekNo} 周 · {parityText}{(_weekParity.IsCurrentWeekOverridden ? "（手动覆盖）" : "")}"
                : $"  |  开学前 · {parityText}";
        }
        headerStack.Children.Add(new TextBlock
        {
            Text = $"今天 · {DayName(now.DayOfWeek)}  {now:MM 月 dd 日}{paritySuffix}",
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
