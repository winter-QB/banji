using System.Windows;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Settings;
using Banji.Views;

namespace Banji.Core.Notices;

/// <summary>
/// 提醒服务（v1.3.0）：
/// <list type="bullet">
///   <item>上课 / 下课 / 大课间 / 放学：构造 <see cref="NoticeToast"/> 并触发
///         <see cref="ToastRequested"/>，由 PetWindow 弹出宠物气泡（不再全屏遮挡老师 PPT）；</item>
///   <item>计时器超时：唯一保留的全屏强提醒（features-ui 的 <see cref="TimerTimeoutWindow"/>，15 秒自动关）；</item>
///   <item><see cref="DismissAll"/>：关闭全屏提醒并触发 <see cref="ToastDismissed"/> 让气泡收回。</item>
/// </list>
/// 气泡内容/保持时长由 <see cref="NoticeToastText"/> 纯函数生成（默认保持 7 秒）。
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly List<Window> _open = new();
    private readonly object _gate = new();

    /// <summary>保留无参构造（AppServices 通过它创建）。</summary>
    public NotificationService()
    {
    }

    /// <summary>请求弹出宠物气泡（PetWindow 订阅；标题/正文/种类/保持毫秒数）。</summary>
    public event EventHandler<NoticeToast>? ToastRequested;

    /// <summary>要求立即收回当前气泡（DismissAll 时触发）。</summary>
    public event EventHandler? ToastDismissed;

    // =========================================================================
    // 气泡提醒（v1.3.0）
    // =========================================================================

    public void ShowToast(NoticeToast toast)
    {
        if (toast is null) return;
        AppServices.RunOnUi(() =>
        {
            try
            {
                RaiseToastRequested(toast);
            }
            catch (Exception ex)
            {
                Log.Error("显示气泡提醒失败", ex);
            }
        });
    }

    /// <summary>上课提醒：改为宠物气泡（标题“上课”+ 科目与时间范围），不再开全屏窗。</summary>
    public void ShowClassStart(ClassTransitionEventArgs transition)
    {
        if (transition is null) return;
        AppServices.RunOnUi(() =>
        {
            try
            {
                var toast = BuildToast(transition);
                DismissAllInternal(); // 先收回旧气泡/旧全屏，再弹新气泡
                RaiseToastRequested(toast);
            }
            catch (Exception ex)
            {
                Log.Error("显示上课气泡提醒失败", ex);
            }
        });
    }

    /// <summary>下课提醒：改为宠物气泡（标题“下课/大课间/放学”+ 下一节信息），不再开全屏窗。</summary>
    public void ShowClassEnd(ClassTransitionEventArgs transition)
    {
        if (transition is null) return;
        AppServices.RunOnUi(() =>
        {
            try
            {
                var toast = BuildToast(transition);
                DismissAllInternal();
                RaiseToastRequested(toast);
            }
            catch (Exception ex)
            {
                Log.Error("显示下课气泡提醒失败", ex);
            }
        });
    }

    private static NoticeToast BuildToast(ClassTransitionEventArgs transition)
    {
        var settings = CurrentSettings();
        return NoticeToastText.ForTransition(transition, NoticeToastText.HoldMilliseconds(settings));
    }

    private void RaiseToastRequested(NoticeToast toast)
    {
        try
        {
            ToastRequested?.Invoke(this, toast);
            Log.Info($"显示气泡提醒：{toast.Title}｜{toast.Message}（Kind={toast.Kind}，保持 {toast.HoldMilliseconds}ms）");
        }
        catch (Exception ex)
        {
            Log.Error("气泡提醒事件处理失败", ex);
        }
    }

    // =========================================================================
    // 全屏提醒（仅计时器超时）
    // =========================================================================

    public void ShowTimerTimeout()
    {
        AppServices.RunOnUi(() =>
        {
            try
            {
                DismissAllInternal();
                // 计时器超时是全屏强提醒（需求明确保留）：features-ui 的 TimerTimeoutWindow（15 秒自动关）
                var window = new TimerTimeoutWindow();
                Track(window);
                window.Closed += (_, _) =>
                {
                    // 兜底：窗口自身也会复位；AcknowledgeExpiry 幂等，重复调用无副作用
                    try { AppServices.Current.Timers.AcknowledgeExpiry(); }
                    catch (Exception ex) { Log.Error("超时提示关闭后复位计时器失败", ex); }
                };
                window.Show();
                Log.Info("显示全屏提醒：计时器超时（15 秒自动关闭）");
            }
            catch (Exception ex)
            {
                Log.Error("显示计时器超时提示失败", ex);
            }
        });
    }

    // =========================================================================
    // 关闭所有提醒
    // =========================================================================

    public void DismissAll()
    {
        AppServices.RunOnUi(DismissAllInternal);
    }

    private void Track(Window window)
    {
        lock (_gate) _open.Add(window);
        window.Closed += (_, _) =>
        {
            lock (_gate) _open.Remove(window);
        };
    }

    /// <summary>关闭全屏提醒窗口，并通知气泡收回（ToastDismissed）。</summary>
    private void DismissAllInternal()
    {
        List<Window> snapshot;
        lock (_gate)
        {
            snapshot = _open.ToList();
            _open.Clear();
        }
        foreach (var window in snapshot)
        {
            try { window.Close(); }
            catch (Exception ex) { Log.Error("关闭全屏提醒窗口失败", ex); }
        }

        try { ToastDismissed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("收回气泡提醒失败", ex); }
    }

    private static AppSettings CurrentSettings()
    {
        try
        {
            if (AppServices.IsInitialized) return AppServices.Current.Settings.Current;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取气泡提醒设置失败，使用默认值：{ex.Message}");
        }
        var defaults = new AppSettings();
        defaults.Normalize();
        return defaults;
    }
}
