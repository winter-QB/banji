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

        // 开发/QA：BANJI_INSTANCE_SUFFIX 环境变量可隔离出独立实例（独立 mutex/管道），
        // 用于机器上存在无法结束的旧实例时做发布版验证。正式使用不设置该变量。
        var instanceSuffix = Environment.GetEnvironmentVariable("BANJI_INSTANCE_SUFFIX");
        if (!string.IsNullOrWhiteSpace(instanceSuffix))
        {
            var safeSuffix = new string(instanceSuffix.Where(char.IsLetterOrDigit).ToArray());
            if (safeSuffix.Length == 0) safeSuffix = "qa";
            IpcServer.PipeName = "Banji.Ipc." + safeSuffix;
            Environment.SetEnvironmentVariable("BANJI_INSTANCE_SUFFIX", safeSuffix);
        }

        var mutexName = @"Local\Banji.SingleInstance" +
                        (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BANJI_INSTANCE_SUFFIX"))
                            ? ""
                            : "." + Environment.GetEnvironmentVariable("BANJI_INSTANCE_SUFFIX"));
        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
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
            case "pat":
                svc.PetWindow?.TestPat();
                break;
            case "quip":
                svc.PetWindow?.ShowTestQuip(args.Length > 1 ? string.Join(' ', args[1..]) : null);
                break;
            case "settings":
            {
                var w = svc.ShowSettings();
                if (args.Length > 1 && !args[1].StartsWith("--")) w.InitialSection = args[1];
                break;
            }
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
            case "grouping":
                svc.ShowGroupingWindow();
                break;
            case "ring-calibration":
                svc.ShowRingCalibrationWindow();
                break;
            case "dismiss-notices":
                svc.Notices.DismissAll();
                Log.Info("QA：已关闭所有提醒（全屏+气泡）");
                break;
            case "collapse-pet":
                svc.PetWindow?.CollapseToEdge();
                Log.Info("QA：宠物已收进侧边");
                break;
            case "expand-pet":
                svc.PetWindow?.ExpandFromEdge();
                Log.Info("QA：宠物已展开");
                break;
            case "fold-timetable":
                svc.PetWindow?.FoldTimetable();
                Log.Info("QA：课表已折叠");
                break;
            case "unfold-timetable":
                svc.PetWindow?.UnfoldTimetable();
                Log.Info("QA：课表已展开");
                break;
            case "toast":
                // QA：--cmd toast "标题" "内容"（测试气泡提醒）
                if (args.Length > 1)
                {
                    svc.Notices.ShowToast(new NoticeToast
                    {
                        Title = args[1],
                        Message = args.Length > 2 ? string.Join(' ', args[2..]) : "",
                        Kind = NoticeToastKind.Generic,
                        HoldMilliseconds = svc.Settings.Current.NoticeBubbleHoldSeconds * 1000
                    });
                    Log.Info("QA：已发送气泡提醒");
                }
                break;
            case "chat":
                svc.ShowChatWindow();
                break;
            case "ai-models":
                svc.ShowAiModelManager();
                break;
            case "ai-status":
                Log.Info($"AI 状态: {svc.Ai.Status} loaded={svc.Ai.IsModelLoaded} model={svc.Ai.LoadedModelName ?? "-"} path={svc.Ai.LoadedModelPath ?? "-"} dir={svc.Ai.ModelDirectory}");
                break;
            case "ai-download":
                if (args.Length > 1)
                {
                    var modelId = args[1];
                    var option = svc.Ai.ModelOptions.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
                    if (option is null) { Log.Warn($"未知模型: {modelId}"); break; }
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            svc.Ai.DownloadProgress += (_, p) => Log.Info($"AI 下载 {p.Phase} {p.Percent:0.0}% ({p.BytesReceived}/{p.TotalBytes})");
                            var ok = await svc.Ai.DownloadModelAsync(option);
                            Log.Info($"AI 下载结果: {ok}，状态={svc.Ai.Status}");
                        }
                        catch (Exception ex) { Log.Error("AI 下载失败", ex); }
                    });
                }
                break;
            case "ai-load":
                if (args.Length > 1)
                {
                    var modelPath = string.Join(' ', args[1..]);
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var ok = await svc.Ai.LoadModelAsync(modelPath);
                            if (ok)
                            {
                                svc.Settings.Update(x =>
                                {
                                    x.Ai.ModelPath = modelPath;
                                    x.Ai.ModelName = System.IO.Path.GetFileNameWithoutExtension(modelPath);
                                });
                            }
                            Log.Info($"AI 加载结果: {ok}，状态={svc.Ai.Status}，模型={svc.Ai.LoadedModelName ?? "-"}");
                        }
                        catch (Exception ex) { Log.Error("AI 加载失败", ex); }
                    });
                }
                break;
            case "ai-quip":
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var quip = await svc.Ai.TryGenerateQuipAsync();
                        Log.Info("AI 台词: " + (quip ?? "<null>"));
                    }
                    catch (Exception ex) { Log.Error("AI 台词失败", ex); }
                });
                break;
            case "ai-say":
                if (args.Length > 1)
                {
                    var prompt = string.Join(' ', args[1..]);
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var msgs = new List<AiChatMessage> { new() { Role = AiChatRole.User, Content = prompt } };
                            var reply = await svc.Ai.GenerateChatReplyAsync(msgs, null);
                            Log.Info("AI 回复: " + (reply ?? "<null>"));
                        }
                        catch (Exception ex) { Log.Error("AI 生成失败", ex); }
                    });
                }
                break;
            case "backup-export":
                if (args.Length > 1)
                {
                    var size = svc.Backup.ExportTo(args[1]);
                    Log.Info($"备份已导出: {args[1]} ({size} bytes)");
                }
                break;
            case "backup-inspect":
                if (args.Length > 1)
                {
                    var info = svc.Backup.Inspect(args[1]);
                    Log.Info($"备份信息: 创建={info.CreatedAt:yyyy-MM-dd HH:mm} 版本={info.AppVersion} 名单={info.RosterCount} 学生={info.StudentCount} 文件={string.Join(',', info.Files)}");
                }
                break;
            case "backup-import":
                if (args.Length > 1)
                {
                    svc.Backup.RestoreFrom(args[1]);
                    Log.Info("备份已恢复（重启后生效）");
                }
                break;
            case "restart":
                svc.RestartApplication();
                break;
            case "set-notice-duration":
                if (args.Length > 1 && int.TryParse(args[1], out var noticeSecs))
                    svc.Settings.Update(x => x.NoticeDurationSeconds = noticeSecs);
                break;
            case "set":
                // QA：--cmd set petscale 1.6 / panelscale 0.8 / panelx 80 / panely -40 / uiscale 1.2
                //     --cmd set hidepet 1 / petidle 10 / tableidle 10 / noticehold 7
                if (args.Length > 2 && double.TryParse(args[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var settingValue))
                {
                    var key = args[1].ToLowerInvariant();
                    AppServices.RunOnUi(() =>
                    {
                        svc.Settings.Update(x =>
                        {
                            switch (key)
                            {
                                case "petscale": x.PetScale = settingValue; break;
                                case "panelscale": x.PanelScale = settingValue; break;
                                case "panelx": x.PanelOffsetX = settingValue; break;
                                case "panely": x.PanelOffsetY = settingValue; break;
                                case "uiscale": x.UiScale = settingValue; break;
                                case "hidepet": x.HidePet = settingValue > 0.5; break;
                                case "hidetable": x.ShowTimetablePanel = settingValue <= 0.5; break;
                                case "petidle": x.PetIdleCollapseMinutes = (int)settingValue; break;
                                case "tableidle": x.TimetableIdleFoldMinutes = (int)settingValue; break;
                                case "noticehold": x.NoticeBubbleHoldSeconds = (int)settingValue; break;
                            }
                        });
                        svc.Clock.Refresh();
                    });
                    Log.Info($"QA：设置 {key} = {settingValue}");
                }
                break;
            case "exam-demo":
            {
                // QA：写入演示考试倒计时（高考/期末/校运会），方便截图与验证 D-Day
                var today = DateOnly.FromDateTime(DateTime.Now);
                svc.Settings.Update(x =>
                {
                    x.CountdownEvents = new List<CountdownEventItem>
                    {
                        new() { Name = "高考", Date = new DateOnly(2027, 6, 7), Enabled = true, Color = "#FF6B6B" },
                        new() { Name = "期末考试", Date = today.AddDays(45), Enabled = true, Color = "#FF9F1C" },
                        new() { Name = "校运动会", Date = today.AddDays(12), Enabled = true, Color = "#22C55E" }
                    };
                    x.ShowCountdownOnPet = true;
                });
                svc.Clock.Refresh();
                Log.Info("QA：已写入演示考试倒计时");
                break;
            }
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
