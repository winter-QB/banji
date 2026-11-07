using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using CoreDuty = Banji.Core.Duty;

namespace Banji.Views;

/// <summary>首次配置向导中可选的预设包。</summary>
public enum DutyPresetKind
{
    /// <summary>不使用预设（完全空白，稍后自己配置）。</summary>
    Empty = 0,
    /// <summary>值周包。</summary>
    Weekly = 1,
    /// <summary>卫生值日包。</summary>
    Cleaning = 2,
    /// <summary>班委包。</summary>
    Committee = 3,
    /// <summary>课代表包。</summary>
    SubjectRep = 4,
    /// <summary>不使用预设（不生成岗位，仅保留班委/课代表选项）。</summary>
    None = 5
}

/// <summary>
/// v1.4.0 值周/值日预设生成器（纯函数，便于单测）。
/// 预设只生成可编辑的配置，生成后全部可在窗口里修改。
/// </summary>
public static class DutyPresetFactory
{
    /// <summary>把日期对齐到所在周的周一（周一=0 … 周日=6）。</summary>
    public static DateOnly MondayOf(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }

    /// <summary>把窗口用的预设枚举映射到 core-roster 的 <see cref="CoreDuty.DutyPresetKind"/>。</summary>
    private static IEnumerable<CoreDuty.DutyPresetKind> MapKinds(DutyPresetKind kind)
    {
        switch (kind)
        {
            case DutyPresetKind.Weekly:
                yield return CoreDuty.DutyPresetKind.Weekly;
                break;
            case DutyPresetKind.Cleaning:
                yield return CoreDuty.DutyPresetKind.DailyCleanup;
                break;
            case DutyPresetKind.Committee:
                yield return CoreDuty.DutyPresetKind.Committee;
                break;
            case DutyPresetKind.SubjectRep:
                yield return CoreDuty.DutyPresetKind.SubjectReps;
                break;
            case DutyPresetKind.Empty:
                yield return CoreDuty.DutyPresetKind.Empty;
                break;
            default:
                break; // None：不生成任何岗位
        }
    }

    public static string PresetName(DutyPresetKind kind) => kind switch
    {
        DutyPresetKind.Weekly => "值周包（擦黑板/讲台整理/扫地/拖地/倒垃圾/门窗）",
        DutyPresetKind.Cleaning => "卫生值日包（擦黑板/讲台/扫地/拖地/倒垃圾/摆桌椅/门窗/卫生角）",
        DutyPresetKind.Committee => "班委包（12 个职位）",
        DutyPresetKind.SubjectRep => "课代表包（9 个科目）",
        _ => "空模板（不预置任何岗位）"
    };

    /// <summary>按预设生成岗位列表（直接复用 core-roster 的 DutyPresets，生成后可编辑）。</summary>
    public static List<DutyTask> BuildTasks(DutyPresetKind kind)
    {
        var kinds = MapKinds(kind).ToList();
        return kinds.Count == 0
            ? new List<DutyTask>()
            : CoreDuty.DutyPresets.CreateConfig(kinds.ToArray()).Tasks;
    }

    /// <summary>班委预设（12 个职位，各 1 人，成员留空待选）。</summary>
    public static List<CommitteeRole> BuildCommitteePreset()
        => CoreDuty.DutyPresets.CreateCommitteeRoles();

    /// <summary>课代表预设（9 个科目，各 1 人，成员留空待选）。</summary>
    public static List<SubjectRepRole> BuildSubjectRepPreset()
        => CoreDuty.DutyPresets.CreateSubjectRepRoles();

