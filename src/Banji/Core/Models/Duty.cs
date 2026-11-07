namespace Banji.Core.Models;

// ============================================================================
// v1.4.0 值周 / 值日 / 固定任职 数据模型（Lead 冻结契约）
// ============================================================================

public enum DutyMode
{
    /// <summary>值周：整组负责一周，按组轮换。</summary>
    Weekly = 0,
    /// <summary>值日：整组负责一天，按组轮换；组内任务固定。</summary>
    Daily = 1
}

public enum DutyAssignmentMode
{
    /// <summary>每组各自固定任务（第 1 组张三擦黑板、第 2 组李四擦黑板…）。</summary>
    FixedPerGroup = 0,
    /// <summary>所有组共用“任务顺序 → 组员顺序”的自动分配。</summary>
    AutoByOrder = 1
}

/// <summary>岗位（擦黑板 / 扫地 / 班长 / 语文课代表…）。</summary>
public sealed class DutyTask
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>卫生 / 值周 / 班委 / 课代表 / 其他。</summary>
    public string Category { get; set; } = "卫生";
    /// <summary>默认需要人数（每组可覆盖为更多人，如力气活分给女生时安排 2 人）。</summary>
    public int RequiredCount { get; set; } = 1;
    public string Note { get; set; } = "";
    public int Sort { get; set; }

    public DutyTask Clone() => (DutyTask)MemberwiseClone();
}

public sealed class DutyAssignment
{
    public string StudentId { get; set; } = "";
    public string TaskId { get; set; } = "";

    public DutyAssignment Clone() => (DutyAssignment)MemberwiseClone();
}

/// <summary>轮值组：成员名单 + 组内任务分配（每组的成员人数、岗位人数可不同）。</summary>
public sealed class DutyGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> MemberIds { get; set; } = new();
    /// <summary>组内「学生 → 岗位」分配；同一岗位可以有多名学生（一个岗位 2 人）。</summary>
    public List<DutyAssignment> Assignments { get; set; } = new();

    public DutyGroup Clone()
    {
        var copy = (DutyGroup)MemberwiseClone();
        copy.MemberIds = MemberIds.ToList();
        copy.Assignments = Assignments.Select(a => a.Clone()).ToList();
        return copy;
    }
}

/// <summary>某一周的手动覆盖（换组 / 暂停值周）。</summary>
public sealed class DutyWeekOverride
{
    public DateOnly WeekMonday { get; set; }
    /// <summary>null 且 Paused=false 表示按正常轮换。</summary>
    public string? GroupId { get; set; }
    public bool Paused { get; set; }
    public string Note { get; set; } = "";

    public DutyWeekOverride Clone() => (DutyWeekOverride)MemberwiseClone();
}

/// <summary>固定任职：班委（类型/人数/成员均可自定义）。</summary>
public sealed class CommitteeRole
{
    public string Type { get; set; } = "";
    public int Count { get; set; } = 1;
    public List<string> MemberIds { get; set; } = new();

    public CommitteeRole Clone()
    {
        var copy = (CommitteeRole)MemberwiseClone();
        copy.MemberIds = MemberIds.ToList();
        return copy;
    }
}

/// <summary>固定任职：课代表（按科目，人数可自定义）。</summary>
public sealed class SubjectRepRole
{
    public string Subject { get; set; } = "";
    public int Count { get; set; } = 1;
    public List<string> MemberIds { get; set; } = new();

    public SubjectRepRole Clone()
    {
        var copy = (SubjectRepRole)MemberwiseClone();
        copy.MemberIds = MemberIds.ToList();
        return copy;
    }
}

/// <summary>提醒设置（全部可关；v1.4 不强制早读/放学气泡提醒）。</summary>
public sealed class DutyReminderSettings
{
    public bool Enabled { get; set; } = false;
    public bool WeekChangeEnabled { get; set; } = true;
    public int WeekChangeMinutesBefore { get; set; } = 10;
    public bool DailyEnabled { get; set; } = true;
    public int DailyMinutesBefore { get; set; } = 5;
    public bool HandoverEnabled { get; set; } = true;
    public int HandoverMinutesBefore { get; set; } = 5;

    public DutyReminderSettings Clone() => (DutyReminderSettings)MemberwiseClone();
}

