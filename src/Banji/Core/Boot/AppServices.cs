using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Banji.Core.Abstractions;
using Banji.Core.Ai;
using Banji.Core.Audio;
using Banji.Core.Backup;
using Banji.Core.Grouping;
using Banji.Core.Growth;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Notices;
using Banji.Core.Picker;
using Banji.Core.Rosters;
using Banji.Core.Schedule;
using Banji.Core.Settings;
using Banji.Core.Timers;
using Banji.Core.Timetable;
using Banji.Views;

namespace Banji.Core.Boot;

/// <summary>
/// 应用服务容器 + 窗口注册表。所有窗口/服务通过 AppServices.Current 取用。
/// 本文件由 Lead 维护；队友通过接口调用服务，不要直接 new 服务。
/// </summary>
public sealed class AppServices
{
    private static AppServices? _current;
    public static AppServices Current => _current ?? throw new InvalidOperationException("AppServices 尚未初始化");
    public static bool IsInitialized => _current is not null;

    public ISettingsService Settings { get; }
    public IRosterService Rosters { get; }
    public IPickService Picker { get; }
    public ITimetableService Timetable { get; }
    public IScheduleService Schedule { get; }
    public IClassClock Clock { get; }
    public ITimerService Timers { get; }
    public IAudioService Audio { get; }
    public INotificationService Notices { get; }
    // 第二轮新增
    public IGroupingService Grouping { get; }
    public IPetGrowthService Growth { get; }
    public IBackupService Backup { get; }
    // v1.2.0：端侧生成式 AI
    public IAiService Ai { get; }

    public PetWindow? PetWindow { get; private set; }

    private readonly List<Window> _windows = new();
    private readonly Dictionary<Window, DateTime> _shownAt = new();

    private AppServices(
        ISettingsService settings,
        IRosterService rosters,
        IPickService picker,
        ITimetableService timetable,
        IScheduleService schedule,
        IClassClock clock,
        ITimerService timers,
        IAudioService audio,
        INotificationService notices,
        IGroupingService grouping,
        IPetGrowthService growth,
        IBackupService backup,
        IAiService ai)
    {
        Settings = settings;
        Rosters = rosters;
        Picker = picker;
        Timetable = timetable;
        Schedule = schedule;
        Clock = clock;
        Timers = timers;
        Audio = audio;
        Notices = notices;
        Grouping = grouping;
        Growth = growth;
        Backup = backup;
        Ai = ai;
        Wire();
    }

    public static AppServices Create()
    {
        if (_current is not null) return _current;
        var settings = new SettingsService();
        var timetable = new TimetableService();
        var schedule = new ScheduleService(timetable, settings);
        var clock = new ClassClock(schedule);
        var rosters = new RosterService();
        var picker = new PickService(rosters, settings);
        var timers = new TimerService();
        var audio = new AudioService();
        var notices = new NotificationService();
        var grouping = new GroupingService(rosters);
        var growth = new PetGrowthService();
        var backup = new BackupService();
        var ai = new AiService(settings);
        _current = new AppServices(settings, rosters, picker, timetable, schedule, clock, timers, audio, notices, grouping, growth, backup, ai);
        return _current;
    }

    private void Wire()
    {
        // 计时器归零 -> 全屏模糊超时提醒
        Timers.Expired += (_, _) =>
        {
            try { Notices.ShowTimerTimeout(); }
            catch (Exception ex) { Log.Error("计时器超时提醒失败", ex); }
        };

        // 上下课/放学/大课间边界
        Clock.Transition += OnClassTransition;

        // 课表/设置变化 -> 时钟立即重算
        Timetable.Changed += (_, _) => Clock.Refresh();
        Settings.Changed += (_, _) => Clock.Refresh();

        // 花名册变化 -> 若当前激活花名册已被删除，回退到第一份
        Rosters.Changed += (_, _) =>
        {
            try
            {
                if (Rosters.Rosters.Count > 0 && Rosters.Active is null)
                    Rosters.SetActive(Rosters.Rosters[0].Id);
            }
            catch (Exception ex) { Log.Error("花名册回退失败", ex); }
        };

        // 养成统计：抽签计数
        Picker.Picked += (_, _) =>
        {
            try { if (Settings.Current.EnableGrowth) Growth.RecordPick(); }
            catch (Exception ex) { Log.Error("养成统计记录抽签失败", ex); }
        };
    }

