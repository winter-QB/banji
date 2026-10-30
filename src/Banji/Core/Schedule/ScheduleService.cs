using Banji.Core.Abstractions;
using Banji.Core.Models;

namespace Banji.Core.Schedule;

/// <summary>
/// 时间引擎：把「周课表 + 临时换课 + 铃声误差」折算成某一时刻的课表快照。
/// <para>
/// <see cref="SnapshotAt"/> 是纯函数：不读取真实时钟、不修改任何状态，可对任意时间调用，便于单元测试。
/// 铃声误差只在生成快照时对课表时间做偏移（显示/触发口径），底层 <see cref="WeekTimetable"/> 时间不变。
/// </para>
/// </summary>
public sealed class ScheduleService : IScheduleService
{
    /// <summary>“下一节”强调窗口：该节开始后 10 分钟内仍算强调，之后转为普通“历史”。</summary>
    public static readonly TimeSpan HighlightDuration = TimeSpan.FromMinutes(10);

    /// <summary>铃声误差允许范围（秒）。</summary>
    public const int MaxBellOffsetSeconds = 60;

    private readonly ITimetableService _timetable;
    private readonly ISettingsService _settings;

    public ScheduleService(ITimetableService timetable, ISettingsService settings)
    {
        _timetable = timetable ?? throw new ArgumentNullException(nameof(timetable));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timetable.Changed += (_, _) => RaiseChanged();
        _settings.Changed += (_, _) => RaiseChanged();
    }

