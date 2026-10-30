using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Models;

namespace Banji.Core.Timetable;

/// <summary>占位实现，由 core-schedule 负责整体替换（含 Excel 课表识别）。</summary>
public sealed class TimetableService : ITimetableService
{
    private WeekTimetable _current = WeekTimetable.CreateDefault();
    private List<DayOverride> _overrides = new();

    public TimetableService()
    {
        _current = SafeJsonStore.Load(AppPaths.TimetableFile, () => WeekTimetable.CreateDefault());
        _overrides = SafeJsonStore.Load(AppPaths.OverridesFile, () => new List<DayOverride>());
    }

    public WeekTimetable Current => _current;
    public IReadOnlyList<DayOverride> Overrides => _overrides;
    public event EventHandler? Changed;

    public TimetableImportPreview PreviewImport(string filePath) => throw new NotSupportedException("课表导入待实现");
    public WeekTimetable Import(string filePath, string? sheetName = null) => throw new NotSupportedException("课表导入待实现");

    public void SetTimetable(WeekTimetable timetable) { _current = timetable; Save(); Changed?.Invoke(this, EventArgs.Empty); }

    public void SetPeriods(IEnumerable<ClassPeriod> periods)
    {
        _current.Periods = periods.OrderBy(p => p.Start).ToList();
        for (var i = 0; i < _current.Periods.Count; i++) _current.Periods[i].Index = i + 1;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetBreaks(IEnumerable<BreakSegment> breaks)
    {
        _current.Breaks = breaks.OrderBy(b => b.Start).ToList();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetCourse(DayOfWeek day, int periodIndex, string course)
    {
        var list = _current.Days.TryGetValue(day, out var l) ? l : (_current.Days[day] = new List<TimetableEntry>());
        var entry = list.FirstOrDefault(e => e.PeriodIndex == periodIndex);
        if (entry is null) list.Add(new TimetableEntry { PeriodIndex = periodIndex, Course = course });
        else entry.Course = course;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DayOverride? GetOverride(DateOnly date) => _overrides.FirstOrDefault(o => o.Date == date);

    public void SetOverride(DayOverride overrideData)
    {
        _overrides.RemoveAll(o => o.Date == overrideData.Date);
        _overrides.Add(overrideData);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveOverride(DateOnly date)
    {
        _overrides.RemoveAll(o => o.Date == date);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        SafeJsonStore.Save(AppPaths.TimetableFile, _current);
        SafeJsonStore.Save(AppPaths.OverridesFile, _overrides);
    }
}
