using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Core.WeekParityEngine;

/// <summary>
/// v1.4.0 单双周解析（core-schedule 负责完善：手动覆盖、自动恢复、周次显示、事件）。
/// </summary>
public sealed class WeekParityService : IWeekParityService
{
    private readonly ISettingsService _settings;

    public WeekParityService(ISettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.Changed += (_, _) => Refresh();
    }

    public bool Enabled => _settings.Current.EnableWeekParity;

    public DateOnly? AnchorMonday
    {
        get
        {
            var raw = _settings.Current.WeekParityAnchorDate ?? _settings.Current.SemesterFirstWeekMonday;
            if (raw is null) return null;
            return ToMonday(raw.Value);
        }
    }

    public WeekParityOverrideMode OverrideMode => _settings.Current.WeekParityOverride;

    public bool IsCurrentWeekOverridden
        => _settings.Current.WeekParityOverride != WeekParityOverrideMode.Auto
           && _settings.Current.WeekParityOverrideWeekStart is { } weekStart
           && ToMonday(DateOnly.FromDateTime(DateTime.Now)) == weekStart;

    public WeekParity CurrentParity => GetParity(DateOnly.FromDateTime(DateTime.Now));

    public event EventHandler? Changed;

    public WeekParity GetParity(DateOnly date)
    {
        var settings = _settings.Current;
        if (settings.EnableWeekParity
            && settings.WeekParityOverride != WeekParityOverrideMode.Auto
            && settings.WeekParityOverrideWeekStart is { } overrideWeek
            && ToMonday(date) == overrideWeek)
        {
            return settings.WeekParityOverride == WeekParityOverrideMode.Odd ? WeekParity.Odd : WeekParity.Even;
        }

        var anchor = AnchorMonday;
        if (anchor is null) return WeekParity.Odd;
        var index = (int)Math.Floor((ToMonday(date).DayNumber - anchor.Value.DayNumber) / 7.0);
        var odd = settings.FirstWeekIsOdd ? index % 2 == 0 : index % 2 != 0;
        return odd ? WeekParity.Odd : WeekParity.Even;
    }

    public int GetWeekNumber(DateOnly date)
    {
        var anchor = AnchorMonday;
        if (anchor is null) return 0;
        return (int)Math.Floor((ToMonday(date).DayNumber - anchor.Value.DayNumber) / 7.0) + 1;
    }

    public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>把日期对齐到所在周的周一。</summary>
    public static DateOnly ToMonday(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7; // Monday=0 ... Sunday=6
        return date.AddDays(-offset);
    }
}
