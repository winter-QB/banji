using Banji.Core.Models;
using Banji.Core.Schedule;
using Banji.Core.Timetable;

namespace Banji.Tests;

/// <summary>
/// v1.7.0「临时修改当天时间表」测试：DayOverride.PeriodsOverride 优先于周课表时段，
/// 覆盖缩短课时/提前放学、与临时换课/取消叠加、跨日自动恢复、空覆盖等于无覆盖、
/// 排序与 Index 连续、周末调休、上下课边界、持久化 round-trip、旧 overrides.json 兼容。
/// </summary>
public static class SchedulePeriodsOverrideTests
{
    private static readonly DateOnly MondayDate = DateOnly.FromDateTime(ScheduleTestData.Monday); // 2026-11-02 周一
    private static readonly DateOnly TuesdayDate = MondayDate.AddDays(1);

    private static ClassPeriod Period(int index, int startHour, int startMinute, int endHour, int endMinute, string? name = null)
        => new()
        {
            Index = index,
            Name = name ?? $"第{index}节",
            Start = new TimeSpan(startHour, startMinute, 0),
            End = new TimeSpan(endHour, endMinute, 0)
        };

    /// <summary>缩短为两节：08:00-08:20、08:30-08:50（原课表 8 节到 17:30）。</summary>
    private static List<ClassPeriod> ShortDay() => new()
    {
        Period(1, 8, 0, 8, 20),
        Period(2, 8, 30, 8, 50)
    };

    private static (ScheduleService Service, FakeTimetableService Timetable, ScheduleFakeSettingsService Settings) Create()
        => ScheduleTestData.Create();

    // =========================================================================
    // 1. 当天时段覆盖：缩短课时 / 提前放学
    // =========================================================================

    [BanjiTest]
    public static void 当天时段覆盖_缩短课时与提前放学()
    {
        var (svc, tt, _) = Create();
        tt.SetOverride(new DayOverride { Date = MondayDate, Note = "半天课", PeriodsOverride = ShortDay() });

        var inClass = svc.SnapshotAt(ScheduleTestData.At(8, 10));
        Assert.Equal(SchedulePhase.InClass, inClass.Phase);
        Assert.NotNull(inClass.Current);
        Assert.Equal(1, inClass.Current!.PeriodIndex);
        Assert.Equal("语文", inClass.Current.Course);
        Assert.Equal(ScheduleTestData.At(8, 0), inClass.FirstStart);
        Assert.Equal(ScheduleTestData.At(8, 50), inClass.SchoolEnd);
        Assert.Equal(TimeSpan.FromMinutes(40), inClass.UntilSchoolEnd!.Value);
        Assert.Equal(2, inClass.Today.Count);
        Assert.Equal(2, inClass.PeriodsRemaining);
        Assert.NotNull(inClass.Next);
        Assert.Equal(2, inClass.Next!.PeriodIndex);
        Assert.Equal(TimeSpan.FromMinutes(20), inClass.UntilNextStart!.Value);

        // 8:50 之后按覆盖放学（常规课表 17:30 才放学）
        var after = svc.SnapshotAt(ScheduleTestData.At(9, 0));
        Assert.Equal(SchedulePhase.AfterSchool, after.Phase);
        Assert.Null(after.Current);
        Assert.Null(after.Next);
        Assert.Null(after.UntilSchoolEnd);
        Assert.Equal(0, after.PeriodsRemaining);
        Assert.Equal(2, after.Today.Count);
    }

    [BanjiTest]
    public static void 当天时段覆盖_下一节与强调项使用覆盖时间()
    {
        var (svc, tt, _) = Create();
        tt.SetOverride(new DayOverride { Date = MondayDate, PeriodsOverride = ShortDay() });

        var beforeSchool = svc.SnapshotAt(ScheduleTestData.At(7, 30));
        Assert.Equal(SchedulePhase.BeforeSchool, beforeSchool.Phase);
        Assert.Equal(ScheduleTestData.At(8, 0), beforeSchool.Next!.Start);
        Assert.True(ReferenceEquals(beforeSchool.Highlighted, beforeSchool.Next));

        var breakBetween = svc.SnapshotAt(ScheduleTestData.At(8, 25));
        Assert.Equal(SchedulePhase.Break, breakBetween.Phase);
        Assert.Equal(TimeSpan.FromMinutes(5), breakBetween.UntilNextStart!.Value);
    }

