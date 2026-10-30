using Banji.Core.Models;
using Banji.Core.Picker;
using Banji.Core.Rosters;

namespace Banji.Tests;

/// <summary>抽签引擎的算法约束测试（公平性 / 窗口 / 间隔 / 兜底 / 历史）。</summary>
public static class PickerTests
{
    private const int StudentCount = 30;
    private const int PickCount = 5000;
    private static readonly DateTime BaseTime = new(2026, 10, 30, 8, 0, 0);

    [BanjiTest]
    public static void 抽签_5000次间隔4人约束无违例()
    {
        using var ws = new TempWorkspace();
        var students = CreateStudents();
        var settings = new FakeSettingsService();
        var now = BaseTime;
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => now);
        var roster = rosters.CreateManual("高一(3)班", students);
        rosters.SetActive(roster.Id);
        var picker = new PickService(rosters, settings, () => now, () => null, ws.File("pick-history.json"), persistHistory: false);

        var lastPickIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < PickCount; i++)
        {
            now = now.AddSeconds(2); // 模拟老师每 2 秒抽一次，约 2.8 小时
            var result = picker.Pick();
            Assert.NotNull(result.Student, $"第 {i} 次抽签结果为空");
            Assert.True(result.Roster.Students.Any(s => s.Key == result.Student.Key), "抽中的学生应来自当前花名册");

            var key = result.Student.Key;
            if (lastPickIndex.TryGetValue(key, out var lastIndex))
            {
                var othersBetween = i - lastIndex - 1;
                Assert.True(othersBetween >= 4,
                    $"间隔违例：{key} 在第 {lastIndex} 次和第 {i} 次抽签之间只隔了 {othersBetween} 次其他人的抽取");
            }
            lastPickIndex[key] = i;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        Assert.Equal(StudentCount, counts.Count, "5000 次抽签应覆盖全部学生");
        var average = (double)PickCount / StudentCount;
        Assert.True(counts.Values.Max() < average * 2.0,
            $"抽签过于集中：最多 {counts.Values.Max()} 次，均值 {average:F1}");
        Assert.True(counts.Values.Min() > average * 0.4,
            $"有学生几乎抽不到：最少 {counts.Values.Min()} 次，均值 {average:F1}");
    }

    [BanjiTest]
    public static void 抽签_窗口内每人最多2次_全员抽完后放开()
    {
        using var ws = new TempWorkspace();
        var students = CreateStudents();
        var settings = new FakeSettingsService();
        var now = BaseTime; // 时间固定 = 同一节课内（滚动窗口覆盖全部抽签）
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => now);
        var roster = rosters.CreateManual("高一(3)班", students);
        rosters.SetActive(roster.Id);
        var picker = new PickService(rosters, settings, () => now, () => null, ws.File("pick-history.json"), persistHistory: false);

        var windowCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var coveredAll = false;
        var coveredAt = -1;

        for (var i = 0; i < 300; i++)
        {
            var result = picker.Pick();
            var key = result.Student.Key;
            windowCounts[key] = windowCounts.GetValueOrDefault(key) + 1;

            var allCovered = students.All(s => windowCounts.GetValueOrDefault(s.Key) > 0);
            if (allCovered && !coveredAll)
            {
                coveredAll = true;
                coveredAt = i;
            }

            if (!allCovered)
            {
                foreach (var pair in windowCounts)
                {
                    Assert.True(pair.Value <= 2,
                        $"窗口上限违例：全员尚未抽完（第 {i} 次），但 {pair.Key} 已被抽 {pair.Value} 次");
                }
            }
        }

        Assert.True(coveredAll, "300 次抽签内应该已经把全员至少抽过一遍");
        Assert.True(coveredAt <= 61, $"全员抽完不应超过 60 次左右（实际第 {coveredAt + 1} 次）");
        Assert.True(windowCounts.Values.Max() > 2,
            "全员都抽过之后应放开“最多 2 次”的限制（继续抽签会有人超过 2 次）");
    }

    [BanjiTest]
    public static void 抽签_5000次跨课时_滚动窗口约束无违例()
    {
        using var ws = new TempWorkspace();
        var students = CreateStudents();
        var settings = new FakeSettingsService();
        settings.Current.ClassDurationMinutes = 45;
        var now = BaseTime;
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => now);
        var roster = rosters.CreateManual("高一(3)班", students);
        rosters.SetActive(roster.Id);
        var picker = new PickService(rosters, settings, () => now, () => null, ws.File("pick-history.json"), persistHistory: false);

        var sessionStart = BaseTime;
        var sessionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var coveredInSession = false;
        var lastPickIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < PickCount; i++)
        {
            if (now >= sessionStart.AddMinutes(45))
            {
                // 每节课开始，AppServices 会调用 ResetSession
                picker.ResetSession();
                sessionStart = now;
                sessionCounts.Clear();
                coveredInSession = false;
            }

            now = now.AddSeconds(3);
            var result = picker.Pick();
            var key = result.Student.Key;

            if (lastPickIndex.TryGetValue(key, out var last))
            {
                var othersBetween = i - last - 1;
                Assert.True(othersBetween >= 4,
                    $"间隔违例（跨课时）：{key} 在第 {last} 次和第 {i} 次之间只隔 {othersBetween} 次");
            }
            lastPickIndex[key] = i;

            sessionCounts[key] = sessionCounts.GetValueOrDefault(key) + 1;
            var allCovered = students.All(s => sessionCounts.GetValueOrDefault(s.Key) > 0);
            if (allCovered) coveredInSession = true;

            if (!coveredInSession)
            {
                foreach (var pair in sessionCounts)
                {
                    Assert.True(pair.Value <= 2,
                        $"本节课窗口上限违例：第 {i} 次（会话内第 {sessionCounts.Values.Sum()} 抽），{pair.Key} 已 {pair.Value} 次");
                }
            }
        }

        Assert.True(lastPickIndex.Count == StudentCount, "跨课时 5000 次抽签后应覆盖全部学生");
    }

    [BanjiTest]
    public static void 抽签_ResetSession重置会话序号()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var now = BaseTime;
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => now);
        var roster = rosters.CreateManual("测试班", CreateStudents());
        rosters.SetActive(roster.Id);
        var picker = new PickService(rosters, settings, () => now, () => null, ws.File("pick-history.json"), persistHistory: false);

        Assert.Equal(1, picker.Pick().SessionPickIndex);
        Assert.Equal(2, picker.Pick().SessionPickIndex);
        Assert.Equal(3, picker.Pick().SessionPickIndex);

        picker.ResetSession();

        var afterReset = picker.Pick();
        Assert.Equal(1, afterReset.SessionPickIndex, "每节课开始后本节课序号应从 1 重新开始");
        Assert.NotNull(picker.RecentPicks);
    }

    [BanjiTest]
    public static void 抽签_窗口时长跟随当前课时长()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        settings.Current.ClassDurationMinutes = 42;
        settings.Current.PickWindowFollowsCurrentPeriod = true;
        var now = BaseTime;
        ScheduleSnapshot? snapshot = null;
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => now);
        var roster = rosters.CreateManual("测试班", CreateStudents());
        rosters.SetActive(roster.Id);
        var picker = new PickService(rosters, settings, () => now, () => snapshot, ws.File("pick-history.json"), persistHistory: false);

        // 课表不可用 -> 用设置的 42 分钟
        Assert.Equal(TimeSpan.FromMinutes(42), picker.CurrentWindow);

        // 课表可用：当前节课 38 分 23 秒（精确到秒）
        var periodLength = TimeSpan.FromSeconds(38 * 60 + 23);
        snapshot = new ScheduleSnapshot
        {
            Now = BaseTime,
            Phase = SchedulePhase.InClass,
            HasTimetable = true,
            Current = new CourseOccurrence
            {
                PeriodIndex = 3,
                PeriodName = "第3节",
                Course = "数学",
                Start = BaseTime,
                End = BaseTime + periodLength
            }
        };
        Assert.Equal(periodLength, picker.CurrentWindow, "有课表时应使用当前这一节的真实时长");

        // 关闭“跟随当前课时” -> 回到设置值
        settings.Current.PickWindowFollowsCurrentPeriod = false;
        Assert.Equal(TimeSpan.FromMinutes(42), picker.CurrentWindow);
    }

    [BanjiTest]
    public static void 抽签_显示模式按花名册覆盖或全局()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        settings.Current.PickDisplay = PickDisplayMode.Both;
        var now = BaseTime;
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => now);
        var roster = rosters.CreateManual("测试班", new[]
        {
            new Student { Id = "20260101", Name = "王浩然" },
            new Student { Id = "20260102", Name = "李思远" },
            new Student { Id = "20260103", Name = "张雨桐" }
        });
        rosters.SetActive(roster.Id);
        var picker = new PickService(rosters, settings, () => now, () => null, ws.File("pick-history.json"), persistHistory: false);

        // 全局 Both
        var bothResult = picker.Pick();
        Assert.Equal(PickDisplayMode.Both, bothResult.DisplayMode);
        var target = bothResult.Student;
        Assert.Equal($"{target.DisplayId} {target.DisplayName}", bothResult.DisplayText);

        // 花名册覆盖为“只有姓名”
        var current = rosters.Get(roster.Id)!;
        current.DisplayModeOverride = PickDisplayMode.Name;
        rosters.Update(current);
        var nameResult = picker.Pick();
        Assert.Equal(PickDisplayMode.Name, nameResult.DisplayMode);
        Assert.Equal(nameResult.Student.DisplayName, nameResult.DisplayText);
        Assert.False(nameResult.DisplayText.Contains(nameResult.Student.DisplayId), "只要姓名时不应显示学号");

        // 花名册覆盖为“只有学号”
        var current2 = rosters.Get(roster.Id)!;
        current2.DisplayModeOverride = PickDisplayMode.StudentId;
        rosters.Update(current2);
        var idResult = picker.Pick();
        Assert.Equal(PickDisplayMode.StudentId, idResult.DisplayMode);
        Assert.Equal(idResult.Student.DisplayId, idResult.DisplayText);
        Assert.Equal(3, idResult.Roster.Students.Count);
    }

    [BanjiTest]
    public static void 抽签_空花名册给出友好中文异常()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var now = BaseTime;
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => now);
        var empty = rosters.CreateManual("空班", Array.Empty<Student>());
        rosters.SetActive(empty.Id);
        var picker = new PickService(rosters, settings, () => now, () => null, ws.File("pick-history.json"), persistHistory: false);

        var ex = Assert.Throws<InvalidOperationException>(() => picker.Pick());
        Assert.True(ex.Message.Contains("花名册"), "异常应为友好中文提示");
        Assert.True(ex.Message.Contains("学生"), "异常应提示没有学生");

        var missing = Assert.Throws<InvalidOperationException>(() => picker.Pick("不存在的花名册"));
        Assert.True(missing.Message.Contains("找不到"), "找不到花名册时应给出中文提示");
    }

    [BanjiTest]
    public static void 抽签_历史持久化与30天清理()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var now = BaseTime;
        var historyFile = ws.File("pick-history.json");
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => now);
        var roster = rosters.CreateManual("测试班", CreateStudents(10));
        rosters.SetActive(roster.Id);

        var picker = new PickService(rosters, settings, () => now, () => null, historyFile);
        for (var i = 0; i < 10; i++)
        {
            now = now.AddMinutes(1);
            picker.Pick();
        }
        Assert.Equal(10, picker.RecentPicks.Count);
        Assert.True(File.Exists(historyFile), "抽签历史应持久化到 pick-history.json");

        // 重新创建服务应能恢复历史
        var reloaded = new PickService(rosters, settings, () => now, () => null, historyFile);
        Assert.Equal(10, reloaded.RecentPicks.Count, "重新加载后应恢复抽签历史");
        Assert.Equal(rosters.ActiveId(), reloaded.RecentPicks[0].RosterId);

        // 31 天后再加载：超过 30 天的历史应被清理
        var later = now.AddDays(31);
        var late = new PickService(rosters, settings, () => later, () => null, historyFile);
        Assert.Equal(0, late.RecentPicks.Count, "超过 30 天的抽签历史应被清理");
    }

    [BanjiTest]
    public static void 抽签_Picked事件广播结果()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var now = BaseTime;
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => now);
        var roster = rosters.CreateManual("测试班", CreateStudents(8));
        rosters.SetActive(roster.Id);
        var picker = new PickService(rosters, settings, () => now, () => null, ws.File("pick-history.json"), persistHistory: false);

        PickResult? received = null;
        picker.Picked += (_, result) => received = result;

        var returned = picker.Pick();
        Assert.NotNull(received, "Picked 事件应广播抽签结果");
        Assert.Equal(returned.Student.Key, received!.Student.Key);
        Assert.Equal(1, received.SessionPickIndex);
    }

    [BanjiTest]
    public static void 抽签_花名册人数过少时兜底不死锁()
    {
        using var ws = new TempWorkspace();
        var settings = new FakeSettingsService();
        var now = BaseTime;
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => now);
        var roster = rosters.CreateManual("只有两个人", new[]
        {
            new Student { Id = "20260101", Name = "甲同学" },
            new Student { Id = "20260102", Name = "乙同学" }
        });
        rosters.SetActive(roster.Id);
        var picker = new PickService(rosters, settings, () => now, () => null, ws.File("pick-history.json"), persistHistory: false);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 50; i++)
        {
            now = now.AddSeconds(1);
            var result = picker.Pick();
            Assert.NotNull(result.Student, "人数的极端情况下也必须给出结果，绝不死锁");
            counts[result.Student.Key] = counts.GetValueOrDefault(result.Student.Key) + 1;
        }
        Assert.Equal(2, counts.Count);
        Assert.True(counts.Values.All(c => c > 0), "两个人应该都能被抽到");
    }

    // =========================================================================

    private static List<Student> CreateStudents(int count = StudentCount)
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
                Score = 100 - i
            })
            .ToList();
    }
}

internal static class RosterServiceTestExtensions
{
    /// <summary>测试便捷：当前激活花名册 Id。</summary>
    public static string ActiveId(this RosterService service)
        => service.Active?.Id ?? "";
}
