namespace Banji.Core.Models;

// ============================================================================
// 第二轮新增数据模型（Lead 冻结）
// ============================================================================

/// <summary>考试/重要日期倒计时（宠物常驻显示 D-Day）。</summary>
public sealed class CountdownEventItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>目标日期（只到日，不含时间）。</summary>
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public bool Enabled { get; set; } = true;
    public string Color { get; set; } = "#FF6B6B";

    public CountdownEventItem Clone() => new() { Id = Id, Name = Name, Date = Date, Enabled = Enabled, Color = Color };

    /// <summary>剩余天数：0=今天，1=明天，负数=已过去。</summary>
    public int DaysLeft(DateOnly today) => Date.DayNumber - today.DayNumber;
}

public enum ClassBoundaryKind
{
    ClassStart = 0,
    ClassEnd = 1,
    SchoolEnd = 2
}

/// <summary>课表上的一个时间边界（用于铃声校准向导）。</summary>
public sealed class ClassBoundary
{
    public DateTime Time { get; init; }
    public ClassBoundaryKind Kind { get; init; }
    public string Title { get; init; } = "";
    public int PeriodIndex { get; init; }
}

/// <summary>随机分组里的一个小组。</summary>
public sealed class StudentGroup
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public List<Student> Students { get; init; } = new();
    public double AverageScore => Students.Where(s => s.Score.HasValue).Select(s => s.Score!.Value).DefaultIfEmpty(0).Average();
}

public sealed class GroupingOptions
{
    /// <summary>组数（2~12）。</summary>
    public int GroupCount { get; set; } = 4;
    /// <summary>按成绩均衡分组（各组成绩均值接近）。</summary>
    public bool BalanceByScore { get; set; } = true;
    public string Title { get; set; } = "";
}

public sealed class GroupingResult
{
    public string Title { get; init; } = "";
    public string RosterName { get; init; } = "";
    public int TotalStudents { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public List<StudentGroup> Groups { get; init; } = new();

    public string ToPlainText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Title.Length > 0 ? Title : "随机分组结果");
        sb.AppendLine($"名单：{RosterName}（{TotalStudents} 人，{Groups.Count} 组）");
        sb.AppendLine();
        foreach (var g in Groups)
        {
            sb.AppendLine($"{g.Name}（{g.Students.Count} 人）：{string.Join("、", g.Students.Select(s => s.DisplayName))}");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>花名册里一名学生的积分（仪表盘排行用）。</summary>
public sealed class StudentPoints
{
    public string RosterId { get; init; } = "";
    public string StudentKey { get; init; } = "";
    public string StudentName { get; init; } = "";
    public string StudentId { get; init; } = "";
    public int Points { get; init; }
}

/// <summary>宠物陪伴养成统计（纯本地，永不上传）。</summary>
public sealed class PetGrowthStats
{
    public DateOnly FirstUseDate { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public DateOnly LastUseDate { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    /// <summary>累计使用天数（按自然日去重）。</summary>
    public int DaysUsed { get; set; } = 1;
    public int SessionCount { get; set; }
    public int PatCount { get; set; }
    public int PickCount { get; set; }
    public int GroupCount { get; set; }
    public int NoticeCount { get; set; }
    /// <summary>已解锁成就 Id 列表。</summary>
    public List<string> Achievements { get; set; } = new();

    public void Normalize()
    {
        if (DaysUsed < 1) DaysUsed = 1;
        if (PatCount < 0) PatCount = 0;
        if (PickCount < 0) PickCount = 0;
        if (GroupCount < 0) GroupCount = 0;
        if (NoticeCount < 0) NoticeCount = 0;
        Achievements ??= new();
    }
}

/// <summary>一个可解锁的成就（由 PetGrowthService 判断条件）。</summary>
public sealed class Achievement
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public DateTime UnlockedAt { get; init; }
}

/// <summary>备份包的元信息。</summary>
public sealed class BackupInfo
{
    public DateTime CreatedAt { get; init; }
    public string AppVersion { get; init; } = "";
    public int RosterCount { get; init; }
    public int StudentCount { get; init; }
    public bool HasTimetable { get; init; }
    public bool HasSettings { get; init; }
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
}
