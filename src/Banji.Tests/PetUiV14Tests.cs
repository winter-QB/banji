using Banji.Controls.Pet;
using Banji.Core.Models;

namespace Banji.Tests;

/// <summary>v1.4.0 宠物面板：值周/值日、黑板类置顶、放学后替换、单双周标题的纯展示逻辑测试。</summary>
public static class PetUiV14Tests
{
    private static readonly DateTime SchoolEnd = new(2026, 11, 6, 17, 5, 0);

    private static DutyTaskView Task(string name, bool isTop, params string[] members) => new()
    {
        TaskId = name,
        TaskName = name,
        Category = "卫生",
        StudentIds = members.Select((_, i) => $"s{i}").ToList(),
        StudentNames = members,
        IsTopTask = isTop
    };

    private static DutyScheduleView Schedule(DutyMode mode, string? group, params DutyTaskView[] tasks) => new()
    {
        Date = new DateOnly(2026, 11, 6),
        Mode = mode,
        GroupName = group,
        Tasks = tasks,
        PanelLines = Array.Empty<string>()
    };

    private static ScheduleSnapshot AfterSchool(DateTime now, DateTime? end, SchedulePhase phase = SchedulePhase.AfterSchool)
        => new()
        {
            Now = now,
            Phase = phase,
            HasTimetable = true,
            SchoolEnd = end
        };

    // =========================================================================
    // 单双周标题
    // =========================================================================

    [BanjiTest]
    public static void 周次标题按单双周生成()
    {
        Assert.Equal("第 9 周 · 单周", DutyPanelFormatter.WeekHeaderText(9, WeekParity.Odd, true, true));
        Assert.Equal("第 9 周 · 双周", DutyPanelFormatter.WeekHeaderText(9, WeekParity.Even, true, true));
        Assert.Equal("第 10 周 · 双周", DutyPanelFormatter.WeekHeaderText(10, WeekParity.Even, true, true));
    }

    [BanjiTest]
    public static void 周次标题受设置与启用状态控制()
    {
        Assert.Equal("", DutyPanelFormatter.WeekHeaderText(9, WeekParity.Odd, false, true));
        Assert.Equal("", DutyPanelFormatter.WeekHeaderText(9, WeekParity.Odd, true, false));
        Assert.Equal("", DutyPanelFormatter.WeekHeaderText(0, WeekParity.Odd, true, true));

        Assert.Equal("第9周·单周", DutyPanelFormatter.WeekShortText(9, WeekParity.Odd, true, true));
        Assert.Equal("第9周·双周", DutyPanelFormatter.WeekShortText(9, WeekParity.Even, true, true));
        Assert.Equal("点击展开", DutyPanelFormatter.WeekShortText(9, WeekParity.Odd, false, true, "点击展开"));
        Assert.Equal("", DutyPanelFormatter.WeekShortText(0, WeekParity.Odd, true, true));
    }

    [BanjiTest]
    public static void 单双周文案()
    {
        Assert.Equal("单周", DutyPanelFormatter.ParityLabel(WeekParity.Odd));
        Assert.Equal("双周", DutyPanelFormatter.ParityLabel(WeekParity.Even));
    }

    // =========================================================================
    // 值周 / 值日行
    // =========================================================================

    [BanjiTest]
    public static void 值周行与值日行文案()
    {
        var weekly = Schedule(DutyMode.Weekly, "第 3 组", Task("擦黑板", true, "张三"));
        var daily = Schedule(DutyMode.Daily, "第 2 组", Task("擦黑板", true, "李四"));
        Assert.Equal("本周值周：第 3 组", DutyPanelFormatter.DutySummaryText(DutyMode.Weekly, weekly));
        Assert.Equal("今日值日：第 2 组", DutyPanelFormatter.DutySummaryText(DutyMode.Daily, daily));
        Assert.Equal("本周值周", DutyPanelFormatter.DutyLabel(DutyMode.Weekly));
        Assert.Equal("今日值日", DutyPanelFormatter.DutyLabel(DutyMode.Daily));
    }

    [BanjiTest]
    public static void 暂停与未分组时也有可读文案()
    {
        var paused = new DutyScheduleView
        {
            Date = new DateOnly(2026, 11, 6), Mode = DutyMode.Weekly, IsPaused = true,
            GroupName = "第 1 组", Tasks = Array.Empty<DutyTaskView>()
        };
        Assert.Equal("本周值周：本周暂停", DutyPanelFormatter.DutySummaryText(DutyMode.Weekly, paused));

        var pausedDaily = new DutyScheduleView
        {
            Date = new DateOnly(2026, 11, 6), Mode = DutyMode.Daily, IsPaused = true,
            Tasks = Array.Empty<DutyTaskView>()
        };
        Assert.Equal("今日值日：今日暂停", DutyPanelFormatter.DutySummaryText(DutyMode.Daily, pausedDaily));

        var noGroup = new DutyScheduleView { Date = new DateOnly(2026, 11, 6), Mode = DutyMode.Weekly };
        Assert.Equal("本周值周：未分组", DutyPanelFormatter.DutySummaryText(DutyMode.Weekly, noGroup));

        Assert.Equal("", DutyPanelFormatter.DutySummaryText(DutyMode.Weekly, null));
    }

    // =========================================================================
    // 黑板类岗位置顶
    // =========================================================================

