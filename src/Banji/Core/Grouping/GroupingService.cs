using System.Security.Cryptography;
using Banji.Core.Abstractions;
using Banji.Core.Models;

namespace Banji.Core.Grouping;

/// <summary>
/// 随机分组服务（第二轮新增）。
///
/// 规则：
/// • 组数收敛到 [2, min(12, 人数)]，每组人数差 ≤1，全员覆盖且不重复；
/// • 普通模式：CSPRNG（<see cref="RandomNumberGenerator"/>）Fisher–Yates 洗牌后依次分给各组；
/// • 均衡模式（<see cref="GroupingOptions.BalanceByScore"/>）：按成绩降序（同分用 CSPRNG 随机键）蛇形（S 形）分配，
///   使各组平均分尽量接近；
/// • 组名固定为“第N组”，结果含人数与平均分（<see cref="StudentGroup.AverageScore"/>）。
/// 时间通过 <c>Func&lt;DateTime&gt;</c> 注入，便于测试。
/// </summary>
public sealed class GroupingService : IGroupingService
{
    public const int MinGroupCount = 2;
    public const int MaxGroupCount = 12;

    private readonly IRosterService _rosters;
    private readonly Func<DateTime> _now;

    /// <summary>宿主（AppServices）使用的构造函数。</summary>
    public GroupingService(IRosterService rosters)
        : this(rosters, null)
    {
    }

    /// <summary>可测试构造函数：注入时间。</summary>
    public GroupingService(IRosterService rosters, Func<DateTime>? now = null)
    {
        _rosters = rosters ?? throw new ArgumentNullException(nameof(rosters));
        _now = now ?? (() => DateTime.Now);
    }

    public GroupingResult Group(GroupingOptions options, string? rosterId = null)
    {
        options ??= new GroupingOptions();

        var roster = string.IsNullOrWhiteSpace(rosterId) ? _rosters.Active : _rosters.Get(rosterId);
        if (roster is null)
            throw new InvalidOperationException("还没有可分组的花名册，请先在“花名册管理”中导入或新建一份。");

        var students = roster.Students.Where(s => s.IsValid).ToList();
        if (students.Count < MinGroupCount)
            throw new InvalidOperationException($"至少需要 {MinGroupCount} 名学生才能分组（当前只有 {students.Count} 人）。");

        // 组数收敛：2 ~ min(12, 人数)；请求值超范围时静默取边界（UI 会显示实际组数）
        var maxGroups = Math.Min(MaxGroupCount, students.Count);
        var groupCount = Math.Clamp(options.GroupCount, MinGroupCount, maxGroups);

        var groups = Enumerable.Range(0, groupCount).Select(_ => new List<Student>()).ToList();

        if (options.BalanceByScore)
        {
            AssignBalanced(students, groups);
        }
        else
        {
            AssignRandom(students, groups);
        }

        var resultGroups = groups
            .Select((list, index) => new StudentGroup
            {
                Index = index + 1,
                Name = $"第{index + 1}组",
                Students = list
            })
            .ToList();

        return new GroupingResult
        {
            Title = string.IsNullOrWhiteSpace(options.Title) ? "随机分组结果" : options.Title.Trim(),
            RosterName = roster.Name,
            TotalStudents = students.Count,
            CreatedAt = _now(),
            Groups = resultGroups
        };
    }

    /// <summary>普通模式：CSPRNG 洗牌后按轮次分给各组（人数差 ≤1）。</summary>
    private static void AssignRandom(IReadOnlyList<Student> students, IReadOnlyList<List<Student>> groups)
    {
        var shuffled = Shuffle(students);
        for (var i = 0; i < shuffled.Count; i++)
            groups[i % groups.Count].Add(shuffled[i]);
    }

    /// <summary>
    /// 均衡模式：按成绩降序（同分随机）后蛇形分配（人数差 ≤1），
    /// 再做两两交换的局部优化，让各组平均分尽量接近。
    /// </summary>
    private static void AssignBalanced(IReadOnlyList<Student> students, IReadOnlyList<List<Student>> groups)
    {
        var count = groups.Count;
        var ordered = students
            .OrderByDescending(s => s.Score ?? double.MinValue)
            .ThenBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .ToList();

        var index = 0;
        var direction = 1;
        foreach (var student in ordered)
        {
            groups[index].Add(student);
            if (direction > 0)
            {
                if (index >= count - 1) direction = -1;
                else index++;
            }
            else
            {
                if (index <= 0) direction = 1;
                else index--;
            }
        }

        OptimizeAverage(groups);
    }

    /// <summary>
    /// 局部交换优化：反复交换两组各一名学生，使两组平均分之差变小，直到无法再改进。
    /// 只在有成绩的学生之间交换，不改变各组人数（人数均衡由蛇形分配保证）。
    /// </summary>
    private static void OptimizeAverage(IReadOnlyList<List<Student>> groups)
    {
        const int maxIterations = 200;
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var sums = new double[groups.Count];
            var scoredCounts = new int[groups.Count];
            for (var g = 0; g < groups.Count; g++)
            {
                foreach (var student in groups[g])
                {
                    if (student.Score is not { } score) continue;
                    sums[g] += score;
                    scoredCounts[g]++;
                }
            }

            var bestGain = 1e-9;
            var best = (GroupA: -1, GroupB: -1, StudentA: -1, StudentB: -1);

            for (var i = 0; i < groups.Count; i++)
            {
                if (scoredCounts[i] == 0) continue;
                for (var j = i + 1; j < groups.Count; j++)
                {
                    if (scoredCounts[j] == 0) continue;
                    var before = Math.Abs(sums[i] / scoredCounts[i] - sums[j] / scoredCounts[j]);
                    for (var a = 0; a < groups[i].Count; a++)
                    {
                        if (groups[i][a].Score is not { } scoreA) continue;
                        for (var b = 0; b < groups[j].Count; b++)
                        {
                            if (groups[j][b].Score is not { } scoreB) continue;
                            var after = Math.Abs(
                                (sums[i] - scoreA + scoreB) / scoredCounts[i]
                                - (sums[j] - scoreB + scoreA) / scoredCounts[j]);
                            var gain = before - after;
                            if (gain > bestGain)
                            {
                                bestGain = gain;
                                best = (i, j, a, b);
                            }
                        }
                    }
                }
            }

            if (best.GroupA < 0) break;
            (groups[best.GroupA][best.StudentA], groups[best.GroupB][best.StudentB]) =
                (groups[best.GroupB][best.StudentB], groups[best.GroupA][best.StudentA]);
        }
    }

    /// <summary>CSPRNG Fisher–Yates 洗牌（不修改入参）。</summary>
    private static List<Student> Shuffle(IReadOnlyList<Student> students)
    {
        var list = students.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
