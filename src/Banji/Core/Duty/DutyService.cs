using System.Text;
using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Core.Duty;

/// <summary>
/// v1.4.0 值周 / 值日 / 固定任职服务（完整实现）。
///
/// 设计依据：`docs/值周值日与单双周设计-v1.4.md`（§29 为最终决定）+ `ARCHITECTURE.md` §13。
/// 核心规则：
/// • 统一“按组轮换”：Weekly = 每周换一组；Daily = 每天换一组；
///   轮换起点 = <see cref="DutyConfig.AnchorMonday"/>（null 时取 <c>Settings.SemesterFirstWeekMonday</c>），
///   顺序 = <see cref="DutyConfig.Order"/>；负数周 / 跨年用 floor-division + 非负取模。
/// • 周覆盖优先：暂停周不安排但仍占一个周期（下周继续下一组）；覆盖周强制指定组/恢复自动。
/// • 任务分配两种模式：FixedPerGroup（每组各自固定）/ AutoByOrder（任务顺序 → 组员顺序自动分配）；
///   同一岗位可安排多人（同 TaskId 多条 Assignment）；允许一人多岗；每组成员数/岗位人数可不同。
/// • 班委 / 课代表为固定任职，不参与轮换；姓名按当前花名册解析，缺失显示“已不在花名册”。
/// • 黑板/讲台类岗位（关键词可配置）在排班与宠物面板中置顶。
/// • 打卡默认不积分；开启 <see cref="DutyConfig.AwardPointsOnCheckIn"/> 时调用 <see cref="IRosterService.AddPoints"/>。
/// 配置持久化到 <c>%APPDATA%\Banji\duty.json</c>（原子写），并纳入备份。
/// 时间通过 <c>Func&lt;DateTime&gt;</c> 注入，轮换只依赖传入日期，便于测试。
/// </summary>
public sealed class DutyService : IDutyService
{
    /// <summary>周覆盖/轮换最多向前预览的周数。</summary>
    public const int MaxPreviewWeeks = 52;

    private readonly object _gate = new();
    private readonly ISettingsService _settings;
    private readonly IRosterService _rosters;
    private readonly string _path;
    private readonly Func<DateTime> _now;
    private DutyConfig _config;

    // =========================================================================
    // 构造 / 生命周期
    // =========================================================================

    /// <summary>宿主（AppServices）使用的构造函数。</summary>
    public DutyService(ISettingsService settings, IRosterService rosters)
        : this(settings, rosters, Path.Combine(AppPaths.Root, "duty.json"), null)
    {
    }

    public DutyService(ISettingsService settings, IRosterService rosters, string path)
        : this(settings, rosters, path, null)
    {
    }

