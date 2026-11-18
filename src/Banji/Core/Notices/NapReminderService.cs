using System.Windows.Threading;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Views;

namespace Banji.Core.Notices;

/// <summary>
/// 班级私有版：每天 12:50 触发全屏强提醒（2 分钟自动关闭 + 语音循环 5 遍）。
/// 默认关闭，需在「设置 → 提醒与隐藏」里打开「午间 12:50 强提醒（班级专用）」。
/// </summary>
public sealed class NapReminderService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private DateOnly? _lastTriggeredDate;
    private NapReminderWindow? _window;
    private bool _disposed;

    public NapReminderService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Check();
    }

    public void Start()
    {
        _timer.Start();
        Log.Info("午间 12:50 强提醒服务已启动（班级私有版，默认关闭）");
    }

    /// <summary>立即触发一次（QA / 手动预览用）。</summary>
    public void TriggerNow() => ShowReminder();

    private void Check()
    {
        try
        {
            if (_disposed) return;
            if (!AppServices.Current.Settings.Current.NapReminderEnabled) return;
            var now = DateTime.Now;
            if (now.Hour != 12 || now.Minute != 50) return;
            var today = DateOnly.FromDateTime(now);
            if (_lastTriggeredDate == today) return;
            _lastTriggeredDate = today;
            ShowReminder();
        }
        catch (Exception ex)
        {
            Log.Warn($"检查午间强提醒失败：{ex.Message}");
        }
    }

    private void ShowReminder()
    {
        if (_disposed) return;
        if (_window is { IsVisible: true }) return;
        var window = new NapReminderWindow();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window)) _window = null;
        };
        _window = window;
        window.Show();
        Log.Info("午间 12:50 强提醒已显示（全屏 2 分钟，语音 5 遍）");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        try { _window?.Close(); } catch { /* ignore */ }
        _window = null;
    }
}