/// <summary>展示设置：宠物面板、放学后替换课表、黑板类岗位置顶。</summary>
public sealed class DutyDisplaySettings
{
    public bool ShowOnPet { get; set; } = true;
    /// <summary>放学后把课表位置换成轮值信息。</summary>
    public bool ShowAfterSchoolInfo { get; set; } = true;
    /// <summary>放学后展示时长（分钟），之后恢复“已放学”。</summary>
    public int AfterSchoolMinutes { get; set; } = 60;
    /// <summary>这些关键词的岗位在课表折叠/展开态常驻最顶端（如擦黑板）。</summary>
    public List<string> TopTaskKeywords { get; set; } = new() { "擦黑板", "黑板", "讲台" };
    public bool ShowBlackboardTop { get; set; } = true;

    public DutyDisplaySettings Clone()
    {
        var copy = (DutyDisplaySettings)MemberwiseClone();
        copy.TopTaskKeywords = TopTaskKeywords.ToList();
        return copy;
    }
}

public sealed class DutyCheckInItem
{
    public DateOnly Date { get; set; }
    public List<string> StudentIds { get; set; } = new();
    public int Points { get; set; }
    public DateTime At { get; set; } = DateTime.Now;

    public DutyCheckInItem Clone()
    {
        var copy = (DutyCheckInItem)MemberwiseClone();
        copy.StudentIds = StudentIds.ToList();
        return copy;
    }
}

/// <summary>值周/值日完整配置，持久化到 %APPDATA%\Banji\duty.json。</summary>
public sealed class DutyConfig
{
    public int Version { get; set; } = 1;
    public DutyMode Mode { get; set; } = DutyMode.Weekly;
    public DutyAssignmentMode AssignmentMode { get; set; } = DutyAssignmentMode.FixedPerGroup;
    /// <summary>第一个值周组 / 第一轮值日从哪一周（周一）开始；null = 用学期第一周。</summary>
    public DateOnly? AnchorMonday { get; set; }
    public List<DutyTask> Tasks { get; set; } = new();
    public List<DutyGroup> Groups { get; set; } = new();
    /// <summary>组轮换顺序（GroupId）。</summary>
    public List<string> Order { get; set; } = new();
    public List<DutyWeekOverride> WeekOverrides { get; set; } = new();
    public List<CommitteeRole> Committee { get; set; } = new();
    public List<SubjectRepRole> SubjectReps { get; set; } = new();
    public DutyReminderSettings Reminders { get; set; } = new();
    public DutyDisplaySettings Display { get; set; } = new();
    /// <summary>打卡是否加课堂积分（默认关），打卡记录历史。</summary>
    public bool AwardPointsOnCheckIn { get; set; } = false;
    public List<DutyCheckInItem> CheckIns { get; set; } = new();

    public DutyConfig Clone()
    {
        var copy = (DutyConfig)MemberwiseClone();
        copy.Tasks = Tasks.Select(t => t.Clone()).ToList();
        copy.Groups = Groups.Select(g => g.Clone()).ToList();
        copy.Order = Order.ToList();
        copy.WeekOverrides = WeekOverrides.Select(w => w.Clone()).ToList();
        copy.Committee = Committee.Select(c => c.Clone()).ToList();
        copy.SubjectReps = SubjectReps.Select(s => s.Clone()).ToList();
        copy.Reminders = Reminders.Clone();
        copy.Display = Display.Clone();
        copy.CheckIns = CheckIns.Select(c => c.Clone()).ToList();
        return copy;
    }
}

// ------------------------- 解析后的展示 DTO -------------------------

public sealed class DutyTaskView
{
    public string TaskId { get; init; } = "";
    public string TaskName { get; init; } = "";
    public string Category { get; init; } = "";
    public IReadOnlyList<string> StudentIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StudentNames { get; init; } = Array.Empty<string>();
    public bool IsTopTask { get; init; }
    public string MembersText => StudentNames.Count == 0 ? "未安排" : string.Join("、", StudentNames);
}

public sealed class DutyScheduleView
{
    public DateOnly Date { get; init; }
    public DutyMode Mode { get; init; }
    public bool IsPaused { get; init; }
    public string? GroupId { get; init; }
    public string? GroupName { get; init; }
    public IReadOnlyList<DutyTaskView> Tasks { get; init; } = Array.Empty<DutyTaskView>();
    /// <summary>宠物面板用短文本行（已按“黑板类置顶”排序）。</summary>
    public IReadOnlyList<string> PanelLines { get; init; } = Array.Empty<string>();

    public string Summary => IsPaused
        ? "本周暂停"
        : !string.IsNullOrEmpty(GroupName) ? GroupName! : "未配置";
}
