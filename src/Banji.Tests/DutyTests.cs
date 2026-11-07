using Banji.Core.Duty;
using Banji.Core.Models;
using Banji.Core.Rosters;

namespace Banji.Tests;

/// <summary>v1.4.0 值周/值日内核（DutyService + DutyPresets）测试。</summary>
public static class DutyTests
{
    private static readonly DateOnly Anchor = new(2026, 9, 7); // 周一（设计稿示例：开学第一周）
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0);

    // =========================================================================
    // 轮换算法
    // =========================================================================

    [BanjiTest]
    public static void 值周_四组按周轮换_跨年与负数周正确()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 40);
        var duty = CreateDuty(ws, settings, rosters, out _);

        var config = CreateWeeklyConfig(Anchor, 4, 3);
        config.AnchorMonday = Anchor;
        duty.Save(config);

        // 第 1~5 周
        Assert.Equal("第1组", duty.GetSchedule(Anchor).GroupName);
        Assert.Equal("第2组", duty.GetSchedule(Anchor.AddDays(7)).GroupName);
        Assert.Equal("第4组", duty.GetSchedule(Anchor.AddDays(21)).GroupName);
        Assert.Equal("第1组", duty.GetSchedule(Anchor.AddDays(28)).GroupName, "第 5 周应回到第 1 组");

        // 周内任意一天都算同一组（周三）
        Assert.Equal("第1组", duty.GetSchedule(Anchor.AddDays(2)).GroupName);

        // 跨年：2026-12-28 距锚点 112 天 = 16 周 → index 16 → 第 1 组
        var yearEnd = new DateOnly(2026, 12, 28);
        Assert.Equal(DayOfWeek.Monday, yearEnd.DayOfWeek);
        Assert.Equal("第1组", duty.GetSchedule(yearEnd).GroupName);

        // 负数周（锚点之前 3 周）→ -3 mod 4 = 1 → 第 2 组，不能崩溃
        var before = Anchor.AddDays(-21);
        Assert.Equal(DayOfWeek.Monday, before.DayOfWeek);
        Assert.Equal("第2组", duty.GetSchedule(before).GroupName);
    }

    [BanjiTest]
    public static void 值日_每天轮换一组_跨周连续()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 40);
        var duty = CreateDuty(ws, settings, rosters, out _);

        var config = CreateWeeklyConfig(Anchor, 4, 3);
        config.Mode = DutyMode.Daily;
        config.AnchorMonday = Anchor;
        duty.Save(config);

        Assert.Equal("第1组", duty.GetSchedule(Anchor).GroupName);
        Assert.Equal("第2组", duty.GetSchedule(Anchor.AddDays(1)).GroupName);
        Assert.Equal("第3组", duty.GetSchedule(Anchor.AddDays(2)).GroupName);
        Assert.Equal("第4组", duty.GetSchedule(Anchor.AddDays(3)).GroupName);
        Assert.Equal("第1组", duty.GetSchedule(Anchor.AddDays(4)).GroupName, "第 5 天回到第 1 组");
        // 跨周连续（第 8 天）
        Assert.Equal("第4组", duty.GetSchedule(Anchor.AddDays(7)).GroupName);
        // 负数天
        Assert.Equal("第4组", duty.GetSchedule(Anchor.AddDays(-1)).GroupName, "-1 mod 4 = 3 → 第 4 组");
    }

    [BanjiTest]
    public static void 值周_暂停周计入周期_覆盖周可换组与恢复()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 40);
        var duty = CreateDuty(ws, settings, rosters, out _);

        var config = CreateWeeklyConfig(Anchor, 4, 3);
        config.AnchorMonday = Anchor;
        duty.Save(config);

        var week2 = Anchor.AddDays(7);
        duty.SetWeekOverride(week2, null, paused: true, "期中考试周");

        var paused = duty.GetSchedule(week2);
        Assert.True(paused.IsPaused, "暂停周应标记 IsPaused");
        Assert.Null(paused.GroupName);
        Assert.Equal(0, paused.Tasks.Count);
        Assert.True(paused.PanelLines.Count > 0 && paused.PanelLines[0].Contains("暂停"));

        // 暂停周仍计入周期：第 3 周仍应第 3 组
        Assert.Equal("第3组", duty.GetSchedule(Anchor.AddDays(14)).GroupName, "暂停周后应继续下一组（不错位）");

        // 覆盖第 4 周为第 1 组
        var week4 = Anchor.AddDays(21);
        duty.SetWeekOverride(week4, "g1", paused: false, "调换值周");
        Assert.Equal("第1组", duty.GetSchedule(week4).GroupName);

        // 恢复自动：第 4 周应回到第 4 组
        duty.SetWeekOverride(week4, null, paused: false, note: null);
        Assert.Equal("第4组", duty.GetSchedule(week4).GroupName);

        // 预览 8 周：第 2 周暂停，第 4 周恢复自动后为第 4 组
        var preview = duty.GetRotationPreview(Anchor, 8);
        Assert.Equal(8, preview.Count);
        Assert.True(preview[1].Paused);
        Assert.Equal("第3组", preview[2].GroupName);
        Assert.Equal("第4组", preview[3].GroupName);
    }

    // =========================================================================
    // 任务分配
    // =========================================================================

    [BanjiTest]
    public static void 任务分配_固定模式与自动模式_多人与一人多岗()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 40);
        var duty = CreateDuty(ws, settings, rosters, out _);

        // --- 固定模式：同一岗位 2 人 ---
        var fixedConfig = CreateWeeklyConfig(Anchor, 2, 3);
        fixedConfig.AnchorMonday = Anchor;
        fixedConfig.AssignmentMode = DutyAssignmentMode.FixedPerGroup;
        fixedConfig.Groups[0].Assignments.Clear();
        fixedConfig.Groups[0].Assignments.Add(new DutyAssignment { StudentId = "20260101", TaskId = "t-blackboard" });
        fixedConfig.Groups[0].Assignments.Add(new DutyAssignment { StudentId = "20260102", TaskId = "t-blackboard" });
        fixedConfig.Groups[0].Assignments.Add(new DutyAssignment { StudentId = "20260103", TaskId = "t-sweep" });
        fixedConfig.Groups[1].Assignments.Clear();
        fixedConfig.Groups[1].Assignments.Add(new DutyAssignment { StudentId = "20260111", TaskId = "t-trash" });
        duty.Save(fixedConfig);

        var schedule = duty.GetSchedule(Anchor);
        Assert.Equal("第1组", schedule.GroupName);
        var blackboard = schedule.Tasks.First(t => t.TaskId == "t-blackboard");
        Assert.Equal(2, blackboard.StudentIds.Count, "一个岗位可以安排 2 人");
        Assert.Equal(2, blackboard.StudentNames.Count);
        Assert.True(blackboard.IsTopTask, "擦黑板应被识别为置顶岗位");
        Assert.Equal("t-blackboard", schedule.Tasks[0].TaskId, "黑板类岗位应排在任务列表最前面");

        // --- 自动模式：任务顺序 → 组员顺序（2 人干 3 个坑 → 一人多岗）---
        var autoConfig = CreateWeeklyConfig(Anchor, 2, 2);
        autoConfig.AnchorMonday = Anchor;
        autoConfig.AssignmentMode = DutyAssignmentMode.AutoByOrder;
        autoConfig.Tasks = new List<DutyTask>
        {
            new() { Id = "a1", Name = "擦黑板", Category = "卫生", RequiredCount = 1, Sort = 0 },
            new() { Id = "a2", Name = "扫地", Category = "卫生", RequiredCount = 2, Sort = 1 }
        };
        autoConfig.Groups[0].MemberIds = new List<string> { "20260101", "20260102" };
        duty.Save(autoConfig);

        var autoSchedule = duty.GetSchedule(Anchor);
        var sweep = autoSchedule.Tasks.First(t => t.TaskId == "a2");
        Assert.Equal(2, sweep.StudentIds.Count, "扫地 2 人应分配满");
        Assert.True(sweep.StudentIds.Contains("20260101") && sweep.StudentIds.Contains("20260102"));
        var board = autoSchedule.Tasks.First(t => t.TaskId == "a1");
        Assert.Equal(1, board.StudentIds.Count);
        // 学生 1 同时有擦黑板与扫地 → 一人多岗
        Assert.True(sweep.StudentIds.Contains("20260101") && board.StudentIds.Contains("20260101"));
    }

    [BanjiTest]
    public static void 任务分配_校验未分配岗位与成员()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 40);
        var duty = CreateDuty(ws, settings, rosters, out _);

        var config = CreateWeeklyConfig(Anchor, 2, 3);
        config.AnchorMonday = Anchor;
        config.Groups[0].Assignments.Clear();
        config.Groups[0].Assignments.Add(new DutyAssignment { StudentId = "20260101", TaskId = "t-blackboard" });
        duty.Save(config);

        var check = duty.CheckAssignments("g1");
        Assert.Equal("第1组", check.GroupName);
        Assert.True(check.UnassignedTaskNames.Contains("扫地"));
        Assert.True(check.UnassignedTaskNames.Contains("倒垃圾"));
        Assert.True(check.UnassignedStudentNames.Count >= 2, "没分到任务的同学应提示");
        Assert.True(check.HasIssues);
        Assert.True(check.Summary.Contains("未分配岗位"));
    }

    // =========================================================================
    // 姓名解析 / 缺失成员
    // =========================================================================

    [BanjiTest]
    public static void 姓名解析_花名册匹配与缺失成员提示()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 20);
        var duty = CreateDuty(ws, settings, rosters, out _);

        var config = CreateWeeklyConfig(Anchor, 1, 2);
        config.AnchorMonday = Anchor;
        config.Groups[0].MemberIds = new List<string> { "20260101", "99999999" };
        config.Groups[0].Assignments = new List<DutyAssignment>
        {
            new() { StudentId = "20260101", TaskId = "t-blackboard" },
            new() { StudentId = "99999999", TaskId = "t-sweep" }
        };
        duty.Save(config);

        var schedule = duty.GetSchedule(Anchor);
        var board = schedule.Tasks.First(t => t.TaskId == "t-blackboard");
        Assert.Equal("王浩然", board.StudentNames[0], "应把学号解析成花名册姓名");

        var sweep = schedule.Tasks.First(t => t.TaskId == "t-sweep");
        Assert.Equal(1, sweep.StudentNames.Count);
        Assert.True(sweep.StudentNames[0].Contains("已不在花名册"), "花名册里找不到的成员应明确提示");

        var check = duty.CheckAssignments("g1");
        Assert.True(check.MissingStudentIds.Contains("99999999"));
    }

    // =========================================================================
    // 打卡
    // =========================================================================

    [BanjiTest]
    public static void 打卡_默认不加积分_开启后加分且同日去重()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 20);
        var duty = CreateDuty(ws, settings, rosters, out var roster);

        var config = CreateWeeklyConfig(Anchor, 2, 2);
        config.AnchorMonday = Anchor;
        config.AwardPointsOnCheckIn = false;
        duty.Save(config);

        // 默认关闭：只记录，不加分
        duty.CheckIn(Anchor);
        Assert.Equal(1, duty.Config.CheckIns.Count);
        Assert.Equal(0, duty.Config.CheckIns[0].Points);
        Assert.True(roster.Students.All(s => s.Points == 0), "关闭打卡积分时不应改积分");

        // 同日重复打卡不重复记录
        duty.CheckIn(Anchor);
        Assert.Equal(1, duty.Config.CheckIns.Count, "同一天重复打卡应去重");

        // 开启积分后：另一天打卡给参与学生 +1
        config.AwardPointsOnCheckIn = true;
        config.AnchorMonday = Anchor;
        duty.Save(config);
        duty.CheckIn(Anchor.AddDays(7)); // 第 2 周 → 第 2 组的学生

        var updatedRoster = rosters.Get(roster.Id)!;
        Assert.True(updatedRoster.Students.Any(s => s.Points == 1), "开启后打卡应给学生 +1 积分");
        Assert.Equal(2, duty.Config.CheckIns.Count);
    }

    // =========================================================================
    // 展示
    // =========================================================================

    [BanjiTest]
    public static void 展示_黑板类岗位置顶与面板行()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 20);
        var duty = CreateDuty(ws, settings, rosters, out _);

        var config = CreateWeeklyConfig(Anchor, 2, 3);
        config.AnchorMonday = Anchor;
        // 故意让“擦黑板”排最后，验证置顶排序依然生效
        config.Tasks = new List<DutyTask>
        {
            new() { Id = "t-sweep", Name = "扫地", Category = "卫生", RequiredCount = 1, Sort = 0 },
            new() { Id = "t-trash", Name = "倒垃圾", Category = "卫生", RequiredCount = 1, Sort = 1 },
            new() { Id = "t-blackboard", Name = "擦黑板", Category = "卫生", RequiredCount = 1, Sort = 2 }
        };
        config.Groups[0].Assignments = new List<DutyAssignment>
        {
            new() { StudentId = "20260101", TaskId = "t-sweep" },
            new() { StudentId = "20260102", TaskId = "t-trash" },
            new() { StudentId = "20260103", TaskId = "t-blackboard" }
        };
        duty.Save(config);

        var schedule = duty.GetSchedule(Anchor);
        Assert.Equal("擦黑板", schedule.Tasks[0].TaskName, "擦黑板类岗位必须置顶");
        var lines = duty.GetPetPanelLines(Anchor);
        Assert.Equal("本周值周：第1组", lines[0]);
        Assert.True(lines.Count >= 2 && lines[1].Contains("擦黑板"), "面板第二行应是置顶的黑板类任务");

        // 值日模式文案
        config.Mode = DutyMode.Daily;
        duty.Save(config);
        Assert.Equal("今日值日：第1组", duty.GetPetPanelLines(Anchor)[0]);

        // 关闭宠物展示 → 不返回面板行
        settings.Update(x => x.ShowDutyOnPet = false);
        Assert.Equal(0, duty.GetPetPanelLines(Anchor).Count);
        settings.Update(x => x.ShowDutyOnPet = true);
        Assert.True(duty.GetPetPanelLines(Anchor).Count > 0);
    }

    [BanjiTest]
    public static void 复制文本_包含组名任务与固定任职()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 20);
        var duty = CreateDuty(ws, settings, rosters, out _);

        var config = CreateWeeklyConfig(Anchor, 2, 2);
        config.AnchorMonday = Anchor;
        config.Committee = new List<CommitteeRole>
        {
            new() { Type = "班长", Count = 1, MemberIds = new List<string> { "20260101" } }
        };
        config.SubjectReps = new List<SubjectRepRole>
        {
            new() { Subject = "语文", Count = 1, MemberIds = new List<string> { "20260102" } }
        };
        duty.Save(config);

        var text = duty.BuildCopyText(Anchor);
        Assert.True(text.Contains("值周安排"));
        Assert.True(text.Contains("第1组"));
        Assert.True(text.Contains("擦黑板：王浩然"), "任务成员应按花名册显示姓名");
        Assert.True(text.Contains("【班委】") && text.Contains("班长：王浩然"));
        Assert.True(text.Contains("【课代表】") && text.Contains("语文：李思远"));
    }

    // =========================================================================
    // 持久化 / 首配状态
    // =========================================================================

    [BanjiTest]
    public static void 持久化_round_trip与设置同步()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 20);
        var duty = CreateDuty(ws, settings, rosters, out _);

        var config = CreateWeeklyConfig(Anchor, 3, 2);
        config.AnchorMonday = Anchor;
        config.Display.TopTaskKeywords = new List<string> { "擦黑板", "讲台" };
        config.Reminders.DailyMinutesBefore = 8;
        config.Committee = DutyPresets.CreateCommitteeRoles();
        config.SubjectReps = DutyPresets.CreateSubjectRepRoles();
        duty.Save(config);

        Assert.Equal(Anchor, settings.Current.SemesterFirstWeekMonday, "Save 应把轮换起点同步到“开学第一周”设置");

        var reloaded = CreateDuty(ws, settings, rosters, out _);
        Assert.Equal(3, reloaded.Config.Tasks.Count);
        Assert.Equal(3, reloaded.Config.Groups.Count);
        Assert.Equal(3, reloaded.Config.Order.Count);
        Assert.Equal(12, reloaded.Config.Committee.Count);
        Assert.Equal(9, reloaded.Config.SubjectReps.Count);
        Assert.Equal(8, reloaded.Config.Reminders.DailyMinutesBefore);
        Assert.True(reloaded.Config.Display.TopTaskKeywords.Contains("讲台"));

        // 周覆盖也应持久化（第 2 周强制换回第 1 组）
        reloaded.SetWeekOverride(Anchor.AddDays(7), "g1", false, "调换");
        var again = CreateDuty(ws, settings, rosters, out _);
        Assert.Equal("第1组", again.GetSchedule(Anchor.AddDays(7)).GroupName);
    }

    [BanjiTest]
    public static void 首配状态_未完成配置与缺开学第一周()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 20);
        var duty = CreateDuty(ws, settings, rosters, out _);

        settings.Update(x =>
        {
            x.DutySetupCompleted = false;
            x.SemesterFirstWeekMonday = null;
        });
        Assert.True(duty.NeedsFirstTimeSetup, "未标记完成时应要求首次配置");
        Assert.False(duty.IsConfigured, "空配置不应算已完成");

        var schedule = duty.GetSchedule(Anchor);
        Assert.Equal(0, schedule.Tasks.Count);
        Assert.Equal(0, duty.GetPetPanelLines(Anchor).Count);
        Assert.Equal(0, duty.GetRotationPreview(Anchor, 4).Count);

        // 有岗位+分组但没有“开学第一周” → 仍不算完成
        var config = CreateWeeklyConfig(Anchor, 2, 2);
        config.AnchorMonday = null;
        duty.Save(config);
        Assert.False(duty.IsConfigured, "缺少开学第一周时不能算配置完成");

        // 设置开学第一周 + 完成标记
        settings.Update(x => x.SemesterFirstWeekMonday = Anchor);
        Assert.True(duty.IsConfigured);
        settings.Update(x => x.DutySetupCompleted = true);
        Assert.False(duty.NeedsFirstTimeSetup);
    }

    // =========================================================================
    // 预设包
    // =========================================================================

    [BanjiTest]
    public static void 预设包_工厂方法与合并去重()
    {
        var weekly = DutyPresets.CreateWeeklyTasks();
        Assert.Equal(6, weekly.Count);
        Assert.True(weekly.Any(t => t.Name == "擦黑板" && t.RequiredCount == 1));
        Assert.True(weekly.Any(t => t.Name == "扫地" && t.RequiredCount == 2));
        Assert.True(weekly.All(t => t.Category == "值周"));

        var cleanup = DutyPresets.CreateDailyCleanupTasks();
        Assert.Equal(8, cleanup.Count);
        Assert.True(cleanup.Any(t => t.Name == "卫生角"));

        Assert.Equal(12, DutyPresets.CreateCommitteeRoles().Count);
        Assert.Equal(9, DutyPresets.CreateSubjectRepRoles().Count);

        // 空模板
        var empty = DutyPresets.CreateConfig(DutyPresetKind.Empty);
        Assert.Equal(0, empty.Tasks.Count);
        Assert.Equal(0, empty.Committee.Count);

        // 值周 + 班委 + 课代表
        var full = DutyPresets.CreateConfig(DutyPresetKind.Weekly, DutyPresetKind.Committee, DutyPresetKind.SubjectReps);
        Assert.Equal(6, full.Tasks.Count);
        Assert.Equal(12, full.Committee.Count);
        Assert.Equal(9, full.SubjectReps.Count);

        // 两个任务包合并：同名岗位去重
        var merged = DutyPresets.CreateConfig(DutyPresetKind.Weekly, DutyPresetKind.DailyCleanup);
        Assert.Equal(9, merged.Tasks.Count, "值周包 6 + 卫生包 8，5 个同名去重后应为 9");

        // 预设返回的是新对象，可自由编辑
        var custom = DutyPresets.CreateWeeklyTasks();
        custom[0].Name = "擦白板";
        custom[0].RequiredCount = 2;
        Assert.Equal("擦黑板", DutyPresets.CreateWeeklyTasks()[0].Name, "预设工厂每次返回新对象");
        Assert.Equal(2, custom[0].RequiredCount);
    }

    [BanjiTest]
    public static void 预设包_分组与自动分配()
    {
        using var ws = new TempWorkspace();
        var (_, rosters) = CreateRoster(ws, 30);
        var students = rosters.Active!.Students;

        var groups = DutyPresets.CreateGroups(students, 4);
        Assert.Equal(4, groups.Count);
        var counts = groups.Select(g => g.MemberIds.Count).ToList();
        Assert.True(counts.Max() - counts.Min() <= 1, "分组人数差应 ≤1");
        Assert.Equal(30, groups.Sum(g => g.MemberIds.Count), "分组应覆盖全员且不重复");

        var tasks = DutyPresets.CreateWeeklyTasks();
        // 用 12 人分 4 组（每组 3 人）来验证“一人多岗”
        var smallGroups = DutyPresets.CreateGroups(students.Take(12).ToList(), 4);
        DutyPresets.AutoAssignGroup(smallGroups[0], tasks);
        var totalSlots = tasks.Sum(t => t.RequiredCount);
        Assert.Equal(totalSlots, smallGroups[0].Assignments.Count);
        Assert.True(smallGroups[0].Assignments.GroupBy(a => a.StudentId).Any(g => g.Count() > 1), "自动分配应支持一人多岗");

        var config = new DutyConfig { Tasks = tasks, Groups = groups, Order = groups.Select(g => g.Id).ToList() };
        DutyPresets.AutoAssignAllGroups(config);
        Assert.True(config.Groups.All(g => g.Assignments.Count > 0));
    }

    // =========================================================================
    // 其他边界
    // =========================================================================

    [BanjiTest]
    public static void 轮换_不同组人数与岗位人数可不同()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 30);
        var duty = CreateDuty(ws, settings, rosters, out _);

        var config = new DutyConfig
        {
            Mode = DutyMode.Weekly,
            AnchorMonday = Anchor,
            Tasks = new List<DutyTask>
            {
                new() { Id = "t-blackboard", Name = "擦黑板", RequiredCount = 1, Sort = 0 },
                new() { Id = "t-floor", Name = "拖地", RequiredCount = 2, Sort = 1 }
            },
            Groups = new List<DutyGroup>
            {
                new() { Id = "g1", Name = "第1组", MemberIds = new List<string> { "20260101", "20260102" } },
                new() { Id = "g2", Name = "第2组", MemberIds = new List<string> { "20260111", "20260112", "20260113", "20260114" } }
            },
            Order = new List<string> { "g1", "g2" },
            AssignmentMode = DutyAssignmentMode.AutoByOrder
        };
        duty.Save(config);

        var week1 = duty.GetSchedule(Anchor);
        Assert.Equal("第1组", week1.GroupName);
        Assert.Equal(3, week1.Tasks.Sum(t => t.StudentIds.Count), "2 人组按循环分配 3 个坑（其中一人多岗）");

        var week2 = duty.GetSchedule(Anchor.AddDays(7));
        Assert.Equal("第2组", week2.GroupName);
        Assert.Equal(3, week2.Tasks.Sum(t => t.StudentIds.Count), "4 人组满足 3 个坑后剩 1 人机动");
    }

    [BanjiTest]
    public static void 预览_值日模式返回未来N天()
    {
        using var ws = new TempWorkspace();
        var (settings, rosters) = CreateRoster(ws, 30);
        var duty = CreateDuty(ws, settings, rosters, out _);
        var config = CreateWeeklyConfig(Anchor, 2, 2);
        config.Mode = DutyMode.Daily;
        config.AnchorMonday = Anchor;
        duty.Save(config);

        var preview = duty.GetRotationPreview(Anchor, 5);
        Assert.Equal(5, preview.Count);
        Assert.Equal("第1组", preview[0].GroupName);
        Assert.Equal("第2组", preview[1].GroupName);
        Assert.Equal("第1组", preview[2].GroupName);
        Assert.Equal(Anchor.AddDays(2), preview[2].WeekMonday);
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static (FakeSettingsService Settings, RosterService Rosters) CreateRoster(TempWorkspace ws, int count)
    {
        var settings = new FakeSettingsService();
        var rosters = new RosterService(settings, ws.File("rosters.json"), () => Now);
        var names = new[]
        {
            "王浩然", "李思远", "张雨桐", "刘一诺", "陈子轩", "杨若曦", "赵嘉怡", "周明远",
            "吴亦菲", "徐子墨", "孙悦然", "马晨曦", "朱婉清", "胡俊杰", "郭静怡", "何思源",
            "高子涵", "林雨欣", "罗天宇", "梁诗琪", "宋佳明", "谢雨萱", "唐子豪", "韩雪晴",
            "冯逸辰", "曹语彤", "彭若尘", "曾可欣", "萧亦航", "田若溪", "沈知远", "孟书瑶"
        };
        var roster = rosters.CreateManual("高一(3)班", Enumerable.Range(0, count).Select(i => new Student
        {
            Id = (20260101 + i).ToString(),
            Name = i < names.Length ? names[i] : $"学生{i + 1:00}",
            Score = 90 - i
        }));
        rosters.SetActive(roster.Id);
        return (settings, rosters);
    }

    private static DutyService CreateDuty(
        TempWorkspace ws, FakeSettingsService settings, RosterService rosters, out Roster roster)
    {
        roster = rosters.Active!;
        return new DutyService(settings, rosters, ws.File("duty.json"), () => Now);
    }

    private static DutyConfig CreateWeeklyConfig(DateOnly anchor, int groupCount, int membersPerGroup)
    {
        var config = new DutyConfig
        {
            Mode = DutyMode.Weekly,
            AssignmentMode = DutyAssignmentMode.FixedPerGroup,
            AnchorMonday = anchor,
            Tasks = new List<DutyTask>
            {
                new() { Id = "t-blackboard", Name = "擦黑板", Category = "卫生", RequiredCount = 1, Sort = 0 },
                new() { Id = "t-sweep", Name = "扫地", Category = "卫生", RequiredCount = 1, Sort = 1 },
                new() { Id = "t-trash", Name = "倒垃圾", Category = "卫生", RequiredCount = 1, Sort = 2 }
            }
        };
        for (var g = 0; g < groupCount; g++)
        {
            var group = new DutyGroup { Id = $"g{g + 1}", Name = $"第{g + 1}组" };
            for (var m = 0; m < membersPerGroup; m++)
            {
                var studentId = (20260101 + g * 10 + m).ToString();
                group.MemberIds.Add(studentId);
                group.Assignments.Add(new DutyAssignment
                {
                    StudentId = studentId,
                    TaskId = config.Tasks[m % config.Tasks.Count].Id
                });
            }
            config.Groups.Add(group);
            config.Order.Add(group.Id);
        }
        return config;
    }
}
