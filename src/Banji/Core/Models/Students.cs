namespace Banji.Core.Models;

/// <summary>一名学生。姓名与学号至少有一个必填（空行在导入时会被过滤）。</summary>
public sealed class Student
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double? Score { get; set; }
    public string? Note { get; set; }

    public bool HasId => !string.IsNullOrWhiteSpace(Id);
    public bool HasName => !string.IsNullOrWhiteSpace(Name);
    public bool IsValid => HasId || HasName;

    /// <summary>用于抽签历史去重的稳定键。</summary>
    public string Key => HasId ? Id.Trim() : Name.Trim();

    public string DisplayName => HasName ? Name.Trim() : Id.Trim();
    public string DisplayId => HasId ? Id.Trim() : "—";

    public Student Clone() => new() { Id = Id, Name = Name, Score = Score, Note = Note };

    public override string ToString() => HasId && HasName ? $"{Id} {Name}" : DisplayName;
}

public enum PickDisplayMode
{
    StudentId = 0,
    Name = 1,
    Both = 2
}

public enum RosterSortMode
{
    /// <summary>原始导入顺序。</summary>
    Original = 0,
    /// <summary>按成绩从高到低（无成绩者排最后）。</summary>
    Score = 1,
    /// <summary>按姓名（zh-CN 拼音序）。</summary>
    Name = 2,
    /// <summary>按学号（自然序）。</summary>
    StudentId = 3
}

public sealed class Roster
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Subject { get; set; }
    public string? SourceFile { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public RosterSortMode SortMode { get; set; } = RosterSortMode.Original;
    public PickDisplayMode? DisplayModeOverride { get; set; }
    public List<Student> Students { get; set; } = new();

    public Roster Clone()
    {
        var copy = (Roster)MemberwiseClone();
        copy.Students = Students.Select(s => s.Clone()).ToList();
        return copy;
    }

    /// <summary>排序后的学生列表（不改动原列表）。</summary>
    public IReadOnlyList<Student> SortedStudents()
    {
        var list = Students.Where(s => s.IsValid).ToList();
        var zh = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);
        switch (SortMode)
        {
            case RosterSortMode.Score:
                return list.OrderByDescending(s => s.Score ?? double.MinValue).ThenBy(s => s.Name, zh).ToList();
            case RosterSortMode.Name:
                return list.OrderBy(s => s.HasName ? s.Name.Trim() : s.Id.Trim(), zh).ToList();
            case RosterSortMode.StudentId:
                return list.OrderBy(s => NaturalKey(s.HasId ? s.Id : s.Name), StringComparer.OrdinalIgnoreCase).ToList();
            default:
                return list;
        }
    }

    private static string NaturalKey(string s)
    {
        // 让 "2" 排在 "10" 前面：把每一段连续数字整体补零到 16 位
        var sb = new System.Text.StringBuilder(s.Length + 16);
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsDigit(s[i]))
            {
                var j = i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                var digits = s.Substring(i, j - i).TrimStart('0');
                if (digits.Length == 0) digits = "0";
                sb.Append(digits.PadLeft(16, '0'));
                i = j;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(s[i]));
                i++;
            }
        }
        return sb.ToString();
    }
}

public sealed class PickRecord
{
    public string RosterId { get; set; } = "";
    public string StudentKey { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string StudentName { get; set; } = "";
    public DateTime PickedAt { get; set; }
}

public sealed class PickResult
{
    public Roster Roster { get; set; } = new();
    public Student Student { get; set; } = new();
    public PickDisplayMode DisplayMode { get; set; } = PickDisplayMode.Both;
    public DateTime PickedAt { get; set; }
    public int SessionPickIndex { get; set; }

    public string DisplayText => DisplayMode switch
    {
        PickDisplayMode.StudentId => Student.DisplayId,
        PickDisplayMode.Name => Student.DisplayName,
        _ => Student.HasId && Student.HasName ? $"{Student.DisplayId} {Student.DisplayName}" : Student.DisplayName
    };
}