    // =========================================================================
    // 2. 与临时换课 / 取消叠加（课程替换与取消优先于时段）
    // =========================================================================

    [BanjiTest]
    public static void 时段覆盖与临时换课取消叠加()
    {
        var (svc, tt, _) = Create();
        var dayOverride = new DayOverride
        {
            Date = MondayDate,
            Note = "活动日",
            PeriodsOverride = new List<ClassPeriod>
            {
                Period(1, 8, 0, 8, 20),
                Period(2, 8, 30, 8, 50),
                Period(3, 9, 0, 9, 20)
            },
            CourseReplacements = { [1] = "数学竞赛" },
            CancelledPeriods = { 2 }
        };
        tt.SetOverride(dayOverride);

        var atFirst = svc.SnapshotAt(ScheduleTestData.At(8, 10));
        Assert.Equal(SchedulePhase.InClass, atFirst.Phase);
        Assert.Equal("数学竞赛", atFirst.Current!.Course); // 替换按覆盖后的第 1 节生效
        Assert.Equal("第1节", atFirst.Current.PeriodName);

        // 第 2 节被取消：不在 Today 中，8:30-8:50 视为课间
        Assert.Equal(2, atFirst.Today.Count);
        Assert.False(atFirst.Today.Any(c => c.PeriodIndex == 2));
        var duringCancelled = svc.SnapshotAt(ScheduleTestData.At(8, 40));
        Assert.Equal(SchedulePhase.Break, duringCancelled.Phase);
        Assert.Equal(3, duringCancelled.Next!.PeriodIndex);
        Assert.Equal("英语", duringCancelled.Next.Course); // 周一第 3 节常规课程
        Assert.Equal(ScheduleTestData.At(9, 20), duringCancelled.SchoolEnd);
    }

    [BanjiTest]
    public static void 时段覆盖里替换成空课()
    {
        var (svc, tt, _) = Create();
        tt.SetOverride(new DayOverride
        {
            Date = MondayDate,
            PeriodsOverride = ShortDay(),
            CourseReplacements = { [2] = null } // 改成空课
        });

        var snap = svc.SnapshotAt(ScheduleTestData.At(8, 40));
        Assert.Equal(SchedulePhase.InClass, snap.Phase);
        Assert.Equal(2, snap.Current!.PeriodIndex);
        Assert.Equal("", snap.Current.Course);
        Assert.Equal("未设置", snap.Current.CourseDisplay);
    }

    // =========================================================================
    // 3. 只在当天生效，次日自动恢复
    // =========================================================================

    [BanjiTest]
    public static void 时段覆盖只在当天生效_次日恢复()
    {
        var (svc, tt, _) = Create();
        tt.SetOverride(new DayOverride { Date = MondayDate, PeriodsOverride = ShortDay() });

        Assert.Equal(ScheduleTestData.At(8, 50), svc.SnapshotAt(ScheduleTestData.At(8, 10)).SchoolEnd);

        // 次日（周二）自动恢复常规 8 节
        var nextDay = svc.SnapshotAt(ScheduleTestData.Monday.AddDays(1).AddHours(8).AddMinutes(10));
        Assert.Equal(SchedulePhase.InClass, nextDay.Phase);
        Assert.Equal(8, nextDay.Today.Count);
        Assert.Equal(ScheduleTestData.Monday.AddDays(1).AddHours(17).AddMinutes(30), nextDay.SchoolEnd);
        Assert.Equal("数学", nextDay.Current!.Course);
        Assert.Null(tt.GetOverride(TuesdayDate));
    }

    // =========================================================================
    // 4. 空覆盖 / 非法覆盖等于无覆盖
    // =========================================================================

