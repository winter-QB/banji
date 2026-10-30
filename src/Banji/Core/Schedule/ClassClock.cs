using System.Windows.Threading;
using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Core.Schedule;

/// <summary>
/// 课堂时钟：约 250ms 刷一次课表快照，并在跨过（含铃声误差偏移后的）上课 / 下课 / 放学 /
/// 大课间边界时触发一次 <see cref="Transition"/>。
/// <para>
/// 防重复：边界事件只落在 (上次采样时刻, 当前采样时刻] 的开区间里，同一秒不会触发两次。
/// 防积压：系统休眠/挂起恢复导致的超大跳变（&gt; <see cref="MaxReplayGap"/>）只处理最近的一个状态，
/// 不补放积压的上下课提示。
/// </para>
/// </summary>
public sealed class ClassClock : IClassClock
{
    /// <summary>两次采样间隔超过该值视为“挂起恢复”，只处理最近状态。</summary>
    public static readonly TimeSpan MaxReplayGap = TimeSpan.FromSeconds(5);

    private readonly IScheduleService _schedule;
    private readonly Func<DateTime> _nowProvider;

    private DispatcherTimer? _timer;
    private ScheduleSnapshot _current;
    private ScheduleSnapshot? _prevSnapshot;
    private DateTime _prevNow;
    private bool _hasPrev;

    public ClassClock(IScheduleService schedule) : this(schedule, () => DateTime.Now)
    {
    }

    /// <summary>可注入时钟（测试用）。</summary>
    public ClassClock(IScheduleService schedule, Func<DateTime> nowProvider)
    {
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _nowProvider = nowProvider ?? (() => DateTime.Now);
        _current = ScheduleSnapshot.Empty(_nowProvider());
    }

    public ScheduleSnapshot Current => _current;

    public event EventHandler<ScheduleSnapshot>? Tick;
    public event EventHandler<ClassTransitionEventArgs>? Transition;

