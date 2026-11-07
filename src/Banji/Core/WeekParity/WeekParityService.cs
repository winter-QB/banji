using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Core.WeekParityEngine;

/// <summary>
/// v1.4.0 单双周解析服务（core-schedule 完整实现）。
/// <para>
/// 规则（见 docs/值周值日与单双周设计-v1.4.md §15 / §20 / §29-B）：
/// 锚点取 <c>WeekParityAnchorDate ?? SemesterFirstWeekMonday</c>，自动对齐到所在周的周一；
/// 锚点周为第 1 周，单双周按周差奇偶翻转；<c>FirstWeekIsOdd</c> 决定锚点周是单还是双。
/// 手动覆盖只影响“当前周”（<c>WeekParityOverrideWeekStart</c>），跨周自动恢复 Auto 并写回设置。
/// 功能关闭时 <see cref="GetParity"/> 恒返回 Odd，时间引擎走旧逻辑，行为与 v1.3 完全一致。
/// </para>
/// </summary>
public sealed class WeekParityService : IWeekParityService
{
    private readonly ISettingsService _settings;
    private readonly Func<DateTime> _nowProvider;
    private bool _normalizingOverride;

    public WeekParityService(ISettingsService settings) : this(settings, () => DateTime.Now)
    {
    }

    /// <summary>可注入时钟（测试用）。</summary>
    public WeekParityService(ISettingsService settings, Func<DateTime> nowProvider)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _nowProvider = nowProvider ?? (() => DateTime.Now);
        _settings.Changed += (_, _) =>
        {
            EnsureCurrentWeekOverrideFresh();
            RaiseChanged();
        };

        // 启动时清理过期的手动覆盖（跨周自动恢复 Auto 并写回设置）
        EnsureCurrentWeekOverrideFresh();
    }

    public event EventHandler? Changed;

    public bool Enabled => _settings.Current.EnableWeekParity;

    /// <summary>解析后的锚点周一（<see cref="AppSettings.WeekParityAnchorDate"/> 优先，否则取开学第一周）。</summary>
    public DateOnly? AnchorMonday
    {
        get
        {
            var settings = _settings.Current;
            var raw = settings.WeekParityAnchorDate ?? settings.SemesterFirstWeekMonday;
            return raw is null ? null : ToMonday(raw.Value);
        }
    }

    public WeekParity CurrentParity => GetParity(DateOnly.FromDateTime(_nowProvider()));

    public WeekParityOverrideMode OverrideMode
    {
        get
        {
            EnsureCurrentWeekOverrideFresh();
            var settings = _settings.Current;
            if (settings.WeekParityOverride == WeekParityOverrideMode.Auto) return WeekParityOverrideMode.Auto;
            if (settings.WeekParityOverrideWeekStart is { } weekStart
                && ToMonday(weekStart) == CurrentWeekMonday())
            {
                return settings.WeekParityOverride;
            }
            return WeekParityOverrideMode.Auto;
        }
    }

    private bool IsOverrideActive
        => OverrideMode is WeekParityOverrideMode.Odd or WeekParityOverrideMode.Even;

    /// <summary>本周是否处于手动覆盖（跨周自动恢复 Auto）。</summary>
    public bool IsCurrentWeekOverridden => Enabled && IsOverrideActive;

    /// <summary>当前周（真实时钟）的周一。</summary>
    public DateOnly CurrentWeekMonday() => ToMonday(DateOnly.FromDateTime(_nowProvider()));

    public WeekParity GetParity(DateOnly date)
    {
        var settings = _settings.Current;

        // 功能关闭：返回 Odd，且时间引擎不使用 parity 解析，行为与 v1.3 完全一致。
        if (!settings.EnableWeekParity) return WeekParity.Odd;

        // 手动覆盖：只影响被覆盖的那一周（调休/考试周），其余周仍自动推算。
        if (settings.WeekParityOverride is WeekParityOverrideMode.Odd or WeekParityOverrideMode.Even
            && settings.WeekParityOverrideWeekStart is { } overrideStart
            && ToMonday(date) == ToMonday(overrideStart))
        {
            return settings.WeekParityOverride == WeekParityOverrideMode.Odd ? WeekParity.Odd : WeekParity.Even;
        }

        var anchor = AnchorMonday;
        if (anchor is null) return WeekParity.Odd; // 未设置开学第一周：安全回退

        var weekIndex = WeekIndexOf(date, anchor.Value);
        var isOdd = settings.FirstWeekIsOdd ? IsEvenIndex(weekIndex) : !IsEvenIndex(weekIndex);
        return isOdd ? WeekParity.Odd : WeekParity.Even;
    }

    /// <summary>第几周（锚点周 = 第 1 周）；锚点之前为 0 或负数（安全，不抛异常）。</summary>
    public int GetWeekNumber(DateOnly date)
    {
        var anchor = AnchorMonday;
        if (anchor is null) return 0;
        var weekIndex = WeekIndexOf(date, anchor.Value);
        return (int)(weekIndex + 1);
    }

    /// <summary>手动覆盖结束后由设置变化/跨周自动调用，强制重算并广播（宠物/课表立即刷新）。</summary>
    public void Refresh()
    {
        EnsureCurrentWeekOverrideFresh();
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("单双周 Changed 订阅者异常", ex); }
    }

    /// <summary>
    /// 清理过期的手动覆盖：
    /// 1) 覆盖周不是本周 → 恢复 Auto 并清空覆盖周（跨周自动恢复）；
    /// 2) 有覆盖值但没记录覆盖周 → 按“本周”补上；
    /// 3) 覆盖周不是周一 → 对齐到所在周周一。
    /// 只有确实需要修正时才写回设置，避免启动时无谓写盘。
    /// </summary>
    private void EnsureCurrentWeekOverrideFresh()
    {
        if (_normalizingOverride) return;
        var settings = _settings.Current;
        var currentMonday = CurrentWeekMonday();

        if (settings.WeekParityOverride == WeekParityOverrideMode.Auto)
        {
            if (settings.WeekParityOverrideWeekStart is null) return;
            WriteOverride(WeekParityOverrideMode.Auto, null);
            return;
        }

        if (settings.WeekParityOverrideWeekStart is not { } weekStart)
        {
            WriteOverride(settings.WeekParityOverride, currentMonday);
            return;
        }

        var aligned = ToMonday(weekStart);
        if (aligned != weekStart)
        {
            WriteOverride(settings.WeekParityOverride, aligned);
            return;
        }

        if (aligned != currentMonday)
        {
            // 跨周：自动恢复 Auto
            WriteOverride(WeekParityOverrideMode.Auto, null);
        }
    }

    private void WriteOverride(WeekParityOverrideMode mode, DateOnly? weekStart)
    {
        _normalizingOverride = true;
        try
        {
            _settings.Update(x =>
            {
                x.WeekParityOverride = mode;
                x.WeekParityOverrideWeekStart = weekStart;
            });
        }
        catch (Exception ex)
        {
            Log.Error("自动清理单双周手动覆盖失败", ex);
        }
        finally
        {
            _normalizingOverride = false;
        }
    }

    private static long WeekIndexOf(DateOnly date, DateOnly anchorMonday)
        => (long)Math.Floor((ToMonday(date).DayNumber - anchorMonday.DayNumber) / 7.0);

    private static bool IsEvenIndex(long weekIndex) => ((weekIndex % 2) + 2) % 2 == 0;

    /// <summary>把日期对齐到所在周的周一（周一 = 每周第一天）。</summary>
    public static DateOnly ToMonday(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7; // Monday=0 ... Sunday=6
        return date.AddDays(-offset);
    }
}
