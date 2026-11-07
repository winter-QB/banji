using Banji.Core.Models;

namespace Banji.Core.Timetable;

/// <summary>
/// v1.4.0 单双周课表编辑/统计的纯函数（TimetableWindow 三页签与测试共用）。
/// <para>
/// 数据规则：同一 (星期, 节次) 最多允许 <see cref="WeekType.All"/> 1 条 + Odd 1 条 + Even 1 条；
/// 单周/双周页签只保存“与通用不同”的专用条目，清空专用条目即恢复为通用。
/// </para>
/// </summary>
public static class TimetableParityEditor
{
    /// <summary>取指定周类型的条目（不存在返回 null）。</summary>
    public static TimetableEntry? GetEntry(WeekTimetable timetable, DayOfWeek day, int periodIndex, WeekType weeks)
    {
        if (timetable is null) return null;
        return timetable.EntryAt(day, periodIndex, weeks);
    }

    /// <summary>
    /// 写一个格子的课程：
    /// <list type="bullet">
    ///   <item><paramref name="weeks"/> = All：写入/更新通用条目（空字符串表示“未设置”）；</item>
    ///   <item><paramref name="weeks"/> = Odd/Even：写入专用条目；课程为空则删除专用条目（恢复为通用）。</item>
    /// </list>
    /// 返回是否发生了修改。
    /// </summary>
    public static bool SetEntry(
        WeekTimetable timetable, DayOfWeek day, int periodIndex, WeekType weeks, string? course)
    {
        if (timetable is null || periodIndex <= 0) return false;
        var text = (course ?? "").Trim();
        var list = EnsureDayList(timetable, day);
        var existing = list.FirstOrDefault(e => e.PeriodIndex == periodIndex && e.Weeks == weeks);

        if (weeks != WeekType.All && text.Length == 0)
        {
            if (existing is null) return false;
            list.Remove(existing);
            Sort(list);
            return true;
        }

        if (existing is null)
        {
            list.Add(new TimetableEntry { PeriodIndex = periodIndex, Course = text, Weeks = weeks });
            Sort(list);
            return true;
        }
        if (existing.Course == text) return false;
        existing.Course = text;
        return true;
    }

    /// <summary>删除某格某周类型的条目（单/双页签“恢复为通用”）。返回是否删除。</summary>
    public static bool RemoveEntry(WeekTimetable timetable, DayOfWeek day, int periodIndex, WeekType weeks)
    {
        if (timetable is null || weeks == WeekType.All) return false;
        if (!timetable.Days.TryGetValue(day, out var list) || list is null) return false;
        var removed = list.RemoveAll(e => e.PeriodIndex == periodIndex && e.Weeks == weeks) > 0;
        if (removed) Sort(list);
        return removed;
    }

    /// <summary>把通用课复制到单周/双周（仅复制非空通用课），返回复制的格子数。</summary>
    public static int CopyAllTo(WeekTimetable timetable, WeekType target)
    {
        if (timetable is null || target == WeekType.All) return 0;
        var copied = 0;
        foreach (var (day, entries) in timetable.Days.ToList())
        {
            foreach (var all in entries.Where(e => e.Weeks == WeekType.All && !string.IsNullOrWhiteSpace(e.Course)).ToList())
            {
                if (SetEntry(timetable, day, all.PeriodIndex, target, all.Course)) copied++;
            }
        }
        return copied;
    }

    /// <summary>清空某一周的专用条目（target = Odd/Even），返回清掉的条数。</summary>
    public static int ClearWeek(WeekTimetable timetable, WeekType target)
    {
        if (timetable is null || target == WeekType.All) return 0;
        var removed = 0;
        foreach (var (day, entries) in timetable.Days.ToList())
        {
            removed += entries.RemoveAll(e => e.Weeks == target);
            Sort(entries);
        }
        return removed;
    }

    /// <summary>删除全部单/双专用条目，全部恢复为通用，返回删除条数。</summary>
    public static int RestoreAllToAll(WeekTimetable timetable)
        => ClearWeek(timetable, WeekType.Odd) + ClearWeek(timetable, WeekType.Even);

