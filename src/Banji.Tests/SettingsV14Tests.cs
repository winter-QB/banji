using Banji.Core.IO;
using Banji.Core.Models;
using Banji.Core.Settings;
using Banji.Views;

namespace Banji.Tests;

/// <summary>v1.4.0 设置单测：单双周/值日字段默认值、round-trip 与预设生成数量。</summary>
public static class SettingsV14Tests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "banji-v14-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }

    [BanjiTest(Name = "v1.4 新字段默认值（单双周/值周值日）")]
    public static void v14新字段默认值()
    {
        var s = new AppSettings();
        s.Normalize();

        Assert.Null(s.SemesterFirstWeekMonday, "开学第一周默认未设置（首次配置必填）");
        Assert.False(s.EnableWeekParity, "单双周默认关闭");
        Assert.Null(s.WeekParityAnchorDate, "锚点默认跟随开学第一周");
        Assert.True(s.FirstWeekIsOdd, "锚点周默认单周");
        Assert.True(s.ShowWeekNumberOnPet, "宠物显示周次默认开");
        Assert.False(s.NoticeIncludeWeekParity, "气泡单双周后缀默认关");
        Assert.Equal(WeekParityOverrideMode.Auto, s.WeekParityOverride);
        Assert.Null(s.WeekParityOverrideWeekStart);
        Assert.True(s.ShowDutyOnPet, "宠物显示轮值默认开");
        Assert.False(s.DutySetupCompleted, "首次配置默认未完成");
    }

    [BanjiTest(Name = "v1.4 设置 JSON round-trip（单双周/值日）")]
    public static void v14设置roundtrip()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        var semester = new DateOnly(2026, 9, 7);

        var s = new AppSettings
        {
            SemesterFirstWeekMonday = semester,
            EnableWeekParity = true,
            WeekParityAnchorDate = new DateOnly(2026, 9, 14),
            FirstWeekIsOdd = false,
            ShowWeekNumberOnPet = false,
            NoticeIncludeWeekParity = true,
            WeekParityOverride = WeekParityOverrideMode.Odd,
            WeekParityOverrideWeekStart = new DateOnly(2026, 11, 2),
            ShowDutyOnPet = false,
            DutySetupCompleted = true
        };
        s.Normalize();

        SafeJsonStore.Save(path, s);
        var loaded = SafeJsonStore.Load(path, () => new AppSettings());
        loaded.Normalize();

        Assert.Equal(semester, loaded.SemesterFirstWeekMonday);
        Assert.True(loaded.EnableWeekParity);
        Assert.Equal(new DateOnly(2026, 9, 14), loaded.WeekParityAnchorDate);
        Assert.False(loaded.FirstWeekIsOdd);
        Assert.False(loaded.ShowWeekNumberOnPet);
        Assert.True(loaded.NoticeIncludeWeekParity);
        Assert.Equal(WeekParityOverrideMode.Odd, loaded.WeekParityOverride);
        Assert.Equal(new DateOnly(2026, 11, 2), loaded.WeekParityOverrideWeekStart);
        Assert.False(loaded.ShowDutyOnPet);
        Assert.True(loaded.DutySetupCompleted);

        // Clone 后修改不影响原对象
        var clone = loaded.Clone();
        clone.DutySetupCompleted = false;
        clone.WeekParityOverride = WeekParityOverrideMode.Even;
        Assert.True(loaded.DutySetupCompleted);
        Assert.Equal(WeekParityOverrideMode.Odd, loaded.WeekParityOverride);
    }

    [BanjiTest(Name = "v1.4 预设包生成数量（值周/卫生/班委/课代表）")]
    public static void v14预设生成数量()
    {
        var weekly = DutyPresetFactory.BuildTasks(DutyPresetKind.Weekly);
        Assert.Equal(6, weekly.Count);
        Assert.Equal(8, weekly.Sum(t => t.RequiredCount), "值周包共 8 人次");
        Assert.Equal("擦黑板", weekly[0].Name);

        var cleaning = DutyPresetFactory.BuildTasks(DutyPresetKind.Cleaning);
        Assert.Equal(8, cleaning.Count);
        Assert.Equal(10, cleaning.Sum(t => t.RequiredCount), "卫生值日包共 10 人次");

        Assert.Equal(0, DutyPresetFactory.BuildTasks(DutyPresetKind.Empty).Count);
        Assert.Equal(0, DutyPresetFactory.BuildTasks(DutyPresetKind.None).Count);

        Assert.Equal(12, DutyPresetFactory.BuildCommitteePreset().Count, "班委包 12 个职位");
        Assert.Equal(9, DutyPresetFactory.BuildSubjectRepPreset().Count, "课代表包 9 个科目");
        Assert.True(DutyPresetFactory.BuildCommitteePreset().All(r => r.Count == 1));
        Assert.True(DutyPresetFactory.BuildSubjectRepPreset().All(r => r.Count == 1));
    }

    [BanjiTest(Name = "v1.4 预设配置：分组覆盖全部学生且任务分配满足人数")]
    public static void v14预设配置分组与分配()
    {
        var students = Enumerable.Range(0, 30)
            .Select(i => new Student { Id = (20260101 + i).ToString(), Name = "学生" + i })
            .ToList();
        var anchor = new DateOnly(2026, 9, 9); // 周三 → 应对齐到 9/7 周一
        var config = DutyPresetFactory.BuildConfig(
            DutyPresetKind.Weekly, includeCommitteePack: true, includeSubjectRepPack: false,
            DutyMode.Weekly, groupCount: 4, DutyAssignmentMode.FixedPerGroup, students, anchor);

        Assert.Equal(4, config.Groups.Count);
        Assert.Equal(4, config.Order.Count);
        Assert.Equal(6, config.Tasks.Count);
        Assert.Equal(12, config.Committee.Count);
        Assert.Equal(0, config.SubjectReps.Count);
        Assert.Equal(new DateOnly(2026, 9, 7), config.AnchorMonday, "锚点应对齐到周一");

        var allMembers = config.Groups.SelectMany(g => g.MemberIds).ToList();
        Assert.Equal(30, allMembers.Count, "全部学生都应进入某个组");
        Assert.Equal(30, allMembers.Distinct().Count(), "成员不应重复");

        var requiredTotal = config.Tasks.Sum(t => t.RequiredCount);
        foreach (var group in config.Groups)
        {
            Assert.Equal(requiredTotal, group.Assignments.Count,
                $"{group.Name} 的分配人数应等于岗位需求总人次（允许一人多岗/一岗多人）");
            foreach (var task in config.Tasks)
            {
                var assigned = group.Assignments.Count(a => a.TaskId == task.Id);
                Assert.Equal(task.RequiredCount, assigned, $"{group.Name}·{task.Name} 的分配人数");
            }
            Assert.True(group.MemberIds.Count > 0);
        }
    }

    [BanjiTest(Name = "v1.4 自动分配支持一个岗位 2 人与一人多岗")]
    public static void v14自动分配边界()
    {
        var tasks = new List<DutyTask>
        {
            new() { Id = "t1", Name = "擦黑板", RequiredCount = 1, Sort = 0 },
            new() { Id = "t2", Name = "扫地", RequiredCount = 2, Sort = 1 },
            new() { Id = "t3", Name = "拖地", RequiredCount = 2, Sort = 2 }
        };
        // 只有 2 人：总共 5 人次，必然一人多岗
        var assignments = DutyPresetFactory.AutoAssign(new[] { "a", "b" }, tasks);
        Assert.Equal(5, assignments.Count);
        Assert.Equal(1, assignments.Count(a => a.TaskId == "t1"));
        Assert.Equal(2, assignments.Count(a => a.TaskId == "t2"));
        Assert.Equal(2, assignments.Count(a => a.TaskId == "t3"));
        Assert.True(assignments.Select(a => a.StudentId).Distinct().Count() <= 2);

        // 空成员/空任务不崩溃
        Assert.Equal(0, DutyPresetFactory.AutoAssign(Array.Empty<string>(), tasks).Count);
        Assert.Equal(0, DutyPresetFactory.AutoAssign(new[] { "a" }, new List<DutyTask>()).Count);
    }

    [BanjiTest(Name = "v1.4 空模板/不使用预设不生成岗位与分组")]
    public static void v14空模板()
    {
        var students = new List<Student> { new() { Id = "1", Name = "甲" } };
        var empty = DutyPresetFactory.BuildConfig(
            DutyPresetKind.Empty, false, false, DutyMode.Daily, 4, DutyAssignmentMode.AutoByOrder,
            students, new DateOnly(2026, 9, 7));
        Assert.Equal(0, empty.Tasks.Count);
        Assert.Equal(0, empty.Groups.Count);
        Assert.Equal(DutyMode.Daily, empty.Mode);

        var none = DutyPresetFactory.BuildConfig(
            DutyPresetKind.None, true, true, DutyMode.Weekly, 4, DutyAssignmentMode.FixedPerGroup,
            students, null);
        Assert.Equal(0, none.Tasks.Count);
        Assert.Equal(0, none.Groups.Count);
        Assert.Equal(12, none.Committee.Count);
        Assert.Equal(9, none.SubjectReps.Count);
        Assert.Null(none.AnchorMonday);
    }

    [BanjiTest(Name = "v1.4 MondayOf 周一计算")]
    public static void v14周一计算()
    {
        Assert.Equal(new DateOnly(2026, 9, 7), DutyPresetFactory.MondayOf(new DateOnly(2026, 9, 7)));  // 周一
        Assert.Equal(new DateOnly(2026, 9, 7), DutyPresetFactory.MondayOf(new DateOnly(2026, 9, 9)));  // 周三
        Assert.Equal(new DateOnly(2026, 9, 7), DutyPresetFactory.MondayOf(new DateOnly(2026, 9, 13))); // 周日
        Assert.Equal(new DateOnly(2026, 9, 14), DutyPresetFactory.MondayOf(new DateOnly(2026, 9, 14))); // 下一周周一
    }
}
