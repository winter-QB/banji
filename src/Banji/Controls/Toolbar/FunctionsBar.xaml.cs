using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Controls.Toolbar;

/// <summary>功能区布局模式。</summary>
public enum FunctionsBarLayoutMode
{
    /// <summary>按可用空间自动选择（宽则一行，窄则两列多行，更窄则竖排一列）。</summary>
    Auto = 0,
    OneRow = 1,
    /// <summary>两列多行（5 个功能区按钮时自动排成 2 列 3 行）。</summary>
    TwoByTwo = 2,
    Vertical = 3
}

/// <summary>
/// 宠物左下角展开的功能区（开发文档第 4 章 + 第二轮新增“分组”）：
/// 抽签（右侧小 + 号选择花名册）、倒计时、正计时、清醒一下、分组。
/// <para>• 计时进行中，对应的倒计时/正计时按钮变成“取消计时”并使用强调样式，点击即取消；</para>
/// <para>• 按钮四态颜色/透明度/边框/圆角/阴影/字号全部来自 <c>Settings.Current.Buttons</c>，改设置实时生效；</para>
/// <para>• 布局随宿主给的宽度自适应（默认两列多行，宽则一行，窄则一列），随宠物整体缩放不破版。</para>
/// </summary>
public partial class FunctionsBar : UserControl
{
    private const double Gap = 4;
    private const double InnerGap = 2;
    private const double OuterMargin = 4; // 与 XAML 里 Host.Margin 一致
    private const int TwoColumnRowCount = 3; // 5 个按钮按两列排 = 3 行

    private ToolButton _pickButton = null!;
    private ToolButton _plusButton = null!;
    private ToolButton _countdownButton = null!;
    private ToolButton _stopwatchButton = null!;
    private ToolButton _stingButton = null!;
    private ToolButton _groupingButton = null!;

    private ButtonStyleSettings _style = new();
    private FunctionsBarLayoutMode _mode = (FunctionsBarLayoutMode)(-1);
    private bool _subscribed;

    /// <summary>强制布局模式；默认 Auto 自适应。</summary>
    public FunctionsBarLayoutMode PreferredLayout { get; set; } = FunctionsBarLayoutMode.Auto;

    public FunctionsBar()
    {
        InitializeComponent();
        BuildButtons();
        ApplyStyle();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => EnsureLayout();
    }

    // =========================================================================
    // 构建 / 交互
    // =========================================================================

    private void BuildButtons()
    {
        _pickButton = CreateButton("抽签", OnPickClick);
        _plusButton = CreateButton("+", OnPickPlusClick);
        _countdownButton = CreateButton("倒计时", OnCountdownClick);
        _stopwatchButton = CreateButton("正计时", OnStopwatchClick);
        _stingButton = CreateButton("清醒一下", OnStingClick);
        _groupingButton = CreateButton("分组", OnGroupingClick);
        _pickButton.ToolTip = "抽签";
        _plusButton.ToolTip = "选择花名册";
        _countdownButton.ToolTip = "倒计时";
        _stopwatchButton.ToolTip = "正计时";
        _stingButton.ToolTip = "清醒一下";
        _groupingButton.ToolTip = "随机分组";
    }

    private static ToolButton CreateButton(string text, RoutedEventHandler onClick)
    {
        var button = new ToolButton
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        button.Click += onClick;
        return button;
    }

