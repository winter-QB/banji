using System.Windows;
using System.Windows.Controls;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Views;

/// <summary>
/// 花名册管理窗口：左侧花名册列表（数量无上限），右侧学生表格。
/// 支持手动新建、导入、编辑信息、删除、设为当前、排序方式、抽签显示模式、成绩列开关。
/// </summary>
public partial class RosterWindow : Window
{
    private readonly ObservableCollection<RosterListItem> _items = new();
    private readonly ObservableCollection<Student> _students = new();
    private bool _suppressEvents;
    private bool _initialized;
    private bool _subscribed;
    private string? _selectedRosterId;

    public RosterWindow()
    {
        InitializeComponent();
        RosterList.ItemsSource = _items;
        StudentGrid.ItemsSource = _students;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private static IRosterService Rosters => AppServices.Current.Rosters;

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            Rosters.Changed += OnRostersChanged;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            Log.Error("花名册窗口订阅 Changed 失败", ex);
        }
        RefreshAll(keepSelection: false);
    }

    private void OnClosed(object sender, EventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        try
        {
            AppServices.Current.Rosters.Changed -= OnRostersChanged;
        }
        catch
        {
            // 退出阶段忽略
        }
    }

    private void OnRostersChanged(object? sender, EventArgs e)
    {
        AppServices.RunOnUi(() => RefreshAll(keepSelection: true));
    }

    // =========================================================================
    // 刷新
    // =========================================================================

    private void RefreshAll(bool keepSelection)
    {
        var rosters = Rosters.Rosters;
        var activeId = Rosters.Active?.Id;
        var targetId = keepSelection ? _selectedRosterId ?? activeId : activeId;

        _suppressEvents = true;
        try
        {
            _items.Clear();
            foreach (var roster in rosters)
                _items.Add(new RosterListItem(roster, string.Equals(roster.Id, activeId, StringComparison.Ordinal)));

            RosterCountText.Text = $"{rosters.Count} 份";

            if (rosters.Count == 0)
            {
                _selectedRosterId = null;
                RosterList.SelectedItem = null;
                SetEmptyState(true);
                return;
            }

            SetEmptyState(false);
            var chosen = _items.FirstOrDefault(i => string.Equals(i.Roster.Id, targetId, StringComparison.Ordinal)) ?? _items[0];
            RosterList.SelectedItem = chosen;
            _selectedRosterId = chosen.Roster.Id;
        }
        finally
        {
            _suppressEvents = false;
        }

        if (_selectedRosterId is not null) LoadDetail(_selectedRosterId);
    }

    private void SetEmptyState(bool empty)
    {
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DetailArea.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        DeleteRosterButton.IsEnabled = !empty;
        SetActiveButton.IsEnabled = !empty;
    }

    private void LoadDetail(string rosterId)
    {
        var roster = Rosters.Get(rosterId);
        if (roster is null)
        {
            RefreshAll(keepSelection: true);
            return;
        }

        _selectedRosterId = roster.Id;
        var activeId = Rosters.Active?.Id;
        var isActive = string.Equals(roster.Id, activeId, StringComparison.Ordinal);

        DetailNameText.Text = roster.Name;
        DetailMetaText.Text = BuildMeta(roster);
        SetActiveButton.IsEnabled = !isActive;
        SetActiveButton.Content = isActive ? "当前花名册" : "设为当前";

        _suppressEvents = true;
        try
        {
            DetailNameBox.Text = roster.Name;
            DetailSubjectBox.Text = roster.Subject ?? "";
            SortCombo.SelectedIndex = (int)roster.SortMode;
            DisplayCombo.SelectedIndex = roster.DisplayModeOverride switch
            {
                PickDisplayMode.StudentId => 1,
                PickDisplayMode.Name => 2,
                PickDisplayMode.Both => 3,
                _ => 0
            };

            _students.Clear();
            var sorted = roster.SortedStudents();
            foreach (var student in sorted) _students.Add(student);
            StudentCountText.Text = $"共 {sorted.Count} 人";
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateScoreColumnVisibility();
    }

    private static string BuildMeta(Roster roster)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(roster.Subject)) parts.Add(roster.Subject!.Trim());
        parts.Add($"{roster.Students.Count(s => s.IsValid)} 人");
        parts.Add(roster.SortMode switch
        {
            RosterSortMode.Score => "按成绩排序",
            RosterSortMode.Name => "按姓名排序",
            RosterSortMode.StudentId => "按学号排序",
            _ => "原始顺序"
        });
        parts.Add(roster.DisplayModeOverride switch
        {
            PickDisplayMode.StudentId => "抽签只显示学号",
            PickDisplayMode.Name => "抽签只显示姓名",
            PickDisplayMode.Both => "抽签显示学号+姓名",
            _ => "抽签显示跟随全局"
        });
        if (!string.IsNullOrWhiteSpace(roster.SourceFile))
            parts.Add("来源：" + Path.GetFileName(roster.SourceFile));
        return string.Join("  ·  ", parts);
    }

    private void UpdateScoreColumnVisibility()
    {
        if (StudentGrid.Columns.Count < 3) return;
        StudentGrid.Columns[2].Visibility = ShowScoreCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // =========================================================================
    // 交互
    // =========================================================================

    private void OnRosterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (RosterList.SelectedItem is RosterListItem item) LoadDetail(item.Roster.Id);
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _selectedRosterId is null) return;
        var roster = Rosters.Get(_selectedRosterId);
        if (roster is null) return;

        var mode = SortCombo.SelectedIndex switch
        {
            1 => RosterSortMode.Score,
            2 => RosterSortMode.Name,
            3 => RosterSortMode.StudentId,
            _ => RosterSortMode.Original
        };
        if (roster.SortMode == mode) return;

        roster.SortMode = mode;
        Rosters.Update(roster);
        StatusText.Text = "排序方式已改为：" + mode switch
        {
            RosterSortMode.Score => "按成绩",
            RosterSortMode.Name => "按姓名",
            RosterSortMode.StudentId => "按学号",
            _ => "原始顺序"
        };
    }

    private void OnDisplayModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _selectedRosterId is null) return;
        var roster = Rosters.Get(_selectedRosterId);
        if (roster is null) return;

        PickDisplayMode? mode = DisplayCombo.SelectedIndex switch
        {
            1 => PickDisplayMode.StudentId,
            2 => PickDisplayMode.Name,
            3 => PickDisplayMode.Both,
            _ => null
        };
        if (roster.DisplayModeOverride == mode) return;

        roster.DisplayModeOverride = mode;
        Rosters.Update(roster);
        StatusText.Text = mode is null
            ? "抽签显示已改为跟随全局设置（可在“设置”里调整）"
            : "抽签显示已改为：" + mode switch
            {
                PickDisplayMode.StudentId => "只显示学号",
                PickDisplayMode.Name => "只显示姓名",
                _ => "学号 + 姓名"
            };
    }

    private void OnShowScoreChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        UpdateScoreColumnVisibility();
    }

    private void OnSetActiveClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRosterId is null) return;
        Rosters.SetActive(_selectedRosterId);
        RefreshAll(keepSelection: true);
        StatusText.Text = "已设为当前抽签花名册";
    }

    private void OnToggleEditClick(object sender, RoutedEventArgs e)
    {
        EditPanel.Visibility = EditPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (EditPanel.Visibility == Visibility.Visible) DetailNameBox.Focus();
    }

    private void OnSaveEditClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRosterId is null) return;
        var roster = Rosters.Get(_selectedRosterId);
        if (roster is null) return;

        roster.Name = string.IsNullOrWhiteSpace(DetailNameBox.Text) ? "未命名花名册" : DetailNameBox.Text.Trim();
        roster.Subject = string.IsNullOrWhiteSpace(DetailSubjectBox.Text) ? null : DetailSubjectBox.Text.Trim();
        Rosters.Update(roster);
        EditPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "花名册信息已保存";
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRosterId is not null) LoadDetail(_selectedRosterId);
        EditPanel.Visibility = Visibility.Collapsed;
    }

    private void OnSaveStudentsClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRosterId is null) return;
        var roster = Rosters.Get(_selectedRosterId);
        if (roster is null) return;

        StudentGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        var valid = new List<Student>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dropped = 0;
        foreach (var student in roster.Students)
        {
            if (student is null) continue;
            student.Id = (student.Id ?? "").Trim();
            student.Name = (student.Name ?? "").Trim();
            if (!student.IsValid || !seen.Add(student.Key))
            {
                dropped++;
                continue;
            }
            valid.Add(student);
        }

        if (valid.Count == 0)
        {
            MessageBox.Show(this, "请至少填写一位学生的姓名或学号。", "无法保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        roster.Students = valid;
        Rosters.Update(roster);
        StatusText.Text = dropped > 0
            ? $"已保存 {valid.Count} 人，忽略了 {dropped} 行空行或重复项"
            : $"已保存 {valid.Count} 人";
    }

    private void OnNewManualClick(object sender, RoutedEventArgs e)
    {
        AppServices.Current.ShowManualRosterDialog();
    }

    private void OnImportRosterClick(object sender, RoutedEventArgs e)
    {
        AppServices.Current.ShowRosterImport();
    }

    private void OnDeleteRosterClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRosterId is null) return;
        var roster = Rosters.Get(_selectedRosterId);
        if (roster is null) return;

        var confirm = MessageBox.Show(this,
            $"确定要删除花名册“{roster.Name}”（{roster.Students.Count(s => s.IsValid)} 人）吗？\n删除后无法恢复。",
            "删除花名册", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var removedName = roster.Name;
        Rosters.Remove(roster.Id);
        StatusText.Text = $"已删除花名册“{removedName}”";
    }
}

/// <summary>左侧列表项（花名册摘要）。</summary>
internal sealed class RosterListItem
{
    public RosterListItem(Roster roster, bool isActive)
    {
        Roster = roster;
        IsActive = isActive;
    }

    public Roster Roster { get; }
    public bool IsActive { get; }
    public string Name => Roster.Name;
    public string CountText => $"{Roster.Students.Count(s => s.IsValid)} 人";
    public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Roster.Subject)) parts.Add(Roster.Subject!.Trim());
            parts.Add(Roster.SortMode switch
            {
                RosterSortMode.Score => "按成绩",
                RosterSortMode.Name => "按姓名",
                RosterSortMode.StudentId => "按学号",
                _ => "原始顺序"
            });
            return string.Join(" · ", parts);
        }
    }
}
