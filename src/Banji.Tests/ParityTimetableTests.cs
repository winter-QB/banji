using Banji.Core.Models;
using Banji.Core.Timetable;

namespace Banji.Tests;

/// <summary>
/// v1.4.0 单双周课表数据测试：
/// 编辑器纯函数（写入/复制/清空/恢复/去重）、TimetableService 持久化 round-trip、
/// 旧 SchemaVersion 1→2 迁移、删除节次时保留单双周标记。
/// </summary>
public static class ParityTimetableTests
{
    private static WeekTimetable NewTimetable()
    {
        var tt = WeekTimetable.CreateDefault();
        foreach (var entry in tt.Days.Values.SelectMany(list => list))
            entry.Course = "";
        return tt;
    }

    // =========================================================================
    // TimetableParityEditor 纯函数
    // =========================================================================

    [BanjiTest]
    public static void 写入通用单周双周并统计()
    {
        var tt = NewTimetable();
        Assert.True(TimetableParityEditor.SetEntry(tt, DayOfWeek.Monday, 1, WeekType.All, "语文"));
        Assert.True(TimetableParityEditor.SetEntry(tt, DayOfWeek.Monday, 1, WeekType.Odd, "数学"));
        Assert.True(TimetableParityEditor.SetEntry(tt, DayOfWeek.Monday, 1, WeekType.Even, "英语"));

        Assert.Equal("语文", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.All)!.Course);
        Assert.Equal("数学", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.Odd)!.Course);
        Assert.Equal("英语", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.Even)!.Course);

        var (all, odd, even) = TimetableParityEditor.CountByWeekType(tt);
        Assert.Equal(1, all);
        Assert.Equal(1, odd);
        Assert.Equal(1, even);
    }

    [BanjiTest]
    public static void 单双页清空课程等于恢复为通用()
    {
        var tt = NewTimetable();
        TimetableParityEditor.SetEntry(tt, DayOfWeek.Monday, 1, WeekType.All, "语文");
        TimetableParityEditor.SetEntry(tt, DayOfWeek.Monday, 1, WeekType.Odd, "数学");

        Assert.True(TimetableParityEditor.SetEntry(tt, DayOfWeek.Monday, 1, WeekType.Odd, ""));
        Assert.Null(tt.EntryAt(DayOfWeek.Monday, 1, WeekType.Odd));
        // 通用条目保持不变
        Assert.Equal("语文", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.All)!.Course);

        // 单页显示回退到通用
        Assert.Equal("语文", TimetableParityEditor.DisplayCourse(tt, DayOfWeek.Monday, 1, WeekType.Odd));
        Assert.False(TimetableParityEditor.HasSpecific(tt, DayOfWeek.Monday, 1, WeekType.Odd));
    }

    [BanjiTest]
    public static void 把通用复制到单周双周()
    {
        var tt = NewTimetable();
        TimetableParityEditor.SetEntry(tt, DayOfWeek.Monday, 1, WeekType.All, "语文");
        TimetableParityEditor.SetEntry(tt, DayOfWeek.Tuesday, 2, WeekType.All, "数学");

        Assert.Equal(2, TimetableParityEditor.CopyAllTo(tt, WeekType.Odd));
        Assert.Equal(2, TimetableParityEditor.CopyAllTo(tt, WeekType.Even));
        Assert.Equal("语文", tt.CourseAt(DayOfWeek.Monday, 1, WeekParity.Odd));
        Assert.Equal("数学", tt.CourseAt(DayOfWeek.Tuesday, 2, WeekParity.Even));

        // 单页专用条目与通用相同（编辑器显示会标记为专用，但解析结果一致）
        Assert.Equal(2, TimetableParityEditor.Count(tt, WeekType.Odd));
        Assert.Equal(2, TimetableParityEditor.Count(tt, WeekType.Even));
    }

    [BanjiTest]
    public static void 清空专用项与全部恢复为通用()
    {
        var tt = NewTimetable();
        TimetableParityEditor.SetEntry(tt, DayOfWeek.Monday, 1, WeekType.All, "语文");
        TimetableParityEditor.SetEntry(tt, DayOfWeek.Monday, 1, WeekType.Odd, "数学");
        TimetableParityEditor.SetEntry(tt, DayOfWeek.Tuesday, 1, WeekType.Odd, "物理");
        TimetableParityEditor.SetEntry(tt, DayOfWeek.Wednesday, 1, WeekType.Even, "化学");

        Assert.Equal(2, TimetableParityEditor.ClearWeek(tt, WeekType.Odd));
        Assert.Equal(0, TimetableParityEditor.Count(tt, WeekType.Odd));
        Assert.Equal(1, TimetableParityEditor.Count(tt, WeekType.Even));

        Assert.Equal(1, TimetableParityEditor.RestoreAllToAll(tt));
        Assert.Equal(0, TimetableParityEditor.Count(tt, WeekType.Even));
        Assert.Equal(1, TimetableParityEditor.Count(tt, WeekType.All));
    }

    [BanjiTest]
    public static void 同格重复条目保留最后一条()
    {
        var tt = NewTimetable();
        tt.Days[DayOfWeek.Monday] = new List<TimetableEntry>
        {
            new() { PeriodIndex = 1, Course = "旧", Weeks = WeekType.Odd },
            new() { PeriodIndex = 1, Course = "新", Weeks = WeekType.Odd },
            new() { PeriodIndex = 1, Course = "语文", Weeks = WeekType.All }
        };

        TimetableParityEditor.NormalizeEntries(tt);
        Assert.Equal(1, TimetableParityEditor.Count(tt, WeekType.Odd));
        Assert.Equal("新", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.Odd)!.Course);
        Assert.Equal(1, TimetableParityEditor.Count(tt, WeekType.All));
    }

    [BanjiTest]
    public static void 非法周类型归为通用()
    {
        var tt = NewTimetable();
        tt.Days[DayOfWeek.Monday] = new List<TimetableEntry>
        {
            new() { PeriodIndex = 1, Course = "语文", Weeks = (WeekType)9 }
        };
        TimetableParityEditor.NormalizeEntries(tt);
        Assert.Equal(1, TimetableParityEditor.Count(tt, WeekType.All));
    }

    // =========================================================================
    // TimetableService：持久化 + 迁移 + 节次迁移
    // =========================================================================

    private static (TimetableService Service, string TimetablePath, string OverridesPath) CreateService()
    {
        var dir = TestFileBuilder.TempDir();
        var timetablePath = Path.Combine(dir, "timetable.json");
        var overridesPath = Path.Combine(dir, "overrides.json");
        return (new TimetableService(timetablePath, overridesPath), timetablePath, overridesPath);
    }

    [BanjiTest]
    public static void 旧SchemaVersion1自动迁移为2且课程按通用()
    {
        var dir = TestFileBuilder.TempDir();
        var timetablePath = Path.Combine(dir, "timetable.json");
        var overridesPath = Path.Combine(dir, "overrides.json");
        File.WriteAllText(timetablePath,
            "{\"schemaVersion\":1,\"sourceFile\":\"old.csv\",\"periods\":[{\"index\":1,\"name\":\"第1节\",\"start\":\"08:00:00\",\"end\":\"08:45:00\"}]," +
            "\"breaks\":[],\"days\":{\"monday\":[{\"periodIndex\":1,\"course\":\"语文\"}]}}");

        var service = new TimetableService(timetablePath, overridesPath);
        Assert.Equal(2, service.Current.SchemaVersion);
        Assert.True(service.Current.Periods.Count >= 1, "迁移后应有节次数据");
        var entry = service.Current.EntryAt(DayOfWeek.Monday, 1, WeekType.All);
        Assert.NotNull(entry);
        Assert.Equal("语文", entry!.Course);

        // 迁移后应写回新 SchemaVersion 2 文件（含 weeks 字段）
        var json = File.ReadAllText(timetablePath, System.Text.Encoding.UTF8);
        Assert.True(json.Contains("\"schemaVersion\": 2"), "应保存 schemaVersion 2：" + json[..Math.Min(120, json.Length)]);
        Assert.True(json.Contains("\"weeks\": \"all\""), "旧课程应写为 all");
    }

    [BanjiTest]
    public static void 单双周课程持久化round_trip()
    {
        var (service, timetablePath, overridesPath) = CreateService();
        service.SetEntry(DayOfWeek.Monday, 1, WeekType.All, "语文");
        service.SetEntry(DayOfWeek.Monday, 1, WeekType.Odd, "数学");
        service.SetEntry(DayOfWeek.Monday, 1, WeekType.Even, "英语");
        service.SetEntry(DayOfWeek.Tuesday, 2, WeekType.All, "物理");

        var reloaded = new TimetableService(timetablePath, overridesPath);
        Assert.Equal("数学", reloaded.Current.CourseAt(DayOfWeek.Monday, 1, WeekParity.Odd));
        Assert.Equal("英语", reloaded.Current.CourseAt(DayOfWeek.Monday, 1, WeekParity.Even));
        Assert.Equal("物理", reloaded.Current.CourseAt(DayOfWeek.Tuesday, 2, WeekParity.Odd));
        Assert.Equal(2, reloaded.Current.SchemaVersion);
    }

    [BanjiTest]
    public static void 编辑通用不会影响单双专用条目()
    {
        var (service, _, _) = CreateService();
        service.SetEntry(DayOfWeek.Monday, 1, WeekType.Odd, "数学");
        service.SetCourse(DayOfWeek.Monday, 1, "语文");

        Assert.Equal("语文", service.Current.EntryAt(DayOfWeek.Monday, 1, WeekType.All)!.Course);
        Assert.Equal("数学", service.Current.EntryAt(DayOfWeek.Monday, 1, WeekType.Odd)!.Course);
        Assert.Equal("数学", service.Current.CourseAt(DayOfWeek.Monday, 1, WeekParity.Odd));
        Assert.Equal("语文", service.Current.CourseAt(DayOfWeek.Monday, 1, WeekParity.Even));
    }

    [BanjiTest]
    public static void 服务层复制清空恢复工具()
    {
        var (service, _, _) = CreateService();
        service.SetCourse(DayOfWeek.Monday, 1, "语文");
        service.SetCourse(DayOfWeek.Tuesday, 1, "数学");

        Assert.Equal(2, service.CopyAllTo(WeekType.Odd));
        Assert.Equal(2, service.CopyAllTo(WeekType.Even));
        Assert.Equal(2, service.Current.Days.Values.SelectMany(l => l).Count(e => e.Weeks == WeekType.Odd));

        Assert.Equal(2, service.ClearWeek(WeekType.Even));
        Assert.Equal(0, service.Current.Days.Values.SelectMany(l => l).Count(e => e.Weeks == WeekType.Even));

        Assert.True(service.RestoreCellToAll(DayOfWeek.Monday, 1));
        Assert.Null(service.Current.EntryAt(DayOfWeek.Monday, 1, WeekType.Odd));

        Assert.Equal(1, service.RestoreAllToAll());
        Assert.Equal(0, service.Current.Days.Values.SelectMany(l => l).Count(e => e.Weeks != WeekType.All));
    }

    [BanjiTest]
    public static void 删除节次时单双周条目同步迁移()
    {
        var (service, _, _) = CreateService();
        service.SetEntry(DayOfWeek.Monday, 3, WeekType.All, "语文");
        service.SetEntry(DayOfWeek.Monday, 3, WeekType.Odd, "数学");

        // 删除第 1 节：原第 3 节 → 新第 2 节
        var remaining = service.Current.Periods
            .Where(p => p.Index != 1)
            .Select(p => p.Clone())
            .ToList();
        service.SetPeriods(remaining);

        Assert.Equal(7, service.Current.Periods.Count);
        Assert.Equal("语文", service.Current.EntryAt(DayOfWeek.Monday, 2, WeekType.All)?.Course);
        Assert.Equal("数学", service.Current.EntryAt(DayOfWeek.Monday, 2, WeekType.Odd)?.Course);
        Assert.Equal("数学", service.Current.CourseAt(DayOfWeek.Monday, 2, WeekParity.Odd));
    }

    [BanjiTest]
    public static void SetTimetable去重并保留最后一条()
    {
        var (service, _, _) = CreateService();
        var tt = service.Current.Clone();
        tt.Days[DayOfWeek.Monday] = new List<TimetableEntry>
        {
            new() { PeriodIndex = 1, Course = "旧", Weeks = WeekType.All },
            new() { PeriodIndex = 1, Course = "新", Weeks = WeekType.All }
        };
        service.SetTimetable(tt);
        Assert.Equal("新", service.Current.EntryAt(DayOfWeek.Monday, 1, WeekType.All)!.Course);
        Assert.Equal(1, service.Current.Days[DayOfWeek.Monday].Count(e => e.PeriodIndex == 1));
    }

    [BanjiTest]
    public static void 导入器结果写入服务后周类型保留()
    {
        var (service, timetablePath, overridesPath) = CreateService();
        var file = TestFileBuilder.WriteCsv("parity-service.csv",
            new[] { "星期", "节次", "课程", "开始时间", "结束时间", "周次" },
            new[] { "周一", "第1节", "语文", "08:00", "08:45", "通用" },
            new[] { "周一", "第1节", "数学", "08:00", "08:45", "单" },
            new[] { "周一", "第1节", "英语", "08:00", "08:45", "双" });

        var imported = service.Import(file);
        Assert.Equal(2, imported.SchemaVersion);
        Assert.Equal("数学", imported.CourseAt(DayOfWeek.Monday, 1, WeekParity.Odd));
        Assert.Equal("英语", imported.CourseAt(DayOfWeek.Monday, 1, WeekParity.Even));

        var reloaded = new TimetableService(timetablePath, overridesPath);
        Assert.Equal("数学", reloaded.Current.CourseAt(DayOfWeek.Monday, 1, WeekParity.Odd));
        Assert.Equal("语文", reloaded.Current.EntryAt(DayOfWeek.Monday, 1, WeekType.All)!.Course);
    }
}
