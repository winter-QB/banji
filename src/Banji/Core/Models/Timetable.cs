namespace Banji.Core.Models;

/// <summary>一节课的时间段（第 N 节）。课堂时段可完全自定义。</summary>
public sealed class ClassPeriod
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }

    public TimeSpan Duration => End - Start;
    public string TimeText => $"{Start:hh\\:mm}-{End:hh\\:mm}";

    public ClassPeriod Clone() => new() { Index = Index, Name = Name, Start = Start, End = End };
}

/// <summary>午休 / 大课间等非课堂时间段。</summary>
public sealed class BreakSegment
{
    public string Name { get; set; } = "午休";
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }

    public string TimeText => $"{Start:hh\\:mm}-{End:hh\\:mm}";
    public BreakSegment Clone() => new() { Name = Name, Start = Start, End = End };
}

public sealed class TimetableEntry
{
    public int PeriodIndex { get; set; }
    public string Course { get; set; } = "";

    public TimetableEntry Clone() => new() { PeriodIndex = PeriodIndex, Course = Course };
}

/// <summary>周课表：课堂时段 + 午休 + 每天每节的课程名。</summary>
public sealed class WeekTimetable
{
    public int SchemaVersion { get; set; } = 1;
    public string? SourceFile { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<ClassPeriod> Periods { get; set; } = new();
    public List<BreakSegment> Breaks { get; set; } = new();
    public Dictionary<DayOfWeek, List<TimetableEntry>> Days { get; set; } = new();

    public bool IsEmpty => Periods.Count == 0;

    public IReadOnlyList<TimetableEntry> EntriesFor(DayOfWeek day)
        => Days.TryGetValue(day, out var list) ? list : Array.Empty<TimetableEntry>();

    public string? CourseAt(DayOfWeek day, int periodIndex)
        => EntriesFor(day).FirstOrDefault(e => e.PeriodIndex == periodIndex)?.Course;

    public ClassPeriod? PeriodAt(int periodIndex)
        => Periods.FirstOrDefault(p => p.Index == periodIndex);

    public WeekTimetable Clone()
    {
        var copy = new WeekTimetable
        {
            SchemaVersion = SchemaVersion,
            SourceFile = SourceFile,
            UpdatedAt = UpdatedAt,
            Periods = Periods.Select(p => p.Clone()).ToList(),
            Breaks = Breaks.Select(b => b.Clone()).ToList(),
            Days = Days.ToDictionary(kv => kv.Key, kv => kv.Value.Select(e => e.Clone()).ToList())
        };
        return copy;
    }

    /// <summary>一周的默认示例课表（未导入时使用，课程名为空，界面显示“未设置”）。</summary>
    public static WeekTimetable CreateDefault()
    {
        var tt = new WeekTimetable();
        var starts = new[]
        {
            new TimeSpan(8, 0, 0), new TimeSpan(8, 55, 0), new TimeSpan(10, 0, 0), new TimeSpan(10, 55, 0),
            new TimeSpan(14, 0, 0), new TimeSpan(14, 55, 0), new TimeSpan(15, 50, 0), new TimeSpan(16, 45, 0)
        };
        for (int i = 0; i < starts.Length; i++)
        {
            tt.Periods.Add(new ClassPeriod
            {
                Index = i + 1,
                Name = $"第{i + 1}节",
                Start = starts[i],
                End = starts[i] + TimeSpan.FromMinutes(45)
            });
        }
        tt.Breaks.Add(new BreakSegment { Name = "午休", Start = new TimeSpan(11, 40, 0), End = new TimeSpan(14, 0, 0) });

        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
        {
            tt.Days[day] = Enumerable.Range(1, starts.Length)
                .Select(i => new TimetableEntry { PeriodIndex = i, Course = "" })
                .ToList();
        }
        return tt;
    }
}

/// <summary>某一天的临时换课（换课/调课/取消）。</summary>
public sealed class DayOverride
{
    public DateOnly Date { get; set; }
    public string? Note { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>PeriodIndex → 替换后的课程名。null 或空表示“改成空课”。</summary>
    public Dictionary<int, string?> CourseReplacements { get; set; } = new();

    /// <summary>被取消的节次（如临时放假/活动）。</summary>
    public HashSet<int> CancelledPeriods { get; set; } = new();

    public bool IsEmpty => CourseReplacements.Count == 0 && CancelledPeriods.Count == 0 && string.IsNullOrWhiteSpace(Note);

    public DayOverride Clone() => new()
    {
        Date = Date,
        Note = Note,
        UpdatedAt = UpdatedAt,
        CourseReplacements = new Dictionary<int, string?>(CourseReplacements),
        CancelledPeriods = new HashSet<int>(CancelledPeriods)
    };
}
