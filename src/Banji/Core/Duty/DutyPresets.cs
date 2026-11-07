using Banji.Core.Models;

namespace Banji.Core.Duty;

/// <summary>预设包类型（配置向导里勾选/合并，生成后仍可编辑）。</summary>
public enum DutyPresetKind
{
    /// <summary>空模板：什么都不预设。</summary>
    Empty = 0,
    /// <summary>值周包（每周一组，组内固定任务）。</summary>
    Weekly = 1,
    /// <summary>卫生值日包（每天一组，组内固定任务）。</summary>
    DailyCleanup = 2,
    /// <summary>班委包。</summary>
    Committee = 3,
    /// <summary>课代表包（九科各 1 人）。</summary>
    SubjectReps = 4
}

/// <summary>预设包元信息（路径/名称/说明，供向导列表展示）。</summary>
public sealed class DutyPresetInfo
{
    public DutyPresetKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>
/// v1.4.0 值周/值日预设包工厂（配置向导用）。
/// 所有方法都返回**全新的可编辑对象**，调用方可以随意改名 / 改人数 / 增删。
/// 设计依据：`docs/值周值日与单双周设计-v1.4.md` §4、§29 A4（预设只是模板，选完仍可编辑）。
/// </summary>
public static class DutyPresets
{
    /// <summary>全部预设包（向导列出用）。</summary>
    public static IReadOnlyList<DutyPresetInfo> All { get; } = new[]
    {
        new DutyPresetInfo
        {
            Kind = DutyPresetKind.Weekly,
            Name = "值周包",
            Description = "擦黑板 1、讲台整理 1、扫地 2、拖地 2、倒垃圾 1、门窗 1（适合按周轮换）"
        },
        new DutyPresetInfo
        {
            Kind = DutyPresetKind.DailyCleanup,
            Name = "卫生值日包",
            Description = "擦黑板 1、讲台 1、扫地 2、拖地 2、倒垃圾 1、摆桌椅 1、门窗 1、卫生角 1（适合按天轮换）"
        },
        new DutyPresetInfo
        {
            Kind = DutyPresetKind.Committee,
            Name = "班委包",
            Description = "班长、副班长、团支书、学习/纪律/卫生/体育/文艺/宣传/生活/心理委员、电教管理员各 1 人"
        },
        new DutyPresetInfo
        {
            Kind = DutyPresetKind.SubjectReps,
            Name = "课代表包",
            Description = "语文、数学、英语、物理、化学、生物、政治、历史、地理各 1 人（可改 0~4 人）"
        },
        new DutyPresetInfo
        {
            Kind = DutyPresetKind.Empty,
            Name = "空模板",
            Description = "什么都不预设，自己逐个添加岗位"
        }
    };

    /// <summary>值周包任务（名称/人数可按需修改返回值）。</summary>
    public static List<DutyTask> CreateWeeklyTasks() => new()
    {
        Task("w-blackboard", "擦黑板", "值周", 1, 0),
        Task("w-podium", "讲台整理", "值周", 1, 1),
        Task("w-sweep", "扫地", "值周", 2, 2),
        Task("w-mop", "拖地", "值周", 2, 3),
        Task("w-trash", "倒垃圾", "值周", 1, 4),
        Task("w-windows", "门窗", "值周", 1, 5)
    };

    /// <summary>卫生值日包任务（名称/人数可按需修改返回值）。</summary>
    public static List<DutyTask> CreateDailyCleanupTasks() => new()
    {
        Task("d-blackboard", "擦黑板", "卫生", 1, 0),
        Task("d-podium", "讲台", "卫生", 1, 1),
        Task("d-sweep", "扫地", "卫生", 2, 2),
        Task("d-mop", "拖地", "卫生", 2, 3),
        Task("d-trash", "倒垃圾", "卫生", 1, 4),
        Task("d-chairs", "摆桌椅", "卫生", 1, 5),
        Task("d-windows", "门窗", "卫生", 1, 6),
        Task("d-corner", "卫生角", "卫生", 1, 7)
    };

    /// <summary>班委包（12 个常见职位，人数可改；成员留空由向导/窗口绑定）。</summary>
    public static List<CommitteeRole> CreateCommitteeRoles() => new()
    {
        Committee("班长"), Committee("副班长"), Committee("团支书"),
        Committee("学习委员"), Committee("纪律委员"), Committee("卫生委员"),
        Committee("体育委员"), Committee("文艺委员"), Committee("宣传委员"),
        Committee("生活委员"), Committee("心理委员"), Committee("电教管理员")
    };

    /// <summary>课代表包（九科，可改名/增删科目）。</summary>
    public static List<SubjectRepRole> CreateSubjectRepRoles()
        => new[] { "语文", "数学", "英语", "物理", "化学", "生物", "政治", "历史", "地理" }
            .Select(subject => new SubjectRepRole { Subject = subject, Count = 1 })
            .ToList();

