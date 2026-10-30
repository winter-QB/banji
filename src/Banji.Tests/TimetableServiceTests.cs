using Banji.Core.Models;
using Banji.Core.Schedule;
using Banji.Core.Timetable;

namespace Banji.Tests;

/// <summary>TimetableService：课表/课堂时段/午休/临时换课的 CRUD 与 JSON 持久化（用临时文件，不碰真实数据）。</summary>
public static class TimetableServiceTests
{
    private static (TimetableService Service, string TimetablePath, string OverridesPath) Create()
    {
        var dir = TestFileBuilder.TempDir();
        var timetablePath = Path.Combine(dir, "timetable.json");
        var overridesPath = Path.Combine(dir, "overrides.json");
        return (new TimetableService(timetablePath, overridesPath), timetablePath, overridesPath);
    }

    [BanjiTest]
    public static void 无文件时加载默认课表()
    {
        var (service, _, _) = Create();
        Assert.Equal(8, service.Current.Periods.Count);
        Assert.True(service.Current.Breaks.Count >= 1);
        Assert.Equal(0, service.Overrides.Count);
        Assert.Equal(new TimeSpan(8, 0, 0), service.Current.Periods[0].Start);
    }

    [BanjiTest]
    public static void 课堂时段可完全自定义且持久化到秒()
    {
        var (service, timetablePath, _) = Create();
        service.SetPeriods(new[]
        {
            new ClassPeriod { Index = 1, Name = "早读", Start = new TimeSpan(7, 30, 15), End = new TimeSpan(8, 0, 45) },
            new ClassPeriod { Index = 2, Name = "第一节", Start = new TimeSpan(8, 10, 0), End = new TimeSpan(8, 55, 30) }
        });
        service.SetBreaks(new[]
        {
            new BreakSegment { Name = "大课间", Start = new TimeSpan(8, 0, 45), End = new TimeSpan(8, 10, 0) }
        });

        Assert.Equal(2, service.Current.Periods.Count);
        Assert.Equal(new TimeSpan(8, 55, 30), service.Current.Periods[1].End);
        Assert.True(File.Exists(timetablePath), "应写入 timetable.json");

        // 重新加载（模拟重启）
        var reloaded = new TimetableService(timetablePath, Path.Combine(Path.GetDirectoryName(timetablePath)!, "overrides.json"));
        Assert.Equal(2, reloaded.Current.Periods.Count);
        Assert.Equal("早读", reloaded.Current.Periods[0].Name);
        Assert.Equal(new TimeSpan(7, 30, 15), reloaded.Current.Periods[0].Start);
        Assert.Equal(new TimeSpan(8, 55, 30), reloaded.Current.Periods[1].End);
        Assert.Equal(1, reloaded.Current.Breaks.Count);
        Assert.Equal("大课间", reloaded.Current.Breaks[0].Name);
    }

    [BanjiTest]
    public static void 删除节次时课程数据同步迁移()
    {
        var (service, _, _) = Create();
        service.SetCourse(DayOfWeek.Monday, 1, "语文");
        service.SetCourse(DayOfWeek.Monday, 2, "数学");
        service.SetCourse(DayOfWeek.Monday, 3, "英语");

        // 删除第 2 节（传入原编号 1,3,4..8 的节次）
        var remaining = service.Current.Periods
            .Where(p => p.Index != 2)
            .Select(p => p.Clone())
            .ToList();
        service.SetPeriods(remaining);

        Assert.Equal(7, service.Current.Periods.Count);
        Assert.Equal(1, service.Current.Periods[0].Index);
        Assert.Equal(2, service.Current.Periods[1].Index);
        // 原第 3 节的“英语”迁移到新的第 2 节
        Assert.Equal("英语", service.Current.CourseAt(DayOfWeek.Monday, 2));
        Assert.True(string.IsNullOrEmpty(service.Current.CourseAt(DayOfWeek.Monday, 3)), "新第 3 节应没有课程");
        Assert.Equal("语文", service.Current.CourseAt(DayOfWeek.Monday, 1));
    }

