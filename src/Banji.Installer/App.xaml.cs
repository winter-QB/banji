using System.IO;
using System.Threading;
using System.Windows;

namespace Banji.Installer;

/// <summary>安装器入口：区分「界面安装 / 静默安装 / 卸载」三种运行方式。</summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = InstallerArgs.Parse(e.Args);

        // 单实例保护：避免两个安装/卸载同时改注册表和字体
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\Banji.Installer.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            if (args.Silent)
            {
                InstallerLog.Error("已有另一个安装程序实例正在运行，静默任务退出。");
                Environment.Exit(1);
            }
            MessageBox.Show("班机安装程序已经在运行了。请先完成当前窗口里的操作。", "班机安装程序",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
            return;
        }

        InstallerLog.Info($"===== 安装器启动：{Environment.CommandLine} =====");

        if (args.Silent)
        {
            // 静默模式：不显示任何窗口，只用退出码表示结果（0 成功 / 1 失败）
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = args.Uninstall ? SilentRunner.RunUninstall() : SilentRunner.RunInstall(args.TargetDir);
            Environment.Exit(code);
            return;
        }

        if (args.Uninstall)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunUninstallUi();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>双击「卸载班机.exe」或带 /uninstall 运行时的中文确认与卸载流程。</summary>
    private static void RunUninstallUi()
    {
        var installDir = InstallerEngine.FindInstalledDir();
        if (installDir is null)
        {
            MessageBox.Show("没有找到已安装的班机，可能已经卸载过了。", "卸载班机",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
            return;
        }

        var answer = MessageBox.Show(
            $"确定要卸载班机吗？\n\n" +
            $"将会删除：\n" +
            $"  · 程序文件（{installDir}）\n" +
            $"  · 桌面与开始菜单里的快捷方式\n" +
            $"  · 已安装的鸿蒙字体\n\n" +
            $"你的花名册、课表、设置等数据保存在：\n" +
            $"  {InstallerEngine.DataDir}\n" +
            $"这些数据不会被删除。",
            "卸载班机", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            Environment.Exit(0);
            return;
        }

        try
        {
            var result = new InstallerEngine().Uninstall(installDir);
            MessageBox.Show("班机已卸载。\n\n如果不再需要，可以手动删除数据目录：\n" + InstallerEngine.DataDir,
                "卸载班机", MessageBoxButton.OK, MessageBoxImage.Information);
            if (result.NeedSelfCleanup)
            {
                CleanupScheduler.ScheduleDirectoryDelete(result.InstallDir, result.SelfPath);
            }
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            InstallerLog.Error("卸载失败", ex);
            MessageBox.Show("卸载失败：" + ex.Message + "\n\n详细信息见日志：\n" + InstallerLog.LogPath,
                "卸载班机", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
