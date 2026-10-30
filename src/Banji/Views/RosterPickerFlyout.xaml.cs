using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Views;

/// <summary>
/// 抽签花名册选择小窗（功能区“抽签 +”用）：无边框、置顶，点选后设为当前并关闭。
/// </summary>
public partial class RosterPickerFlyout : Window
{
    private bool _suppressDeactivate = true;

    public RosterPickerFlyout()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildList();
        PositionNearBottomRight();
        _suppressDeactivate = false;
        Activate();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivate) return;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void PositionNearBottomRight()
    {
        try
        {
            var workArea = SystemParameters.WorkArea;
            var height = ActualHeight > 0 ? ActualHeight : MaxHeight;
            Left = Math.Max(workArea.Left + 8, workArea.Right - Width - 24);
            Top = Math.Max(workArea.Top + 8, workArea.Bottom - height - 24);
        }
        catch (Exception ex)
        {
            Log.Error("定位花名册选择窗失败", ex);
        }
    }

    private void BuildList()
    {
        var items = new List<PickerRosterItem>();
        try
        {
            var service = AppServices.Current.Rosters;
            var activeId = service.Active?.Id;
            foreach (var roster in service.Rosters)
            {
                items.Add(new PickerRosterItem(roster, string.Equals(roster.Id, activeId, StringComparison.Ordinal)));
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取花名册失败", ex);
        }

        RosterItems.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRosterItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string rosterId }) return;
        try
        {
            _suppressDeactivate = true;
            AppServices.Current.Rosters.SetActive(rosterId);
            var roster = AppServices.Current.Rosters.Get(rosterId);
            Log.Info($"抽签花名册切换为：{roster?.Name ?? rosterId}");
        }
        catch (Exception ex)
        {
            Log.Error("切换抽签花名册失败", ex);
        }
        Close();
    }

    private void OnManageClick(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        try
        {
            AppServices.Current.ShowRosterManager();
        }
        catch (Exception ex)
        {
            Log.Error("打开花名册管理失败", ex);
        }
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>选择窗列表项。</summary>
internal sealed class PickerRosterItem
{
    public PickerRosterItem(Roster roster, bool isActive)
    {
        Id = roster.Id;
        Name = roster.Name;
        IsActive = isActive;
        var count = roster.Students.Count(s => s.IsValid);
        CountText = $"{count} 人";
        Subtitle = string.IsNullOrWhiteSpace(roster.Subject) ? "未设置学科" : roster.Subject!.Trim();
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsActive { get; }
    public string CountText { get; }
    public string Subtitle { get; }
    public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
}