    /// <summary>某页签某格应显示的课程：专用条目优先，单/双页签回退显示通用课（用于编辑参考）。</summary>
    public static string DisplayCourse(WeekTimetable timetable, DayOfWeek day, int periodIndex, WeekType page)
    {
        if (timetable is null) return "";
        if (page == WeekType.All)
            return GetEntry(timetable, day, periodIndex, WeekType.All)?.Course ?? "";
        return GetEntry(timetable, day, periodIndex, page)?.Course
               ?? GetEntry(timetable, day, periodIndex, WeekType.All)?.Course
               ?? "";
    }

    /// <summary>该格在单/双页签是否有“与通用不同”的专用条目。</summary>
    public static bool HasSpecific(WeekTimetable timetable, DayOfWeek day, int periodIndex, WeekType page)
        => page != WeekType.All && GetEntry(timetable, day, periodIndex, page) is not null;

    /// <summary>统计某周类型的有效课程条目数（忽略空课程占位，通用/单周/双周）。</summary>
    public static int Count(WeekTimetable timetable, WeekType weeks)
    {
        if (timetable is null) return 0;
        return timetable.Days.Values.SelectMany(list => list)
            .Count(e => e.Weeks == weeks && !string.IsNullOrWhiteSpace(e.Course));
    }

    /// <summary>统计三周类型（通用、单周、双周）的条目数。</summary>
    public static (int All, int Odd, int Even) CountByWeekType(WeekTimetable timetable)
        => (Count(timetable, WeekType.All), Count(timetable, WeekType.Odd), Count(timetable, WeekType.Even));

    /// <summary>
    /// 规整条目：同一 (节次, 周类型) 只保留最后一条；非法 <see cref="WeekType"/> 归为 All；按节次排序。
    /// （导入的合并单元格、手工 JSON 都可能产生重复条目。）
    /// </summary>
    public static void NormalizeEntries(WeekTimetable timetable)
    {
        if (timetable?.Days is null) return;
        foreach (var day in timetable.Days.Keys.ToList())
        {
            var list = timetable.Days[day];
            if (list is null)
            {
                timetable.Days[day] = new List<TimetableEntry>();
                continue;
            }

            var normalized = new List<TimetableEntry>();
            var indexByKey = new Dictionary<(int, WeekType), int>();
            foreach (var entry in list)
            {
                if (entry is null) continue;
                var weeks = entry.Weeks is WeekType.All or WeekType.Odd or WeekType.Even ? entry.Weeks : WeekType.All;
                var key = (entry.PeriodIndex, weeks);
                var cleaned = new TimetableEntry
                {
                    PeriodIndex = entry.PeriodIndex,
                    Course = entry.Course?.Trim() ?? "",
                    Weeks = weeks
                };
                if (indexByKey.TryGetValue(key, out var at))
                {
                    normalized[at] = cleaned; // 保留最后编辑的一条
                }
                else
                {
                    indexByKey[key] = normalized.Count;
                    normalized.Add(cleaned);
                }
            }
            Sort(normalized);
            timetable.Days[day] = normalized;
        }
    }

    /// <summary>按 (节次, 周类型) 稳定排序。</summary>
    public static void Sort(List<TimetableEntry> entries)
        => entries.Sort((a, b) =>
        {
            var byPeriod = a.PeriodIndex.CompareTo(b.PeriodIndex);
            return byPeriod != 0 ? byPeriod : ((int)a.Weeks).CompareTo((int)b.Weeks);
        });

    private static List<TimetableEntry> EnsureDayList(WeekTimetable timetable, DayOfWeek day)
    {
        timetable.Days ??= new Dictionary<DayOfWeek, List<TimetableEntry>>();
        if (!timetable.Days.TryGetValue(day, out var list) || list is null)
        {
            list = new List<TimetableEntry>();
            timetable.Days[day] = list;
        }
        return list;
    }
}