    /// <summary>
    /// 生成完整配置：岗位 + 轮值组（按学号顺序平均分组并自动分配任务）+ 可选班委/课代表预设。
    /// 岗位/班委/课代表生成复用 core-roster 的 <see cref="CoreDuty.DutyPresets"/>（多包合并去重）。
    /// </summary>
    public static DutyConfig BuildConfig(
        DutyPresetKind preset,
        bool includeCommitteePack,
        bool includeSubjectRepPack,
        DutyMode mode,
        int groupCount,
        DutyAssignmentMode assignmentMode,
        IReadOnlyList<Student>? students,
        DateOnly? anchorMonday)
    {
        var kinds = MapKinds(preset).ToList();
        if (includeCommitteePack) kinds.Add(CoreDuty.DutyPresetKind.Committee);
        if (includeSubjectRepPack) kinds.Add(CoreDuty.DutyPresetKind.SubjectReps);
        if (kinds.Count == 0) kinds.Add(CoreDuty.DutyPresetKind.Empty);

        var baseConfig = CoreDuty.DutyPresets.CreateConfig(kinds.Distinct().ToArray());
        var config = new DutyConfig
        {
            Mode = mode,
            AssignmentMode = assignmentMode,
            AnchorMonday = anchorMonday is null ? null : MondayOf(anchorMonday.Value),
            Tasks = baseConfig.Tasks,
            Committee = baseConfig.Committee,
            SubjectReps = baseConfig.SubjectReps
        };

        var list = (students ?? Array.Empty<Student>())
            .Where(s => s is not null && s.IsValid)
            .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (config.Tasks.Count == 0 || list.Count == 0)
        {
            config.Order = new List<string>();
            return config;
        }

        var groups = CoreDuty.DutyPresets.CreateGroups(list, Math.Clamp(groupCount, 1, 20));
        config.Groups = groups;
        config.Order = groups.Select(g => g.Id).ToList();
        CoreDuty.DutyPresets.AutoAssignAllGroups(config);
        return config;
    }

    /// <summary>按“任务顺序 → 组员顺序”循环分配本组任务（允许一人多岗；直接复用 core-roster 实现）。</summary>
    public static List<DutyAssignment> AutoAssign(IReadOnlyList<string> memberIds, IReadOnlyList<DutyTask> tasks)
        => CoreDuty.DutyPresets.BuildAutoAssignments(memberIds, tasks);
}

/// <summary>
/// v1.4.0 值周/值日首次配置向导（强制 4 步）：
/// 1) 开学第一周（必填，不可跳过）→ 2) 值周/值日模式 → 3) 预设选择 → 4) 分组与任务分配。
/// 完成后写入 DutyConfig 并把 Settings.DutySetupCompleted 置 true；关闭向导视为未完成。
/// </summary>
public partial class DutySetupWizard : Window
{
    private const int StepCount = 4;

    private int _step = 1;
    private DateOnly? _semesterMonday;
    private DutyMode _mode = DutyMode.Weekly;
    private DutyPresetKind _preset = DutyPresetKind.Weekly;
    private bool _includeCommitteePack;
    private bool _includeSubjectRepPack;
    private int _groupCount = 4;
    private DutyAssignmentMode _assignmentMode = DutyAssignmentMode.FixedPerGroup;

    private DatePicker? _datePicker;
    private TextBlock? _dateHintText;
    private TextBlock? _presetSummaryText;

    /// <summary>向导是否正常完成并保存。</summary>
    public bool Completed { get; private set; }

