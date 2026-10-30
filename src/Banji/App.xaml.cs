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
            case "qa-transition-demo":
                QaTransitionDemo(svc);
                break;
            case "qa-transition-restore":
                QaTransitionRestore(svc);
                break;
            case "seed-demo":
                var report = DevSeed.Run();
                Log.Info($"演示数据: {report}");
                break;
            case "font-check":
                var fontReport = FontCheck.Run();
                Log.Info(fontReport);
                break;
            case "menu":
                svc.ShowMenuFlyout();
                break;
            case "roster-picker":
                svc.ShowRosterPicker();
                break;
            case "close-windows":
                svc.CloseAuxiliaryWindows();
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

    // =========================================================================
    // QA：上下课过渡实测（临时把课表改成“当前时刻附近有课”，验证自动全屏提示/下课超时行）
    // =========================================================================
    private static WeekTimetable? _qaTimetableBackup;

    private static void QaTransitionDemo(AppServices svc)
    {
        if (_qaTimetableBackup is null)
            _qaTimetableBackup = svc.Timetable.Current.Clone();

        var now = DateTime.Now;
        var demo = new WeekTimetable
        {
            SourceFile = "QA",
            UpdatedAt = now,
            Periods = new List<ClassPeriod>
            {
                new() { Index = 1, Name = "当前课", Start = (now - TimeSpan.FromMinutes(5)).TimeOfDay, End = (now + TimeSpan.FromSeconds(8)).TimeOfDay },
                new() { Index = 2, Name = "下一节", Start = (now + TimeSpan.FromSeconds(20)).TimeOfDay, End = (now + TimeSpan.FromMinutes(2)).TimeOfDay }
            },
            Breaks = new List<BreakSegment>()
        };
        demo.Days[now.DayOfWeek] = new List<TimetableEntry>
        {
            new() { PeriodIndex = 1, Course = "语文" },
            new() { PeriodIndex = 2, Course = "数学" }
        };
        svc.Timetable.SetTimetable(demo);
        Log.Info("QA：已切换到上下课过渡演示课表（8 秒后下课，20 秒后上课）");
    }

    private static void QaTransitionRestore(AppServices svc)
    {
        if (_qaTimetableBackup is null) { Log.Warn("QA：没有可恢复的课表备份"); return; }
        svc.Timetable.SetTimetable(_qaTimetableBackup);
        _qaTimetableBackup = null;
        Log.Info("QA：已恢复原课表");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipc?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
