using Banji.Core.Abstractions;
using Banji.Core.Models;

namespace Banji.Core.Schedule;

/// <summary>占位实现，由 core-schedule 负责整体替换。</summary>
public sealed class ScheduleService : IScheduleService
{
    private readonly ITimetableService _timetable;
    private readonly ISettingsService _settings;

    public ScheduleService(ITimetableService timetable, ISettingsService settings)
    {
        _timetable = timetable;
        _settings = settings;
        _timetable.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _settings.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public ScheduleSnapshot SnapshotAt(DateTime now) => ScheduleSnapshot.Empty(now);
}