    /// <summary>
    /// 按勾选的预设包生成一份 DutyConfig（任务/班委/课代表；分组与学期第一周由向导补充）。
    /// 多个包合并时：任务按名称去重（保留第一个，RequiredCount 取较大值），班委/课代表按名称去重。
    /// </summary>
    public static DutyConfig CreateConfig(params DutyPresetKind[] kinds)
    {
        var config = new DutyConfig();
        var selected = (kinds ?? Array.Empty<DutyPresetKind>()).Distinct().ToList();
        if (selected.Count == 0) selected.Add(DutyPresetKind.Empty);

        var tasks = new List<DutyTask>();
        if (selected.Contains(DutyPresetKind.Weekly)) tasks.AddRange(CreateWeeklyTasks());
        if (selected.Contains(DutyPresetKind.DailyCleanup)) tasks.AddRange(CreateDailyCleanupTasks());
        config.Tasks = MergeTasks(tasks);
        for (var i = 0; i < config.Tasks.Count; i++) config.Tasks[i].Sort = i;

        if (selected.Contains(DutyPresetKind.Committee)) config.Committee = CreateCommitteeRoles();
        if (selected.Contains(DutyPresetKind.SubjectReps)) config.SubjectReps = CreateSubjectRepRoles();

        config.Reminders = new DutyReminderSettings();
        config.Display = new DutyDisplaySettings();
        return config;
    }

    /// <summary>
    /// 按名单顺序把学生平均分成 groupCount 组（人数差 ≤1，组名“第N组”，成员按名单顺序轮流分配）。
    /// 只生成分组与成员，不生成任务分配（可用 <see cref="AutoAssignAllGroups"/> 一键分配）。
    /// </summary>
    public static List<DutyGroup> CreateGroups(IReadOnlyList<Student> students, int groupCount)
    {
        var valid = (students ?? Array.Empty<Student>()).Where(s => s is not null && s.IsValid).ToList();
        var count = Math.Max(1, Math.Min(groupCount <= 0 ? 4 : groupCount, Math.Max(1, valid.Count)));
        var groups = Enumerable.Range(0, count)
            .Select(i => new DutyGroup { Id = $"g{i + 1}", Name = $"第{i + 1}组" })
            .ToList();
        for (var i = 0; i < valid.Count; i++)
            groups[i % count].MemberIds.Add(valid[i].Key);
        return groups;
    }

    /// <summary>按“任务顺序 → 组员顺序”把任务分配写入一个组（对应 FixedPerGroup 模式的自动分配）。</summary>
    public static void AutoAssignGroup(DutyGroup group, IReadOnlyList<DutyTask> tasks)
    {
        if (group is null) throw new ArgumentNullException(nameof(group));
        group.Assignments = BuildAutoAssignments(group.MemberIds, tasks);
    }

    /// <summary>对所有组做一次“任务顺序 → 组员顺序”的自动分配。</summary>
    public static void AutoAssignAllGroups(DutyConfig config)
    {
        if (config is null) return;
        foreach (var group in config.Groups) AutoAssignGroup(group, config.Tasks);
    }

    /// <summary>
    /// 生成“任务顺序 → 组员顺序”的分配表（成员不足时一人多岗循环复用；岗位按 RequiredCount 展开）。
    /// </summary>
    public static List<DutyAssignment> BuildAutoAssignments(
        IReadOnlyList<string> memberIds, IReadOnlyList<DutyTask> tasks)
    {
        var result = new List<DutyAssignment>();
        var members = (memberIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (members.Count == 0) return result;

        var slots = new List<string>();
        foreach (var task in (tasks ?? Array.Empty<DutyTask>())
                     .Where(t => t is not null && t.RequiredCount > 0)
                     .OrderBy(t => t.Sort))
        {
            for (var i = 0; i < Math.Clamp(task.RequiredCount, 0, 20); i++) slots.Add(task.Id);
        }
        for (var i = 0; i < slots.Count; i++)
        {
            result.Add(new DutyAssignment
            {
                StudentId = members[i % members.Count],
                TaskId = slots[i]
            });
        }
        return result;
    }

    // =========================================================================

    private static DutyTask Task(string id, string name, string category, int requiredCount, int sort)
        => new() { Id = id, Name = name, Category = category, RequiredCount = requiredCount, Sort = sort };

    private static CommitteeRole Committee(string type)
        => new() { Type = type, Count = 1 };

    private static List<DutyTask> MergeTasks(IEnumerable<DutyTask> tasks)
    {
        var result = new List<DutyTask>();
        var byName = new Dictionary<string, DutyTask>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (task is null) continue;
            if (byName.TryGetValue(task.Name, out var existing))
            {
                existing.RequiredCount = Math.Max(existing.RequiredCount, task.RequiredCount);
                continue;
            }
            byName[task.Name] = task;
            result.Add(task);
        }
        return result;
    }
}
