using Banji.Core.Models;

namespace Banji.Core.Abstractions;

// ============================================================================
// 服务间传输对象（Lead 冻结）
// ============================================================================

/// <summary>花名册文件解析预览（导入窗口展示用）。</summary>
public sealed class RosterImportPreview
{
    public string FilePath { get; init; } = "";
    public string FormatDescription { get; init; } = "";
    public IReadOnlyList<string> Sheets { get; init; } = Array.Empty<string>();
    public string? SuggestedSheet { get; init; }
    public string SuggestedRosterName { get; init; } = "";
    /// <summary>0-based；-1 表示无表头。</summary>
    public int SuggestedHeaderRow { get; init; }
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyList<string>> SampleRows { get; init; } = Array.Empty<IReadOnlyList<string>>();
    public int EstimatedRows { get; init; }
    public int? SuggestedIdColumn { get; init; }
    public int? SuggestedNameColumn { get; init; }
    public int? SuggestedScoreColumn { get; init; }
    public string? Warning { get; init; }
}

public sealed class RosterImportOptions
{
    public string RosterName { get; set; } = "";
    public string? SheetName { get; set; }
    /// <summary>0-based 表头行；-1 表示无表头。</summary>
    public int HeaderRow { get; set; } = 0;
    public int? IdColumn { get; set; }
    public int? NameColumn { get; set; }
    public int? ScoreColumn { get; set; }
    public RosterSortMode SortMode { get; set; } = RosterSortMode.Original;
    public PickDisplayMode? DisplayModeOverride { get; set; }
}

/// <summary>课表文件解析预览。</summary>
public sealed class TimetableImportPreview
{
    public string FilePath { get; init; } = "";
    public string FormatDescription { get; init; } = "";
    public IReadOnlyList<string> Sheets { get; init; } = Array.Empty<string>();
    public string? SuggestedSheet { get; init; }
    /// <summary>自动识别出的结果（可在导入窗口中预览确认）。为空表示识别失败。</summary>
    public WeekTimetable? Candidate { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public enum ClassTransitionKind
{
    ClassStarted = 0,
    ClassEnded = 1,
    BreakStarted = 2,
    SchoolStarted = 3,
    SchoolEnded = 4,
    DayEnded = 5
}

public sealed class ClassTransitionEventArgs : EventArgs
{
    public ClassTransitionKind Kind { get; init; }
    public DateTime Time { get; init; }
    public CourseOccurrence? Current { get; init; }
    public CourseOccurrence? Next { get; init; }
    public ScheduleSnapshot Snapshot { get; init; } = ScheduleSnapshot.Empty(DateTime.Now);
}
