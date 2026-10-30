using Banji.Core.Abstractions;
using Banji.Core.Models;

namespace Banji.Core.Timers;

/// <summary>占位实现，由 features-ui 负责整体替换。</summary>
public sealed class TimerService : ITimerService
{
    public TimerSnapshot? Current => null;
    public event EventHandler? Tick;
    public event EventHandler? StateChanged;
    public event EventHandler? Expired;

    public void StartCountdown(TimeSpan total) { }
    public void StartStopwatch() { }
    public void Cancel() { }
    public void AcknowledgeExpiry() { }
}