    /// <summary>可测试构造函数：注入 duty.json 路径与时间。</summary>
    public DutyService(ISettingsService settings, IRosterService rosters, string path, Func<DateTime>? now)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _rosters = rosters ?? throw new ArgumentNullException(nameof(rosters));
        _path = string.IsNullOrWhiteSpace(path) ? Path.Combine(AppPaths.Root, "duty.json") : path;
        _now = now ?? (() => DateTime.Now);
        _config = LoadCore();
    }

    public DutyConfig Config
    {
        get { lock (_gate) return _config; }
    }

    public bool IsConfigured
    {
        get
        {
            lock (_gate)
            {
                return _config.Tasks.Count > 0 && EffectiveOrder(_config).Count > 0 && ResolveAnchor(_config) is not null;
            }
        }
    }

    public bool NeedsFirstTimeSetup => !_settings.Current.DutySetupCompleted;

    public event EventHandler? Changed;

    // =========================================================================
    // 保存 / 重载
    // =========================================================================

    public void Save(DutyConfig config)
    {
        lock (_gate)
        {
            _config = Normalize(config ?? new DutyConfig());
            PersistCore();
        }
        SyncSettingsFromConfig();
        RaiseChanged();
    }

    public void Reload()
    {
        lock (_gate)
        {
            _config = LoadCore();
        }
        RaiseChanged();
    }

    // =========================================================================
    // 排班解析
    // =========================================================================

    public DutyScheduleView GetSchedule(DateOnly date)
    {
        DutyConfig config;
        lock (_gate) config = _config;
        return BuildSchedule(config, date);
    }

    public IReadOnlyList<DutyWeekInfo> GetRotationPreview(DateOnly fromAnyDay, int weeks)
    {
        DutyConfig config;
        lock (_gate) config = _config;
        weeks = Math.Clamp(weeks, 1, MaxPreviewWeeks);
        var result = new List<DutyWeekInfo>(weeks);
        var anchor = ResolveAnchor(config);
        var order = EffectiveOrder(config);
        if (anchor is null || order.Count == 0 || config.Tasks.Count == 0) return result;

        if (config.Mode == DutyMode.Weekly)
        {
            var startMonday = ToMonday(fromAnyDay);
            for (var i = 0; i < weeks; i++)
            {
                var monday = startMonday.AddDays(7 * i);
                result.Add(BuildWeekInfo(config, anchor.Value, order, monday));
            }
        }
        else
        {
            // 值日模式：每天一组，这里返回未来 N 天的安排（每项一个日期）
            for (var i = 0; i < weeks; i++)
            {
                var day = fromAnyDay.AddDays(i);
                var schedule = BuildSchedule(config, day);
                result.Add(new DutyWeekInfo
                {
                    WeekMonday = day,
                    GroupId = schedule.GroupId,
                    GroupName = schedule.GroupName,
                    Paused = schedule.IsPaused,
                    Note = schedule.IsPaused ? "值日暂停" : ""
                });
            }
        }
        return result;
    }

    public void SetWeekOverride(DateOnly weekMonday, string? groupId, bool paused, string? note)
    {
        var monday = ToMonday(weekMonday);
        var groupIdTrimmed = groupId?.Trim() ?? "";
        var noteTrimmed = note?.Trim() ?? "";

        lock (_gate)
        {
            var existing = _config.WeekOverrides.FirstOrDefault(w => w.WeekMonday == monday);
            if (!paused && groupIdTrimmed.Length == 0)
            {
                // 恢复自动轮换
                if (existing is null) return;
                _config.WeekOverrides.Remove(existing);
            }
            else
            {
                var targetGroupId = paused ? null : groupIdTrimmed;
                if (!paused && !_config.Groups.Any(g => string.Equals(g.Id, targetGroupId, StringComparison.Ordinal)))
                    throw new InvalidOperationException("找不到要指定的小组，请刷新后重试。");

                if (existing is null)
                {
                    _config.WeekOverrides.Add(new DutyWeekOverride
                    {
                        WeekMonday = monday,
                        GroupId = targetGroupId,
                        Paused = paused,
                        Note = noteTrimmed
                    });
                }
                else
                {
                    existing.GroupId = targetGroupId;
                    existing.Paused = paused;
                    existing.Note = noteTrimmed;
                }
            }

            _config = Normalize(_config);
            PersistCore();
        }
        RaiseChanged();
    }

    public void CheckIn(DateOnly date)
    {
        var awardPoints = false;
        List<string> awardStudentIds = new();
        var recorded = false;

        lock (_gate)
        {
            if (!_config.CheckIns.Any(c => c.Date == date)) // 同日防重复
            {
                var schedule = BuildSchedule(_config, date);
                var ids = schedule.Tasks
                    .SelectMany(t => t.StudentIds)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (ids.Count > 0)
                {
                    _config.CheckIns.Add(new DutyCheckInItem
                    {
                        Date = date,
                        StudentIds = ids,
                        Points = _config.AwardPointsOnCheckIn ? 1 : 0,
                        At = _now()
                    });
                    recorded = true;
                    if (_config.AwardPointsOnCheckIn)
                    {
                        awardPoints = true;
                        awardStudentIds = ids;
                    }
                    _config = Normalize(_config);
                    PersistCore();
                }
            }
        }

        // 积分在锁外执行，避免花名册 Changed 事件回调里再访问 Duty 造成阻塞
        if (awardPoints && awardStudentIds.Count > 0) AwardCheckInPoints(awardStudentIds, 1);
        if (recorded) RaiseChanged();
    }

    public IReadOnlyList<string> GetPetPanelLines(DateOnly date)
    {
        bool enabled;
        lock (_gate)
        {
            enabled = _config.Display.ShowOnPet && _settings.Current.ShowDutyOnPet;
        }
        if (!enabled) return Array.Empty<string>();
        return GetSchedule(date).PanelLines;
    }

    public string BuildCopyText(DateOnly date)
    {
        DutyConfig config;
        lock (_gate) config = _config;

        var schedule = BuildSchedule(config, date);
        var rosterName = _rosters.Active?.Name;
        var title = string.IsNullOrWhiteSpace(rosterName) ? "本班" : rosterName.Trim();
        var sb = new StringBuilder();

        if (config.Mode == DutyMode.Weekly)
        {
            var monday = ToMonday(date);
            sb.AppendLine($"{title} 值周安排（{monday:yyyy-MM-dd} 起）");
        }
        else
        {
            sb.AppendLine($"{title} 值日安排（{date:yyyy-MM-dd}）");
        }

        var modeText = config.Mode == DutyMode.Weekly ? "本周值周" : "今日值日";
        if (schedule.IsPaused)
        {
            sb.AppendLine($"{modeText}：暂停");
            if (schedule.PanelLines.Count > 0)
                sb.AppendLine($"原因：{schedule.PanelLines[0]}");
        }
        else if (string.IsNullOrWhiteSpace(schedule.GroupName))
        {
            sb.AppendLine($"{modeText}：暂未配置");
        }
        else
        {
            sb.AppendLine($"{modeText}：{schedule.GroupName}");
            foreach (var task in schedule.Tasks)
                sb.AppendLine($"{task.TaskName}：{task.MembersText}");
        }

        AppendFixedRoles(sb, config);
        return sb.ToString().TrimEnd();
    }

    // =========================================================================
    // 排班校验（未分配岗位 / 人员 / 花名册缺失成员，供 UI 提示）
    // =========================================================================

    /// <summary>校验某个组的任务分配情况（UI 向导/窗口用；不属于冻结接口，但稳定可用）。</summary>
    public DutyAssignmentCheck CheckAssignments(string? groupId)
    {
        DutyConfig config;
        lock (_gate) config = _config;

        if (string.IsNullOrWhiteSpace(groupId))
            return new DutyAssignmentCheck { GroupId = "", GroupName = "" };

        var group = config.Groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.Ordinal));
        if (group is null)
            return new DutyAssignmentCheck { GroupId = groupId, GroupName = "" };

        var students = RosterMap();
        var assignments = ResolveAssignments(config, group);
        var enabledTasks = config.Tasks.Where(t => t.RequiredCount > 0).ToList();
        var assignedTaskIds = assignments.Select(a => a.TaskId).ToHashSet(StringComparer.Ordinal);

        var unassignedTasks = enabledTasks
            .Where(t => !assignedTaskIds.Contains(t.Id))
            .Select(t => t.Name)
            .ToList();

        var assignedStudentIds = assignments.Select(a => a.StudentId).ToHashSet(StringComparer.Ordinal);
        var unassignedStudents = group.MemberIds
            .Where(id => !assignedStudentIds.Contains(id))
            .Select(id => students.TryGetValue(id, out var student) ? student.DisplayName : $"{id}（已不在花名册）")
            .ToList();

        var missingStudentIds = group.MemberIds.Where(id => !students.ContainsKey(id)).ToList();

        return new DutyAssignmentCheck
        {
            GroupId = group.Id,
            GroupName = group.Name,
            UnassignedTaskNames = unassignedTasks,
            UnassignedStudentNames = unassignedStudents,
            MissingStudentIds = missingStudentIds
        };
    }

    // =========================================================================
    // 核心算法
    // =========================================================================

    private DutyScheduleView BuildSchedule(DutyConfig config, DateOnly date)
    {
        var anchor = ResolveAnchor(config);
        var order = EffectiveOrder(config);
        if (config.Tasks.Count == 0 || order.Count == 0 || anchor is null)
            return EmptySchedule(date, config.Mode);

        var (groupId, paused, note) = ResolveRotation(config, anchor.Value, order, date);
        if (paused)
        {
            var text = config.Mode == DutyMode.Weekly ? "本周值周暂停" : "今日值日暂停";
            if (!string.IsNullOrWhiteSpace(note)) text += $"（{note}）";
            return new DutyScheduleView
            {
                Date = date,
                Mode = config.Mode,
                IsPaused = true,
                Tasks = Array.Empty<DutyTaskView>(),
                PanelLines = new[] { text }
            };
        }

        var group = groupId is null ? null : config.Groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.Ordinal));
        if (group is null) return EmptySchedule(date, config.Mode);

        var students = RosterMap();
        var assignments = ResolveAssignments(config, group);
        var tasks = BuildTaskViews(config, assignments, students);
        return new DutyScheduleView
        {
            Date = date,
            Mode = config.Mode,
            GroupId = group.Id,
            GroupName = group.Name,
            Tasks = tasks,
            PanelLines = BuildPanelLines(config, group.Name, tasks)
        };
    }

    private static DutyScheduleView EmptySchedule(DateOnly date, DutyMode mode) => new()
    {
        Date = date,
        Mode = mode,
        Tasks = Array.Empty<DutyTaskView>(),
        PanelLines = Array.Empty<string>()
    };

    /// <summary>解析某一周的轮换组：周覆盖（暂停/指定组）优先，否则按周期取模。</summary>
    private (string? GroupId, bool Paused, string Note) ResolveRotation(
        DutyConfig config, DateOnly anchorMonday, IReadOnlyList<string> order, DateOnly date)
    {
        var monday = ToMonday(date);
        var weekOverride = config.WeekOverrides.FirstOrDefault(w => w.WeekMonday == monday);

        if (weekOverride?.Paused == true) return (null, true, weekOverride.Note ?? "");
        if (weekOverride?.GroupId is { Length: > 0 } overriddenGroup
            && config.Groups.Any(g => string.Equals(g.Id, overriddenGroup, StringComparison.Ordinal)))
        {
            return (overriddenGroup, false, weekOverride.Note ?? "");
        }
        if (order.Count == 0) return (null, false, weekOverride?.Note ?? "");

        var index = config.Mode == DutyMode.Weekly
            ? WeekIndex(anchorMonday, monday)
            : DayIndex(anchorMonday, date);
        return (order[Mod(index, order.Count)], false, weekOverride?.Note ?? "");
    }

    private DutyWeekInfo BuildWeekInfo(DutyConfig config, DateOnly anchorMonday, IReadOnlyList<string> order, DateOnly monday)
    {
        var overrideEntry = config.WeekOverrides.FirstOrDefault(w => w.WeekMonday == monday);
        if (overrideEntry?.Paused == true)
        {
            return new DutyWeekInfo { WeekMonday = monday, Paused = true, Note = overrideEntry.Note ?? "" };
        }

        string? groupId = null;
        if (overrideEntry?.GroupId is { Length: > 0 } overridden
            && config.Groups.Any(g => string.Equals(g.Id, overridden, StringComparison.Ordinal)))
        {
            groupId = overridden;
        }
        groupId ??= order[Mod(WeekIndex(anchorMonday, monday), order.Count)];

        var group = config.Groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.Ordinal));
        return new DutyWeekInfo
        {
            WeekMonday = monday,
            GroupId = group?.Id,
            GroupName = group?.Name,
            Paused = false,
            Note = overrideEntry?.Note ?? ""
        };
    }

    /// <summary>按模式解析任务分配：FixedPerGroup 用组内固定分配，AutoByOrder 用任务顺序→组员顺序自动分配。</summary>
    private static List<DutyAssignment> ResolveAssignments(DutyConfig config, DutyGroup group)
    {
        var enabledTasks = config.Tasks.Where(t => t.RequiredCount > 0).ToList();
        var enabledTaskIds = enabledTasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        if (config.AssignmentMode == DutyAssignmentMode.AutoByOrder)
        {
            var members = group.MemberIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (members.Count == 0) return new List<DutyAssignment>();

            var result = new List<DutyAssignment>();
            var slotIndex = 0;
            foreach (var task in enabledTasks.OrderBy(t => t.Sort))
            {
                for (var i = 0; i < Math.Clamp(task.RequiredCount, 0, 50); i++)
                {
                    result.Add(new DutyAssignment
                    {
                        StudentId = members[slotIndex % members.Count],
                        TaskId = task.Id
                    });
                    slotIndex++;
                }
            }
            return result;
        }

        // FixedPerGroup：使用组内配置（同 TaskId 可以有多名学生 = 一个岗位 2 人）
        return group.Assignments
            .Where(a => a is not null
                        && !string.IsNullOrWhiteSpace(a.StudentId)
                        && !string.IsNullOrWhiteSpace(a.TaskId)
                        && enabledTaskIds.Contains(a.TaskId))
            .Select(a => new DutyAssignment { StudentId = a.StudentId.Trim(), TaskId = a.TaskId.Trim() })
            .GroupBy(a => a.StudentId + "\u0001" + a.TaskId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private List<DutyTaskView> BuildTaskViews(
        DutyConfig config, IReadOnlyList<DutyAssignment> assignments, IReadOnlyDictionary<string, Student> students)
    {
        var views = new List<DutyTaskView>();
        var orderedTasks = config.Tasks
            .Where(t => t.RequiredCount > 0)
            .Select((task, index) => (Task: task, Index: index))
            .OrderBy(x => IsTopTask(config, x.Task) ? 0 : 1)
            .ThenBy(x => x.Task.Sort)
            .ThenBy(x => x.Index)
            .ToList();

        foreach (var (task, _) in orderedTasks)
        {
            var studentIds = assignments
                .Where(a => string.Equals(a.TaskId, task.Id, StringComparison.Ordinal))
                .Select(a => a.StudentId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var studentNames = studentIds
                .Select(id => students.TryGetValue(id, out var student)
                    ? student.DisplayName
                    : $"{id}（已不在花名册）")
                .ToList();

            views.Add(new DutyTaskView
            {
                TaskId = task.Id,
                TaskName = task.Name,
                Category = task.Category,
                StudentIds = studentIds,
                StudentNames = studentNames,
                IsTopTask = IsTopTask(config, task)
            });
        }
        return views;
    }

    private static IReadOnlyList<string> BuildPanelLines(
        DutyConfig config, string groupName, IReadOnlyList<DutyTaskView> tasks)
    {
        var lines = new List<string>(4)
        {
            config.Mode == DutyMode.Weekly ? $"本周值周：{groupName}" : $"今日值日：{groupName}"
        };

        // 黑板类岗位优先（tasks 已按置顶排序），最多再显示 2 行
        foreach (var task in tasks.Where(t => t.IsTopTask).Take(2))
            lines.Add($"{task.TaskName}：{task.MembersText}");
        foreach (var task in tasks.Where(t => !t.IsTopTask && t.StudentNames.Count > 0).Take(1))
            lines.Add($"{task.TaskName}：{task.MembersText}");
        return lines;
    }

    private void AppendFixedRoles(StringBuilder sb, DutyConfig config)
    {
        var committee = config.Committee
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Type))
            .ToList();
        var reps = config.SubjectReps
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Subject))
            .ToList();
        if (committee.Count == 0 && reps.Count == 0) return;

        var students = RosterMap();
        sb.AppendLine();
        if (committee.Count > 0)
        {
            sb.AppendLine("【班委】");
            foreach (var role in committee)
                sb.AppendLine($"{role.Type}：{MembersText(role.MemberIds, students)}");
        }
        if (reps.Count > 0)
        {
            if (committee.Count > 0) sb.AppendLine();
            sb.AppendLine("【课代表】");
            foreach (var role in reps)
                sb.AppendLine($"{role.Subject}：{MembersText(role.MemberIds, students)}");
        }
    }

    private static string MembersText(IEnumerable<string> memberIds, IReadOnlyDictionary<string, Student> students)
    {
        var names = (memberIds ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => students.TryGetValue(id, out var student) ? student.DisplayName : $"{id}（已不在花名册）")
            .ToList();
        return names.Count == 0 ? "未安排" : string.Join("、", names);
    }

    private bool IsTopTask(DutyConfig config, DutyTask task)
    {
        var display = config.Display;
        if (!display.ShowBlackboardTop) return false;
        var keywords = display.TopTaskKeywords;
        if (keywords is null || keywords.Count == 0) return false;
        return keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Any(k => task.Name.Contains(k, StringComparison.OrdinalIgnoreCase)
                      || task.Category.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    // =========================================================================
    // 打卡积分
    // =========================================================================

    private void AwardCheckInPoints(IReadOnlyList<string> studentIds, int points)
    {
        try
        {
            var roster = _rosters.Active;
            if (roster is null) return;
            foreach (var studentId in studentIds)
            {
                try
                {
                    _rosters.AddPoints(roster.Id, studentId, points);
                }
                catch (Exception ex)
                {
                    Log.Error($"值日打卡积分失败：{studentId}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("值日打卡积分失败（花名册不可用）", ex);
        }
    }

    // =========================================================================
    // 配置规范化 / 持久化
    // =========================================================================

    private DutyConfig LoadCore()
    {
        try
        {
            var loaded = SafeJsonStore.Load(_path, () => new DutyConfig()) ?? new DutyConfig();
            return Normalize(loaded);
        }
        catch (Exception ex)
        {
            Log.Error("读取 duty.json 失败，已从默认配置开始", ex);
            return Normalize(new DutyConfig());
        }
    }

    private void PersistCore()
    {
        try
        {
            SafeJsonStore.Save(_path, _config);
        }
        catch (Exception ex)
        {
            Log.Error("保存 duty.json 失败", ex);
        }
    }

    private void SyncSettingsFromConfig()
    {
        try
        {
            DutyConfig config;
            lock (_gate) config = _config;
            if (config.AnchorMonday is not { } anchor) return;
            if (_settings.Current.SemesterFirstWeekMonday == anchor) return;
            // 值周轮换起点与单双周锚点共用“开学第一周”
            _settings.Update(x => x.SemesterFirstWeekMonday = anchor);
        }
        catch (Exception ex)
        {
            Log.Error("同步开学第一周到设置失败", ex);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("值周/值日 Changed 事件处理失败", ex);
        }
    }

    /// <summary>把配置收敛到合法状态：补 Id/名称、去重、清空非法引用、限制数值范围。</summary>
    private DutyConfig Normalize(DutyConfig config)
    {
        config.Tasks ??= new List<DutyTask>();
        config.Groups ??= new List<DutyGroup>();
        config.Order ??= new List<string>();
        config.WeekOverrides ??= new List<DutyWeekOverride>();
        config.Committee ??= new List<CommitteeRole>();
        config.SubjectReps ??= new List<SubjectRepRole>();
        config.Reminders ??= new DutyReminderSettings();
        config.Display ??= new DutyDisplaySettings();
        config.CheckIns ??= new List<DutyCheckInItem>();
        config.Version = Math.Max(1, config.Version);

        // ---------- 岗位 ----------
        var usedTaskIds = new HashSet<string>(StringComparer.Ordinal);
        var tasks = new List<DutyTask>();
        foreach (var task in config.Tasks)
        {
            if (task is null) continue;
            task.Name = string.IsNullOrWhiteSpace(task.Name) ? "未命名岗位" : task.Name.Trim();
            task.Category = string.IsNullOrWhiteSpace(task.Category) ? "卫生" : task.Category.Trim();
            task.Note ??= "";
            task.RequiredCount = Math.Clamp(task.RequiredCount, 0, 50);
            if (string.IsNullOrWhiteSpace(task.Id) || !usedTaskIds.Add(task.Id))
                task.Id = UniqueId(usedTaskIds, "task-");
            tasks.Add(task);
        }
        config.Tasks = tasks;

        // ---------- 轮值组 ----------
        var usedGroupIds = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<DutyGroup>();
        var groupIndex = 0;
        foreach (var group in config.Groups)
        {
            if (group is null) continue;
            groupIndex++;
            group.Name = string.IsNullOrWhiteSpace(group.Name) ? $"第{groupIndex}组" : group.Name.Trim();
            group.MemberIds = (group.MemberIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            group.Assignments = (group.Assignments ?? new List<DutyAssignment>())
                .Where(a => a is not null
                            && !string.IsNullOrWhiteSpace(a.StudentId)
                            && !string.IsNullOrWhiteSpace(a.TaskId))
                .Select(a => new DutyAssignment { StudentId = a.StudentId.Trim(), TaskId = a.TaskId.Trim() })
                .GroupBy(a => a.StudentId + "\u0001" + a.TaskId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (string.IsNullOrWhiteSpace(group.Id) || !usedGroupIds.Add(group.Id))
                group.Id = UniqueId(usedGroupIds, "group-");
            groups.Add(group);
        }
        config.Groups = groups;

        // ---------- 轮换顺序 ----------
        var validGroupIds = config.Groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var order = (config.Order ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id) && validGroupIds.Contains(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var group in config.Groups)
        {
            if (!order.Contains(group.Id)) order.Add(group.Id);
        }
        config.Order = order;

        // ---------- 周覆盖 ----------
        var weekOverrides = new List<DutyWeekOverride>();
        foreach (var entry in config.WeekOverrides)
        {
            if (entry is null) continue;
            entry.WeekMonday = ToMonday(entry.WeekMonday);
            entry.Note ??= "";
            if (entry.GroupId is { Length: > 0 } groupId && !validGroupIds.Contains(groupId))
                entry.GroupId = null;
            if (entry.GroupId is not null) entry.GroupId = entry.GroupId.Trim();
            if (entry.Paused) entry.GroupId = null;
            var duplicate = weekOverrides.FirstOrDefault(w => w.WeekMonday == entry.WeekMonday);
            if (duplicate is not null) weekOverrides.Remove(duplicate);
            weekOverrides.Add(entry);
        }
        config.WeekOverrides = weekOverrides
            .OrderBy(w => w.WeekMonday.DayNumber)
            .ToList();

        // ---------- 固定任职 ----------
        config.Committee = NormalizeRoles(config.Committee, r => r.Type, (r, key) => r.Type = key);
        config.SubjectReps = NormalizeRoles(config.SubjectReps, r => r.Subject, (r, key) => r.Subject = key);

        // ---------- 提醒 / 展示 ----------
        config.Reminders.WeekChangeMinutesBefore = Math.Clamp(config.Reminders.WeekChangeMinutesBefore, 0, 120);
        config.Reminders.DailyMinutesBefore = Math.Clamp(config.Reminders.DailyMinutesBefore, 0, 120);
        config.Reminders.HandoverMinutesBefore = Math.Clamp(config.Reminders.HandoverMinutesBefore, 0, 120);

        var display = config.Display;
        display.AfterSchoolMinutes = Math.Clamp(display.AfterSchoolMinutes, 0, 600);
        var keywords = (display.TopTaskKeywords ?? new List<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
        if (keywords.Count == 0) keywords = new List<string> { "擦黑板", "黑板", "讲台" };
        display.TopTaskKeywords = keywords;

        // ---------- 打卡记录 ----------
        var checkIns = new List<DutyCheckInItem>();
        foreach (var item in config.CheckIns)
        {
            if (item is null) continue;
            item.StudentIds = (item.StudentIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (item.StudentIds.Count == 0) continue;
            item.Points = Math.Clamp(item.Points, 0, 100);
            checkIns.Add(item);
        }
        config.CheckIns = checkIns
            .OrderBy(c => c.Date.DayNumber)
            .ThenBy(c => c.At)
            .TakeLast(730)
            .ToList();

        return config;
    }

    private static List<T> NormalizeRoles<T>(List<T> roles, Func<T, string> getKey, Action<T, string> setKey)
        where T : class
    {
        var result = new List<T>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (role is null) continue;
            var key = (getKey(role) ?? "").Trim();
            if (key.Length == 0) continue;
            if (!used.Add(key)) continue;
            setKey(role, key);

            switch (role)
            {
                case CommitteeRole committee:
                    committee.Count = Math.Clamp(committee.Count, 0, 20);
                    committee.MemberIds = CleanIds(committee.MemberIds);
                    break;
                case SubjectRepRole rep:
                    rep.Count = Math.Clamp(rep.Count, 0, 20);
                    rep.MemberIds = CleanIds(rep.MemberIds);
                    break;
            }
            result.Add(role);
        }
        return result;
    }

    private static List<string> CleanIds(IEnumerable<string>? ids)
        => (ids ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string UniqueId(HashSet<string> used, string prefix)
    {
        var index = 1;
        while (true)
        {
            var id = prefix + index;
            if (used.Add(id)) return id;
            index++;
        }
    }

    // =========================================================================
    // 轮换数学 / 花名册
    // =========================================================================

    private DateOnly? ResolveAnchor(DutyConfig config)
    {
        var raw = config.AnchorMonday ?? _settings.Current.SemesterFirstWeekMonday;
        return raw is null ? null : ToMonday(raw.Value);
    }

    private static IReadOnlyList<string> EffectiveOrder(DutyConfig config)
    {
        if (config.Groups.Count == 0) return Array.Empty<string>();
        var valid = config.Groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var order = config.Order
            .Where(id => !string.IsNullOrWhiteSpace(id) && valid.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var group in config.Groups)
        {
            if (!order.Contains(group.Id)) order.Add(group.Id);
        }
        return order;
    }

    private static int WeekIndex(DateOnly anchorMonday, DateOnly monday)
        => (int)Math.Floor((ToMonday(monday).DayNumber - anchorMonday.DayNumber) / 7.0);

    private static int DayIndex(DateOnly anchorMonday, DateOnly date)
        => date.DayNumber - anchorMonday.DayNumber;

    /// <summary>非负取模（负数周也能得到正确组号）。</summary>
    private static int Mod(int value, int modulus)
    {
        if (modulus <= 0) return 0;
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    /// <summary>把日期对齐到所在周的周一（Monday=0 … Sunday=6）。</summary>
    public static DateOnly ToMonday(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }

    private Dictionary<string, Student> RosterMap()
    {
        var map = new Dictionary<string, Student>(StringComparer.Ordinal);
        try
        {
            var roster = _rosters.Active;
            if (roster is null) return map;
            foreach (var student in roster.Students.Where(s => s is not null && s.IsValid))
                map[student.Key] = student;
        }
        catch (Exception ex)
        {
            Log.Error("读取花名册失败（值日姓名解析）", ex);
        }
        return map;
    }
}

/// <summary>某个轮值组的任务分配体检结果（UI 提示用）。</summary>
public sealed class DutyAssignmentCheck
{
    public string GroupId { get; init; } = "";
    public string GroupName { get; init; } = "";
    /// <summary>没人负责的岗位名（启用但无分配）。</summary>
    public IReadOnlyList<string> UnassignedTaskNames { get; init; } = Array.Empty<string>();
    /// <summary>没有岗位的组成员显示名。</summary>
    public IReadOnlyList<string> UnassignedStudentNames { get; init; } = Array.Empty<string>();
    /// <summary>花名册里已找不到的成员 Id。</summary>
    public IReadOnlyList<string> MissingStudentIds { get; init; } = Array.Empty<string>();

    public bool HasIssues
        => UnassignedTaskNames.Count > 0 || MissingStudentIds.Count > 0;

    /// <summary>中文提示（没有问题时返回空串）。</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (UnassignedTaskNames.Count > 0)
                parts.Add($"未分配岗位：{string.Join("、", UnassignedTaskNames)}");
            if (UnassignedStudentNames.Count > 0)
                parts.Add($"未分配任务：{string.Join("、", UnassignedStudentNames)}");
            if (MissingStudentIds.Count > 0)
                parts.Add($"已不在花名册：{string.Join("、", MissingStudentIds)}");
            return string.Join("；", parts);
        }
    }
}
