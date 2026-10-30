using System.Windows;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Settings;
using Banji.Views;

namespace Banji.Core.Notices;

/// <summary>
/// 全屏提示服务：
/// <list type="bullet">
///   <item>上课：<see cref="FullscreenNoticeWindow"/> 醒目背景 + 大字（自动/点击关闭）；</item>
///   <item>下课：<see cref="FullscreenNoticeWindow"/> 透明纯文字（点击穿透，自动关闭）；</item>
///   <item>计时器超时：直接集成 features-ui 的 <see cref="TimerTimeoutWindow"/>（全屏模糊 + 关闭按钮 + 15 秒自动关）。</item>
/// </list>
/// 所有窗口只覆盖主屏、置顶、不抢焦点、不进 Alt-Tab；<see cref="DismissAll"/> 立刻关闭全部提示。
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly List<Window> _open = new();
    private readonly object _gate = new();

    /// <summary>保留无参构造（AppServices 通过它创建）。</summary>
    public NotificationService()
    {
    }

    public void ShowClassStart(ClassTransitionEventArgs transition)
    {
        if (transition is null) return;
        AppServices.RunOnUi(() =>
        {
            try
            {
                DismissAllInternal();
                var settings = CurrentSettings();
                var window = new FullscreenNoticeWindow();
                window.ConfigureClassStart(transition, settings);
                Track(window);
                window.Show();
            }
            catch (Exception ex)
            {
                Log.Error("显示上课全屏提示失败", ex);
            }
        });
    }

    public void ShowClassEnd(ClassTransitionEventArgs transition)
    {
        if (transition is null) return;
        AppServices.RunOnUi(() =>
        {
            try
            {
                DismissAllInternal();
                var settings = CurrentSettings();
                var window = new FullscreenNoticeWindow();
                window.ConfigureClassEnd(transition, settings);
                Track(window);
                window.Show();
            }
            catch (Exception ex)
            {
                Log.Error("显示下课全屏提示失败", ex);
            }
        });
    }

    public void ShowTimerTimeout()
    {
        AppServices.RunOnUi(() =>
        {
            try
            {
                DismissAllInternal();
                // 计时器超时窗口由 features-ui 实现（截主屏 + BlurEffect + AcknowledgeExpiry）
                var window = new TimerTimeoutWindow();
                Track(window);
                window.Closed += (_, _) =>
                {
                    // 兜底：窗口自身也会复位；AcknowledgeExpiry 幂等，重复调用无副作用
                    try { AppServices.Current.Timers.AcknowledgeExpiry(); }
                    catch (Exception ex) { Log.Error("超时提示关闭后复位计时器失败", ex); }
                };
                window.Show();
            }
            catch (Exception ex)
            {
                Log.Error("显示计时器超时提示失败", ex);
            }
        });
    }

    public void DismissAll()
    {
        AppServices.RunOnUi(DismissAllInternal);
    }

    // =========================================================================
    // 内部
    // =========================================================================

    private void Track(Window window)
    {
        lock (_gate) _open.Add(window);
        window.Closed += (_, _) =>
        {
            lock (_gate) _open.Remove(window);
        };
    }

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
            catch (Exception ex) { Log.Error("关闭全屏提示窗口失败", ex); }
        }
    }

    private static AppSettings CurrentSettings()
    {
        try
        {
            if (AppServices.IsInitialized) return AppServices.Current.Settings.Current;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取提示样式设置失败，使用默认值：{ex.Message}");
        }
        var defaults = new AppSettings();
        defaults.Normalize();
        return defaults;
    }
}