    [BanjiTest]
    public static void 空覆盖等于无覆盖()
    {
        var (svc, tt, _) = Create();
        tt.SetOverride(new DayOverride { Date = MondayDate, PeriodsOverride = new List<ClassPeriod>() });

        var snap = svc.SnapshotAt(ScheduleTestData.At(8, 10));
        Assert.Equal(8, snap.Today.Count);
        Assert.Equal(ScheduleTestData.At(17, 30), snap.SchoolEnd);

        // 只有非法时段（End <= Start）同样视为无覆盖
        var (svc2, tt2, _) = Create();
        tt2.SetOverride(new DayOverride
        {
            Date = MondayDate,
            PeriodsOverride = new List<ClassPeriod> { Period(1, 9, 0, 8, 0) }
        });
        Assert.Equal(ScheduleTestData.At(17, 30), svc2.SnapshotAt(ScheduleTestData.At(8, 10)).SchoolEnd);
    }

    // =========================================================================
    // 5. 覆盖时段按 Start 排序、Index 连续 1..N
    // =========================================================================

    [BanjiTest]
    public static void 覆盖时段排序并重排Index()
    {
        var (svc, tt, _) = Create();
        tt.SetOverride(new DayOverride
        {
            Date = MondayDate,
            PeriodsOverride = new List<ClassPeriod>
            {
                Period(9, 8, 30, 8, 50, "第二节"), // 乱序 + 乱编号
                Period(5, 8, 0, 8, 20, "第一节")
            },
            CourseReplacements = { [1] = "数学竞赛" } // 按重排后的第 1 节
        });

        var snap = svc.SnapshotAt(ScheduleTestData.At(8, 10));
        Assert.Equal(2, snap.Today.Count);
        Assert.Equal(1, snap.Today[0].PeriodIndex);
        Assert.Equal("第一节", snap.Today[0].PeriodName);
        Assert.Equal("数学竞赛", snap.Today[0].Course);
        Assert.Equal(2, snap.Today[1].PeriodIndex);
        Assert.Equal("第二节", snap.Today[1].PeriodName);
        Assert.Equal(ScheduleTestData.At(8, 50), snap.SchoolEnd);
    }

    // =========================================================================
    // 6. 周末调休补课：当天时段覆盖让周末变成上课日
    // =========================================================================

    [BanjiTest]
    public static void 周末时段覆盖视为上课日()
    {
        var saturday = DateOnly.FromDateTime(ScheduleTestData.Saturday); // 2026-10-31 周六
        var (svc, tt, _) = Create();
        Assert.Equal(SchedulePhase.NoTimetable, svc.SnapshotAt(ScheduleTestData.AtSat(8, 10)).Phase);

        tt.SetOverride(new DayOverride
        {
            Date = saturday,
            Note = "调休补课",
            PeriodsOverride = ShortDay(),
            CourseReplacements = { [1] = "补课数学" }
        });

        var snap = svc.SnapshotAt(ScheduleTestData.AtSat(8, 10));
        Assert.Equal(SchedulePhase.InClass, snap.Phase);
        Assert.Equal("补课数学", snap.Current!.Course);
        Assert.Equal(ScheduleTestData.AtSat(8, 50), snap.SchoolEnd);
        Assert.Equal(2, snap.Today.Count);
    }

    // =========================================================================
    // 7. UpcomingBoundaries 使用当天覆盖时段
    // =========================================================================

    [BanjiTest]
    public static void 上下课边界使用当天覆盖时段()
    {
        var (svc, tt, _) = Create();
        tt.SetOverride(new DayOverride { Date = MondayDate, PeriodsOverride = ShortDay() });

        var boundaries = svc.UpcomingBoundaries(ScheduleTestData.At(7, 0), 20);
        Assert.True(boundaries.Count >= 4);

        var mondayBoundaries = boundaries.Where(b => b.Time.Date == ScheduleTestData.Monday.Date).ToList();
        Assert.Equal(4, mondayBoundaries.Count); // 8:00 上课、8:20 下课、8:30 上课、8:50 放学
        Assert.Equal(ScheduleTestData.At(8, 0), mondayBoundaries[0].Time);
        Assert.Equal(ClassBoundaryKind.ClassStart, mondayBoundaries[0].Kind);
        Assert.True(mondayBoundaries[0].Title.Contains("语文"), mondayBoundaries[0].Title);
        Assert.Equal(ScheduleTestData.At(8, 20), mondayBoundaries[1].Time);
        Assert.Equal(ClassBoundaryKind.ClassEnd, mondayBoundaries[1].Kind);
        Assert.Equal(ScheduleTestData.At(8, 50), mondayBoundaries[3].Time);
        Assert.Equal(ClassBoundaryKind.SchoolEnd, mondayBoundaries[3].Kind);

        // 不会再有覆盖之外的常规时段边界（例如 10:00 第 3 节）
        Assert.False(mondayBoundaries.Any(b => b.Time > ScheduleTestData.At(8, 50)));
        // 次日恢复常规边界
        Assert.True(boundaries.Any(b => b.Time.Date == ScheduleTestData.Monday.Date.AddDays(1)
                                        && b.Time == ScheduleTestData.Monday.AddDays(1).AddHours(8)));
    }

