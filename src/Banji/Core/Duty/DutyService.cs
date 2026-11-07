using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Core.Duty;

/// <summary>
/// v1.4.0 值周/值日服务（core-roster 负责完整实现：轮换算法、配置向导数据、提醒、展示、打卡）。
/// 这里先提供可编译的最小实现，接口已冻结。
/// </summary>
public sealed class DutyService : IDutyService
{
    private readonly ISettingsService _settings;
    private readonly IRosterService _rosters;
    private readonly string _path;
    private DutyConfig _config;

    public DutyService(ISettingsService settings, IRosterService rosters)
        : this(settings, rosters, Path.Combine(AppPaths.Root, "duty.json"))
    {
    }

    public DutyService(ISettingsService settings, IRosterService rosters, string path)
    {
        _settings = settings;
        _rosters = rosters;
        _path = path;
        _config = SafeJsonStore.Load(_path, () => new DutyConfig()) ?? new DutyConfig();
    }

    public DutyConfig Config => _config;
    public bool IsConfigured => _config.Tasks.Count > 0 && _config.Groups.Count > 0;
    public bool NeedsFirstTimeSetup => !_settings.Current.DutySetupCompleted;

    public event EventHandler? Changed;

    public void Save(DutyConfig config)
    {
        _config = config ?? new DutyConfig();
        SafeJsonStore.Save(_path, _config);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reload()
    {
        _config = SafeJsonStore.Load(_path, () => new DutyConfig()) ?? new DutyConfig();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DutyScheduleView GetSchedule(DateOnly date)
    {
        var group = _config.Groups.FirstOrDefault();
        var tasks = _config.Tasks.Where(t => t.RequiredCount > 0)
            .OrderBy(t => t.Sort)
            .Select(t => new DutyTaskView
            {
                TaskId = t.Id,
                TaskName = t.Name,
                Category = t.Category,
                IsTopTask = _config.Display.ShowBlackboardTop && IsTopTask(t.Name)
            })
            .ToList();
        return new DutyScheduleView
        {
            Date = date,
            Mode = _config.Mode,
            GroupId = group?.Id,
            GroupName = group?.Name,
            Tasks = tasks
        };
    }

    public IReadOnlyList<DutyWeekInfo> GetRotationPreview(DateOnly fromAnyDay, int weeks)
        => Array.Empty<DutyWeekInfo>();

    public void SetWeekOverride(DateOnly weekMonday, string? groupId, bool paused, string? note)
    {
        _config.WeekOverrides.RemoveAll(w => w.WeekMonday == weekMonday);
        _config.WeekOverrides.Add(new DutyWeekOverride
        {
            WeekMonday = weekMonday, GroupId = groupId, Paused = paused, Note = note ?? ""
        });
        Save(_config);
    }

    public void CheckIn(DateOnly date) { }

    public IReadOnlyList<string> GetPetPanelLines(DateOnly date)
    {
        var schedule = GetSchedule(date);
        return schedule.Tasks.Count == 0
            ? Array.Empty<string>()
            : new[] { $"本周值周：{schedule.Summary}" };
    }

    public string BuildCopyText(DateOnly date)
    {
        var schedule = GetSchedule(date);
        return $"【{schedule.Summary}】\n" + string.Join("\n",
            schedule.Tasks.Select(t => $"{t.TaskName}：{MembersText(t)}"));
    }

    private bool IsTopTask(string taskName)
    {
        if (!_config.Display.ShowBlackboardTop) return false;
        return _config.Display.TopTaskKeywords.Any(k =>
            !string.IsNullOrWhiteSpace(k) && taskName.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private string MembersText(DutyTaskView task)
        => task.StudentNames.Count == 0 ? "未安排" : string.Join("、", task.StudentNames);
}
