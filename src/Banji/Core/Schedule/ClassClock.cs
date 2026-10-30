using System.Windows.Threading;
using Banji.Core.Abstractions;
using Banji.Core.Models;

namespace Banji.Core.Schedule;

/// <summary>占位实现，由 core-schedule 负责整体替换（含上下课/放学边界触发）。</summary>
public sealed class ClassClock : IClassClock
{
    private readonly IScheduleService _schedule;
    private readonly DispatcherTimer _timer;
    private ScheduleSnapshot _current = ScheduleSnapshot.Empty(DateTime.Now);

    public ClassClock(IScheduleService schedule)
    {
        _schedule = schedule;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Pulse();
    }

    public ScheduleSnapshot Current => _current;
    public event EventHandler<ScheduleSnapshot>? Tick;
    public event EventHandler<ClassTransitionEventArgs>? Transition;

    public void Start() { Pulse(); _timer.Start(); }
    public void Stop() => _timer.Stop();
    public void Refresh() => Pulse();

    private void Pulse()
    {
        _current = _schedule.SnapshotAt(DateTime.Now);
        Tick?.Invoke(this, _current);
    }
}
