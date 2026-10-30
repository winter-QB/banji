using Banji.Core.Abstractions;

namespace Banji.Core.Notices;

/// <summary>占位实现，由 core-schedule 负责整体替换（全屏提示窗口）。</summary>
public sealed class NotificationService : INotificationService
{
    public void ShowClassStart(ClassTransitionEventArgs transition) { }
    public void ShowClassEnd(ClassTransitionEventArgs transition) { }
    public void ShowTimerTimeout() { }
    public void DismissAll() { }
}
