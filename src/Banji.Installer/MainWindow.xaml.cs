using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Banji.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerEngine _engine = new();
    private string _installDir = "";
    private bool _installing;

    public MainWindow()
    {
        InitializeComponent();

        TxtVersion.Text = $"版本 v{InstallerEngine.VersionText}";

        var installed = InstallerEngine.FindInstalledDir();
        _installDir = installed ?? InstallerEngine.DefaultInstallDir;
        TxtInstallPath.Text = _installDir;

        if (installed is not null)
        {
            TxtInstalledHint.Text = $"检测到已安装的班机（{installed}），继续安装会覆盖升级，你的花名册、课表和设置不会丢失。";
            TxtInstalledHint.Visibility = Visibility.Visible;
        }
        else if (!InstallerEngine.PayloadAvailable)
        {
            TxtInstalledHint.Text = "警告：这个安装程序里没有内置安装数据（开发构建），无法真正完成安装。请使用 build\\build.ps1 生成的完整安装包。";
            TxtInstalledHint.Foreground = System.Windows.Media.Brushes.Crimson;
            TxtInstalledHint.Visibility = Visibility.Visible;
        }
    }

    // =========================================================================
    // 页面切换
    // =========================================================================

    private void SetPage(int page)
    {
        PageWelcome.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageDir.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageProgress.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageDone.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;

        if (page == 2)
        {
            BarProgress.Value = 0;
            TxtPercent.Text = "0%";
            TxtStatus.Text = "准备中…";
        }
    }

    // =========================================================================
    // 按钮事件
    // =========================================================================

    private void Next_Click(object sender, RoutedEventArgs e) => SetPage(1);

    private void Back_Click(object sender, RoutedEventArgs e) => SetPage(0);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择班机的安装位置",
            Multiselect = false,
        };
        if (Directory.Exists(_installDir)) dialog.InitialDirectory = _installDir;
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            TxtInstallPath.Text = dialog.FolderName;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        string targetDir;
        try
        {
            targetDir = InstallerEngine.NormalizeTarget(TxtInstallPath.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "安装位置不正确", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TxtInstallPath.Text = targetDir;
        _installing = true;
        SetPage(2);

        IProgress<InstallProgress> progress = new Progress<InstallProgress>(p =>
        {
            BarProgress.Value = p.Percent;
            TxtPercent.Text = p.Percent + "%";
            TxtStatus.Text = p.Message;
        });

        try
        {
            var result = await Task.Run(() => _engine.Install(targetDir, p => progress.Report(p)));
            _installDir = result.InstallDir;
            TxtDonePath.Text = result.InstallDir;
            TxtProgressTitle.Text = "安装完成";
            SetPage(3);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                ex.Message + "\n\n安装日志：\n" + InstallerLog.LogPath,
                "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            SetPage(1);
        }
        finally
        {
            _installing = false;
        }
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (ChkLaunch.IsChecked == true) TryLaunchApp();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_installing)
        {
            e.Cancel = true;
            MessageBox.Show(this, "安装正在进行中，请等安装完成后再关闭窗口。", "班机安装程序",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        base.OnClosing(e);
    }

    // =========================================================================
    // 窗口细节
    // =========================================================================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { /* ignore */ }
    }

    private void TryLaunchApp()
    {
        try
        {
            var exe = Path.Combine(_installDir, InstallerEngine.AppExeName);
            if (!File.Exists(exe))
                throw new FileNotFoundException("找不到班机主程序。", exe);
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = _installDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动班机失败：" + ex.Message + "\n\n你可以从桌面或开始菜单手动启动。",
                "班机", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