    private void OnPickClick(object sender, RoutedEventArgs e) => Guard("抽签", () =>
    {
        var svc = AppServices.Current;
        if (svc.Rosters.Rosters.Count == 0 || svc.Rosters.Active is null)
        {
            MessageBox.Show("还没有花名册哦，先点击右边的 + 号导入或手动录入一份吧。", "班机",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        svc.Picker.Pick();
    });

    private void OnPickPlusClick(object sender, RoutedEventArgs e) => Guard("打开花名册选择", () =>
        AppServices.Current.ShowRosterPicker());

    private void OnCountdownClick(object sender, RoutedEventArgs e) => Guard("倒计时", () =>
    {
        var svc = AppServices.Current;
        var current = svc.Timers.Current;
        if (current is { Status: TimerStatus.Running, Kind: TimerKind.Countdown })
        {
            svc.Timers.Cancel(); // 计时中：取消计时
            return;
        }
        svc.ShowTimerDialog(TimerKind.Countdown);
    });

    private void OnStopwatchClick(object sender, RoutedEventArgs e) => Guard("正计时", () =>
    {
        var svc = AppServices.Current;
        var current = svc.Timers.Current;
        if (current is { Status: TimerStatus.Running, Kind: TimerKind.Stopwatch })
        {
            svc.Timers.Cancel();
            return;
        }
        svc.ShowTimerDialog(TimerKind.Stopwatch);
    });

    private void OnStingClick(object sender, RoutedEventArgs e) => Guard("清醒一下", () =>
        AppServices.Current.Audio.PlayRandomSting());

    private void OnGroupingClick(object sender, RoutedEventArgs e) => Guard("随机分组", () =>
        AppServices.Current.ShowGroupingWindow());

    private static void Guard(string action, Action body)
    {
        try
        {
            if (!AppServices.IsInitialized) return;
            body();
        }
        catch (Exception ex)
        {
            Log.Error($"功能区「{action}」执行失败", ex);
            MessageBox.Show($"{action}失败：{ex.Message}", "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // =========================================================================
    // 样式 / 计时状态
    // =========================================================================

    private void ApplyStyle()
    {
        _style = ReadStyle();
        ToolButtonStyler.Apply(_pickButton, _style);
        ToolButtonStyler.Apply(_plusButton, _style);
        ToolButtonStyler.Apply(_countdownButton, _style);
        ToolButtonStyler.Apply(_stopwatchButton, _style);
        ToolButtonStyler.Apply(_stingButton, _style);
        ToolButtonStyler.Apply(_groupingButton, _style);

        UpdateSizes();
        UpdateTimerLabels();
        EnsureLayout();
    }

    private static ButtonStyleSettings ReadStyle()
    {
        if (AppServices.IsInitialized)
        {
            try { return AppServices.Current.Settings.Current.Buttons; }
            catch { /* 回退默认 */ }
        }
        return new ButtonStyleSettings();
    }

    /// <summary>根据字号计算各按钮尺寸（4 字宽度容纳“取消计时”，避免计时中跳动）。</summary>
    private void UpdateSizes()
    {
        var fs = Math.Clamp(_style.FontSize, 8, 40);
        var height = Math.Round(fs + 12);

        _pickButton.Width = Math.Round(2 * fs + 12);   // 抽签
        _plusButton.Width = Math.Round(fs + 10);       // +
        var timerWidth = Math.Round(4 * fs + 12);      // 倒计时 / 取消计时
        _countdownButton.Width = timerWidth;
        _stopwatchButton.Width = timerWidth;
        _stingButton.Width = Math.Round(4 * fs + 12);  // 清醒一下
        _groupingButton.Width = Math.Round(2 * fs + 12); // 分组

        foreach (var button in AllButtons())
            button.Height = height;
    }

    private IEnumerable<ToolButton> AllButtons()
    {
        yield return _pickButton;
        yield return _plusButton;
        yield return _countdownButton;
        yield return _stopwatchButton;
        yield return _stingButton;
        yield return _groupingButton;
    }

    /// <summary>计时进行中“倒计时/正计时”按钮变“取消计时”强调样式。</summary>
    private void UpdateTimerLabels()
    {
        var countdownActive = false;
        var stopwatchActive = false;
        if (AppServices.IsInitialized)
        {
            try
            {
                var current = AppServices.Current.Timers.Current;
                if (current is { Status: TimerStatus.Running })
                {
                    countdownActive = current.Kind == TimerKind.Countdown;
                    stopwatchActive = current.Kind == TimerKind.Stopwatch;
                }
            }
            catch { /* ignore */ }
        }

        SetTimerButton(_countdownButton, countdownActive, countdownActive ? "取消计时" : "倒计时",
            countdownActive ? "取消计时" : "倒计时");
        SetTimerButton(_stopwatchButton, stopwatchActive, stopwatchActive ? "取消计时" : "正计时",
            stopwatchActive ? "取消计时" : "正计时");
    }

    private static void SetTimerButton(ToolButton button, bool accent, string text, string tooltip)
    {
        if (!Equals(button.Content, text)) button.Content = text;
        if (button.IsAccent != accent) button.IsAccent = accent; // 强调色 #FF3030
        button.ToolTip = tooltip;
    }

    // =========================================================================
    // 自适应布局
    // =========================================================================

    private double ContentWidth => Math.Max(0, ActualWidth - OuterMargin * 2);
    private double ContentHeight => Math.Max(0, ActualHeight - OuterMargin * 2);

    private double TwoColumnWidth
    {
        get
        {
            var col0 = Math.Max(
                Math.Max(_pickButton.Width + InnerGap + _plusButton.Width, _stopwatchButton.Width),
                _groupingButton.Width);
            var col1 = Math.Max(_countdownButton.Width, _stingButton.Width);
            return col0 + Gap + col1;
        }
    }

    private double OneRowWidth =>
        _pickButton.Width + InnerGap + _plusButton.Width + Gap +
        _countdownButton.Width + Gap + _stopwatchButton.Width + Gap + _stingButton.Width +
        Gap + _groupingButton.Width;

    private double TwoColumnHeight => _pickButton.Height * TwoColumnRowCount + Gap * (TwoColumnRowCount - 1);

    private FunctionsBarLayoutMode DecideMode()
    {
        if (PreferredLayout != FunctionsBarLayoutMode.Auto) return PreferredLayout;
        // 首次测量（ActualWidth 为 0）时用紧凑的两列多行，避免把宠物窗先撑宽。
        if (ActualWidth <= 0.5 || ActualHeight <= 0.5) return FunctionsBarLayoutMode.TwoByTwo;

        var width = ContentWidth;
        var height = ContentHeight;
        if (width + 0.6 >= OneRowWidth && height + 0.6 >= _pickButton.Height)
            return FunctionsBarLayoutMode.OneRow;
        if (width + 0.6 >= TwoColumnWidth && height + 0.6 >= TwoColumnHeight)
            return FunctionsBarLayoutMode.TwoByTwo;
        return FunctionsBarLayoutMode.Vertical;
    }

    private void EnsureLayout()
    {
        var mode = DecideMode();
        if (mode != _mode) RebuildLayout(mode);
    }

    private void RebuildLayout(FunctionsBarLayoutMode mode)
    {
        _mode = mode;

        // 先把按钮从旧容器里摘出来，再清空 Host
        DetachAllButtons();
        Host.Children.Clear();
        Host.ColumnDefinitions.Clear();
        Host.RowDefinitions.Clear();

        switch (mode)
        {
            case FunctionsBarLayoutMode.OneRow:
                BuildOneRow();
                break;
            case FunctionsBarLayoutMode.Vertical:
                BuildVertical();
                break;
            default:
                BuildTwoByTwo();
                break;
        }

        Host.InvalidateMeasure();
        InvalidateMeasure();
    }

    private void DetachAllButtons()
    {
        foreach (var button in AllButtons())
        {
            if (button.Parent is Panel panel) panel.Children.Remove(button);
            else if (button.Parent is Grid grid) grid.Children.Remove(button);
        }
        foreach (var child in Host.Children.OfType<Panel>().ToArray())
            child.Children.Clear();
        Host.Children.Clear();
    }

    private void BuildOneRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _pickButton.Margin = new Thickness(0, 0, InnerGap, 0);
        _plusButton.Margin = new Thickness(0, 0, Gap, 0);
        _countdownButton.Margin = new Thickness(0, 0, Gap, 0);
        _stopwatchButton.Margin = new Thickness(0, 0, Gap, 0);
        _stingButton.Margin = new Thickness(0, 0, Gap, 0);
        _groupingButton.Margin = new Thickness(0);
        row.Children.Add(_pickButton);
        row.Children.Add(_plusButton);
        row.Children.Add(_countdownButton);
        row.Children.Add(_stopwatchButton);
        row.Children.Add(_stingButton);
        row.Children.Add(_groupingButton);
        Host.Children.Add(row);
    }

    /// <summary>两列多行（当前 5 个按钮 → 3 行：抽签+/ / 倒计时；正计时 / 清醒一下；分组 / 空）。</summary>
    private void BuildTwoByTwo()
    {
        Host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < TwoColumnRowCount; i++)
            Host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var pickGroup = new StackPanel { Orientation = Orientation.Horizontal };
        _pickButton.Margin = new Thickness(0, 0, InnerGap, 0);
        _plusButton.Margin = new Thickness(0);
        pickGroup.Children.Add(_pickButton);
        pickGroup.Children.Add(_plusButton);
        Place(pickGroup, 0, 0);

        _countdownButton.Margin = new Thickness(Gap, 0, 0, 0);
        Place(_countdownButton, 0, 1);

        _stopwatchButton.Margin = new Thickness(0, Gap, 0, 0);
        Place(_stopwatchButton, 1, 0);

        _stingButton.Margin = new Thickness(Gap, Gap, 0, 0);
        Place(_stingButton, 1, 1);

        _groupingButton.Margin = new Thickness(0, Gap, 0, 0);
        Place(_groupingButton, 2, 0);
    }

    private void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Host.Children.Add(element);
    }

    private void BuildVertical()
    {
        var column = new StackPanel { Orientation = Orientation.Vertical };
        _pickButton.Margin = new Thickness(0, 0, 0, InnerGap);
        _countdownButton.Margin = new Thickness(0, 0, 0, Gap);
        _stopwatchButton.Margin = new Thickness(0, 0, 0, Gap);
        _stingButton.Margin = new Thickness(0, 0, 0, Gap);
        _groupingButton.Margin = new Thickness(0);

        var pickGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, Gap)
        };
        _pickButton.Margin = new Thickness(0, 0, InnerGap, 0);
        _plusButton.Margin = new Thickness(0);
        pickGroup.Children.Add(_pickButton);
        pickGroup.Children.Add(_plusButton);

        column.Children.Add(pickGroup);
        column.Children.Add(_countdownButton);
        column.Children.Add(_stopwatchButton);
        column.Children.Add(_stingButton);
        column.Children.Add(_groupingButton);
        Host.Children.Add(column);
    }