    [BanjiTest]
    public static void 临时换课增删改查与持久化()
    {
        var (service, _, overridesPath) = Create();
        var date = new DateOnly(2026, 11, 2);
        service.SetOverride(new DayOverride
        {
            Date = date,
            Note = "英语老师请假",
            CourseReplacements = { [1] = "数学", [3] = null },
            CancelledPeriods = { 2 }
        });

        var loaded = service.GetOverride(date);
        Assert.NotNull(loaded);
        Assert.Equal("英语老师请假", loaded!.Note);
        Assert.Equal("数学", loaded.CourseReplacements[1]);
        Assert.True(loaded.CourseReplacements.ContainsKey(3));
        Assert.True(loaded.CancelledPeriods.Contains(2));
        Assert.True(File.Exists(overridesPath), "应写入 overrides.json");

        // 重新加载
        var reloaded = new TimetableService(
            Path.Combine(Path.GetDirectoryName(overridesPath)!, "timetable.json"),
            overridesPath);
        var loaded2 = reloaded.GetOverride(date);
        Assert.NotNull(loaded2);
        Assert.Equal("数学", loaded2!.CourseReplacements[1]);

        // 删除
        service.RemoveOverride(date);
        Assert.Null(service.GetOverride(date));
        var reloaded2 = new TimetableService(
            Path.Combine(Path.GetDirectoryName(overridesPath)!, "timetable.json"),
            overridesPath);
        Assert.Null(reloaded2.GetOverride(date));

        // 返回的是副本：外部修改不影响服务内部数据
        service.SetOverride(new DayOverride { Date = date, Note = "备注" });
        var copy = service.GetOverride(date)!;
        copy.Note = "被篡改";
        Assert.Equal("备注", service.GetOverride(date)!.Note);
    }

    [BanjiTest]
    public static void 空换课等于删除()
    {
        var (service, _, _) = Create();
        var date = new DateOnly(2026, 11, 2);
        service.SetOverride(new DayOverride { Date = date, Note = "临时" });
        Assert.NotNull(service.GetOverride(date));
        service.SetOverride(new DayOverride { Date = date }); // 空覆盖
        Assert.Null(service.GetOverride(date));
    }

    [BanjiTest]
    public static void 换课优先于周课表并影响时间引擎()
    {
        var (service, _, _) = Create();
        var settings = new ScheduleFakeSettingsService();
        var schedule = new ScheduleService(service, settings);
        var monday = new DateTime(2026, 11, 2, 8, 5, 0);

        // 周课表课程为空（默认课表），先设置
        service.SetCourse(DayOfWeek.Monday, 1, "语文");
        service.SetBreaks(new[]
        {
            new BreakSegment { Name = "午休", Start = new TimeSpan(11, 40, 0), End = new TimeSpan(14, 0, 0) }
        });
        var before = schedule.SnapshotAt(monday);
        Assert.Equal("语文", before.Current!.Course);

        service.SetOverride(new DayOverride
        {
            Date = DateOnly.FromDateTime(monday),
            CourseReplacements = { [1] = "数学竞赛" }
        });
        var after = schedule.SnapshotAt(monday);
        Assert.Equal("数学竞赛", after.Current!.Course);

        service.RemoveOverride(DateOnly.FromDateTime(monday));
        Assert.Equal("语文", schedule.SnapshotAt(monday).Current!.Course);
    }