    public void Start()
    {
        _timer ??= CreateTimer();
        Sync(_nowProvider(), allowTransitions: false);
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    /// <summary>课表/设置变化后立即重算（不补触发过渡事件，避免改设置时误弹提示）。</summary>
    public void Refresh() => Sync(_nowProvider(), allowTransitions: false);

    /// <summary>在指定时刻采样一次（测试 / QA 用；正常运行由 DispatcherTimer 每 250ms 调用）。</summary>
    public void PulseAt(DateTime now) => Sync(now, allowTransitions: true);

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                Sync(_nowProvider(), allowTransitions: true);
            }
            catch (Exception ex)
            {
                Log.Error("课堂时钟采样失败", ex);
            }
        };
        return timer;
    }

    private void Sync(DateTime now, bool allowTransitions)
    {
        ScheduleSnapshot snapshot;
        try
        {
            snapshot = _schedule.SnapshotAt(now);
        }
        catch (Exception ex)
        {
            Log.Error("计算课表快照失败", ex);
            return;
        }

        var previous = _prevSnapshot;
        var prevNow = _prevNow;
        var hadPrev = _hasPrev;

        _current = snapshot;
        _prevSnapshot = snapshot;
        _prevNow = now;
        _hasPrev = true;

        if (allowTransitions && hadPrev && previous is not null)
        {
            try
            {
                EmitTransitions(prevNow, previous, now, snapshot);
            }
            catch (Exception ex)
            {
                Log.Error("上下课过渡触发失败", ex);
            }
        }

        try
        {
            Tick?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("课堂时钟 Tick 订阅者异常", ex);
        }
    }

    private void EmitTransitions(DateTime prevNow, ScheduleSnapshot prev, DateTime now, ScheduleSnapshot current)
    {
        var gap = now - prevNow;
        if (gap <= TimeSpan.Zero)
        {
            // 系统时间被往回调：静默重新同步，不触发任何事件。
            return;
        }

        var events = BuildEvents(prevNow, now, prev, current);
        if (gap > MaxReplayGap)
        {
            // 休眠/挂起恢复：只处理“当前时刻最近的一个状态”，不补放积压。
            if (events.Count > 0)
            {
                Raise(events[^1]);
            }
            else
            {
                var single = BuildSingleStateEvent(prev, current, now);
                if (single is not null) Raise(single);
            }
            return;
        }

        foreach (var e in events) Raise(e);
    }

    private void Raise(ClassTransitionEventArgs args)
    {
        try
        {
            Transition?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            Log.Error($"过渡事件处理失败（{args.Kind}）", ex);
        }
    }

    private sealed record PendingEvent(DateTime Time, int Priority, ClassTransitionEventArgs Args);

    /// <summary>收集 (prevNow, now] 内跨过的所有边界事件，按时间排序。</summary>
    private static List<ClassTransitionEventArgs> BuildEvents(
        DateTime prevNow, DateTime now, ScheduleSnapshot prev, ScheduleSnapshot current)
    {
        var pending = new List<PendingEvent>();
        var today = current.Today;
        if (today.Count > 0)
        {
            var first = today[0];
            var last = today[^1];
            foreach (var occ in today)
            {
                var isFirst = occ.PeriodIndex == first.PeriodIndex;
                var isLast = occ.PeriodIndex == last.PeriodIndex;

                if (InWindow(occ.Start, prevNow, now))
                {
                    if (isFirst)
                        pending.Add(new PendingEvent(occ.Start, 1, MakeArgs(ClassTransitionKind.SchoolStarted, occ.Start, occ, current)));
                    pending.Add(new PendingEvent(occ.Start, 3, MakeArgs(ClassTransitionKind.ClassStarted, occ.Start, occ, current)));
                }

                if (InWindow(occ.End, prevNow, now))
                {
                    pending.Add(new PendingEvent(occ.End, 0, MakeArgs(ClassTransitionKind.ClassEnded, occ.End, occ, current)));
                    if (isLast)
                        pending.Add(new PendingEvent(occ.End, 2, MakeArgs(ClassTransitionKind.SchoolEnded, occ.End, occ, current)));
                }
            }
        }

        // 大课间 / 午休：快照里没有单独的时间线，只能在“进入 Break/LunchBreak 相位”时补一个事件。
        // 时间取本次采样时刻（sleep 恢复时作为“最近状态”处理也合理）。
        if (IsBreakPhase(current.Phase) && !IsBreakPhase(prev.Phase))
        {
            pending.Add(new PendingEvent(now, 4, new ClassTransitionEventArgs
            {
                Kind = ClassTransitionKind.BreakStarted,
                Time = now,
                Current = prev.Current,
                Next = current.Next,
                Snapshot = current
            }));
        }

        return pending
            .OrderBy(p => p.Time)
            .ThenBy(p => p.Priority)
            .Select(p => p.Args)
            .ToList();
    }

    private static ClassTransitionEventArgs MakeArgs(
        ClassTransitionKind kind, DateTime time, CourseOccurrence occ, ScheduleSnapshot current)
        => new()
        {
            Kind = kind,
            Time = time,
            Current = occ,
            Next = current.Next,
            Snapshot = current
        };

    /// <summary>跳变窗口里没有可定位的课程边界时，按状态变化补一个“最近”的事件。</summary>
    private static ClassTransitionEventArgs? BuildSingleStateEvent(
        ScheduleSnapshot prev, ScheduleSnapshot current, DateTime now)
    {
        if (current.Current is not null &&
            (prev.Current is null || prev.Current.PeriodIndex != current.Current.PeriodIndex))
        {
            return MakeArgs(ClassTransitionKind.ClassStarted, current.Current.Start, current.Current, current);
        }

        if (prev.Current is not null && current.Current is null)
            return MakeArgs(ClassTransitionKind.ClassEnded, prev.Current.End, prev.Current, current);

        if (IsBreakPhase(current.Phase) && !IsBreakPhase(prev.Phase))
        {
            return new ClassTransitionEventArgs
            {
                Kind = ClassTransitionKind.BreakStarted,
                Time = now,
                Current = prev.Current,
                Next = current.Next,
                Snapshot = current
            };
        }

        if (current.Phase == SchedulePhase.AfterSchool && prev.Phase != SchedulePhase.AfterSchool)
        {
            return new ClassTransitionEventArgs
            {
                Kind = ClassTransitionKind.SchoolEnded,
                Time = current.SchoolEnd ?? now,
                Current = current.Today.Count > 0 ? current.Today[^1] : null,
                Next = null,
                Snapshot = current
            };
        }

        return null;
    }

    private static bool InWindow(DateTime time, DateTime fromExclusive, DateTime toInclusive)
        => time > fromExclusive && time <= toInclusive;

    private static bool IsBreakPhase(SchedulePhase phase)
        => phase is SchedulePhase.Break or SchedulePhase.LunchBreak;
}