    private void OnClassTransition(object? sender, ClassTransitionEventArgs e)
    {
        try
        {
            switch (e.Kind)
            {
                case ClassTransitionKind.ClassStarted:
                    if (Settings.Current.ResetPickSessionOnClassStart)
                        Picker.ResetSession();
                    if (Settings.Current.ShowClassStartNotice)
                    {
                        Notices.ShowClassStart(e);
                        if (Settings.Current.EnableGrowth) Growth.RecordNotice();
                    }
                    break;
                case ClassTransitionKind.ClassEnded:
                    if (Settings.Current.ShowClassEndNotice)
                    {
                        Notices.ShowClassEnd(e);
                        if (Settings.Current.EnableGrowth) Growth.RecordNotice();
                    }
                    break;
                case ClassTransitionKind.SchoolEnded:
                    // 放学暂不弹全屏提示（文档只要求课堂上下课提示）
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("处理上下课转换失败", ex);
        }
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    public void Start()
    {
        try
        {
            if (Settings.Current.EnableGrowth) Growth.RecordSessionStart();
        }
        catch (Exception ex) { Log.Error("养成统计记录启动失败", ex); }

        PetWindow = new PetWindow();
        Register(PetWindow);
        PetWindow.Show();
        Clock.Start();
        try { Ai.TryAutoLoad(); }
        catch (Exception ex) { Log.Error("AI 自动加载失败", ex); }
        Log.Info("班机已启动");
    }

    /// <summary>重启应用（恢复备份 / 应用需要重启的设置后使用）。</summary>
    public void RestartApplication()
    {
        try
        {
            Clock.Stop();
            Settings.Save();
            Rosters.Save();
            Timetable.Save();
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("重启应用失败", ex);
        }
        Shutdown();
    }

    /// <summary>
    /// 重启应用。恢复备份后请用 <c>RestartApplication(saveBeforeRestart: false)</c>，
    /// 否则内存里的旧数据会在退出时覆盖刚恢复的备份文件。
    /// </summary>
    public void RestartApplication(bool saveBeforeRestart = true)
    {
        try
        {
            Clock.Stop();
            if (saveBeforeRestart)
            {
                Settings.Save();
                Rosters.Save();
                Timetable.Save();
            }
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("重启应用失败", ex);
        }
        Shutdown(saveBeforeRestart);
    }

    public void Shutdown(bool saveData = true)
    {
        try
        {
            Clock.Stop();
            if (saveData)
            {
                Settings.Save();
                Rosters.Save();
                Timetable.Save();
            }
        }
        catch (Exception ex)
        {
            Log.Error("退出保存失败", ex);
        }

        foreach (var w in _windows.ToArray())
        {
            try { w.Close(); } catch { /* ignore */ }
        }
        _windows.Clear();
        Log.Info("班机已退出");
        Application.Current?.Shutdown();
    }

    // =========================================================================
    // 窗口管理
    // =========================================================================

    public T ShowWindow<T>(Func<T> factory) where T : Window
    {
        var existing = _windows.OfType<T>().FirstOrDefault(w => w.IsVisible);
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var window = factory();
        Register(window);
        window.Show();
        // 打开辅助窗口时聚焦一次（用户点击体验更好；宠物窗不经过这里）
        try { window.Activate(); } catch { /* ignore */ }
        return window;
    }

    private void Register(Window window)
    {
        _windows.Add(window);
        _shownAt[window] = DateTime.Now;
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            _shownAt.Remove(window);
        };
    }

    public void CloseAuxiliaryWindows()
    {
        foreach (var w in _windows.Where(w => w is not Banji.Views.PetWindow).ToArray())
        {
            try { w.Close(); } catch { /* ignore */ }
        }
    }

    // =========================================================================
    // 打开各窗口（UI 线程调用）
    // =========================================================================

    public SettingsWindow ShowSettings()
    {
        var w = ShowWindow(() => new SettingsWindow());
        w.ShowInTaskbar = true;
        return w;
    }

    public DashboardWindow ShowDashboard() => ShowWindow(() => new DashboardWindow());

    public RosterWindow ShowRosterManager() => ShowWindow(() => new RosterWindow());

    public TimetableWindow ShowTimetableEditor() => ShowWindow(() => new TimetableWindow());

    public PeriodSettingsWindow ShowPeriodSettings() => ShowWindow(() => new PeriodSettingsWindow());

    public RosterPickerFlyout ShowRosterPicker() => ShowWindow(() => new RosterPickerFlyout());

    public MenuFlyout ShowMenuFlyout() => ShowWindow(() => new MenuFlyout());

    // ---- 第二轮新增窗口 ----
    public GroupingWindow ShowGroupingWindow()
    {
        var w = ShowWindow(() => new GroupingWindow());
        w.ShowInTaskbar = true;
        return w;
    }

    public RingCalibrationWindow ShowRingCalibrationWindow()
    {
        var w = ShowWindow(() => new RingCalibrationWindow());
        w.ShowInTaskbar = true;
        return w;
    }

    // ---- v1.2.0 端侧 AI 窗口 ----
    public ChatWindow ShowChatWindow()
    {
        var w = ShowWindow(() => new ChatWindow());
        w.ShowInTaskbar = true;
        return w;
    }

    public AiModelDialog ShowAiModelManager()
    {
        var w = ShowWindow(() => new AiModelDialog());
        w.ShowInTaskbar = true;
        return w;
    }

    public TimerDialog ShowTimerDialog(TimerKind kind)
    {
        var existing = _windows.OfType<TimerDialog>().FirstOrDefault(w => w.IsVisible);
        if (existing is not null)
        {
            existing.InitialKind = kind;
            existing.Activate();
            return existing;
        }
        var dialog = new TimerDialog { InitialKind = kind };
        Register(dialog);
        dialog.Show();
        return dialog;
    }

    public RosterImportWindow ShowRosterImport(string? filePath = null)
    {
        var w = ShowWindow(() => new RosterImportWindow());
        if (!string.IsNullOrWhiteSpace(filePath)) w.InitialFilePath = filePath;
        return w;
    }

    public TimetableImportWindow ShowTimetableImport(string? filePath = null)
    {
        var w = ShowWindow(() => new TimetableImportWindow());
        if (!string.IsNullOrWhiteSpace(filePath)) w.InitialFilePath = filePath;
        return w;
    }

    public ManualRosterDialog ShowManualRosterDialog() => ShowWindow(() => new ManualRosterDialog());

    // =========================================================================
    // 预览（设置窗口/QA 使用）
    // =========================================================================

    public void PreviewClassStartNotice()
    {
        var snapshot = Clock.Current;
        var target = snapshot.Next ?? snapshot.Current;
        Notices.ShowClassStart(new ClassTransitionEventArgs
        {
            Kind = ClassTransitionKind.ClassStarted,
            Time = DateTime.Now,
            Current = target,
            Next = target,
            Snapshot = snapshot
        });
    }

    public void PreviewClassEndNotice()
    {
        var snapshot = Clock.Current;
        Notices.ShowClassEnd(new ClassTransitionEventArgs
        {
            Kind = ClassTransitionKind.ClassEnded,
            Time = DateTime.Now,
            Current = snapshot.Current,
            Next = snapshot.Next,
            Snapshot = snapshot
        });
    }

    public void PreviewTimerTimeout() => Notices.ShowTimerTimeout();

    // =========================================================================
    // 线程辅助
    // =========================================================================

    public static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}
