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
    private readonly IWeekParityService? _weekParity;

    public ScheduleService(ITimetableService timetable, ISettingsService settings, IWeekParityService? weekParity = null)
    {
        _weekParity = weekParity;
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

        // v1.4.0：单双周（关闭/未注入时 null → 旧逻辑，行为与 v1.3 完全一致）
        var parity = ResolveParity(date);
        var paritEnabled = parity is not null;

        // 周末默认“无课表”；但若课表里确实给周末排了课（周六上课 / 调休补课）或存在临时换课，
        // 则按正常上课日处理（DevSeed 的周末演示数据也依赖这一点）。
        if (day is DayOfWeek.Saturday or DayOfWeek.Sunday
            && !HasWeekendSchedule(timetable, day, dayOverride, parity, paritEnabled))
        {
            return new ScheduleSnapshot
            {
                Now = now,
                Phase = SchedulePhase.NoTimetable,
                HasTimetable = true,
                BellOffsetSeconds = bellOffsetSeconds
            };
        }

        var occurrences = BuildOccurrences(timetable, day, now.Date, offset, dayOverride, parity);
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

    /// <summary>
    /// v1.4.0：解析某天的单双周。返回 null = 单双周功能关闭/未注入（走 v1.3 的通用课程解析）。
    /// </summary>
    private WeekParity? ResolveParity(DateOnly date)
    {
        if (_weekParity is null || !_weekParity.Enabled) return null;
        return _weekParity.GetParity(date);
    }

    /// <summary>周末是否真的排了课（周六上课 / 调休补课 / 临时换课；单双周开启时按当周筛选）。</summary>
    private static bool HasWeekendSchedule(
        WeekTimetable timetable, DayOfWeek day, DayOverride? dayOverride, WeekParity? parity, bool parityEnabled)
    {
        bool HasCourse(TimetableEntry entry) => !string.IsNullOrWhiteSpace(entry.Course);
        bool MatchesWeek(TimetableEntry entry) => !parityEnabled
            || entry.Weeks == WeekType.All
            || (parity == WeekParity.Odd ? entry.Weeks == WeekType.Odd : entry.Weeks == WeekType.Even);

        var hasAnyCourse = timetable.EntriesFor(day).Any(e => MatchesWeek(e) && HasCourse(e));

        if (dayOverride is not null)
        {
            if (dayOverride.CourseReplacements.Count > 0) return true;
            if (dayOverride.CancelledPeriods.Count > 0 && !hasAnyCourse) return false;
        }

        return hasAnyCourse;
    }

    public IReadOnlyList<ClassBoundary> UpcomingBoundaries(DateTime now, int count = 6)
    {
        count = Math.Clamp(count, 1, 60);
        var timetable = _timetable.Current;
        if (timetable is null || timetable.Periods.Count == 0)
            return Array.Empty<ClassBoundary>();

        var raw = new List<ClassBoundary>();

        // 从今天起向后最多看 8 天：今天不足时用未来几天补满（跨天/周末/假期都能覆盖）。
        const int maxDays = 8;
        for (var dayOffset = 0; dayOffset <= maxDays && raw.Count < count * 4; dayOffset++)
        {
            var date = now.Date.AddDays(dayOffset);

            // 用当天中午的快照取“当天完整课程”（含临时换课/取消/铃声误差），与当前 phase 无关。
            var daySnapshot = SnapshotAt(date.AddHours(12));
            if (daySnapshot.Today.Count == 0) continue;

            foreach (var occ in daySnapshot.Today)
            {
                var title = string.IsNullOrWhiteSpace(occ.Course) ? occ.PeriodName : occ.Course.Trim();
                if (occ.Start > now)
                {
                    raw.Add(new ClassBoundary
                    {
                        Time = occ.Start,
                        Kind = ClassBoundaryKind.ClassStart,
                        Title = $"{title} 上课",
                        PeriodIndex = occ.PeriodIndex
                    });
                }
                if (occ.End > now)
                {
                    raw.Add(new ClassBoundary
                    {
                        Time = occ.End,
                        Kind = ClassBoundaryKind.ClassEnd,
                        Title = $"{title} 下课",
                        PeriodIndex = occ.PeriodIndex
                    });
                }
            }

            // 当天放学的铃声也要能校准（最后一节下课后）
            if (daySnapshot.SchoolEnd is { } schoolEnd && schoolEnd > now)
            {
                raw.Add(new ClassBoundary
                {
                    Time = schoolEnd,
                    Kind = ClassBoundaryKind.SchoolEnd,
                    Title = "放学",
                    PeriodIndex = 0
                });
            }
        }

        // 排序 + 去重：同一时刻同一类型只保留一个；
        // “最后一节下课”与“放学”同一时刻时只保留更明确的“放学”。
        var ordered = raw.OrderBy(b => b.Time).ThenBy(b => (int)b.Kind).ToList();
        var deduped = new List<ClassBoundary>(ordered.Count);
        var seen = new HashSet<(DateTime Time, ClassBoundaryKind Kind)>();
        foreach (var boundary in ordered)
        {
            if (seen.Add((boundary.Time, boundary.Kind))) deduped.Add(boundary);
        }

        var schoolEndTimes = new HashSet<DateTime>(
            deduped.Where(b => b.Kind == ClassBoundaryKind.SchoolEnd).Select(b => b.Time));

        var result = new List<ClassBoundary>(count);
        foreach (var boundary in deduped)
        {
            if (boundary.Kind == ClassBoundaryKind.ClassEnd && schoolEndTimes.Contains(boundary.Time)) continue;
            result.Add(boundary);
            if (result.Count >= count) break;
        }
        return result;
    }

    /// <summary>
    /// 按课表结构 + 临时换课 + 单双周，生成当天所有“实际要上的课”（已应用铃声误差偏移）。
    /// 课程解析优先级：临时换课（DayOverride）&gt; 单/双专用 &gt; 通用。
    /// </summary>
    private static List<CourseOccurrence> BuildOccurrences(
        WeekTimetable timetable, DayOfWeek day, DateTime date, TimeSpan offset, DayOverride? dayOverride,
        WeekParity? parity)
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
            {
                // 1) 临时换课优先级最高
                course = replaced ?? "";
            }
            else if (parity is { } currentParity)
            {
                // 2) 与当前单双周匹配的专用条目 → 3) 通用条目
                course = timetable.CourseAt(day, period.Index, currentParity) ?? "";
            }
            else
            {
                // 单双周关闭：只按通用解析（专用条目忽略），与 v1.3 迁移后的行为一致
                course = timetable.EntryAt(day, period.Index, WeekType.All)?.Course ?? "";
            }

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
