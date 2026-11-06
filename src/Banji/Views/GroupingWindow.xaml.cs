using System.Text;
using System.Windows;
using System.Windows.Controls;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Microsoft.Win32;

namespace Banji.Views;

/// <summary>
/// 随机分组窗口（第二轮新增）：选花名册 / 组数 / 是否按成绩均衡 → 生成结果卡片。
/// 支持重新分组、复制文本、导出 CSV。窗口由 <c>AppServices.ShowGroupingWindow()</c> 创建（无参构造）。
/// </summary>
public partial class GroupingWindow : Window
{
    /// <summary>UI 上允许选择的最大组数（服务还会按人数收敛）。</summary>
    private const int MaxUiGroupCount = 8;

    private readonly ObservableCollection<RosterChoiceItem> _rosterChoices = new();
    private readonly ObservableCollection<GroupCardItem> _cards = new();
    private GroupingResult? _result;
    private bool _initialized;
    private bool _suppressEvents;
    private bool _subscribed;

    public GroupingWindow()
    {
        InitializeComponent();
        RosterCombo.ItemsSource = _rosterChoices;
        ResultList.ItemsSource = _cards;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private static IGroupingService Grouping => AppServices.Current.Grouping;

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            AppServices.Current.Rosters.Changed += OnRostersChanged;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            Log.Error("分组窗口订阅花名册变化失败", ex);
        }
        RefreshRosterChoices();
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
        AppServices.RunOnUi(RefreshRosterChoices);
    }

    // =========================================================================
    // 选项
    // =========================================================================

    private void RefreshRosterChoices()
    {
        try
        {
            var service = AppServices.Current.Rosters;
            var activeId = service.Active?.Id;
            var previousId = (RosterCombo.SelectedItem as RosterChoiceItem)?.Id;

            _suppressEvents = true;
            try
            {
                _rosterChoices.Clear();
                foreach (var roster in service.Rosters)
                    _rosterChoices.Add(new RosterChoiceItem(roster, roster.Students.Count(s => s.IsValid)));

                var targetId = previousId ?? activeId;
                var index = targetId is null
                    ? -1
                    : _rosterChoices.ToList().FindIndex(c => string.Equals(c.Id, targetId, StringComparison.Ordinal));
                if (index < 0 && _rosterChoices.Count > 0) index = 0;
                RosterCombo.SelectedIndex = index;
            }
            finally
            {
                _suppressEvents = false;
            }

            UpdateGroupCountChoices();
            UpdateControlsState();
        }
        catch (Exception ex)
        {
            Log.Error("加载分组窗口花名册失败", ex);
            StatusText.Text = "加载花名册失败：" + ex.Message;
        }
    }

    private void UpdateGroupCountChoices()
    {
        var studentCount = SelectedRosterStudentCount();
        var maxGroups = Math.Min(MaxUiGroupCount, studentCount);
        var previous = GroupCountCombo.SelectedItem is int count ? count : 4;

        _suppressEvents = true;
        try
        {
            GroupCountCombo.Items.Clear();
            for (var n = 2; n <= maxGroups; n++) GroupCountCombo.Items.Add(n);
            if (GroupCountCombo.Items.Count > 0)
            {
                var target = Math.Clamp(previous, 2, maxGroups);
                GroupCountCombo.SelectedItem = target;
                if (GroupCountCombo.SelectedIndex < 0) GroupCountCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private int SelectedRosterStudentCount()
        => (RosterCombo.SelectedItem as RosterChoiceItem)?.StudentCount ?? 0;

    private void UpdateControlsState()
    {
        // XAML 加载期间（InitializeComponent）控件可能还没创建完，直接跳过
        if (!_initialized || StatusText is null || GenerateButton is null || RosterCombo is null) return;

        var studentCount = SelectedRosterStudentCount();
        var hasRoster = RosterCombo.SelectedItem is RosterChoiceItem item && item.StudentCount > 0;
        var canGenerate = hasRoster && studentCount >= 2;

        GenerateButton.IsEnabled = canGenerate;
        RegenerateButton.IsEnabled = canGenerate && _result is not null;
        CopyButton.IsEnabled = _result is not null;
        ExportButton.IsEnabled = _result is not null;

        if (_rosterChoices.Count == 0)
        {
            StatusText.Text = "还没有花名册：请先在“花名册管理”里导入或手动录入一份学生名单。";
        }
        else if (!hasRoster)
        {
            StatusText.Text = "这份花名册还没有学生，请先在“花名册管理”里补充名单。";
        }
        else if (!canGenerate)
        {
            StatusText.Text = $"至少需要 2 名学生才能分组（当前只有 {studentCount} 人）。";
        }
        else if (_result is null)
        {
            StatusText.Text = "选择组数后点“生成分组”。分组结果会显示在上方，可复制或导出 CSV。";
        }
    }

    private void OnRosterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppressEvents) return;
        UpdateGroupCountChoices();
        UpdateControlsState();
    }

    private void OnOptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppressEvents) return;
        UpdateControlsState();
    }

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressEvents) return;
        UpdateControlsState();
    }

    // =========================================================================
    // 生成 / 复制 / 导出
    // =========================================================================

    private void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (RosterCombo.SelectedItem is not RosterChoiceItem rosterItem)
        {
            StatusText.Text = "请先选择一份花名册。";
            return;
        }

        var options = new GroupingOptions
        {
            GroupCount = GroupCountCombo.SelectedItem is int count ? count : 4,
            BalanceByScore = BalanceCheck.IsChecked == true,
            Title = "随机分组结果"
        };

        try
        {
            var result = AppServices.Current.Grouping.Group(options, rosterItem.Id);
            ShowResult(result);
            StatusText.Text = $"已把“{result.RosterName}”的 {result.TotalStudents} 名学生分成 {result.Groups.Count} 组。";

            try
            {
                if (AppServices.Current.Settings.Current.EnableGrowth)
                    AppServices.Current.Growth.RecordGroup();
            }
            catch (Exception growthEx)
            {
                Log.Error("养成统计记录分组失败", growthEx);
            }
        }
        catch (Exception ex)
        {
            Log.Error("生成随机分组失败", ex);
            StatusText.Text = "分组失败：" + ex.Message;
            MessageBox.Show(this, ex.Message, "无法分组", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowResult(GroupingResult result)
    {
        _result = result;
        _cards.Clear();
        foreach (var group in result.Groups)
        {
            var scored = group.Students.Where(s => s.Score.HasValue).Select(s => s.Score!.Value).ToList();
            var meta = scored.Count == 0
                ? $"{group.Students.Count} 人"
                : $"{group.Students.Count} 人 · 平均分 {scored.Average():0.#}";

            _cards.Add(new GroupCardItem
            {
                Title = group.Name,
                Meta = meta,
                Members = group.Students.Count == 0
                    ? "（空组）"
                    : string.Join("、", group.Students.Select(s => s.DisplayName))
            });
        }

        EmptyResultPanel.Visibility = Visibility.Collapsed;
        RegenerateButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
        ExportButton.IsEnabled = true;
        UpdateControlsState();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        try
        {
            Clipboard.SetText(_result.ToPlainText());
            StatusText.Text = "分组结果已复制到剪贴板，可直接粘贴到微信 / Word。";
        }
        catch (Exception ex)
        {
            Log.Error("复制分组结果失败", ex);
            StatusText.Text = "复制失败，请重试一次（剪贴板可能被其他程序占用）。";
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "导出分组结果",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"分组结果_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("组别,序号,学号,姓名,成绩,积分");
            foreach (var group in _result.Groups)
            {
                var index = 0;
                foreach (var student in group.Students)
                {
                    index++;
                    sb.AppendLine(string.Join(",",
                        Csv(group.Name),
                        index.ToString(),
                        Csv(student.DisplayId),
                        Csv(student.DisplayName),
                        student.Score?.ToString("0.##") ?? "",
                        student.Points.ToString()));
                }
            }

            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText.Text = "已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            Log.Error("导出分组 CSV 失败", ex);
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private static string Csv(string? value)
    {
        var text = value ?? "";
        if (text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>花名册下拉项。</summary>
internal sealed class RosterChoiceItem
{
    public RosterChoiceItem(Roster roster, int studentCount)
    {
        Id = roster.Id;
        Name = roster.Name;
        StudentCount = studentCount;
        Display = $"{roster.Name}（{studentCount} 人）";
    }

    public string Id { get; }
    public string Name { get; }
    public int StudentCount { get; }
    public string Display { get; }

    public override string ToString() => Display;
}

/// <summary>结果卡片数据。</summary>
internal sealed class GroupCardItem
{
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Members { get; init; } = "";
}
