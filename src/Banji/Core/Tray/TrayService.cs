using System.Drawing;
using System.Windows;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Views;
using WinForms = System.Windows.Forms;

namespace Banji.Core.Tray;

/// <summary>
/// v1.7.0：系统托盘图标。屏幕比例/分辨率变化后窗口可能跑到屏幕外，
/// 宠物和课表都看不见时，用户可以从托盘把窗口拉回来、显示/隐藏宠物与课表、打开设置或退出。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _petItem;
    private readonly WinForms.ToolStripMenuItem _tableItem;
    private readonly WinForms.ToolStripMenuItem _settingsItem;
    private bool _disposed;

    public TrayService()
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "班机 — 课堂管理桌面宠物",
            Visible = true
        };
        _menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        _petItem = new WinForms.ToolStripMenuItem("隐藏宠物");
        _petItem.Click += (_, _) => TogglePet();
        _tableItem = new WinForms.ToolStripMenuItem("隐藏课表");
        _tableItem.Click += (_, _) => TogglePanel();
        var bringItem = new WinForms.ToolStripMenuItem("全部显示并拉回屏幕");
        bringItem.Click += (_, _) => BringBack();
        _settingsItem = new WinForms.ToolStripMenuItem("设置…");
        _settingsItem.Click += (_, _) => Run(() => AppServices.Current.ShowSettings());
        var exitItem = new WinForms.ToolStripMenuItem("退出班机");
        exitItem.Click += (_, _) => Run(() => AppServices.Current.Shutdown());
        _menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            _petItem,
            _tableItem,
            bringItem,
            new WinForms.ToolStripSeparator(),
            _settingsItem,
            new WinForms.ToolStripSeparator(),
            exitItem
        });
        _menu.Opening += (_, _) => RefreshState();
        _icon.ContextMenuStrip = _menu;
        _icon.DoubleClick += (_, _) => BringBack();
        try
        {
            AppServices.Current.Settings.Changed += OnSettingsChanged;
        }
        catch { /* 服务未就绪时忽略 */ }
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }
        catch { /* 忽略：某些受限环境没有 SystemEvents */ }
        RefreshState();
        Log.Info("托盘图标已创建（右键可显示/隐藏宠物与课表、拉回屏幕、退出）");
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico is not null) return ico;
            }
        }
        catch { /* fallback */ }
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/banji.ico"));
            if (res is not null) return new Icon(res.Stream);
        }
        catch { /* fallback */ }
        return SystemIcons.Application;
    }

    private static void Run(Action action)
    {
        try
        {
            var app = Application.Current;
            if (app is null) { action(); return; }
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { action(); }
                catch (Exception ex) { Log.Error("托盘菜单操作失败", ex); }
            }));
        }
        catch (Exception ex) { Log.Error("托盘菜单调度失败", ex); }
    }

    private void TogglePet()
    {
        Run(() =>
        {
            var svc = AppServices.Current;
            svc.Settings.Update(s => s.HidePet = !s.HidePet);
            svc.Clock.Refresh();
            RefreshState();
        });
    }

    private void TogglePanel()
    {
        Run(() =>
        {
            var svc = AppServices.Current;
            svc.Settings.Update(s => s.ShowTimetablePanel = !s.ShowTimetablePanel);
            svc.Clock.Refresh();
            RefreshState();
        });
    }

    private void BringBack()
    {
        Run(() =>
        {
            var svc = AppServices.Current;
            svc.Settings.Update(s =>
            {
                s.HidePet = false;
                s.ShowTimetablePanel = true;
            });
            svc.PetWindow?.BringWindowsBackToScreen();
            RefreshState();
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // 屏幕比例/分辨率变化后把宠物和课表拉回工作区（比如 3:2 切到 16:9）。
        Run(() =>
        {
            AppServices.Current.PetWindow?.BringWindowsBackToScreen();
            RefreshState();
        });
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => RefreshState();

    private void RefreshState()
    {
        try
        {
            var s = AppServices.Current.Settings.Current;
            _petItem.Text = s.HidePet ? "显示宠物" : "隐藏宠物";
            _tableItem.Text = s.ShowTimetablePanel ? "隐藏课表" : "显示课表";
        }
        catch { /* 服务未就绪 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { AppServices.Current.Settings.Changed -= OnSettingsChanged; } catch { /* ignore */ }
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { /* ignore */ }
        try { _icon.Visible = false; _icon.Dispose(); } catch { /* ignore */ }
        try { _menu.Dispose(); } catch { /* ignore */ }
    }
}