    public event EventHandler? Changed;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch { /* 订阅者异常不影响时间引擎 */ }
    }

    public ScheduleSnapshot SnapshotAt(DateTime now)
    {
        var timetable = _timetable.Current;
        var bellOffsetSeconds = Math.Clamp(_settings.Current.BellOffsetSeconds, -MaxBellOffsetSeconds, MaxBellOffsetSeconds);
        var offset = TimeSpan.FromSeconds(bellOffsetSeconds);

        if (timetable is null || timetable.Periods.Count == 0)
        {
            return new ScheduleSnapshot
            {
                Now = now,
                Phase = SchedulePhase.NoTimetable,
                HasTimetable = false,
                BellOffsetSeconds = bellOffsetSeconds
            };
        }

        var day = now.DayOfWeek;
        var date = DateOnly.FromDateTime(now);
        var dayOverride = _timetable.GetOverride(date);

        // 周末默认“无课表”；但若课表里确实给周末排了课（周六上课 / 调休补课）或存在临时换课，
        // 则按正常上课日处理（DevSeed 的周末演示数据也依赖这一点）。
        if (day is DayOfWeek.Saturday or DayOfWeek.Sunday && !HasWeekendSchedule(timetable, day, dayOverride))
        {
            return new ScheduleSnapshot
            {
                Now = now,
                Phase = SchedulePhase.NoTimetable,
                HasTimetable = true,
                BellOffsetSeconds = bellOffsetSeconds
            };
        }

        var occurrences = BuildOccurrences(timetable, day, now.Date, offset, dayOverride);
        if (occurrences.Count == 0)
        {
            // 今天所有课都被取消（或课表结构异常）——视为今天无课。
            return new ScheduleSnapshot
            {
                Now = now,
                Phase = SchedulePhase.NoTimetable,
                HasTimetable = true,
                BellOffsetSeconds = bellOffsetSeconds
            };
        }

        var firstStart = occurrences[0].Start;
        var schoolEnd = occurrences[^1].End;

        CourseOccurrence? current = null;
        CourseOccurrence? next = null;
        foreach (var occ in occurrences)
        {
            if (occ.Start <= now && now < occ.End) current = occ;
            if (next is null && occ.Start > now) next = occ;
        }

        var phase = current is not null
            ? SchedulePhase.InClass
            : now < firstStart
                ? SchedulePhase.BeforeSchool
                : now >= schoolEnd
                    ? SchedulePhase.AfterSchool
                    : ResolveBreakPhase(timetable, now.Date, now, offset);

        // 当天每节课的状态：Past / Active / Next / Upcoming
        foreach (var occ in occurrences)
        {
            if (ReferenceEquals(occ, current)) occ.Status = CourseStatus.Active;
            else if (occ.End <= now) occ.Status = CourseStatus.Past;
            else if (ReferenceEquals(occ, next)) occ.Status = CourseStatus.Next;
            else occ.Status = CourseStatus.Upcoming;
        }

        // 强调项：下一节；若正在上的课开始未满 10 分钟，则继续强调当前节。
        var highlighted = current is not null && now - current.Start < HighlightDuration ? current : next;
        var periodsRemaining = occurrences.Count(o => o.End > now);

        return new ScheduleSnapshot
        {
            Now = now,
            Phase = phase,
            HasTimetable = true,
            Current = current,
            Next = next,
            Highlighted = highlighted,
            UntilCurrentEnd = current is null ? null : current.End - now,
            UntilNextStart = next is null ? null : next.Start - now,
            UntilSchoolEnd = now < schoolEnd ? schoolEnd - now : null,
            PeriodsRemaining = periodsRemaining,
            Today = occurrences,
            FirstStart = firstStart,
            SchoolEnd = schoolEnd,
            BellOffsetSeconds = bellOffsetSeconds
        };
    }

    /// <summary>周末是否真的排了课（周六上课 / 调休补课 / 临时换课）。</summary>
    private static bool HasWeekendSchedule(WeekTimetable timetable, DayOfWeek day, DayOverride? dayOverride)
    {
        if (dayOverride is not null)
        {
            if (dayOverride.CourseReplacements.Count > 0) return true;
            var hasCancelled = dayOverride.CancelledPeriods.Count > 0;
            var hasAnyCourse = timetable.EntriesFor(day).Any(e => !string.IsNullOrWhiteSpace(e.Course));
            if (hasCancelled && !hasAnyCourse) return false;
        }

        return timetable.EntriesFor(day).Any(e => !string.IsNullOrWhiteSpace(e.Course));
    }

    /// <summary>按课表结构 + 临时换课，生成当天所有“实际要上的课”（已应用铃声误差偏移）。</summary>
    private static List<CourseOccurrence> BuildOccurrences(
        WeekTimetable timetable, DayOfWeek day, DateTime date, TimeSpan offset, DayOverride? dayOverride)
    {
        var list = new List<CourseOccurrence>();
        foreach (var period in timetable.Periods.OrderBy(p => p.Start).ThenBy(p => p.Index))
        {
            if (period.End <= period.Start) continue;
            if (dayOverride is not null && dayOverride.CancelledPeriods.Contains(period.Index)) continue;

            var start = date + period.Start + offset;
            var end = date + period.End + offset;
            if (end <= start) continue;

            string course;
            if (dayOverride is not null && dayOverride.CourseReplacements.TryGetValue(period.Index, out var replaced))
                course = replaced ?? "";
            else
                course = timetable.CourseAt(day, period.Index) ?? "";

            list.Add(new CourseOccurrence
            {
                PeriodIndex = period.Index,
                PeriodName = string.IsNullOrWhiteSpace(period.Name) ? $"第{period.Index}节" : period.Name,
                Course = course ?? "",
                Start = start,
                End = end
            });
        }
        return list;
    }

    /// <summary>判断当前处于哪个非课堂分段：午休（名称含“午”）/ 大课间 / 普通课间。</summary>
    private static SchedulePhase ResolveBreakPhase(WeekTimetable timetable, DateTime date, DateTime now, TimeSpan offset)
    {
        foreach (var brk in timetable.Breaks)
        {
            if (brk.End <= brk.Start) continue;
            var start = date + brk.Start + offset;
            var end = date + brk.End + offset;
            if (start <= now && now < end)
                return IsLunch(brk.Name) ? SchedulePhase.LunchBreak : SchedulePhase.Break;
        }
        return SchedulePhase.Break;
    }

    private static bool IsLunch(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains('午') || name.Contains("lunch", StringComparison.OrdinalIgnoreCase);
    }
}