    // =========================================================================
    // 生命周期 / 设置 & 计时订阅
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyStyle();
        if (_subscribed || !AppServices.IsInitialized) return;
        _subscribed = true;
        var svc = AppServices.Current;
        svc.Settings.Changed += OnSettingsChanged;
        svc.Timers.StateChanged += OnTimerChanged;
        svc.Timers.Expired += OnTimerChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        try
        {
            var svc = AppServices.Current;
            svc.Settings.Changed -= OnSettingsChanged;
            svc.Timers.StateChanged -= OnTimerChanged;
            svc.Timers.Expired -= OnTimerChanged;
        }
        catch { /* 退出过程中可能已不可用 */ }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => AppServices.RunOnUi(() =>
    {
        try
        {
            ApplyStyle();
        }
        catch (Exception ex)
        {
            Log.Error("功能区按钮样式刷新失败", ex);
        }
    });

    private void OnTimerChanged(object? sender, EventArgs e) => AppServices.RunOnUi(() =>
    {
        try
        {
            UpdateTimerLabels();
        }
        catch (Exception ex)
        {
            Log.Error("功能区计时状态刷新失败", ex);
        }
    });

    /// <summary>供外部（如宠物窗缩放/换肤后）主动刷新。</summary>
    public void RefreshStyle() => AppServices.RunOnUi(() =>
    {
        ApplyStyle();
        UpdateLayout();
    });
}


