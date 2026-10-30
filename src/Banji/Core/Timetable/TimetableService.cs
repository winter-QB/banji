using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Models;

namespace Banji.Core.Timetable;

/// <summary>
/// 课表服务：周课表 / 课堂时段 / 午休课间 / 临时换课的读写与持久化。
/// <para>数据文件：<c>%APPDATA%\Banji\timetable.json</c> 与 <c>overrides.json</c>（原子写入）。</para>
/// <para>所有修改都会立即保存并触发 <see cref="Changed"/>（ClassClock / ScheduleService 依赖它）。</para>
/// </summary>
public sealed class TimetableService : ITimetableService
{
    private readonly string _timetablePath;
    private readonly string _overridesPath;
    private readonly object _gate = new();

    private WeekTimetable _current;
    private List<DayOverride> _overrides;

    /// <summary>正式运行用的无参构造（AppServices 通过它创建）。</summary>
    public TimetableService() : this(AppPaths.TimetableFile, AppPaths.OverridesFile)
    {
    }

    /// <summary>可注入文件路径（测试用临时文件）。</summary>
    public TimetableService(string timetablePath, string overridesPath)
    {
        _timetablePath = string.IsNullOrWhiteSpace(timetablePath) ? AppPaths.TimetableFile : timetablePath;
        _overridesPath = string.IsNullOrWhiteSpace(overridesPath) ? AppPaths.OverridesFile : overridesPath;

        _current = SafeJsonStore.Load(_timetablePath, WeekTimetable.CreateDefault) ?? WeekTimetable.CreateDefault();
        _overrides = SafeJsonStore.Load(_overridesPath, () => new List<DayOverride>()) ?? new List<DayOverride>();
        NormalizeTimetable(_current);
        NormalizeOverrides();
    }

    public WeekTimetable Current
    {
        get { lock (_gate) return _current; }
    }

    public IReadOnlyList<DayOverride> Overrides
    {
        get { lock (_gate) return _overrides.Select(o => o.Clone()).ToList(); }
    }