    [BanjiTest]
    public static void 黑板类推前其余保持原顺序()
    {
        var tasks = new[]
        {
            Task("扫地", false, "李四", "王五"),
            Task("擦黑板", true, "张三"),
            Task("讲台整理", true, "赵六"),
            Task("倒垃圾", false, "孙七")
        };
        var lines = DutyPanelFormatter.TaskLines(tasks);
        Assert.Equal(4, lines.Count);
        Assert.Equal("擦黑板：张三", lines[0].Text);
        Assert.Equal("讲台整理：赵六", lines[1].Text);
        Assert.Equal("扫地：李四、王五", lines[2].Text);
        Assert.Equal("倒垃圾：孙七", lines[3].Text);
        Assert.True(lines[0].IsTopTask);
        Assert.False(lines[2].IsTopTask);
    }

    [BanjiTest]
    public static void 置顶岗位与非置顶岗位分组()
    {
        var tasks = new[]
        {
            Task("擦黑板", true, "张三"),
            Task("扫地", false, "李四"),
            Task("黑板槽清理", true, "王五")
        };
        Assert.Equal(2, DutyPanelFormatter.TopTaskLines(tasks).Count);
        Assert.Equal(1, DutyPanelFormatter.NonTopTaskLines(tasks).Count);
        Assert.Equal("扫地：李四", DutyPanelFormatter.NonTopTaskLines(tasks)[0].Text);
    }

    [BanjiTest]
    public static void 空成员显示未安排()
    {
        var lines = DutyPanelFormatter.TaskLines(new[] { Task("擦黑板", true) });
        Assert.Equal("擦黑板：未安排", lines[0].Text);
    }

    // =========================================================================
    // 折叠态短文案
    // =========================================================================

    [BanjiTest]
    public static void 折叠态优先显示黑板岗位再显示值周行()
    {
        var schedule = Schedule(DutyMode.Weekly, "第 1 组",
            Task("扫地", false, "李四"),
            Task("擦黑板", true, "张三"));
        var (main, secondary) = DutyPanelFormatter.FoldedDutyLines(DutyMode.Weekly, schedule);
        Assert.Equal("擦黑板：张三", main);
        Assert.Equal("本周值周：第 1 组", secondary);
    }

    [BanjiTest]
    public static void 无黑板岗位时折叠态显示值周行或首个任务()
    {
        var weekly = Schedule(DutyMode.Weekly, "第 2 组", Task("扫地", false, "李四", "王五"));
        var (main, secondary) = DutyPanelFormatter.FoldedDutyLines(DutyMode.Weekly, weekly);
        Assert.Equal("本周值周：第 2 组", main);
        Assert.Equal("扫地：李四、王五", secondary);

        var onlyGroup = Schedule(DutyMode.Daily, "第 3 组");
        var (dailyMain, dailySecondary) = DutyPanelFormatter.FoldedDutyLines(DutyMode.Daily, onlyGroup);
        Assert.Equal("今日值日：第 3 组", dailyMain);
        Assert.Null(dailySecondary);

        Assert.Equal(("", (string?)null), DutyPanelFormatter.FoldedDutyLines(DutyMode.Weekly, null));
    }

    // =========================================================================
    // 放学后替换窗口
    // =========================================================================

    [BanjiTest]
    public static void 放学后六十分钟内显示轮值信息()
    {
        Assert.True(DutyPanelFormatter.IsAfterSchoolDutyWindow(AfterSchool(SchoolEnd, SchoolEnd), 60, true));
        Assert.True(DutyPanelFormatter.IsAfterSchoolDutyWindow(AfterSchool(SchoolEnd.AddMinutes(30), SchoolEnd), 60, true));
        Assert.True(DutyPanelFormatter.IsAfterSchoolDutyWindow(AfterSchool(SchoolEnd.AddMinutes(60), SchoolEnd), 60, true));
        Assert.False(DutyPanelFormatter.IsAfterSchoolDutyWindow(AfterSchool(SchoolEnd.AddMinutes(61), SchoolEnd), 60, true));
    }

    [BanjiTest]
    public static void 放学后替换受开关与阶段限制()
    {
        var inClass = AfterSchool(SchoolEnd.AddMinutes(10), SchoolEnd, SchedulePhase.InClass);
        Assert.False(DutyPanelFormatter.IsAfterSchoolDutyWindow(inClass, 60, true), "上课中不应替换课表");

        var noEnd = AfterSchool(SchoolEnd.AddMinutes(10), null);
        Assert.False(DutyPanelFormatter.IsAfterSchoolDutyWindow(noEnd, 60, true), "没有放学时间不应替换");

        var enabled = AfterSchool(SchoolEnd.AddMinutes(10), SchoolEnd);
        Assert.False(DutyPanelFormatter.IsAfterSchoolDutyWindow(enabled, 60, false), "展示开关关闭不应替换");
        Assert.False(DutyPanelFormatter.IsAfterSchoolDutyWindow(enabled, 0, true), "0 分钟等于关闭");
        Assert.False(DutyPanelFormatter.IsAfterSchoolDutyWindow(null, 60, true));
    }

    [BanjiTest]
    public static void 放学后替换行不含置顶岗位()
    {
        var schedule = Schedule(DutyMode.Weekly, "第 1 组",
            Task("擦黑板", true, "张三"),
            Task("扫地", false, "李四", "王五"),
            Task("倒垃圾", false, "赵六"));
        var lines = DutyPanelFormatter.AfterSchoolLines(DutyMode.Weekly, schedule);
        Assert.Equal(2, lines.Count);
        Assert.Equal("扫地：李四、王五", lines[0].Text);
        Assert.Equal("倒垃圾：赵六", lines[1].Text);
        Assert.False(lines.Any(l => l.IsTopTask));

        Assert.Equal(0, DutyPanelFormatter.AfterSchoolLines(DutyMode.Weekly, null).Count);
    }
}