    [BanjiTest]
    public static void 导入后课程表可用且不会残留旧数据()
    {
        var (service, timetablePath, _) = Create();
        var path = TestFileBuilder.WriteCsv("import-service.csv",
            new[] { "节次", "时间", "周一", "周二" },
            new[] { "第1节", "08:00-08:45", "语文", "数学" },
            new[] { "第2节", "08:55-09:40", "数学", "英语" });

        var imported = service.Import(path);
        Assert.Equal(2, imported.Periods.Count);
        Assert.Equal("数学", imported.CourseAt(DayOfWeek.Tuesday, 1));
        Assert.Equal(path, imported.SourceFile);
        Assert.True(File.Exists(timetablePath));

        var reloaded = new TimetableService(timetablePath, Path.Combine(Path.GetDirectoryName(timetablePath)!, "overrides.json"));
        Assert.Equal(2, reloaded.Current.Periods.Count);
        Assert.Equal("英语", reloaded.Current.CourseAt(DayOfWeek.Tuesday, 2));

        // 预览接口
        var preview = service.PreviewImport(path);
        Assert.NotNull(preview.Candidate);
        Assert.Equal(1, preview.Sheets.Count);
    }

    [BanjiTest]
    public static void SetCourse修改课程并广播Changed()
    {
        var (service, _, _) = Create();
        var count = 0;
        service.Changed += (_, _) => count++;

        service.SetCourse(DayOfWeek.Friday, 8, "生涯规划");
        Assert.Equal("生涯规划", service.Current.CourseAt(DayOfWeek.Friday, 8));
        Assert.Equal(1, count);

        service.SetCourse(DayOfWeek.Friday, 8, "班会");
        Assert.Equal("班会", service.Current.CourseAt(DayOfWeek.Friday, 8));

        // 空字符串也能保存（表示“未设置”）
        service.SetCourse(DayOfWeek.Friday, 8, "");
        Assert.Equal("", service.Current.CourseAt(DayOfWeek.Friday, 8));
    }

    [BanjiTest]
    public static void 损坏的JSON文件不会崩溃并回退默认课表()
    {
        var dir = TestFileBuilder.TempDir();
        var timetablePath = Path.Combine(dir, "timetable.json");
        var overridesPath = Path.Combine(dir, "overrides.json");
        File.WriteAllText(timetablePath, "{ 这不是合法的 json ");
        File.WriteAllText(overridesPath, "[[[");
        var service = new TimetableService(timetablePath, overridesPath);
        Assert.Equal(8, service.Current.Periods.Count);
        Assert.Equal(0, service.Overrides.Count);
    }

    [BanjiTest]
    public static void 传入非法节次会被过滤()
    {
        var (service, _, _) = Create();
        service.SetPeriods(new[]
        {
            new ClassPeriod { Index = 1, Name = "正常", Start = new TimeSpan(8, 0, 0), End = new TimeSpan(8, 45, 0) },
            new ClassPeriod { Index = 2, Name = "结束早于开始", Start = new TimeSpan(9, 45, 0), End = new TimeSpan(9, 0, 0) }
        });
        Assert.Equal(1, service.Current.Periods.Count);
    }

    [BanjiTest]
    public static void 换课列表按日期排序()
    {
        var (service, _, _) = Create();
        service.SetOverride(new DayOverride { Date = new DateOnly(2026, 11, 4), Note = "周三" });
        service.SetOverride(new DayOverride { Date = new DateOnly(2026, 11, 2), Note = "周一" });
        service.SetOverride(new DayOverride { Date = new DateOnly(2026, 11, 3), Note = "周二" });
        Assert.Equal(3, service.Overrides.Count);
        Assert.Equal(new DateOnly(2026, 11, 2), service.Overrides[0].Date);
        Assert.Equal(new DateOnly(2026, 11, 3), service.Overrides[1].Date);
        Assert.Equal(new DateOnly(2026, 11, 4), service.Overrides[2].Date);

        // 同一天再次设置是覆盖而不是追加
        service.SetOverride(new DayOverride { Date = new DateOnly(2026, 11, 3), Note = "周二改" });
        Assert.Equal(3, service.Overrides.Count);
        Assert.Equal("周二改", service.GetOverride(new DateOnly(2026, 11, 3))!.Note);
    }
}

