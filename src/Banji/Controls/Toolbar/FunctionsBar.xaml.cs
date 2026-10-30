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
    /// <summary>按可用空间自动选择（宽则一行，窄则 2×2，更窄则竖排一列）。</summary>
    Auto = 0,
    OneRow = 1,
    TwoByTwo = 2,
    Vertical = 3
}

/// <summary>
/// 宠物左下角展开的功能区（开发文档第 4 章）：
/// 抽签（右侧小 + 号选择花名册）、倒计时、正计时、清醒一下。
/// <para>• 计时进行中，对应的倒计时/正计时按钮变成“取消计时”并使用强调样式，点击即取消；</para>
/// <para>• 按钮四态颜色/透明度/边框/圆角/阴影/字号全部来自 <c>Settings.Current.Buttons</c>，改设置实时生效；</para>
/// <para>• 布局随宿主给的宽度自适应（2×2 默认，宽则一行，窄则一列），随宠物整体缩放不破版。</para>
/// </summary>
public partial class FunctionsBar : UserControl
{
    private const double Gap = 4;
    private const double InnerGap = 2;
    private const double OuterMargin = 4; // 与 XAML 里 Host.Margin 一致

    private ToolButton _pickButton = null!;
    private ToolButton _plusButton = null!;
    private ToolButton _countdownButton = null!;
    private ToolButton _stopwatchButton = null!;
    private ToolButton _stingButton = null!;

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
        _pickButton.ToolTip = "抽签";
        _plusButton.ToolTip = "选择花名册";
        _countdownButton.ToolTip = "倒计时";
        _stopwatchButton.ToolTip = "正计时";
        _stingButton.ToolTip = "清醒一下";
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

        foreach (var button in new[] { _pickButton, _plusButton, _countdownButton, _stopwatchButton, _stingButton })
            button.Height = height;
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
            var col0 = Math.Max(_pickButton.Width + InnerGap + _plusButton.Width, _countdownButton.Width);
            var col1 = Math.Max(_countdownButton.Width, _stingButton.Width);
            return col0 + Gap + col1;
        }
    }

    private double OneRowWidth =>
        _pickButton.Width + InnerGap + _plusButton.Width + Gap +
        _countdownButton.Width + Gap + _stopwatchButton.Width + Gap + _stingButton.Width;

    private double TwoRowHeight => _pickButton.Height * 2 + Gap;

    private FunctionsBarLayoutMode DecideMode()
    {
        if (PreferredLayout != FunctionsBarLayoutMode.Auto) return PreferredLayout;
        // 首次测量（ActualWidth 为 0）时用紧凑的 2×2，避免把宠物窗先撑宽。
        if (ActualWidth <= 0.5 || ActualHeight <= 0.5) return FunctionsBarLayoutMode.TwoByTwo;

        var width = ContentWidth;
        var height = ContentHeight;
        if (width + 0.6 >= OneRowWidth && height + 0.6 >= _pickButton.Height)
            return FunctionsBarLayoutMode.OneRow;
        if (width + 0.6 >= TwoColumnWidth && height + 0.6 >= TwoRowHeight)
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
        foreach (var button in new[] { _pickButton, _plusButton, _countdownButton, _stopwatchButton, _stingButton })
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
        _stingButton.Margin = new Thickness(0);
        row.Children.Add(_pickButton);
        row.Children.Add(_plusButton);
        row.Children.Add(_countdownButton);
        row.Children.Add(_stopwatchButton);
        row.Children.Add(_stingButton);
        Host.Children.Add(row);
    }

    private void BuildTwoByTwo()
    {
        Host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var pickGroup = new StackPanel { Orientation = Orientation.Horizontal };
        _pickButton.Margin = new Thickness(0, 0, InnerGap, 0);
        _plusButton.Margin = new Thickness(0);
        pickGroup.Children.Add(_pickButton);
        pickGroup.Children.Add(_plusButton);
        Grid.SetRow(pickGroup, 0);
        Grid.SetColumn(pickGroup, 0);
        Host.Children.Add(pickGroup);

        _countdownButton.Margin = new Thickness(Gap, 0, 0, 0);
        Grid.SetRow(_countdownButton, 0);
        Grid.SetColumn(_countdownButton, 1);
        Host.Children.Add(_countdownButton);

        _stopwatchButton.Margin = new Thickness(0, Gap, 0, 0);
        Grid.SetRow(_stopwatchButton, 1);
        Grid.SetColumn(_stopwatchButton, 0);
        Host.Children.Add(_stopwatchButton);

        _stingButton.Margin = new Thickness(Gap, Gap, 0, 0);
        Grid.SetRow(_stingButton, 1);
        Grid.SetColumn(_stingButton, 1);
        Host.Children.Add(_stingButton);
    }

    private void BuildVertical()
    {
        var column = new StackPanel { Orientation = Orientation.Vertical };
        _pickButton.Margin = new Thickness(0, 0, 0, InnerGap);
        _countdownButton.Margin = new Thickness(0, 0, 0, Gap);
        _stopwatchButton.Margin = new Thickness(0, 0, 0, Gap);
        _stingButton.Margin = new Thickness(0);

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