    public DutySetupWizard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var existing = AppServices.IsInitialized ? AppServices.Current.Settings.Current.SemesterFirstWeekMonday : null;
            _semesterMonday = existing is null
                ? DutyPresetFactory.MondayOf(DateOnly.FromDateTime(DateTime.Now))
                : DutyPresetFactory.MondayOf(existing.Value);
            if (AppServices.IsInitialized)
            {
                var config = AppServices.Current.Duty.Config;
                if (config.Groups.Count > 0) _groupCount = Math.Clamp(config.Groups.Count, 1, 20);
                _assignmentMode = config.AssignmentMode;
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取值日配置失败，使用默认值", ex);
            _semesterMonday ??= DutyPresetFactory.MondayOf(DateOnly.FromDateTime(DateTime.Now));
        }
        BuildStep();
    }

    // =========================================================================
    // 步骤切换
    // =========================================================================

    private void BuildStep()
    {
        _step = Math.Clamp(_step, 1, StepCount);
        UpdateChrome();
        ContentHost.Children.Clear();
        switch (_step)
        {
            case 1: BuildStep1(); break;
            case 2: BuildStep2(); break;
            case 3: BuildStep3(); break;
            default: BuildStep4(); break;
        }
    }

    private void UpdateChrome()
    {
        var dots = new[] { Dot1, Dot2, Dot3, Dot4 };
        var accent = TryFindResource("Brush.Brand") as Brush ?? Brushes.RoyalBlue;
        var idle = new SolidColorBrush(Color.FromRgb(0xE1, 0xE7, 0xF0));
        for (var i = 0; i < dots.Length; i++)
            dots[i].Background = i < _step ? accent : idle;

        StepHintText.Text = _step switch
        {
            1 => "第 1 / 4 步 · 开学第一周（必填，不能跳过）",
            2 => "第 2 / 4 步 · 选择值周制还是值日制",
            3 => "第 3 / 4 步 · 选择预设包（弹窗式选择，生成后仍可编辑）",
            _ => "第 4 / 4 步 · 分组与任务分配"
        };

        PrevButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _step < StepCount ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _step == StepCount ? Visibility.Visible : Visibility.Collapsed;
        if (_step == 1)
        {
            NextButton.IsEnabled = _semesterMonday is not null;
            NextButton.Content = "下一步";
        }
        else
        {
            NextButton.Content = "下一步";
        }
    }

    // =========================================================================
    // 第 1 步：开学第一周（必填）
    // =========================================================================

    private void BuildStep1()
    {
        ContentHost.Children.Add(Heading("开学第一周是哪一周？", "必填"));
        ContentHost.Children.Add(Body(
            "请选择本学期开学第一周的周一。这个日期会同时作为：\n" +
            "• 值周轮换的起点（第 1 周第 1 组）\n" +
            "• 单双周课表的锚点周（默认该周为单周）\n\n" +
            "此步骤是首次配置的必填项，不能跳过。之后可在“设置 → 值日与轮值 / 课堂”里修改。"));

        _datePicker = new DatePicker
        {
            SelectedDate = _semesterMonday?.ToDateTime(TimeOnly.MinValue),
            Width = 220,
            FontFamily = FindResource("Font.Regular") as FontFamily,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _datePicker.SelectedDateChanged += (_, _) =>
        {
            _semesterMonday = _datePicker?.SelectedDate is { } date
                ? DutyPresetFactory.MondayOf(DateOnly.FromDateTime(date))
                : null;
            UpdateDateHint();
            NextButton.IsEnabled = _semesterMonday is not null;
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(_datePicker);
        var quick = new Button
        {
            Content = "以本周周一为开学第一周",
            Style = FindResource("Button.FormGhost") as Style,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(12, 0, 0, 0)
        };
        quick.Click += (_, _) =>
        {
            _semesterMonday = DutyPresetFactory.MondayOf(DateOnly.FromDateTime(DateTime.Now));
            if (_datePicker is not null) _datePicker.SelectedDate = _semesterMonday.Value.ToDateTime(TimeOnly.MinValue);
            UpdateDateHint();
            NextButton.IsEnabled = true;
        };
        row.Children.Add(quick);
        ContentHost.Children.Add(row);

        _dateHintText = new TextBlock
        {
            FontFamily = FindResource("Font.Medium") as FontFamily,
            FontSize = 13,
            Foreground = FindResource("Brush.Brand") as Brush ?? Brushes.RoyalBlue,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        ContentHost.Children.Add(_dateHintText);
        UpdateDateHint();
    }

    private void UpdateDateHint()
    {
        if (_dateHintText is null) return;
        _dateHintText.Text = _semesterMonday is null
            ? "尚未选择开学第一周 —— 请先选择（必填）。"
            : $"已选择：{_semesterMonday:yyyy 年 M 月 d 日}（周一）· 该周为第 1 周、默认单周";
        _dateHintText.Foreground = _semesterMonday is null
            ? FindResource("Brush.Accent") as Brush ?? Brushes.Red
            : FindResource("Brush.Brand") as Brush ?? Brushes.RoyalBlue;
    }

    // =========================================================================
    // 第 2 步：值周 / 值日
    // =========================================================================

    private void BuildStep2()
    {
        ContentHost.Children.Add(Heading("你们班用哪种轮值方式？", "之后可切换"));
        ContentHost.Children.Add(Body(
            "值周制：一组人负责整整一周，每周轮换到下一组，组内每人岗位固定。\n" +
            "值日制：一组人负责一天，每天轮换到下一组，组内任务同样固定。\n\n" +
            "两种模式都按“小组轮换”，区别只是换组周期（一周 vs 一天）。"));

        var group = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var weekly = new RadioButton
        {
            Content = "值周制（每周换一组，推荐）",
            GroupName = "DutyMode",
            IsChecked = _mode == DutyMode.Weekly,
            Style = FindResource("WizardRadio") as Style
        };
        var daily = new RadioButton
        {
            Content = "值日制（每天换一组）",
            GroupName = "DutyMode",
            IsChecked = _mode == DutyMode.Daily,
            Style = FindResource("WizardRadio") as Style
        };
        weekly.Checked += (_, _) => _mode = DutyMode.Weekly;
        daily.Checked += (_, _) => _mode = DutyMode.Daily;
        group.Children.Add(weekly);
        group.Children.Add(daily);
        ContentHost.Children.Add(group);
    }

    // =========================================================================
    // 第 3 步：预设包
    // =========================================================================

    private void BuildStep3()
    {
        ContentHost.Children.Add(Heading("第一次配置用哪个预设？", "选完仍可编辑"));
        ContentHost.Children.Add(Body(
            "预设只是模板：生成后可以在「岗位与分组」「班委/课代表」页里随意增删改。\n" +
            "如果暂时不想配置，可以先选“空模板”，之后在窗口里自己加。"));

        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var options = new (DutyPresetKind Kind, string Label, string Desc)[]
        {
            (DutyPresetKind.Weekly, "值周包", "擦黑板 / 讲台整理 / 扫地 / 拖地 / 倒垃圾 / 门窗，共 6 个岗位 8 人次"),
            (DutyPresetKind.Cleaning, "卫生值日包", "擦黑板 / 讲台 / 扫地 / 拖地 / 倒垃圾 / 摆桌椅 / 门窗 / 卫生角，共 8 个岗位 10 人次"),
            (DutyPresetKind.Empty, "空模板", "不预置岗位，之后在窗口里自己添加")
        };
        foreach (var option in options)
        {
            var radio = new RadioButton
            {
                Content = option.Label,
                GroupName = "DutyPreset",
                IsChecked = _preset == option.Kind,
                Style = FindResource("WizardRadio") as Style,
                Margin = new Thickness(0, 8, 0, 0)
            };
            radio.Checked += (_, _) =>
            {
                _preset = option.Kind;
                UpdatePresetSummary();
            };
            panel.Children.Add(radio);
            panel.Children.Add(new TextBlock
            {
                Text = option.Desc,
                FontFamily = FindResource("Font.Regular") as FontFamily,
                FontSize = 11.5,
                Foreground = FindResource("Brush.TextTertiary") as Brush ?? Brushes.Gray,
                Margin = new Thickness(24, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap
            });
        }
        ContentHost.Children.Add(panel);

        var extras = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        extras.Children.Add(new TextBlock
        {
            Text = "另外可选：",
            FontFamily = FindResource("Font.Medium") as FontFamily,
            FontSize = 13,
            Foreground = FindResource("Brush.TextPrimary") as Brush ?? Brushes.Black
        });
        var committee = new CheckBox
        {
            Content = "班委包（12 个职位，各 1 人，成员稍后选）",
            IsChecked = _includeCommitteePack,
            Style = FindResource("WizardCheck") as Style
        };
        committee.Checked += (_, _) => { _includeCommitteePack = true; UpdatePresetSummary(); };
        committee.Unchecked += (_, _) => { _includeCommitteePack = false; UpdatePresetSummary(); };
        var subject = new CheckBox
        {
            Content = "课代表包（9 个科目，各 1 人，成员稍后选）",
            IsChecked = _includeSubjectRepPack,
            Style = FindResource("WizardCheck") as Style
        };
        subject.Checked += (_, _) => { _includeSubjectRepPack = true; UpdatePresetSummary(); };
        subject.Unchecked += (_, _) => { _includeSubjectRepPack = false; UpdatePresetSummary(); };
        extras.Children.Add(committee);
        extras.Children.Add(subject);
        ContentHost.Children.Add(extras);

        _presetSummaryText = new TextBlock
        {
            FontFamily = FindResource("Font.Regular") as FontFamily,
            FontSize = 12.5,
            Foreground = FindResource("Brush.TextSecondary") as Brush ?? Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        };
        ContentHost.Children.Add(_presetSummaryText);
        UpdatePresetSummary();
    }

    private void UpdatePresetSummary()
    {
        if (_presetSummaryText is null) return;
        var tasks = DutyPresetFactory.BuildTasks(_preset);
        var personSlots = tasks.Sum(t => Math.Max(0, t.RequiredCount));
        var extras = new List<string>();
        if (_includeCommitteePack) extras.Add("班委 12 个职位");
        if (_includeSubjectRepPack) extras.Add("课代表 9 个科目");
        _presetSummaryText.Text =
            $"已选：岗位 {(tasks.Count == 0 ? "不预置" : $"{tasks.Count} 个（共 {personSlots} 人次）")}" +
            (extras.Count > 0 ? $"　+　{string.Join(" + ", extras)}" : "　（未选班委/课代表包，可稍后配置）");
    }

    // =========================================================================
    // 第 4 步：分组与任务分配
    // =========================================================================

    private void BuildStep4()
    {
        ContentHost.Children.Add(Heading("把同学分成几组、任务怎么分配？", "之后可调"));
        ContentHost.Children.Add(Body(
            "默认按学号顺序平均分组，组内再按“任务顺序 → 组员顺序”自动分配岗位。\n" +
            "完成后仍然可以在「岗位与分组」页里手动调整成员和岗位（支持一个岗位安排 2 人、一人多岗）。"));

        // 组数
        var countRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        countRow.Children.Add(new TextBlock
        {
            Text = "组数：",
            FontFamily = FindResource("Font.Medium") as FontFamily,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FindResource("Brush.TextPrimary") as Brush ?? Brushes.Black
        });
        var countBox = new TextBox
        {
            Text = _groupCount.ToString(),
            Width = 76,
            Style = FindResource("Input.TextBox") as Style,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var countHint = new TextBlock
        {
            Text = "1~20 组",
            FontFamily = FindResource("Font.Regular") as FontFamily,
            FontSize = 11.5,
            Foreground = FindResource("Brush.TextTertiary") as Brush ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        };
        countBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(countBox.Text.Trim(), out var n)) _groupCount = Math.Clamp(n, 1, 20);
            countBox.Text = _groupCount.ToString();
            UpdateSummary();
        };
        countRow.Children.Add(countBox);
        countRow.Children.Add(countHint);
        ContentHost.Children.Add(countRow);

        // 任务分配方式
        var modePanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var fixedRadio = new RadioButton
        {
            Content = "每组各自固定（第 1 组张三擦黑板、第 2 组李四擦黑板…）",
            GroupName = "DutyAssignMode",
            IsChecked = _assignmentMode == DutyAssignmentMode.FixedPerGroup,
            Style = FindResource("WizardRadio") as Style
        };
        var autoRadio = new RadioButton
        {
            Content = "所有组共用“任务顺序 → 组员顺序”自动分配",
            GroupName = "DutyAssignMode",
            IsChecked = _assignmentMode == DutyAssignmentMode.AutoByOrder,
            Style = FindResource("WizardRadio") as Style
        };
        fixedRadio.Checked += (_, _) => { _assignmentMode = DutyAssignmentMode.FixedPerGroup; UpdateSummary(); };
        autoRadio.Checked += (_, _) => { _assignmentMode = DutyAssignmentMode.AutoByOrder; UpdateSummary(); };
        modePanel.Children.Add(fixedRadio);
        modePanel.Children.Add(autoRadio);
        ContentHost.Children.Add(modePanel);

        _groupSummaryText = new TextBlock
        {
            FontFamily = FindResource("Font.Regular") as FontFamily,
            FontSize = 12.5,
            Foreground = FindResource("Brush.TextSecondary") as Brush ?? Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        };
        ContentHost.Children.Add(_groupSummaryText);
        UpdateSummary();
    }

    private TextBlock? _groupSummaryText;

    private void UpdateSummary()
    {
        if (_groupSummaryText is null) return;
        var students = GetStudents();
        var tasks = DutyPresetFactory.BuildTasks(_preset);
        if (students.Count == 0)
        {
            _groupSummaryText.Text = "当前没有可用花名册：可以先完成向导，之后导入花名册再到「岗位与分组」里一键分组。";
            return;
        }
        var groups = Math.Clamp(_groupCount, 1, 20);
        var per = (int)Math.Ceiling(students.Count / (double)groups);
        _groupSummaryText.Text =
            $"当前花名册 {students.Count} 人 → 分成 {groups} 组（每组约 {per} 人）；" +
            $"岗位 {tasks.Count} 个、共 {tasks.Sum(t => Math.Max(0, t.RequiredCount))} 人次。" +
            "完成后可在窗口里调整。";
    }

    private List<Student> GetStudents()
    {
        try
        {
            return AppServices.IsInitialized
                ? AppServices.Current.Rosters.Active?.Students.Where(s => s.IsValid).ToList() ?? new List<Student>()
                : new List<Student>();
        }
        catch
        {
            return new List<Student>();
        }
    }

    // =========================================================================
    // 按钮
    // =========================================================================

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step == 1 && _semesterMonday is null)
        {
            Info("第 1 步的“开学第一周”是必填项，请先选择日期。", MessageBoxImage.Warning);
            return;
        }
        if (_step < StepCount) _step++;
        BuildStep();
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_step > 1) _step--;
        BuildStep();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Completed = false;
        Close();
    }

    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        if (_semesterMonday is null)
        {
            _step = 1;
            BuildStep();
            Info("请先选择开学第一周（必填）。", MessageBoxImage.Warning);
            return;
        }
        if (!AppServices.IsInitialized)
        {
            Info("班机服务尚未就绪，请稍后再试。", MessageBoxImage.Warning);
            return;
        }

        try
        {
            var duty = AppServices.Current.Duty;
            if (duty.IsConfigured)
            {
                var confirm = MessageBox.Show(
                    "重新运行向导会覆盖当前的岗位与分组配置（班委/课代表也会按本次选择重建）。确定继续吗？",
                    "重新配置值日与轮值", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes) return;
            }

            var students = GetStudents();
            var config = DutyPresetFactory.BuildConfig(
                _preset, _includeCommitteePack, _includeSubjectRepPack,
                _mode, _groupCount, _assignmentMode, students, _semesterMonday);

            duty.Save(config);
            AppServices.Current.Settings.Update(x =>
            {
                x.SemesterFirstWeekMonday = _semesterMonday;
                x.DutySetupCompleted = true;
                if (x.WeekParityAnchorDate is null) x.WeekParityAnchorDate = _semesterMonday;
            });
            AppServices.Current.WeekParity.Refresh();
            Log.Info($"值日与轮值配置完成：模式={_mode} 组数={config.Groups.Count} 岗位={config.Tasks.Count}");
            Completed = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("保存值日与轮值配置失败", ex);
            Info("保存值守配置失败：" + ex.Message, MessageBoxImage.Error);
        }
    }

    // =========================================================================
    // 小工具
    // =========================================================================

    private TextBlock Heading(string title, string badge)
    {
        var panel = new TextBlock
        {
            FontFamily = FindResource("Font.Bold") as FontFamily,
            FontWeight = FontWeights.Bold,
            FontSize = 18,
            Foreground = FindResource("Brush.TextPrimary") as Brush ?? Brushes.Black,
            TextWrapping = TextWrapping.Wrap,
            Text = badge.Length > 0 ? $"{title}　（{badge}）" : title
        };
        return panel;
    }

    private TextBlock Body(string text) => new()
    {
        Text = text,
        FontFamily = FindResource("Font.Regular") as FontFamily,
        FontSize = 13,
        Foreground = FindResource("Brush.TextSecondary") as Brush ?? Brushes.Gray,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 22,
        Margin = new Thickness(0, 8, 0, 0)
    };

    private void Info(string message, MessageBoxImage image = MessageBoxImage.Information)
        => MessageBox.Show(message, "班机", MessageBoxButton.OK, image);
}