    // =========================================================================
    // 8. 持久化 round-trip（TimetableService）
    // =========================================================================

    private static (TimetableService Service, string TimetablePath, string OverridesPath) CreateService()
    {
        var dir = TestFileBuilder.TempDir();
        var timetablePath = Path.Combine(dir, "timetable.json");
        var overridesPath = Path.Combine(dir, "overrides.json");
        var service = new TimetableService(timetablePath, overridesPath);
        service.SetTimetable(ScheduleTestData.StandardWeek());
        return (service, timetablePath, overridesPath);
    }

    [BanjiTest]
    public static void 时段覆盖持久化round_trip()
    {
        var (service, timetablePath, overridesPath) = CreateService();
        service.SetOverride(new DayOverride
        {
            Date = MondayDate,
            Note = "半天课",
            PeriodsOverride = ShortDay(),
            CourseReplacements = { [1] = "数学竞赛" }
        });

        var json = File.ReadAllText(overridesPath);
        Assert.True(json.Contains("periodsOverride"), "overrides.json 应写入 periodsOverride：" + json[..Math.Min(200, json.Length)]);

        var reloaded = new TimetableService(timetablePath, overridesPath);
        var loaded = reloaded.GetOverride(MondayDate);
        Assert.NotNull(loaded);
        Assert.Equal("半天课", loaded!.Note);
        Assert.NotNull(loaded.PeriodsOverride);
        Assert.Equal(2, loaded.PeriodsOverride!.Count);
        Assert.Equal(TimeSpan.FromMinutes(20), loaded.PeriodsOverride[0].End - loaded.PeriodsOverride[0].Start);
        Assert.Equal("数学竞赛", loaded.CourseReplacements[1]);

        var settings = new ScheduleFakeSettingsService();
        var schedule = new ScheduleService(reloaded, settings);
        var snap = schedule.SnapshotAt(ScheduleTestData.At(8, 10));
        Assert.Equal("数学竞赛", snap.Current!.Course);
        Assert.Equal(ScheduleTestData.At(8, 50), snap.SchoolEnd);

        // 删除整天覆盖并持久化 → 次日/重载后恢复常规时段
        reloaded.RemoveOverride(MondayDate);
        var reloaded2 = new TimetableService(timetablePath, overridesPath);
        Assert.Null(reloaded2.GetOverride(MondayDate));
        var schedule2 = new ScheduleService(reloaded2, settings);
        Assert.Equal(ScheduleTestData.At(17, 30), schedule2.SnapshotAt(ScheduleTestData.At(8, 10)).SchoolEnd);
    }

    [BanjiTest]
    public static void 只清除时段覆盖保留换课与备注()
    {
        var (service, _, _) = CreateService();
        service.SetOverride(new DayOverride
        {
            Date = MondayDate,
            Note = "保留备注",
            PeriodsOverride = ShortDay(),
            CourseReplacements = { [1] = "数学竞赛" }
        });

        var loaded = service.GetOverride(MondayDate)!;
        loaded.PeriodsOverride = null;
        service.SetOverride(loaded);

        var cleared = service.GetOverride(MondayDate);
        Assert.NotNull(cleared);
        Assert.Null(cleared!.PeriodsOverride);
        Assert.Equal("保留备注", cleared.Note);
        Assert.Equal("数学竞赛", cleared.CourseReplacements[1]);

        var settings = new ScheduleFakeSettingsService();
        var snap = new ScheduleService(service, settings).SnapshotAt(ScheduleTestData.At(8, 10));
        Assert.Equal(ScheduleTestData.At(17, 30), snap.SchoolEnd); // 恢复常规时段
        Assert.Equal("数学竞赛", snap.Current!.Course);            // 换课仍在
    }

