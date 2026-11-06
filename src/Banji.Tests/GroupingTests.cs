using Banji.Core.Grouping;
using Banji.Core.Models;
using Banji.Core.Rosters;

namespace Banji.Tests;

/// <summary>随机分组服务（第二轮）的算法测试。</summary>
public static class GroupingTests
{
    private static readonly DateTime FixedNow = new(2026, 11, 6, 9, 0, 0);

    [BanjiTest]
    public static void 分组_普通模式1000次_全员覆盖无重复且人数均衡()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var students = CreateStudents(30);
        var roster = rosters.CreateManual("高一(3)班", students, "数学");
        rosters.SetActive(roster.Id);
        var grouping = new GroupingService(rosters, () => FixedNow);
        var options = new GroupingOptions { GroupCount = 4, BalanceByScore = false };

        var originalKeys = students.Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        for (var round = 0; round < 1000; round++)
        {
            var result = grouping.Group(options, roster.Id);
            Assert.Equal(30, result.TotalStudents);
            Assert.Equal(4, result.Groups.Count);
            Assert.Equal("第1组", result.Groups[0].Name);
            Assert.Equal("第4组", result.Groups[3].Name);

            var all = result.Groups.SelectMany(g => g.Students).ToList();
            Assert.Equal(30, all.Count, "全员应被分一次，不能漏也不能重复");
            Assert.Equal(30, all.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count(), "分组结果不能有重复学生");

            var keys = all.Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(keys.SequenceEqual(originalKeys), "分组后学号集合必须与花名册一致");

            var counts = result.Groups.Select(g => g.Students.Count).ToList();
            Assert.True(counts.Max() - counts.Min() <= 1,
                $"每组人数差不得超过 1（本轮：{string.Join("/", counts)}）");
        }
    }

    [BanjiTest]
    public static void 分组_按成绩均衡时组均分极差明显小于随机()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        // 成绩 40、42、…、98（无同分，均衡模式结果稳定）
        var students = Enumerable.Range(0, 30)
            .Select(i => new Student { Id = (20260101 + i).ToString(), Name = $"学生{i + 1:00}", Score = 40 + i * 2 })
            .ToList();
        var roster = rosters.CreateManual("成绩分布班", students);
        rosters.SetActive(roster.Id);
        var grouping = new GroupingService(rosters, () => FixedNow);

        const int rounds = 200;
        var randomSpreadTotal = 0.0;
        for (var i = 0; i < rounds; i++)
        {
            var result = grouping.Group(new GroupingOptions { GroupCount = 4, BalanceByScore = false }, roster.Id);
            randomSpreadTotal += Spread(result);
        }
        var randomSpread = randomSpreadTotal / rounds;

        var balancedSpreadTotal = 0.0;
        for (var i = 0; i < 50; i++)
        {
            var result = grouping.Group(new GroupingOptions { GroupCount = 4, BalanceByScore = true }, roster.Id);
            balancedSpreadTotal += Spread(result);
        }
        var balancedSpread = balancedSpreadTotal / 50;

        Assert.True(balancedSpread < randomSpread * 0.5,
            $"按成绩均衡后组均分极差应明显更小（随机 {randomSpread:F2} 分 vs 均衡 {balancedSpread:F2} 分）");
        Assert.True(balancedSpread < 4.0, $"均衡模式组均分极差应很小（实际 {balancedSpread:F2} 分）");
    }

    [BanjiTest]
    public static void 分组_组数收敛到允许范围()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var roster = rosters.CreateManual("30人班", CreateStudents(30));
        rosters.SetActive(roster.Id);
        var grouping = new GroupingService(rosters, () => FixedNow);

        // 请求超过上限 12 -> 12 组
        Assert.Equal(12, grouping.Group(new GroupingOptions { GroupCount = 20 }, roster.Id).Groups.Count);
        // 请求 0/1 -> 最少 2 组
        Assert.Equal(2, grouping.Group(new GroupingOptions { GroupCount = 1 }, roster.Id).Groups.Count);
        Assert.Equal(2, grouping.Group(new GroupingOptions { GroupCount = 0 }, roster.Id).Groups.Count);
        // 正常范围原样保留
        Assert.Equal(4, grouping.Group(new GroupingOptions { GroupCount = 4 }, roster.Id).Groups.Count);
        Assert.Equal(7, grouping.Group(new GroupingOptions { GroupCount = 7 }, roster.Id).Groups.Count);

        // 5 人班：上限 5 组
        var small = rosters.CreateManual("5人班", CreateStudents(5));
        Assert.Equal(5, grouping.Group(new GroupingOptions { GroupCount = 8 }, small.Id).Groups.Count);
        var fiveGroupCounts = grouping.Group(new GroupingOptions { GroupCount = 8 }, small.Id)
            .Groups.Select(g => g.Students.Count).ToList();
        Assert.True(fiveGroupCounts.Max() - fiveGroupCounts.Min() <= 1, "5 人分 5 组应每组 1 人");
    }

    [BanjiTest]
    public static void 分组_空名单与人数不足给出友好中文异常()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var grouping = new GroupingService(rosters, () => FixedNow);

        // 没有任何花名册
        var noRoster = Assert.Throws<InvalidOperationException>(() => grouping.Group(new GroupingOptions()));
        Assert.True(noRoster.Message.Contains("花名册"), "应提示先创建花名册");

        // 空名单
        var empty = rosters.CreateManual("空班", Array.Empty<Student>());
        rosters.SetActive(empty.Id);
        var emptyError = Assert.Throws<InvalidOperationException>(() => grouping.Group(new GroupingOptions(), empty.Id));
        Assert.True(emptyError.Message.Contains("2") && emptyError.Message.Contains("学生"),
            $"应提示至少 2 名学生（实际：{emptyError.Message}）");

        // 只有 1 人
        var one = rosters.CreateManual("一人班", new[] { new Student { Id = "1", Name = "独苗" } });
        var oneError = Assert.Throws<InvalidOperationException>(() => grouping.Group(new GroupingOptions(), one.Id));
        Assert.True(oneError.Message.Contains("至少"), "应提示人数不足");
    }

    [BanjiTest]
    public static void 分组_结果含组名人数平均分与纯文本导出()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => FixedNow);
        var roster = rosters.CreateManual("导出测试班", CreateStudents(10), "物理");
        rosters.SetActive(roster.Id);
        var grouping = new GroupingService(rosters, () => FixedNow);

        var result = grouping.Group(new GroupingOptions { GroupCount = 3, BalanceByScore = true, Title = "物理分组" }, roster.Id);
        Assert.Equal("物理分组", result.Title);
        Assert.Equal("导出测试班", result.RosterName);
        Assert.Equal(10, result.TotalStudents);
        Assert.Equal(3, result.Groups.Count);
        Assert.Equal(FixedNow, result.CreatedAt);

        foreach (var group in result.Groups)
        {
            Assert.True(group.Name.StartsWith("第") && group.Name.EndsWith("组"), "组名应为“第N组”");
            Assert.True(group.Students.Count >= 1);
            Assert.True(group.AverageScore > 0, "有成绩的学生组平均分应大于 0");
        }

        var text = result.ToPlainText();
        Assert.True(text.Contains("物理分组") && text.Contains("第1组") && text.Contains("导出测试班"));
    }

    // =========================================================================

    private static double Spread(GroupingResult result)
        => result.Groups.Max(g => g.AverageScore) - result.Groups.Min(g => g.AverageScore);

    private static List<Student> CreateStudents(int count)
    {
        var names = new[]
        {
            "王浩然", "李思远", "张雨桐", "刘一诺", "陈子轩", "杨若曦", "赵嘉怡", "周明远",
            "吴亦菲", "徐子墨", "孙悦然", "马晨曦", "朱婉清", "胡俊杰", "郭静怡", "何思源",
            "高子涵", "林雨欣", "罗天宇", "梁诗琪", "宋佳明", "谢雨萱", "唐子豪", "韩雪晴",
            "冯逸辰", "曹语彤", "彭若尘", "曾可欣", "萧亦航", "田若溪", "沈知远", "孟书瑶"
        };
        return Enumerable.Range(0, count)
            .Select(i => new Student
            {
                Id = (20260101 + i).ToString(),
                Name = i < names.Length ? names[i] : $"学生{i + 1:00}",
                Score = 60 + i
            })
            .ToList();
    }
}
