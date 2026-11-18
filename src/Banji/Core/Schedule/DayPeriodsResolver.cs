using Banji.Core.Models;

namespace Banji.Core.Schedule;

/// <summary>
/// v1.7.0「当天临时时间表覆盖」的纯函数解析/归一化：
/// 当天优先使用 <see cref="DayOverride.PeriodsOverride"/>（按开始时间排序、节次编号连续 1..N），
/// 否则使用周课表的 <see cref="WeekTimetable.Periods"/>；覆盖只在对应日期生效（按日期 key，次日自动恢复）。
/// 供 ScheduleService 与 TimetableService 共用，便于单测。
/// </summary>
public static class DayPeriodsResolver
{
    /// <summary>该覆盖是否包含有效的当天时段覆盖（null/空列表 = 无覆盖）。</summary>
    public static bool HasPeriodsOverride(DayOverride? dayOverride)
        => dayOverride?.PeriodsOverride is { Count: > 0 };

    /// <summary>
    /// 解析某天实际使用的课堂时段（返回副本，不修改原始数据）：
    /// 覆盖时段优先（过滤非法时间、按 Start 排序、Index 连续 1..N）；否则用周课表时段（同样排序/编号）。
    /// </summary>
    public static List<ClassPeriod> Resolve(WeekTimetable? timetable, DayOverride? dayOverride)
    {
        var overridden = Normalize(dayOverride?.PeriodsOverride);
        if (overridden.Count > 0) return overridden;
        return Normalize(timetable?.Periods);
    }

    /// <summary>
    /// 归一化一组时段：剔除 null/结束不晚于开始的行，按开始时间排序，重新编号 1..N（名称缺省补“第N节”）。
    /// 返回空列表表示没有可用时段。
    /// </summary>
    public static List<ClassPeriod> Normalize(IEnumerable<ClassPeriod>? periods)
    {
        var result = new List<ClassPeriod>();
        if (periods is null) return result;

        foreach (var period in periods)
        {
            if (period is null) continue;
            if (period.End <= period.Start) continue;
            result.Add(period.Clone());
        }
        result.Sort((a, b) =>
        {
            var byStart = a.Start.CompareTo(b.Start);
            return byStart != 0 ? byStart : a.Index.CompareTo(b.Index);
        });
        for (var i = 0; i < result.Count; i++)
        {
            result[i].Index = i + 1;
            if (string.IsNullOrWhiteSpace(result[i].Name)) result[i].Name = $"第{i + 1}节";
        }
        return result;
    }

    /// <summary>
    /// 归一化“覆盖时段”字段：null/空列表 → null（表示无覆盖），否则返回归一化后的新列表。
    /// </summary>
    public static List<ClassPeriod>? NormalizeOverrideField(List<ClassPeriod>? periods)
    {
        var normalized = Normalize(periods);
        return normalized.Count == 0 ? null : normalized;
    }

    /// <summary>某节次（按当天解析后的编号）是否落在当天时段范围内。</summary>
    public static bool ContainsIndex(IReadOnlyList<ClassPeriod> periods, int index)
        => periods.Any(p => p.Index == index);
}
