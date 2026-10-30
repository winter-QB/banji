using Banji.Core.Abstractions;
using Banji.Core.Models;
using Banji.Core.Schedule;
using Banji.Core.Settings;

namespace Banji.Tests;

// ============================================================================
// 测试替身：内存版课表 / 设置服务（不落盘、可注入任意时间）
// ============================================================================

internal sealed class FakeTimetableService : ITimetableService
{
    private readonly List<DayOverride> _overrides = new();

    public WeekTimetable Current { get; set; } = new();
    public IReadOnlyList<DayOverride> Overrides => _overrides;
    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public TimetableImportPreview PreviewImport(string filePath) => throw new NotSupportedException();

    public WeekTimetable Import(string filePath, string? sheetName = null) => throw new NotSupportedException();

    public void SetTimetable(WeekTimetable timetable)
    {
        Current = timetable;
        RaiseChanged();
    }

    public void SetPeriods(IEnumerable<ClassPeriod> periods)
    {
        Current.Periods = periods.ToList();
        RaiseChanged();
    }

    public void SetBreaks(IEnumerable<BreakSegment> breaks)
    {
        Current.Breaks = breaks.ToList();
        RaiseChanged();
    }

    public void SetCourse(DayOfWeek day, int periodIndex, string course)
    {
        if (!Current.Days.TryGetValue(day, out var list))
            Current.Days[day] = list = new List<TimetableEntry>();
        var entry = list.FirstOrDefault(e => e.PeriodIndex == periodIndex);
        if (entry is null) list.Add(new TimetableEntry { PeriodIndex = periodIndex, Course = course });
        else entry.Course = course;
        RaiseChanged();
    }

    public DayOverride? GetOverride(DateOnly date) => _overrides.FirstOrDefault(o => o.Date == date);

    public void SetOverride(DayOverride overrideData)
    {
        _overrides.RemoveAll(o => o.Date == overrideData.Date);
        _overrides.Add(overrideData);
        RaiseChanged();
    }

    public void RemoveOverride(DateOnly date)
    {
        _overrides.RemoveAll(o => o.Date == date);
        RaiseChanged();
    }

    public void Save()
    {
    }
}

internal sealed class ScheduleFakeSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();
    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Current.Normalize();
        RaiseChanged();
    }

    public void Save()
    {
    }

    public void ResetToDefaults()
    {
        // Fake 不做真正重置，测试按需直接改 Current
        RaiseChanged();
    }
}

// ============================================================================
// 共享测试数据
// ============================================================================

internal static class ScheduleTestData
{
    // 2026-10-30 是周五，so 2026-11-02 是周一。
    public static readonly DateTime Monday = new(2026, 11, 2);
    public static readonly DateTime Saturday = new(2026, 10, 31);

    public static DateTime At(int hour, int minute, int second = 0) => Monday.AddHours(hour).AddMinutes(minute).AddSeconds(second);

    public static DateTime AtSat(int hour, int minute) => Saturday.AddHours(hour).AddMinutes(minute);

    /// <summary>周一至周五 8 节课（45 分钟，10 分钟课间），含大课间 + 午休。</summary>
    public static WeekTimetable StandardWeek()
    {
        var tt = new WeekTimetable { SourceFile = "测试.csv" };
        var times = new (int Sh, int Sm, int Eh, int Em)[]
        {
            (8, 0, 8, 45), (8, 55, 9, 40), (10, 0, 10, 45), (10, 55, 11, 40),
            (14, 0, 14, 45), (14, 55, 15, 40), (15, 50, 16, 35), (16, 45, 17, 30)
        };
        for (var i = 0; i < times.Length; i++)
        {
            var t = times[i];
            tt.Periods.Add(new ClassPeriod
            {
                Index = i + 1,
                Name = $"第{i + 1}节",
                Start = new TimeSpan(t.Sh, t.Sm, 0),
                End = new TimeSpan(t.Eh, t.Em, 0)
            });
        }
        tt.Breaks.Add(new BreakSegment { Name = "大课间", Start = new TimeSpan(9, 40, 0), End = new TimeSpan(10, 0, 0) });
        tt.Breaks.Add(new BreakSegment { Name = "午休", Start = new TimeSpan(11, 40, 0), End = new TimeSpan(14, 0, 0) });

        var week = new Dictionary<DayOfWeek, string[]>
        {
            [DayOfWeek.Monday] = new[] { "语文", "数学", "英语", "物理", "化学", "体育", "思想政治", "历史" },
            [DayOfWeek.Tuesday] = new[] { "数学", "英语", "语文", "生物", "历史", "体育", "物理", "地理" },
            [DayOfWeek.Wednesday] = new[] { "英语", "语文", "数学", "化学", "物理", "信息技术", "音乐", "自习" },
            [DayOfWeek.Thursday] = new[] { "物理", "数学", "语文", "英语", "生物", "体育", "化学", "历史" },
            [DayOfWeek.Friday] = new[] { "语文", "英语", "数学", "思想政治", "地理", "美术", "班会", "生涯规划" }
        };
        foreach (var (day, courses) in week)
        {
            tt.Days[day] = courses.Select((c, i) => new TimetableEntry { PeriodIndex = i + 1, Course = c }).ToList();
        }
        return tt;
    }