    [BanjiTest]
    public static void 空时段覆盖不会额外保存()
    {
        var (service, _, _) = CreateService();
        service.SetOverride(new DayOverride
        {
            Date = MondayDate,
            PeriodsOverride = new List<ClassPeriod>()
        });
        Assert.Null(service.GetOverride(MondayDate)); // 空覆盖 = 无覆盖 = 删除

        service.SetOverride(new DayOverride
        {
            Date = MondayDate,
            PeriodsOverride = new List<ClassPeriod> { Period(1, 9, 0, 8, 0) } // 非法时段
        });
        Assert.Null(service.GetOverride(MondayDate));
    }

    // =========================================================================
    // 9. 旧 overrides.json 兼容（没有 periodsOverride 字段）
    // =========================================================================

    [BanjiTest]
    public static void 旧overrides_json兼容且空覆盖被清理()
    {
        var dir = TestFileBuilder.TempDir();
        var timetablePath = Path.Combine(dir, "timetable.json");
        var overridesPath = Path.Combine(dir, "overrides.json");
        File.WriteAllText(overridesPath,
            "[" +
            "{\"date\":\"2026-11-02\",\"note\":null,\"updatedAt\":\"2026-01-01T00:00:00\"," +
            "\"courseReplacements\":{\"1\":\"数学竞赛\"},\"cancelledPeriods\":[2]}," +
            "{\"date\":\"2026-11-03\",\"note\":null,\"updatedAt\":\"2026-01-01T00:00:00\"," +
            "\"courseReplacements\":{},\"cancelledPeriods\":[],\"periodsOverride\":[]}" +
            "]");

        var service = new TimetableService(timetablePath, overridesPath);
        var legacy = service.GetOverride(MondayDate);
        Assert.NotNull(legacy);
        Assert.Null(legacy!.PeriodsOverride);
        Assert.Equal("数学竞赛", legacy.CourseReplacements[1]);
        Assert.Null(service.GetOverride(TuesdayDate)); // 空覆盖项在加载时被清理

        var settings = new ScheduleFakeSettingsService();
        var schedule = new ScheduleService(service, settings);
        var snap = schedule.SnapshotAt(ScheduleTestData.At(8, 10));
        Assert.Equal("数学竞赛", snap.Current!.Course);
        Assert.Equal(ScheduleTestData.At(17, 30), snap.SchoolEnd); // 无时段覆盖：常规时段
        Assert.Equal(7, snap.Today.Count);                         // 第 2 节被取消
    }

    // =========================================================================
    // 10. 解析器纯函数
    // =========================================================================

    [BanjiTest]
    public static void 解析器优先覆盖并归一化()
    {
        var timetable = ScheduleTestData.StandardWeek();
        Assert.Equal(8, DayPeriodsResolver.Resolve(timetable, null).Count);

        var overrideData = new DayOverride { PeriodsOverride = ShortDay() };
        var periods = DayPeriodsResolver.Resolve(timetable, overrideData);
        Assert.Equal(2, periods.Count);
        Assert.Equal(1, periods[0].Index);
        Assert.Equal(2, periods[1].Index);
        Assert.True(DayPeriodsResolver.HasPeriodsOverride(overrideData));
        Assert.False(DayPeriodsResolver.HasPeriodsOverride(new DayOverride()));
        Assert.False(DayPeriodsResolver.HasPeriodsOverride(new DayOverride { PeriodsOverride = new List<ClassPeriod>() }));

        // 不修改原始覆盖数据
        overrideData.PeriodsOverride!.Reverse();
        Assert.Equal(2, DayPeriodsResolver.Resolve(timetable, overrideData).Count);
        Assert.Equal(2, overrideData.PeriodsOverride.Count);
    }
}
