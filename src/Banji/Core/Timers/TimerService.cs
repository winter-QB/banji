using System.Windows.Threading;
using Banji.Core.Abstractions;
using Banji.Core.Models;

namespace Banji.Core.Timers;

/// <summary>
/// 计时驱动源抽象。生产环境用 <see cref="DispatcherTimerTicker"/>（UI 线程 200ms）；
/// 单测注入假实现即可用注入的时间源精确驱动，不依赖真实时间。
/// </summary>
public interface ITimerTicker : IDisposable
{
    bool IsRunning { get; }
    event EventHandler? Tick;
    void Start();
    void Stop();
}

/// <summary>基于 <see cref="DispatcherTimer"/> 的 200ms 节拍器（在创建它的 UI 线程上触发）。</summary>
internal sealed class DispatcherTimerTicker : ITimerTicker
{
    private readonly DispatcherTimer _timer;

    public DispatcherTimerTicker(TimeSpan interval)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher)
        {
            Interval = interval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(200) : interval
        };
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
    }

    public bool IsRunning => _timer.IsEnabled;

    public event EventHandler? Tick;

    public void Start()
    {
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Stop()
    {
        if (_timer.IsEnabled) _timer.Stop();
    }

    public void Dispose() => _timer.Stop();
}

/// <summary>
/// 计时器服务：倒计时 / 正计时二选一，互斥。
/// <para>• 运行中约每 200ms 发 <see cref="Tick"/>；</para>
/// <para>• 状态变化发 <see cref="StateChanged"/>；</para>
/// <para>• 倒计时归零只发一次 <see cref="Expired"/>，并在 <see cref="AcknowledgeExpiry"/> 前保持 Expired 快照；</para>
/// <para>• 全部时间用 <see cref="DateTime"/> 差值（注入的时间源）计算，长时间运行不漂移。</para>
/// </summary>
public sealed class TimerService : ITimerService
{
    /// <summary>节拍间隔（约 200ms，契约要求 100~250ms）。</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private readonly ITimerTicker _ticker;

    private TimerSnapshot? _current;
    private DateTime _startedAt;
    private TimeSpan _total;
    private bool _expiredRaised;
    private bool _disposed;

    /// <summary>生产用无参构造（AppServices 使用）：真实 UTC 时间 + UI 线程 DispatcherTimer。</summary>
    public TimerService()
        : this(() => DateTime.UtcNow, new DispatcherTimerTicker(TickInterval))
    {
    }

    /// <summary>测试用：注入时间源与节拍源。</summary>
    public TimerService(Func<DateTime> now, ITimerTicker ticker)
    {
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
        _ticker.Tick += OnTickerTick;
    }

    public TimerSnapshot? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>运行中每约 200ms 一次。</summary>
    public event EventHandler? Tick;

    /// <summary>开始/取消/归零/确认等状态变化时触发。</summary>
    public event EventHandler? StateChanged;

    /// <summary>倒计时归零时只触发一次。</summary>
    public event EventHandler? Expired;

    public void StartCountdown(TimeSpan total)
    {
        if (total <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(total), "倒计时时长必须大于零。");

        lock (_gate)
        {
            _ticker.Stop();
            _total = total;
            _startedAt = _now();
            _expiredRaised = false;
            _current = new TimerSnapshot
            {
                Kind = TimerKind.Countdown,
                Status = TimerStatus.Running,
                Elapsed = TimeSpan.Zero,
                Remaining = total,
                Total = total
            };
        }

        RaiseStateChanged();
        try { _ticker.Start(); } catch { /* 计时器启动失败不影响快照 */ }
    }

    public void StartStopwatch()
    {
        lock (_gate)
        {
            _ticker.Stop();
            _total = TimeSpan.Zero;
            _startedAt = _now();
            _expiredRaised = false;
            _current = new TimerSnapshot
            {
                Kind = TimerKind.Stopwatch,
                Status = TimerStatus.Running,
                Elapsed = TimeSpan.Zero,
                Remaining = null,
                Total = null
            };
        }

        RaiseStateChanged();
        try { _ticker.Start(); } catch { /* 同上 */ }
    }

    /// <summary>提前结束当前计时（倒计时/正计时通用），恢复空闲。</summary>
    public void Cancel()
    {
        bool changed;
        lock (_gate)
        {
            _ticker.Stop();
            changed = _current is not null;
            _current = null;
        }

        if (changed) RaiseStateChanged();
    }

    /// <summary>关闭超时全屏提醒后复位为空闲（幂等；不改变计时历史）。</summary>
    public void AcknowledgeExpiry()
    {
        bool changed;
        lock (_gate)
        {
            changed = _current?.Status == TimerStatus.Expired;
            if (changed) _current = null;
        }

        if (changed) RaiseStateChanged();
    }

    private void OnTickerTick(object? sender, EventArgs e)
    {
        TimerSnapshot? snapshot;
        bool justExpired = false;

        lock (_gate)
        {
            if (_current is null || _current.Status != TimerStatus.Running)
            {
                // 已取消/已归零：确保节拍源停掉，不再产生任何 Tick。
                try { _ticker.Stop(); } catch { /* ignore */ }
                return;
            }

            var elapsed = _now() - _startedAt;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            if (_current.Kind == TimerKind.Countdown)
            {
                var remaining = _total - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    _current = new TimerSnapshot
                    {
                        Kind = TimerKind.Countdown,
                        Status = TimerStatus.Expired,
                        Elapsed = _total,
                        Remaining = TimeSpan.Zero,
                        Total = _total
                    };
                    justExpired = !_expiredRaised;
                    _expiredRaised = true;
                    try { _ticker.Stop(); } catch { /* ignore */ }
                }
                else
                {
                    _current = new TimerSnapshot
                    {
                        Kind = TimerKind.Countdown,
                        Status = TimerStatus.Running,
                        Elapsed = elapsed,
                        Remaining = remaining,
                        Total = _total
                    };
                }
            }
            else
            {
                _current = new TimerSnapshot
                {
                    Kind = TimerKind.Stopwatch,
                    Status = TimerStatus.Running,
                    Elapsed = elapsed,
                    Remaining = null,
                    Total = null
                };
            }

            snapshot = _current;
        }

        if (snapshot is null) return;
        RaiseTick();

        if (justExpired)
        {
            RaiseExpired();
            RaiseStateChanged();
        }
    }

    private void RaiseTick()
    {
        try { Tick?.Invoke(this, EventArgs.Empty); }
        catch { /* 订阅方异常不得拖垮计时器 */ }
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch { /* 同上 */ }
    }

    private void RaiseExpired()
    {
        try { Expired?.Invoke(this, EventArgs.Empty); }
        catch { /* 同上 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _current = null;
        }
        try { _ticker.Tick -= OnTickerTick; _ticker.Dispose(); } catch { /* ignore */ }
    }
}