    public static (ScheduleService Service, FakeTimetableService Timetable, ScheduleFakeSettingsService Settings) Create()
    {
        var timetable = new FakeTimetableService { Current = StandardWeek() };
        var settings = new ScheduleFakeSettingsService();
        return (new ScheduleService(timetable, settings), timetable, settings);
    }
}

// ============================================================================
// IScheduleService.SnapshotAt 纯函数测试
// ============================================================================

public static class ScheduleSnapshotTests
{
    [BanjiTest]
    public static void 上课前快照_下一节与倒计时()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var snap = svc.SnapshotAt(ScheduleTestData.At(7, 30));

        Assert.Equal(SchedulePhase.BeforeSchool, snap.Phase);
        Assert.Null(snap.Current);
        Assert.NotNull(snap.Next);
        Assert.Equal(1, snap.Next!.PeriodIndex);
        Assert.Equal("语文", snap.Next.Course);
        Assert.Equal(TimeSpan.FromMinutes(30), snap.UntilNextStart!.Value);
        Assert.Equal(TimeSpan.FromMinutes(30), svc.SnapshotAt(ScheduleTestData.At(7, 30)).PrimaryCountdownSpan());
        Assert.Equal(8, snap.PeriodsRemaining);
        Assert.Equal(ScheduleTestData.At(8, 0), snap.FirstStart);
        Assert.Equal(ScheduleTestData.At(17, 30), snap.SchoolEnd);
        Assert.True(ReferenceEquals(snap.Highlighted, snap.Next), "上课前应强调下一节");
    }

    [BanjiTest]
    public static void 正在上课快照()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var snap = svc.SnapshotAt(ScheduleTestData.At(8, 5));

        Assert.Equal(SchedulePhase.InClass, snap.Phase);
        Assert.NotNull(snap.Current);
        Assert.Equal(1, snap.Current!.PeriodIndex);
        Assert.Equal("语文", snap.Current.Course);
        Assert.Equal("第1节", snap.Current.PeriodName);
        Assert.Equal(TimeSpan.FromMinutes(40), snap.UntilCurrentEnd!.Value);
        Assert.Equal(8, snap.PeriodsRemaining);
        Assert.Equal(1, snap.Today.Count(c => c.Status == CourseStatus.Active));
        Assert.Equal(1, snap.Today.Count(c => c.Status == CourseStatus.Next));
        Assert.Equal(6, snap.Today.Count(c => c.Status == CourseStatus.Upcoming));
        Assert.True(ReferenceEquals(snap.Highlighted, snap.Current), "上课 10 分钟内仍强调当前节");
    }

    [BanjiTest]
    public static void 强调规则_下一节开始10分钟内仍强调之后转普通()
    {
        var (svc, _, _) = ScheduleTestData.Create();

        // 开始后 9:59 —— 仍强调当前节
        var inWindow = svc.SnapshotAt(ScheduleTestData.At(8, 9, 59));
        Assert.True(ReferenceEquals(inWindow.Highlighted, inWindow.Current), "9:59 仍应强调当前节");

        // 正好 10:00 —— 不再算“10 分钟内”，转为强调下一节
        var boundary = svc.SnapshotAt(ScheduleTestData.At(8, 10, 0));
        Assert.True(ReferenceEquals(boundary.Highlighted, boundary.Next), "10:00 起应转为强调下一节");
        Assert.Equal(2, boundary.Highlighted!.PeriodIndex);

        // 上课前也是强调下一节
        var before = svc.SnapshotAt(ScheduleTestData.At(7, 0));
        Assert.True(ReferenceEquals(before.Highlighted, before.Next));
    }

    [BanjiTest]
    public static void 课间与大课间快照()
    {
        var (svc, _, _) = ScheduleTestData.Create();

        var brk = svc.SnapshotAt(ScheduleTestData.At(8, 50));
        Assert.Equal(SchedulePhase.Break, brk.Phase);
        Assert.Null(brk.Current);
        Assert.Equal(2, brk.Next!.PeriodIndex);
        Assert.Equal(TimeSpan.FromMinutes(5), brk.UntilNextStart!.Value);

        var big = svc.SnapshotAt(ScheduleTestData.At(9, 50));
        Assert.Equal(SchedulePhase.Break, big.Phase);
        Assert.Equal(TimeSpan.FromMinutes(10), big.UntilNextStart!.Value);
    }

    [BanjiTest]
    public static void 午休与放学后快照()
    {
        var (svc, _, _) = ScheduleTestData.Create();

        var lunch = svc.SnapshotAt(ScheduleTestData.At(12, 30));
        Assert.Equal(SchedulePhase.LunchBreak, lunch.Phase);
        Assert.Null(lunch.Current);
        Assert.Equal(5, lunch.Next!.PeriodIndex);
        Assert.Equal(TimeSpan.FromMinutes(90), lunch.UntilNextStart!.Value);
        Assert.Equal(4, lunch.PeriodsRemaining);

        var after = svc.SnapshotAt(ScheduleTestData.At(18, 0));
        Assert.Equal(SchedulePhase.AfterSchool, after.Phase);
        Assert.Null(after.Current);
        Assert.Null(after.Next);
        Assert.Null(after.UntilSchoolEnd);
        Assert.Equal(0, after.PeriodsRemaining);
        Assert.Equal(8, after.Today.Count(c => c.Status == CourseStatus.Past));
        Assert.Equal(ScheduleTestData.At(17, 30), after.SchoolEnd);
    }

    [BanjiTest]
    public static void 最后一节课与放学边界()
    {
        var (svc, _, _) = ScheduleTestData.Create();

        var last = svc.SnapshotAt(ScheduleTestData.At(17, 0));
        Assert.Equal(SchedulePhase.InClass, last.Phase);
        Assert.Equal(8, last.Current!.PeriodIndex);
        Assert.Null(last.Next);
        Assert.Null(last.UntilNextStart);
        Assert.Equal(TimeSpan.FromMinutes(30), last.UntilSchoolEnd!.Value);
        Assert.Equal(1, last.PeriodsRemaining);

        var end = svc.SnapshotAt(ScheduleTestData.At(17, 30));
        Assert.Equal(SchedulePhase.AfterSchool, end.Phase);
        Assert.Null(end.Current);
    }

    [BanjiTest]
    public static void 周末按无课表处理()
    {
        var (svc, _, _) = ScheduleTestData.Create();
        var snap = svc.SnapshotAt(ScheduleTestData.AtSat(10, 0));
        Assert.Equal(SchedulePhase.NoTimetable, snap.Phase);
        Assert.Null(snap.Current);
        Assert.Null(snap.Next);
        Assert.True(snap.HasTimetable, "周末仍有课表数据，只是不排课");
        Assert.Equal(0, snap.Today.Count);
        Assert.Equal(0, snap.PeriodsRemaining);
    }

    [BanjiTest]
    public static void 周末排了课则按上课日处理()
    {
        // 周六上课 / 调休补课：课表里确实给周六排了课程
        var (svc, tt, _) = ScheduleTestData.Create();
        tt.SetCourse(DayOfWeek.Saturday, 1, "语文");
        var snap = svc.SnapshotAt(new DateTime(2026, 10, 31, 8, 5, 0));
        Assert.Equal(SchedulePhase.InClass, snap.Phase);
        Assert.Equal("语文", snap.Current!.Course);

        // 没有给周末排课的普通周末仍然无课表
        var (svc2, _, _) = ScheduleTestData.Create();
        Assert.Equal(SchedulePhase.NoTimetable, svc2.SnapshotAt(new DateTime(2026, 10, 31, 9, 0, 0)).Phase);
        Assert.Equal(SchedulePhase.NoTimetable, svc2.SnapshotAt(new DateTime(2026, 11, 1, 9, 0, 0)).Phase);
    }

    [BanjiTest]
    public static void 空课表返回无课表()
    {
        var (svc, tt, _) = ScheduleTestData.Create();
        tt.Current = new WeekTimetable();
        var snap = svc.SnapshotAt(ScheduleTestData.At(9, 0));
        Assert.Equal(SchedulePhase.NoTimetable, snap.Phase);
        Assert.False(snap.HasTimetable);
        Assert.Null(snap.Current);
        Assert.Equal(0, snap.Today.Count);
    }

    [BanjiTest]
    public static void 铃声误差正负60秒只在显示与触发时偏移()
    {
        var (svc, tt, st) = ScheduleTestData.Create();
        var originalStart = tt.Current.Periods[0].Start;
        var originalEnd = tt.Current.Periods[0].End;

        // 默认 0：8:00:30 已在上课
        Assert.Equal(SchedulePhase.InClass, svc.SnapshotAt(ScheduleTestData.At(8, 0, 30)).Phase);

        // +60：所有时间后移一分钟，8:00:30 还在课前，08:01 才上课
        st.Current.BellOffsetSeconds = 60;
        var plus = svc.SnapshotAt(ScheduleTestData.At(8, 0, 30));
        Assert.Equal(SchedulePhase.BeforeSchool, plus.Phase);
        Assert.Equal(ScheduleTestData.At(8, 1), plus.Next!.Start);
        Assert.Equal(ScheduleTestData.At(8, 46), plus.Next.End);
        Assert.Equal(60, plus.BellOffsetSeconds);

        // -60：时间前移一分钟，07:59:30 已经在上课
        st.Current.BellOffsetSeconds = -60;
        var minus = svc.SnapshotAt(ScheduleTestData.At(7, 59, 30));
        Assert.Equal(SchedulePhase.InClass, minus.Phase);
        Assert.Equal(ScheduleTestData.At(7, 59), minus.Current!.Start);
        Assert.Equal(ScheduleTestData.At(8, 44), minus.Current.End);
        Assert.Equal(-60, minus.BellOffsetSeconds);

        // 底层课表时间不变
        Assert.Equal(originalStart, tt.Current.Periods[0].Start);
        Assert.Equal(originalEnd, tt.Current.Periods[0].End);

        // 越界值收敛到 ±60
        st.Current.BellOffsetSeconds = 999;
        Assert.Equal(60, svc.SnapshotAt(ScheduleTestData.At(8, 0, 30)).BellOffsetSeconds);
    }

    [BanjiTest]
    public static void 临时换课替换与取消()
    {
        var (svc, tt, _) = ScheduleTestData.Create();
        var date = DateOnly.FromDateTime(ScheduleTestData.Monday);
        tt.SetOverride(new DayOverride
        {
            Date = date,
            Note = "临时调课",
            CourseReplacements = { [1] = "数学竞赛" },
            CancelledPeriods = { 2 }
        });

        // 第 1 节被替换
        var first = svc.SnapshotAt(ScheduleTestData.At(8, 5));
        Assert.Equal("数学竞赛", first.Current!.Course);

        // 第 2 节被取消：该时间段不上课，下一节直接跳到第 3 节
        var second = svc.SnapshotAt(ScheduleTestData.At(9, 0));
        Assert.Equal(SchedulePhase.Break, second.Phase);
        Assert.Null(second.Current);
        Assert.Equal(3, second.Next!.PeriodIndex);

        // 当天列表不包含被取消的节次
        Assert.Equal(7, second.Today.Count);
        Assert.False(second.Today.Any(o => o.PeriodIndex == 2));

        // 没有覆盖的日期保持周课表
        var nextDay = svc.SnapshotAt(ScheduleTestData.Monday.AddDays(1).AddHours(8).AddMinutes(5));
        Assert.Equal("数学", nextDay.Current!.Course);

        // 删除覆盖后恢复
        tt.RemoveOverride(date);
        var restored = svc.SnapshotAt(ScheduleTestData.At(8, 5));
        Assert.Equal("语文", restored.Current!.Course);
    }

    [BanjiTest]
    public static void 临时换课改成空课()
    {
        var (svc, tt, _) = ScheduleTestData.Create();
        tt.SetOverride(new DayOverride
        {
            Date = DateOnly.FromDateTime(ScheduleTestData.Monday),
            CourseReplacements = { [3] = null }
        });
        var snap = svc.SnapshotAt(ScheduleTestData.At(10, 10));
        Assert.Equal(SchedulePhase.InClass, snap.Phase);
        Assert.True(snap.Current!.HasCourse == false, "替换为 null 应显示未设置");
        Assert.Equal("未设置", snap.Current.CourseDisplay);
    }

    [BanjiTest]
    public static void 自定义课堂时段精确到秒()
    {
        var (svc, tt, _) = ScheduleTestData.Create();
        tt.SetPeriods(new[]
        {
            new ClassPeriod { Index = 1, Name = "早读", Start = new TimeSpan(8, 0, 30), End = new TimeSpan(8, 45, 15) },
            new ClassPeriod { Index = 2, Name = "第一节", Start = new TimeSpan(8, 50, 0), End = new TimeSpan(9, 35, 0) }
        });
        // SetPeriods 会按开始时间排序并重排 Index
        Assert.Equal(2, tt.Current.Periods.Count);

        Assert.Equal(SchedulePhase.BeforeSchool, svc.SnapshotAt(ScheduleTestData.At(8, 0, 29)).Phase);
        var inClass = svc.SnapshotAt(ScheduleTestData.At(8, 0, 30));
        Assert.Equal(SchedulePhase.InClass, inClass.Phase);
        Assert.Equal(new TimeSpan(8, 0, 30), inClass.Current!.Start.TimeOfDay);
        Assert.Equal(TimeSpan.FromSeconds(44 * 60 + 45), inClass.UntilCurrentEnd!.Value);

        var ended = svc.SnapshotAt(ScheduleTestData.At(8, 45, 15));
        Assert.Equal(SchedulePhase.Break, ended.Phase);
        Assert.Equal(2, ended.Next!.PeriodIndex);
    }

    [BanjiTest]
    public static void 已取消全部课程则当天无课()
    {
        var (svc, tt, _) = ScheduleTestData.Create();
        var ov = new DayOverride { Date = DateOnly.FromDateTime(ScheduleTestData.Monday) };
        for (var i = 1; i <= 8; i++) ov.CancelledPeriods.Add(i);
        tt.SetOverride(ov);
        var snap = svc.SnapshotAt(ScheduleTestData.At(9, 0));
        Assert.Equal(SchedulePhase.NoTimetable, snap.Phase);
        Assert.Equal(0, snap.Today.Count);
    }

    [BanjiTest]
    public static void 快照纯函数可重复调用且课表变化广播Changed()
    {
        var (svc, tt, _) = ScheduleTestData.Create();
        var raised = 0;
        svc.Changed += (_, _) => raised++;

        var a = svc.SnapshotAt(ScheduleTestData.At(8, 5));
        var b = svc.SnapshotAt(ScheduleTestData.At(8, 5));
        Assert.Equal(a.Phase, b.Phase);
        Assert.Equal(a.Current!.Course, b.Current!.Course);
        Assert.True(!ReferenceEquals(a.Current, b.Current), "每次快照应是独立对象");

        tt.SetCourse(DayOfWeek.Monday, 1, "自习");
        Assert.Equal(1, raised);
        Assert.Equal("自习", svc.SnapshotAt(ScheduleTestData.At(8, 5)).Current!.Course);
    }
}

internal static class ScheduleSnapshotTestExtensions
{
    /// <summary>快照主倒计时的原始 TimeSpan（用于断言精度，Text 会取整到秒）。</summary>
    public static TimeSpan PrimaryCountdownSpan(this ScheduleSnapshot snap)
        => snap.Phase switch
        {
            SchedulePhase.InClass => snap.UntilCurrentEnd ?? TimeSpan.Zero,
            SchedulePhase.BeforeSchool or SchedulePhase.Break or SchedulePhase.LunchBreak => snap.UntilNextStart ?? TimeSpan.Zero,
            _ => TimeSpan.Zero
        };
}