    public event EventHandler? Changed;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("课表 Changed 订阅者异常", ex); }
    }

    // =========================================================================
    // 导入
    // =========================================================================

    public TimetableImportPreview PreviewImport(string filePath) => TimetableImporter.Preview(filePath);

    public WeekTimetable Import(string filePath, string? sheetName = null)
    {
        var timetable = TimetableImporter.Import(filePath, sheetName);
        SetTimetable(timetable);
        return Current;
    }

    // =========================================================================
    // 修改
    // =========================================================================

    public void SetTimetable(WeekTimetable timetable)
    {
        if (timetable is null) throw new ArgumentNullException(nameof(timetable));
        var copy = timetable.Clone();
        NormalizeTimetable(copy);
        lock (_gate) _current = copy;
        Save();
        RaiseChanged();
    }

    public void SetPeriods(IEnumerable<ClassPeriod> periods)
    {
        var list = (periods ?? Array.Empty<ClassPeriod>())
            .Where(p => p is not null && p.End > p.Start)
            .OrderBy(p => p.Start)
            .ToList();

        // 旧编号 → 新编号（删除/重排节次时同步迁移课程数据）
        var map = new Dictionary<int, int>();
        for (var i = 0; i < list.Count; i++)
        {
            var period = list[i];
            var oldIndex = period.Index;
            period.Index = i + 1;
            if (string.IsNullOrWhiteSpace(period.Name)) period.Name = $"第{i + 1}节";
            if (oldIndex > 0) map[oldIndex] = i + 1;
        }

        lock (_gate)
        {
            _current.Periods = list;
            _current.Days = RemapDays(_current.Days, map);
        }
        Save();
        RaiseChanged();
    }

    public void SetBreaks(IEnumerable<BreakSegment> breaks)
    {
        var list = (breaks ?? Array.Empty<BreakSegment>())
            .Where(b => b is not null && b.End > b.Start)
            .OrderBy(b => b.Start)
            .ToList();
        foreach (var brk in list)
            if (string.IsNullOrWhiteSpace(brk.Name)) brk.Name = "课间";

        lock (_gate) _current.Breaks = list;
        Save();
        RaiseChanged();
    }

    public void SetCourse(DayOfWeek day, int periodIndex, string course)
    {
        if (periodIndex <= 0) return;
        course ??= "";
        lock (_gate)
        {
            if (!_current.Days.TryGetValue(day, out var list) || list is null)
                _current.Days[day] = list = new List<TimetableEntry>();
            var entry = list.FirstOrDefault(e => e.PeriodIndex == periodIndex);
            if (entry is null)
                list.Add(new TimetableEntry { PeriodIndex = periodIndex, Course = course.Trim() });
            else
                entry.Course = course.Trim();
            list.Sort((a, b) => a.PeriodIndex.CompareTo(b.PeriodIndex));
        }
        Save();
        RaiseChanged();
    }

    // =========================================================================
    // 临时换课
    // =========================================================================

    public DayOverride? GetOverride(DateOnly date)
    {
        lock (_gate)
        {
            var found = _overrides.FirstOrDefault(o => o.Date == date);
            return found?.Clone();
        }
    }

    public void SetOverride(DayOverride overrideData)
    {
        if (overrideData is null) throw new ArgumentNullException(nameof(overrideData));
        var copy = overrideData.Clone();
        copy.CourseReplacements ??= new Dictionary<int, string?>();
        copy.CancelledPeriods ??= new HashSet<int>();

        lock (_gate)
        {
            if (copy.IsEmpty)
            {
                _overrides.RemoveAll(o => o.Date == copy.Date);
            }
            else
            {
                copy.UpdatedAt = DateTime.Now;
                _overrides.RemoveAll(o => o.Date == copy.Date);
                _overrides.Add(copy);
                _overrides.Sort((a, b) => a.Date.CompareTo(b.Date));
            }
        }
        Save();
        RaiseChanged();
    }

    public void RemoveOverride(DateOnly date)
    {
        lock (_gate) _overrides.RemoveAll(o => o.Date == date);
        Save();
        RaiseChanged();
    }

    // =========================================================================
    // 持久化
    // =========================================================================

    public void Save()
    {
        WeekTimetable timetable;
        List<DayOverride> overrides;
        lock (_gate)
        {
            timetable = _current;
            overrides = _overrides;
        }
        SafeJsonStore.Save(_timetablePath, timetable);
        SafeJsonStore.Save(_overridesPath, overrides);
    }

    // =========================================================================
    // 规范化
    // =========================================================================

    /// <summary>规整课表：节次按开始时间排序并重排 1..N，课程索引同步迁移，剔除非法时间。</summary>
    private static void NormalizeTimetable(WeekTimetable timetable)
    {
        timetable.Periods ??= new List<ClassPeriod>();
        timetable.Breaks ??= new List<BreakSegment>();
        timetable.Days ??= new Dictionary<DayOfWeek, List<TimetableEntry>>();

        var periods = timetable.Periods
            .Where(p => p is not null && p.End > p.Start)
            .OrderBy(p => p.Start)
            .ThenBy(p => p.Index)
            .ToList();

        var map = new Dictionary<int, int>();
        for (var i = 0; i < periods.Count; i++)
        {
            var period = periods[i];
            var oldIndex = period.Index;
            period.Index = i + 1;
            if (string.IsNullOrWhiteSpace(period.Name)) period.Name = $"第{i + 1}节";
            if (oldIndex > 0) map[oldIndex] = i + 1;
        }
        timetable.Periods = periods;

        // 没有节次数据时保留原课程数据（避免误清空）
        if (map.Count > 0)
            timetable.Days = RemapDays(timetable.Days, map);

        timetable.Breaks = timetable.Breaks
            .Where(b => b is not null && b.End > b.Start)
            .OrderBy(b => b.Start)
            .ToList();
        foreach (var brk in timetable.Breaks)
            if (string.IsNullOrWhiteSpace(brk.Name)) brk.Name = "课间";
    }

    private static Dictionary<DayOfWeek, List<TimetableEntry>> RemapDays(
        Dictionary<DayOfWeek, List<TimetableEntry>>? days, Dictionary<int, int> indexMap)
    {
        var result = new Dictionary<DayOfWeek, List<TimetableEntry>>();
        if (days is null) return result;

        foreach (var (day, entries) in days)
        {
            if (entries is null) continue;
            var list = new List<TimetableEntry>();
            foreach (var entry in entries)
            {
                if (entry is null) continue;
                if (!indexMap.TryGetValue(entry.PeriodIndex, out var newIndex)) continue;
                var existing = list.FirstOrDefault(e => e.PeriodIndex == newIndex);
                if (existing is null)
                {
                    list.Add(new TimetableEntry { PeriodIndex = newIndex, Course = entry.Course ?? "" });
                }
                else if (string.IsNullOrWhiteSpace(existing.Course) && !string.IsNullOrWhiteSpace(entry.Course))
                {
                    existing.Course = entry.Course!;
                }
            }
            if (list.Count > 0)
                result[day] = list.OrderBy(e => e.PeriodIndex).ToList();
        }
        return result;
    }

    private void NormalizeOverrides()
    {
        _overrides = _overrides
            .Where(o => o is not null)
            .GroupBy(o => o.Date)
            .Select(g => g.Last())
            .OrderBy(o => o.Date)
            .ToList();
        foreach (var ov in _overrides)
        {
            ov.CourseReplacements ??= new Dictionary<int, string?>();
            ov.CancelledPeriods ??= new HashSet<int>();
        }
    }
}
