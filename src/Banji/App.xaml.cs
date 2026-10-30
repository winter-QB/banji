using System.Windows;
using System.Windows.Threading;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private IpcServer? _ipc;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\Banji.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // 已有实例：把参数转发过去（无参数则只是“显示/唤起”）
            IpcServer.TrySendCommand(e.Args.Length == 0 ? new[] { "--cmd", "show" } : e.Args);
            // 注意：在 OnStartup 里调用 Application.Shutdown() 可能引发未处理异常导致进程不退出，
            // 这里直接 Environment.Exit 保证第二实例立刻退出。
            Environment.Exit(0);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Error("未处理异常(AppDomain)", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未处理异常(Task)", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("未处理异常(UI)", args.Exception);
            args.Handled = true;
        };

        try
        {
            Log.Info("=== 班机启动 ===");
            AppServices.Create();
            AppServices.Current.Start();
            _ipc = new IpcServer(HandleCommand);
            _ipc.Start();
        }
        catch (Exception ex)
        {
            Log.Error("启动失败", ex);
            MessageBox.Show($"班机启动失败：{ex.Message}\n\n详情见 {AppPaths.LogsDir}", "班机",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
        }
    }

    /// <summary>IPC 命令（第二实例 / QA 自动化调用）。</summary>
    private void HandleCommand(string[] args)
    {
        AppServices.RunOnUi(() =>
        {
            try
            {
                DispatchCommand(args);
            }
            catch (Exception ex)
            {
                Log.Error($"命令执行失败: {string.Join(' ', args)}", ex);
            }
        });
    }

    private static void DispatchCommand(string[] args)
    {
        if (args.Length == 0) return;
        var svc = AppServices.Current;
        var cmd = args[0].TrimStart('-').ToLowerInvariant();
        if (cmd == "cmd" && args.Length > 1)
        {
            cmd = args[1].ToLowerInvariant();
            args = args[1..];
        }

        switch (cmd)
        {
            case "show":
                svc.PetWindow?.Show();
                svc.PetWindow?.Activate();
                break;
            case "pick":
                svc.Picker.Pick();
                break;
            case "settings":
                svc.ShowSettings();
                break;
            case "dashboard":
                svc.ShowDashboard();
                break;
            case "rosters":
                svc.ShowRosterManager();
                break;
            case "timetable":
                svc.ShowTimetableEditor();
                break;
            case "periods":
                svc.ShowPeriodSettings();
                break;
            case "import-roster":
                svc.ShowRosterImport(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null);
                break;
            case "import-timetable":
                svc.ShowTimetableImport(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null);
                break;
            case "timer":
            {
                var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                switch (sub)
                {
                    case "countdown":
                        var secs = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 300;
                        svc.Timers.StartCountdown(TimeSpan.FromSeconds(secs));
                        break;
                    case "stopwatch":
                        svc.Timers.StartStopwatch();
                        break;
                    case "cancel":
                        svc.Timers.Cancel();
                        break;
                    default:
                        svc.ShowTimerDialog(TimerKind.Countdown);
                        break;
                }
                break;
            }
            case "notice":
            {
                var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                if (sub == "start") svc.PreviewClassStartNotice();
                else if (sub == "end") svc.PreviewClassEndNotice();
                else if (sub == "timeout") svc.PreviewTimerTimeout();
                break;
            }
            case "expand":
                svc.Settings.Update(x => x.TimetableExpanded = args.Length > 1 && args[1].Equals("true", StringComparison.OrdinalIgnoreCase));
                svc.Clock.Refresh();
                break;
            case "scale":
                if (args.Length > 1 && double.TryParse(args[1], out var scale))
                    svc.Settings.Update(x => x.UiScale = scale);
                break;
            case "seed-demo":
                var report = DevSeed.Run();
                Log.Info($"演示数据: {report}");
                break;
            case "exit":
            case "quit":
                svc.Shutdown();
                break;
            default:
                Log.Warn($"未知命令: {string.Join(' ', args)}");
                break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipc?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
