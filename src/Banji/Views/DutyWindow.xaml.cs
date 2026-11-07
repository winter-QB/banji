using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Views;

/// <summary>
/// v1.4.0 值日与轮值窗口（4 页签）：
/// ① 本周安排 ② 岗位与分组 ③ 班委/课代表 ④ 历史与打卡。
/// 首次使用（Duty.NeedsFirstTimeSetup）时会强制弹出 <see cref="DutySetupWizard"/>，
/// 必填开学第一周且不可跳过；关闭向导视为未完成，下次打开继续强制引导。
/// v1.4 按用户要求不做打印/CSV，只提供复制文本。
/// </summary>
public partial class DutyWindow : Window
{
    private DutyConfig _draft = new();
    private bool _saving;
    private bool _subscribed;
    private bool _wizardRunning;

    private static IDutyService Duty => AppServices.Current.Duty;
    private static ISettingsService SettingsSvc => AppServices.Current.Settings;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    private static DateOnly CurrentMonday => DutyPresetFactory.MondayOf(Today);
    private static DateOnly NextMonday => CurrentMonday.AddDays(7);

    public DutyWindow()
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
        if (!AppServices.IsInitialized) return;
        Subscribe();
        if (Duty.NeedsFirstTimeSetup)
        {
            // 强制首次配置：窗口显示后再弹向导，避免模态在 Loaded 里阻塞
            Dispatcher.BeginInvoke(new Action(ForceFirstTimeSetup));
            return;
        }
        ReloadDraft();
        BuildAll();
    }

    private void ForceFirstTimeSetup()
    {
        var ok = ShowWizardDialog();
        Log.Info(ok ? "值日与轮值首次配置向导已完成" : "值日与轮值首次配置向导被取消（下次继续强制引导）");
        if (!ok)
        {
            Close();
            return;
        }
        ReloadDraft();
        BuildAll();
    }

    private bool ShowWizardDialog()
    {
        if (_wizardRunning || !AppServices.IsInitialized) return false;
        _wizardRunning = true;
        try
        {
            var wizard = new DutySetupWizard { Owner = this };
            return wizard.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            Log.Error("打开值日配置向导失败", ex);
            Info("打开配置向导失败：" + ex.Message, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            _wizardRunning = false;
        }
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        try { Duty.Changed += OnDutyChanged; } catch (Exception ex) { Log.Error("订阅值日变更失败", ex); }
        try { AppServices.Current.Rosters.Changed += OnRostersChanged; } catch (Exception ex) { Log.Error("订阅花名册变更失败", ex); }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        try { Duty.Changed -= OnDutyChanged; } catch { /* ignore */ }
        try { AppServices.Current.Rosters.Changed -= OnRostersChanged; } catch { /* ignore */ }
    }

    private void OnDutyChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_saving) return;
        ReloadDraft();
        BuildAll();
    });

    private void OnRostersChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_saving) return;
        BuildAll();
    });

    private static void RunOnUi(Action action)
    {
        try { AppServices.RunOnUi(action); } catch (Exception ex) { Log.Error("值日窗口刷新失败", ex); }
    }

    private void ReloadDraft()
    {
        try
        {
            _draft = Duty.Config.Clone();
            // 兜底：保证 Order 覆盖所有组
            foreach (var group in _draft.Groups)
                if (!_draft.Order.Contains(group.Id)) _draft.Order.Add(group.Id);
        }
        catch (Exception ex)
        {
            Log.Error("读取值日配置失败", ex);
            _draft = new DutyConfig();
        }
    }

    private void SaveDraft()
    {
        if (!AppServices.IsInitialized) return;
        try
        {
            _saving = true;
            Duty.Save(_draft.Clone());
        }
        catch (Exception ex)
        {
            Log.Error("保存值日配置失败", ex);
            Info("保存值日配置失败：" + ex.Message, MessageBoxImage.Warning);
        }
        finally
        {
            _saving = false;
        }
        UpdateHeader();
    }

    private void BuildAll()
    {
        UpdateHeader();
        RebuildWeekTab();
        RebuildSetupTab();
        RebuildRolesTab();
        RebuildHistoryTab();
    }

    private void UpdateHeader()
    {
        try
        {
            var mode = _draft.Mode == DutyMode.Weekly ? "值周制" : "值日制";
            var firstWeek = SettingsSvc.Current.SemesterFirstWeekMonday?.ToString("yyyy-MM-dd") ?? "未设置";
            var week = "—";
            try
            {
                var parity = AppServices.Current.WeekParity;
                var number = parity.GetWeekNumber(Today);
                week = number > 0 ? $"第 {number} 周" : number == 0 ? "开学前" : "未设置锚点";
            }
            catch { /* 仅展示 */ }
            HeaderSubText.Text = $"{mode} · 本周：{week} · 开学第一周：{firstWeek}" +
                                 (Duty.NeedsFirstTimeSetup ? " · ⚠ 首次配置未完成" : "");
        }
        catch (Exception ex)
        {
            HeaderSubText.Text = "";
            Log.Error("刷新值日窗口标题失败", ex);
        }
    }

    // =========================================================================
    // ① 本周安排
    // =========================================================================

    private void RebuildWeekTab()
    {
        WeekHost.Children.Clear();
        try
        {
            var view = Duty.GetSchedule(Today);
            var weekRange = $"{CurrentMonday:MM-dd} ~ {CurrentMonday.AddDays(6):MM-dd}";
            var statusCard = Card(
                new DockPanel
                {
                    LastChildFill = true,
                    Children =
                    {
                        new StackPanel
                        {
                            Children =
                            {
                                Text("本周安排", "DutyCardTitle"),
                                Text(view.IsPaused ? "本周暂停" : (string.IsNullOrWhiteSpace(view.GroupName) ? "未配置" : view.GroupName!),
                                    big: true),
                                Hint($"{(_draft.Mode == DutyMode.Weekly ? "值周制 · 每周换组" : "值日制 · 每天换组")} · " +
                                     $"本周 {weekRange} · 今日任务 {view.Tasks.Count} 项" +
                                     (view.IsPaused ? " · 本周已暂停，下周自动继续轮换" : ""))
                            }
                        },
                        BuildWeekActions()
                    }
                });
            WeekHost.Children.Add(statusCard);

            // 今日任务表
            var tasksCard = Card(Text("今日任务", "DutyCardTitle"));
            if (view.Tasks.Count == 0)
            {
                ((StackPanel)tasksCard.Child).Children.Add(Hint("还没有可显示的岗位安排。请先在「岗位与分组」页配置岗位和分组，或重新运行配置向导。"));
                var go = Btn("打开配置向导", () => ShowWizardDialog(), primary: true);
                go.Margin = new Thickness(0, 10, 0, 0);
                go.HorizontalAlignment = HorizontalAlignment.Left;
                ((StackPanel)tasksCard.Child).Children.Add(go);
            }
            else
            {
                var table = new StackPanel();
                var header = NewGrid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                AddCell(header, "岗位", 0, bold: true, secondary: true);
                AddCell(header, "成员", 1, bold: true, secondary: true);
                AddCell(header, "说明", 2, bold: true, secondary: true);
                table.Children.Add(header);
                foreach (var task in view.Tasks.OrderByDescending(t => t.IsTopTask))
                {
                    var row = NewGrid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                    AddCell(row, (task.IsTopTask ? "★ " : "") + task.TaskName, 0);
                    AddCell(row, task.MembersText, 1);
                    AddCell(row, task.Category + (task.IsTopTask ? " · 置顶" : ""), 2, secondary: true);
                    table.Children.Add(row);
                }
                ((StackPanel)tasksCard.Child).Children.Add(table);
            }
            WeekHost.Children.Add(tasksCard);

            // 下周预告
            var previewCard = Card(Text("下周预告", "DutyCardTitle"));
            var nextText = Hint("暂无下周轮换信息（需要先配置分组/岗位，且值周服务实现轮换预览）。");
            try
            {
                var preview = Duty.GetRotationPreview(Today, 2);
                var next = preview?.FirstOrDefault(w => w.WeekMonday == NextMonday);
                if (next is not null)
                {
                    nextText = Text(next.Paused
                        ? "下周：暂停值日 / 值周"
                        : $"下周：{(string.IsNullOrWhiteSpace(next.GroupName) ? "未配置" : next.GroupName)}" +
                          (string.IsNullOrWhiteSpace(next.Note) ? "" : $"（{next.Note}）"), medium: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取下周轮换预览失败", ex);
            }
            ((StackPanel)previewCard.Child).Children.Add(nextText);
            WeekHost.Children.Add(previewCard);
        }
        catch (Exception ex)
        {
            Log.Error("构建本周安排页失败", ex);
            WeekHost.Children.Add(Card(Hint("读取本周安排失败：" + ex.Message)));
        }
    }

    private FrameworkElement BuildWeekActions()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };

        var groupBox = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var group in _draft.Groups) groupBox.Items.Add(group.Name);
        if (groupBox.Items.Count > 0) groupBox.SelectedIndex = 0;
        panel.Children.Add(groupBox);

        var swap = Btn("换组", () =>
        {
            if (groupBox.SelectedIndex < 0 || groupBox.SelectedIndex >= _draft.Groups.Count) return;
            var group = _draft.Groups[groupBox.SelectedIndex];
            Duty.SetWeekOverride(CurrentMonday, group.Id, false, "手动换组");
            ReloadDraft();
            BuildAll();
        });
        panel.Children.Add(swap);

        panel.Children.Add(Btn("暂停本周", () =>
        {
            Duty.SetWeekOverride(CurrentMonday, null, true, "手动暂停");
            ReloadDraft();
            BuildAll();
        }));
        panel.Children.Add(Btn("恢复自动", () =>
        {
            Duty.SetWeekOverride(CurrentMonday, null, false, null);
            ReloadDraft();
            BuildAll();
        }));

        if (_draft.AwardPointsOnCheckIn)
        {
            panel.Children.Add(Btn("值日完成打卡", () =>
            {
                var confirm = MessageBox.Show("确定今天值日/值周已完成并打卡吗？\n\n若已在设置中开启“打卡加分”，会给参与学生加课堂积分。",
                    "值日打卡", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes) return;
                try
                {
                    Duty.CheckIn(Today);
                    ReloadDraft();
                    BuildAll();
                    Info("已记录今天的值日打卡。");
                }
                catch (Exception ex)
                {
                    Log.Error("值日打卡失败", ex);
                    Info("值日打卡失败：" + ex.Message, MessageBoxImage.Warning);
                }
            }, primary: true));
        }
        else
        {
            var tip = Hint("打卡加分未开启");
            tip.VerticalAlignment = VerticalAlignment.Center;
            tip.Margin = new Thickness(6, 0, 0, 0);
            panel.Children.Add(tip);
        }
        return panel;
    }

    private void OnCopyWeekTextClick(object sender, RoutedEventArgs e) => Guard("复制本周文本", () =>
    {
        var text = Duty.BuildCopyText(Today);
        Clipboard.SetText(text);
        Info("已复制本周值日/值周安排到剪贴板，可以直接发班级群。");
    });

    private void OnOpenWizardClick(object sender, RoutedEventArgs e)
    {
        if (!ShowWizardDialog()) return;
        ReloadDraft();
        BuildAll();
    }

    // =========================================================================
    // ② 岗位与分组
    // =========================================================================

    private void RebuildSetupTab()
    {
        SetupHost.Children.Clear();
        SetupHost.Children.Add(BuildTasksCard());
        SetupHost.Children.Add(BuildGroupsCard());
    }

    private FrameworkElement BuildTasksCard()
    {
        var card = Card(Text("岗位库（任务）", "DutyCardTitle"));
        var stack = (StackPanel)card.Child;
        stack.Children.Add(Hint("岗位完全自定义：名称、分类、每组需要几人（0=停用）。一个岗位可以安排多人（如力气活分给两名同学）。"));

        var add = Btn("+ 添加岗位", () =>
        {
            _draft.Tasks.Add(new DutyTask
            {
                Id = "t" + Guid.NewGuid().ToString("N")[..8],
                Name = "新岗位",
                Category = "卫生",
                RequiredCount = 1,
                Sort = _draft.Tasks.Count
            });
            SaveDraft();
            RebuildSetupTab();
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 8);
        stack.Children.Add(add);

        foreach (var task in _draft.Tasks.OrderBy(t => t.Sort).ToList())
        {
            var row = NewGrid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBox = Input(task.Name, 130);
            nameBox.LostFocus += (_, _) => CommitTask(task, nameBox, null, null, null);
            nameBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CommitTask(task, nameBox, null, null, null); e.Handled = true; } };
            Grid.SetColumn(nameBox, 0);
            row.Children.Add(nameBox);

            var categoryBox = new ComboBox { Width = 88, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var category in new[] { "卫生", "值周", "班委", "课代表", "其他" }) categoryBox.Items.Add(category);
            categoryBox.SelectedItem = string.IsNullOrWhiteSpace(task.Category) ? "其他" : task.Category;
            categoryBox.SelectionChanged += (_, _) => CommitTask(task, null, categoryBox, null, null);
            Grid.SetColumn(categoryBox, 1);
            row.Children.Add(categoryBox);

            var countBox = Input(task.RequiredCount.ToString(), 44);
            countBox.ToolTip = "每组需要几人；0 = 停用";
            countBox.LostFocus += (_, _) => CommitTask(task, null, null, countBox, null);
            countBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CommitTask(task, null, null, countBox, null); e.Handled = true; } };
            Grid.SetColumn(countBox, 2);
            row.Children.Add(countBox);

            var noteBox = Input(task.Note, 160);
            noteBox.Margin = new Thickness(8, 0, 8, 0);
            noteBox.ToolTip = "说明（可留空）";
            noteBox.LostFocus += (_, _) => CommitTask(task, null, null, null, noteBox);
            noteBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CommitTask(task, null, null, null, noteBox); e.Handled = true; } };
            Grid.SetColumn(noteBox, 3);
            row.Children.Add(noteBox);

            var ops = new StackPanel { Orientation = Orientation.Horizontal };
            ops.Children.Add(SmallBtn("↑", () => MoveTask(task, -1)));
            ops.Children.Add(SmallBtn("↓", () => MoveTask(task, 1)));
            ops.Children.Add(SmallBtn("删除", () => DeleteTask(task)));
            Grid.SetColumn(ops, 4);
            row.Children.Add(ops);
            row.Margin = new Thickness(0, 3, 0, 3);
            stack.Children.Add(row);
        }

        if (_draft.Tasks.Count == 0)
            stack.Children.Add(Hint("还没有岗位。点“+ 添加岗位”或重新运行配置向导选择预设包。"));
        return card;
    }

    private void CommitTask(DutyTask task, TextBox? nameBox, ComboBox? categoryBox, TextBox? countBox, TextBox? noteBox)
    {
        if (nameBox is not null) task.Name = nameBox.Text.Trim();
        if (categoryBox is not null && categoryBox.SelectedItem is string category) task.Category = category;
        if (countBox is not null)
        {
            task.RequiredCount = int.TryParse(countBox.Text.Trim(), out var n) ? Math.Clamp(n, 0, 9) : task.RequiredCount;
            countBox.Text = task.RequiredCount.ToString();
        }
        if (noteBox is not null) task.Note = noteBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(task.Name)) task.Name = "未命名岗位";
        SaveDraft();
    }

    private void MoveTask(DutyTask task, int delta)
    {
        var ordered = _draft.Tasks.OrderBy(t => t.Sort).ToList();
        var index = ordered.IndexOf(task);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count) return;
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Sort = i;
        SaveDraft();
        RebuildSetupTab();
    }

    private void DeleteTask(DutyTask task)
    {
        var confirm = MessageBox.Show($"确定删除岗位“{task.Name}”吗？组内相关分配也会被清除。", "删除岗位",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        _draft.Tasks.Remove(task);
        foreach (var group in _draft.Groups)
            group.Assignments.RemoveAll(a => a.TaskId == task.Id);
        RebuildSort();
        SaveDraft();
        RebuildSetupTab();
    }

    private void RebuildSort()
    {
        var ordered = _draft.Tasks.OrderBy(t => t.Sort).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Sort = i;
    }

    private FrameworkElement BuildGroupsCard()
    {
        var card = Card(Text("轮值分组与组内任务", "DutyCardTitle"));
        var stack = (StackPanel)card.Child;
        stack.Children.Add(Hint("把同学分成若干组，每组人数可不同；组内每个岗位的成员固定。适合“值周制（每周换组）”和“值日制（每天换组）”。"));

        // 分配模式
        var modePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        var fixedRadio = new RadioButton
        {
            Content = "每组各自固定",
            GroupName = "DutyAssignModeWindow",
            IsChecked = _draft.AssignmentMode == DutyAssignmentMode.FixedPerGroup,
            FontFamily = FindResource("Font.Regular") as FontFamily,
            FontSize = 13,
            Foreground = FindResource("Brush.TextPrimary") as Brush ?? Brushes.Black,
            Margin = new Thickness(0, 0, 18, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var autoRadio = new RadioButton
        {
            Content = "所有组共用自动分配",
            GroupName = "DutyAssignModeWindow",
            IsChecked = _draft.AssignmentMode == DutyAssignmentMode.AutoByOrder,
            FontFamily = FindResource("Font.Regular") as FontFamily,
            FontSize = 13,
            Foreground = FindResource("Brush.TextPrimary") as Brush ?? Brushes.Black,
            Margin = new Thickness(0, 0, 18, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        fixedRadio.Checked += (_, _) => { _draft.AssignmentMode = DutyAssignmentMode.FixedPerGroup; SaveDraft(); RebuildSetupTab(); };
        autoRadio.Checked += (_, _) =>
        {
            _draft.AssignmentMode = DutyAssignmentMode.AutoByOrder;
            SaveDraft();
            RebuildSetupTab();
        };
        modePanel.Children.Add(fixedRadio);
        modePanel.Children.Add(autoRadio);
        stack.Children.Add(modePanel);
        stack.Children.Add(Hint("“每组各自固定”：每个组单独维护岗位成员；“所有组共用自动分配”：同一套“任务顺序→组员顺序”规则套用到所有组。"));

        // 自动分组
        var autoPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
        autoPanel.Children.Add(new TextBlock
        {
            Text = "组数：",
            FontFamily = FindResource("Font.Medium") as FontFamily,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FindResource("Brush.TextPrimary") as Brush ?? Brushes.Black
        });
        var countBox = Input(Math.Max(1, _draft.Groups.Count == 0 ? 4 : _draft.Groups.Count).ToString(), 56);
        countBox.Margin = new Thickness(0, 0, 10, 0);
        autoPanel.Children.Add(countBox);
        autoPanel.Children.Add(Btn("按学号顺序自动分组", () =>
        {
            var count = int.TryParse(countBox.Text.Trim(), out var n) ? Math.Clamp(n, 1, 20) : 4;
            AutoBuildGroups(count);
        }, primary: true));
        autoPanel.Children.Add(Btn("+ 添加一组", () =>
        {
            var group = new DutyGroup
            {
                Id = "g" + Guid.NewGuid().ToString("N")[..8],
                Name = $"第 {_draft.Groups.Count + 1} 组"
            };
            _draft.Groups.Add(group);
            _draft.Order.Add(group.Id);
            SaveDraft();
            RebuildSetupTab();
        }));
        if (_draft.AssignmentMode == DutyAssignmentMode.AutoByOrder && _draft.Groups.Count > 1)
        {
            autoPanel.Children.Add(Btn("用第 1 组分配同步所有组", () =>
            {
                var first = _draft.Groups[0];
                foreach (var group in _draft.Groups.Skip(1))
                    group.Assignments = first.Assignments.Select(a => a.Clone()).ToList();
                SaveDraft();
                RebuildSetupTab();
                Info("已把第 1 组的分配同步到所有组。");
            }));
        }
        stack.Children.Add(autoPanel);

        var students = GetStudents();
        if (students.Count == 0)
            stack.Children.Add(Hint("当前没有启用中的花名册。请先在「花名册管理」导入/录入名单，再回来自动分组。"));

        foreach (var group in _draft.Groups.OrderBy(g => _draft.Order.IndexOf(g.Id)).ToList())
            stack.Children.Add(BuildGroupCard(group, students));

        if (_draft.Groups.Count == 0)
            stack.Children.Add(Hint("还没有分组。设置组数后点“按学号顺序自动分组”。"));
        return card;
    }

    private FrameworkElement BuildGroupCard(DutyGroup group, List<Student> students)
    {
        var card = Card();
        var stack = (StackPanel)card.Child;
        stack.Margin = new Thickness(0);

        var header = NewGrid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameBox = Input(group.Name, 108);
        nameBox.LostFocus += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            if (name.Length == 0) { nameBox.Text = group.Name; return; }
            if (name == group.Name) return;
            group.Name = name;
            SaveDraft();
        };
        Grid.SetColumn(nameBox, 0);
        header.Children.Add(nameBox);

        var membersText = Text($"{group.MemberIds.Count} 人：" +
                                string.Join("、", group.MemberIds.Select(StudentName)), medium: true);
        membersText.Margin = new Thickness(10, 0, 10, 0);
        membersText.VerticalAlignment = VerticalAlignment.Center;
        membersText.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(membersText, 1);
        header.Children.Add(membersText);

        var ops = new StackPanel { Orientation = Orientation.Horizontal };
        ops.Children.Add(SmallBtn("选择成员", () =>
        {
            var options = students.Select(s => (s.Key, $"{s.DisplayId} {s.DisplayName}")).ToList();
            var result = MultiSelectDialog($"选择「{group.Name}」的成员", options, group.MemberIds);
            if (result is null) return;
            group.MemberIds = result;
            group.Assignments.RemoveAll(a => !group.MemberIds.Contains(a.StudentId));
            SaveDraft();
            RebuildSetupTab();
        }));
        ops.Children.Add(SmallBtn("↑", () => MoveGroup(group, -1)));
        ops.Children.Add(SmallBtn("↓", () => MoveGroup(group, 1)));
        ops.Children.Add(SmallBtn("删除组", () => DeleteGroup(group)));
        Grid.SetColumn(ops, 2);
        header.Children.Add(ops);
        stack.Children.Add(header);
        stack.Children.Add(Hint("组内任务分配（一个岗位可安排多人；允许一人多岗）："));

        var tasks = _draft.Tasks.Where(t => t.RequiredCount > 0).OrderBy(t => t.Sort).ToList();
        foreach (var task in tasks)
        {
            var assigned = group.Assignments.Where(a => a.TaskId == task.Id).Select(a => a.StudentId).ToList();
            var row = NewGrid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Text($"{task.Name}（需 {task.RequiredCount} 人）", medium: true);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 2, 8, 2);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(label, 0);
            row.Children.Add(label);
            var assignedText = Text(assigned.Count == 0 ? "未安排" : string.Join("、", assigned.Select(StudentName)));
            assignedText.Foreground = assigned.Count == 0
                ? (FindResource("Brush.Accent") as Brush ?? Brushes.Red)
                : (FindResource("Brush.TextPrimary") as Brush ?? Brushes.Black);
            assignedText.VerticalAlignment = VerticalAlignment.Center;
            assignedText.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(assignedText, 1);
            row.Children.Add(assignedText);
            var pick = SmallBtn("分配…", () =>
            {
                var options = group.MemberIds.Select(id => (id, StudentName(id))).ToList();
                var result = MultiSelectDialog($"「{group.Name}」· {task.Name} 的成员", options,
                    group.Assignments.Where(a => a.TaskId == task.Id).Select(a => a.StudentId));
                if (result is null) return;
                group.Assignments.RemoveAll(a => a.TaskId == task.Id);
                foreach (var id in result)
                    group.Assignments.Add(new DutyAssignment { StudentId = id, TaskId = task.Id });
                SaveDraft();
                RebuildSetupTab();
            });
            Grid.SetColumn(pick, 2);
            row.Children.Add(pick);
            stack.Children.Add(row);
        }

        // 自动分配本组
        var auto = Btn("按任务顺序自动分配本组", () =>
        {
            group.Assignments = DutyPresetFactory.AutoAssign(group.MemberIds, _draft.Tasks);
            SaveDraft();
            RebuildSetupTab();
        });
        auto.HorizontalAlignment = HorizontalAlignment.Left;
        auto.Margin = new Thickness(0, 6, 0, 4);
        stack.Children.Add(auto);

        // 未分配提示：优先用 core-roster 的 DutyService.CheckAssignments（中文 Summary）
        try
        {
            var concrete = AppServices.Current.Duty as Banji.Core.Duty.DutyService;
            var check = concrete?.CheckAssignments(group.Id);
            if (check is not null)
            {
                if (check.HasIssues)
                    stack.Children.Add(Warn("⚠ " + check.Summary));
            }
            else
            {
                var unassignedTasks = tasks.Where(t => group.Assignments.Count(a => a.TaskId == t.Id) < t.RequiredCount)
                    .Select(t => $"{t.Name}({group.Assignments.Count(a => a.TaskId == t.Id)}/{t.RequiredCount})").ToList();
                var unassignedMembers = group.MemberIds.Where(id => group.Assignments.All(a => a.StudentId != id))
                    .Select(StudentName).ToList();
                if (unassignedTasks.Count > 0)
                    stack.Children.Add(Warn("⚠ 未分配满的岗位：" + string.Join("、", unassignedTasks)));
                if (unassignedMembers.Count > 0)
                    stack.Children.Add(Warn("⚠ 未分配任务的同学：" + string.Join("、", unassignedMembers)));
            }
        }
        catch (Exception ex)
        {
            Log.Error("检查组内任务分配失败", ex);
        }
        return card;
    }

    private void AutoBuildGroups(int count)
    {
        var students = GetStudents()
            .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (students.Count == 0)
        {
            Info("当前没有可用花名册，无法自动分组。请先导入或录入花名册。", MessageBoxImage.Warning);
            return;
        }
        var confirm = MessageBox.Show(
            $"将把当前花名册 {students.Count} 人按学号顺序分成 {count} 组，并覆盖现有分组与组内分配。确定继续吗？",
            "自动分组", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _draft.Groups.Clear();
        _draft.Order.Clear();
        for (var g = 0; g < count; g++)
        {
            var group = new DutyGroup
            {
                Id = "g" + Guid.NewGuid().ToString("N")[..8],
                Name = $"第 {g + 1} 组"
            };
            for (var i = g; i < students.Count; i += count)
                group.MemberIds.Add(students[i].Key);
            group.Assignments = DutyPresetFactory.AutoAssign(group.MemberIds, _draft.Tasks);
            _draft.Groups.Add(group);
            _draft.Order.Add(group.Id);
        }
        SaveDraft();
        RebuildSetupTab();
        Info($"已按学号顺序分成 {count} 组。");
    }

    private void MoveGroup(DutyGroup group, int delta)
    {
        var index = _draft.Order.IndexOf(group.Id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _draft.Order.Count) return;
        (_draft.Order[index], _draft.Order[target]) = (_draft.Order[target], _draft.Order[index]);
        SaveDraft();
        RebuildSetupTab();
    }

    private void DeleteGroup(DutyGroup group)
    {
        var confirm = MessageBox.Show($"确定删除“{group.Name}”吗？", "删除组",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        _draft.Groups.Remove(group);
        _draft.Order.Remove(group.Id);
        SaveDraft();
        RebuildSetupTab();
    }

    // =========================================================================
    // ③ 班委 / 课代表
    // =========================================================================

    private void RebuildRolesTab()
    {
        RolesHost.Children.Clear();
        RolesHost.Children.Add(BuildCommitteeCard());
        RolesHost.Children.Add(BuildSubjectRepCard());
    }

    private FrameworkElement BuildCommitteeCard()
    {
        var card = Card(Text("班委（固定任职，不参与轮换）", "DutyCardTitle"));
        var stack = (StackPanel)card.Child;
        stack.Children.Add(Hint("职位类型、人数、成员全部可自定义（人数 0~4）。班委不参与值周/值日轮换。"));

        var add = Btn("+ 添加职位", () =>
        {
            _draft.Committee.Add(new CommitteeRole { Type = "新职位", Count = 1 });
            SaveDraft();
            RebuildRolesTab();
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 8);
        stack.Children.Add(add);
        stack.Children.Add(Btn("一键生成班委预设（12 个职位）", () =>
        {
            if (_draft.Committee.Count > 0)
            {
                var confirm = MessageBox.Show("当前已有班委配置，用预设替换吗？", "生成班委预设",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes) return;
            }
            _draft.Committee = DutyPresetFactory.BuildCommitteePreset();
            SaveDraft();
            RebuildRolesTab();
        }));

        foreach (var role in _draft.Committee.ToList())
            stack.Children.Add(BuildRoleRow(role, isCommittee: true));
        if (_draft.Committee.Count == 0)
            stack.Children.Add(Hint("还没有班委配置。可点上方按钮生成预设或手动添加。"));
        return card;
    }

    private FrameworkElement BuildSubjectRepCard()
    {
        var card = Card(Text("课代表（固定任职，不参与轮换）", "DutyCardTitle"));
        var stack = (StackPanel)card.Child;
        stack.Children.Add(Hint("按科目维护，每科人数可设 0~4；课代表不参与值周/值日轮换。"));

        var add = Btn("+ 添加科目", () =>
        {
            _draft.SubjectReps.Add(new SubjectRepRole { Subject = "新科目", Count = 1 });
            SaveDraft();
            RebuildRolesTab();
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 8);
        stack.Children.Add(add);
        stack.Children.Add(Btn("一键生成课代表预设（9 个科目）", () =>
        {
            if (_draft.SubjectReps.Count > 0)
            {
                var confirm = MessageBox.Show("当前已有课代表配置，用预设替换吗？", "生成课代表预设",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes) return;
            }
            _draft.SubjectReps = DutyPresetFactory.BuildSubjectRepPreset();
            SaveDraft();
            RebuildRolesTab();
        }));

        foreach (var role in _draft.SubjectReps.ToList())
            stack.Children.Add(BuildRoleRow(role, isCommittee: false));
        if (_draft.SubjectReps.Count == 0)
            stack.Children.Add(Hint("还没有课代表配置。可点上方按钮生成预设或手动添加。"));
        return card;
    }

    private FrameworkElement BuildRoleRow(object role, bool isCommittee)
    {
        var committee = isCommittee ? role as CommitteeRole : null;
        var subject = isCommittee ? null : role as SubjectRepRole;
        if (committee is null && subject is null) return new StackPanel();

        var row = NewGrid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var typeBox = Input(isCommittee ? committee!.Type : subject!.Subject, 130);
        typeBox.LostFocus += (_, _) => CommitRole(committee, subject, typeBox, null);
        typeBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CommitRole(committee, subject, typeBox, null); e.Handled = true; } };
        Grid.SetColumn(typeBox, 0);
        row.Children.Add(typeBox);

        var count = isCommittee ? committee!.Count : subject!.Count;
        var countBox = Input(count.ToString(), 44);
        countBox.ToolTip = "人数 0~4";
        countBox.LostFocus += (_, _) => CommitRole(committee, subject, null, countBox);
        countBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CommitRole(committee, subject, null, countBox); e.Handled = true; } };
        Grid.SetColumn(countBox, 1);
        row.Children.Add(countBox);

        var memberIds = isCommittee ? committee!.MemberIds : subject!.MemberIds;
        var memberText = Text(memberIds.Count == 0 ? "未指定成员" : string.Join("、", memberIds.Select(StudentName)));
        memberText.VerticalAlignment = VerticalAlignment.Center;
        memberText.Margin = new Thickness(10, 0, 10, 0);
        memberText.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(memberText, 2);
        row.Children.Add(memberText);

        var ops = new StackPanel { Orientation = Orientation.Horizontal };
        ops.Children.Add(SmallBtn("选择成员", () =>
        {
            var students = GetStudents();
            var options = students.Select(s => (s.Key, $"{s.DisplayId} {s.DisplayName}")).ToList();
            var result = MultiSelectDialog($"选择成员（{(isCommittee ? committee!.Type : subject!.Subject)}）", options, memberIds);
            if (result is null) return;
            memberIds.Clear();
            memberIds.AddRange(result);
            SaveDraft();
            RebuildRolesTab();
        }));
        ops.Children.Add(SmallBtn("删除", () =>
        {
            if (isCommittee) _draft.Committee.Remove(committee!);
            else _draft.SubjectReps.Remove(subject!);
            SaveDraft();
            RebuildRolesTab();
        }));
        Grid.SetColumn(ops, 3);
        row.Children.Add(ops);
        row.Margin = new Thickness(0, 3, 0, 3);
        return row;
    }

    private void CommitRole(CommitteeRole? committee, SubjectRepRole? subject, TextBox? typeBox, TextBox? countBox)
    {
        if (committee is not null)
        {
            if (typeBox is not null) committee.Type = typeBox.Text.Trim();
            if (countBox is not null)
            {
                committee.Count = int.TryParse(countBox.Text.Trim(), out var n) ? Math.Clamp(n, 0, 4) : committee.Count;
                countBox.Text = committee.Count.ToString();
            }
            if (string.IsNullOrWhiteSpace(committee.Type)) committee.Type = "未命名职位";
        }
        if (subject is not null)
        {
            if (typeBox is not null) subject.Subject = typeBox.Text.Trim();
            if (countBox is not null)
            {
                subject.Count = int.TryParse(countBox.Text.Trim(), out var n) ? Math.Clamp(n, 0, 4) : subject.Count;
                countBox.Text = subject.Count.ToString();
            }
            if (string.IsNullOrWhiteSpace(subject.Subject)) subject.Subject = "未命名科目";
        }
        SaveDraft();
    }

    // =========================================================================
    // ④ 历史与打卡
    // =========================================================================

    private void RebuildHistoryTab()
    {
        HistoryHost.Children.Clear();

        var setupCard = Card(Text("首次配置", "DutyCardTitle"));
        var setupStack = (StackPanel)setupCard.Child;
        if (Duty.NeedsFirstTimeSetup)
        {
            setupStack.Children.Add(Warn("⚠ 还没有完成首次配置（开学第一周为必填项）。"));
            var go = Btn("打开配置向导", () => ShowWizardDialog(), primary: true);
            go.HorizontalAlignment = HorizontalAlignment.Left;
            go.Margin = new Thickness(0, 8, 0, 0);
            setupStack.Children.Add(go);
        }
        else
        {
            setupStack.Children.Add(Hint($"已完成首次配置 · 开学第一周：" +
                (SettingsSvc.Current.SemesterFirstWeekMonday?.ToString("yyyy-MM-dd") ?? "未设置")));
        }
        HistoryHost.Children.Add(setupCard);

        // 周覆盖
        var overrideCard = Card(Text("周覆盖记录（换组 / 暂停）", "DutyCardTitle"));
        var overrideStack = (StackPanel)overrideCard.Child;
        var overrides = _draft.WeekOverrides
            .Where(w => w.Paused || !string.IsNullOrWhiteSpace(w.GroupId))
            .OrderByDescending(w => w.WeekMonday)
            .ToList();
        if (overrides.Count == 0)
        {
            overrideStack.Children.Add(Hint("没有手动覆盖记录。在本周安排页可以“换组”或“暂停本周”。"));
        }
        else
        {
            foreach (var item in overrides)
            {
                var row = NewGrid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var date = Text($"{item.WeekMonday:yyyy-MM-dd} 起那周", medium: true);
                date.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(date, 0);
                row.Children.Add(date);
                var groupName = _draft.Groups.FirstOrDefault(g => g.Id == item.GroupId)?.Name;
                var desc = Text((item.Paused ? "已暂停" : $"换成 {(groupName ?? "未知组")}") +
                                (string.IsNullOrWhiteSpace(item.Note) ? "" : $" · {item.Note}"));
                desc.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(desc, 1);
                row.Children.Add(desc);
                var undo = SmallBtn("撤销覆盖", () =>
                {
                    Duty.SetWeekOverride(item.WeekMonday, null, false, null);
                    ReloadDraft();
                    BuildAll();
                });
                Grid.SetColumn(undo, 2);
                row.Children.Add(undo);
                row.Margin = new Thickness(0, 3, 0, 3);
                overrideStack.Children.Add(row);
            }
        }
        HistoryHost.Children.Add(overrideCard);

        // 打卡记录
        var checkCard = Card(Text("打卡记录", "DutyCardTitle"));
        var checkStack = (StackPanel)checkCard.Child;
        checkStack.Children.Add(Hint(_draft.AwardPointsOnCheckIn
            ? "打卡加分已开启（每次打卡给参与学生 +1 课堂积分）。"
            : "打卡加分未开启：可在「设置 → 值日与轮值」开启。"));
        if (_draft.CheckIns.Count == 0)
        {
            checkStack.Children.Add(Hint("还没有打卡记录。"));
        }
        else
        {
            foreach (var item in _draft.CheckIns.OrderByDescending(c => c.Date).ThenByDescending(c => c.At).Take(30))
            {
                var line = Text($"{item.Date:yyyy-MM-dd} · {string.Join("、", item.StudentIds.Select(StudentName))}" +
                                (item.Points > 0 ? $" · +{item.Points} 分" : "") +
                                $" · {item.At:HH:mm}");
                line.Margin = new Thickness(0, 2, 0, 2);
                checkStack.Children.Add(line);
            }
        }
        HistoryHost.Children.Add(checkCard);

        var noteCard = Card(Text("说明", "DutyCardTitle"));
        ((StackPanel)noteCard.Child).Children.Add(Hint(
            "v1.4 按用户要求不做打印 / CSV 导出；需要发班级群时使用顶部“复制本周文本”。\n" +
            "值日/值周提醒为可选功能（默认关闭），可在「设置 → 值日与轮值」开启。"));
        HistoryHost.Children.Add(noteCard);
    }

    // =========================================================================
    // 选择成员对话框
    // =========================================================================

    private List<string>? MultiSelectDialog(string title, IReadOnlyList<(string Key, string Label)> options, IEnumerable<string> selected)
    {
        var window = new Window
        {
            Title = title,
            Width = 430,
            Height = 580,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false,
            Background = FindResource("Brush.SurfaceAlt") as Brush ?? Brushes.WhiteSmoke,
            ResizeMode = ResizeMode.CanResize
        };
        var selectedSet = new HashSet<string>(selected ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var boxesPanel = new StackPanel { Margin = new Thickness(16) };
        var boxList = new List<CheckBox>();
        foreach (var (key, label) in options)
        {
            var box = new CheckBox
            {
                Content = label,
                IsChecked = selectedSet.Contains(key),
                Tag = key,
                FontFamily = FindResource("Font.Regular") as FontFamily,
                FontSize = 13,
                Foreground = FindResource("Brush.TextPrimary") as Brush ?? Brushes.Black,
                Margin = new Thickness(0, 4, 0, 4),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            boxList.Add(box);
            boxesPanel.Children.Add(box);
        }
        if (boxList.Count == 0)
            boxesPanel.Children.Add(new TextBlock
            {
                Text = "没有可选的成员。",
                FontFamily = FindResource("Font.Regular") as FontFamily,
                FontSize = 13,
                Foreground = FindResource("Brush.TextTertiary") as Brush ?? Brushes.Gray
            });

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = boxesPanel
        };
        var ok = new Button { Content = "确定", Style = FindResource("Button.Form") as Style, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 10, 0) };
        var cancel = new Button { Content = "取消", Style = FindResource("Button.FormGhost") as Style, Padding = new Thickness(18, 7, 18, 7) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(scroll);
        window.Content = root;

        List<string>? result = null;
        ok.Click += (_, _) =>
        {
            result = boxList.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!).ToList();
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.ShowDialog();
        return result;
    }

    // =========================================================================
    // 小工具
    // =========================================================================

    private static TextBlock Text(string text, string? styleKey = null, bool big = false, bool medium = false)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
        if (styleKey is not null && Application.Current.TryFindResource(styleKey) is Style style)
        {
            // 交给样式决定字体/字号/颜色（例如卡片小标题）
            block.Style = style;
            return block;
        }
        if (big)
        {
            block.FontFamily = (FontFamily)Application.Current.FindResource("Font.Bold");
            block.FontWeight = FontWeights.Bold;
            block.FontSize = 22;
            block.Foreground = (Brush)Application.Current.FindResource("Brush.TextPrimary");
            block.Margin = new Thickness(0, 2, 0, 4);
        }
        else if (medium)
        {
            block.FontFamily = (FontFamily)Application.Current.FindResource("Font.Medium");
            block.FontSize = 13.5;
            block.Foreground = (Brush)Application.Current.FindResource("Brush.TextPrimary");
        }
        else
        {
            block.FontFamily = (FontFamily)Application.Current.FindResource("Font.Regular");
            block.FontSize = 13;
            block.Foreground = (Brush)Application.Current.FindResource("Brush.TextPrimary");
        }
        return block;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Application.Current.FindResource("Font.Regular"),
        FontSize = 11.5,
        Foreground = (Brush)Application.Current.FindResource("Brush.TextTertiary"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 2)
    };

    private static TextBlock Warn(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Application.Current.FindResource("Font.Medium"),
        FontSize = 12,
        Foreground = (Brush)Application.Current.FindResource("Brush.Accent"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 2)
    };

    private Border Card(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        return new Border { Style = FindResource("DutyCard") as Style, Child = stack };
    }

    private static Grid NewGrid() => new() { Margin = new Thickness(0, 2, 0, 2) };

    private void AddCell(Grid row, string text, int column, bool bold = false, bool secondary = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = (FontFamily)(Application.Current.FindResource(bold ? "Font.Medium" : "Font.Regular")),
            FontSize = 12.5,
            Foreground = (Brush)Application.Current.FindResource(secondary ? "Brush.TextTertiary" : "Brush.TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(block, column);
        row.Children.Add(block);
    }

    private TextBox Input(string text, double width) => new()
    {
        Text = text,
        Width = width,
        Style = FindResource("Input.TextBox") as Style,
        VerticalContentAlignment = VerticalAlignment.Center,
        FontSize = 12.5
    };

    private Button Btn(string text, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Style = FindResource(primary ? "Button.Form" : "Button.FormGhost") as Style,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 12.5
        };
        button.Click += (_, _) => Guard(text, onClick);
        return button;
    }

    private Button SmallBtn(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = FindResource("Button.FormGhost") as Style,
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 11.5
        };
        button.Click += (_, _) => Guard(text, onClick);
        return button;
    }

    private static void Guard(string action, Action body)
    {
        try
        {
            if (!AppServices.IsInitialized) return;
            body();
        }
        catch (Exception ex)
        {
            Log.Error($"值日与轮值「{action}」失败", ex);
            MessageBox.Show($"{action}失败：{ex.Message}", "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string StudentName(string studentKey)
    {
        try
        {
            if (!AppServices.IsInitialized) return studentKey;
            var rosters = AppServices.Current.Rosters.Rosters;
            foreach (var roster in rosters)
            {
                var student = roster.Students.FirstOrDefault(s => string.Equals(s.Key, studentKey, StringComparison.Ordinal));
                if (student is not null) return student.DisplayName;
            }
        }
        catch { /* ignore */ }
        return studentKey + "（已不在花名册）";
    }

    private static List<Student> GetStudents()
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

    private static void Info(string message, MessageBoxImage image = MessageBoxImage.Information)
        => MessageBox.Show(message, "班机", MessageBoxButton.OK, image);
}
