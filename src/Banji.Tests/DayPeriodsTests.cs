using Banji.Core.Models;
using Banji.Views;

namespace Banji.Tests;

/// <summary>v1.7.0 临时时间表纯函数测试：时间解析、每节 40 分钟、编辑行组装、校验/清除。</summary>
public static class DayPeriodsTests
{
    private static DayPeriodEdit Edit(int index, string start, string end, bool cancelled = false)
        => new() { Index = index, Name = $"第{index}节", StartText = start, EndText = end, Cancelled = cancelled };

    [BanjiTest(Name = "v1.7 临时时间表时间解析与格式化")]
    public static void 时间解析与格式化()
    {
        Assert.True(DayPeriodsMath.TryParseTime("08:00", out var a));
        Assert.Equal(new TimeSpan(8, 0, 0), a);
        Assert.True(DayPeriodsMath.TryParseTime("8:05", out var b));
        Assert.Equal(new TimeSpan(8, 5, 0), b);
        Assert.True(DayPeriodsMath.TryParseTime("08:00:30", out var c));
        Assert.Equal(new TimeSpan(8, 0, 30), c);
        Assert.True(DayPeriodsMath.TryParseTime(" 08：30 ", out var d), "支持全角冒号与空格");
        Assert.Equal(new TimeSpan(8, 30, 0), d);
        Assert.True(DayPeriodsMath.TryParseTime("14点05分", out var e));
        Assert.Equal(new TimeSpan(14, 5, 0), e);

        Assert.False(DayPeriodsMath.TryParseTime(null, out _));
        Assert.False(DayPeriodsMath.TryParseTime("", out _));
        Assert.False(DayPeriodsMath.TryParseTime("abc", out _));
        Assert.False(DayPeriodsMath.TryParseTime("25:00", out _), "超过 24 小时视为非法");

        Assert.Equal("08:00", DayPeriodsMath.FormatTime(new TimeSpan(8, 0, 0)));
        Assert.Equal("14:05", DayPeriodsMath.FormatTime(new TimeSpan(14, 5, 12)));
    }

    [BanjiTest(Name = "v1.7 每节 40 分钟：保留课间、重叠顺延")]
    public static void 每节四十分钟重排()
    {
        // 8:00-8:45 / 8:55-9:40 / 10:00-10:45 → 保持开始时间，只缩短为 40 分钟
        var result = DayPeriodsMath.ApplyFortyMinutes(new[]
        {
            new TimeSpan(8, 0, 0), new TimeSpan(8, 55, 0), new TimeSpan(10, 0, 0)
        });
        Assert.Equal(3, result.Count);
        Assert.Equal(new TimeSpan(8, 0, 0), result[0].Start);
        Assert.Equal(new TimeSpan(8, 40, 0), result[0].End);
        Assert.Equal(new TimeSpan(8, 55, 0), result[1].Start);
        Assert.Equal(new TimeSpan(9, 35, 0), result[1].End);
        Assert.Equal(new TimeSpan(10, 0, 0), result[2].Start);
        Assert.Equal(new TimeSpan(10, 40, 0), result[2].End);

        // 连续排课：上一节结束 8:40，下一节原开始 8:30 → 顺延到 8:40
        var compact = DayPeriodsMath.ApplyFortyMinutes(new[]
        {
            new TimeSpan(8, 0, 0), new TimeSpan(8, 30, 0)
        });
        Assert.Equal(new TimeSpan(8, 40, 0), compact[1].Start);
        Assert.Equal(new TimeSpan(9, 20, 0), compact[1].End);

        // 自定义分钟（夹取 10~120）
        var custom = DayPeriodsMath.ApplyFortyMinutes(new[] { new TimeSpan(9, 0, 0) }, minutes: 45);
        Assert.Equal(new TimeSpan(9, 45, 0), custom[0].End);
        var clamped = DayPeriodsMath.ApplyFortyMinutes(new[] { new TimeSpan(9, 0, 0) }, minutes: 500);
        Assert.Equal(new TimeSpan(11, 0, 0), clamped[0].End, "分钟数被夹到 120");

        Assert.Equal(0, DayPeriodsMath.ApplyFortyMinutes(Array.Empty<TimeSpan>()).Count);
    }

    [BanjiTest(Name = "v1.7 临时时间表编辑行组装（当天覆盖/取消态）")]
    public static void 编辑行组装()
    {
        var basePeriods = new List<ClassPeriod>
        {
            new() { Index = 1, Name = "第1节", Start = new TimeSpan(8, 0, 0), End = new TimeSpan(8, 45, 0) },
            new() { Index = 2, Name = "第2节", Start = new TimeSpan(8, 55, 0), End = new TimeSpan(9, 40, 0) },
            new() { Index = 3, Name = "第3节", Start = new TimeSpan(10, 0, 0), End = new TimeSpan(10, 45, 0) }
        };

        var noOverride = DayPeriodsMath.BuildEditList(basePeriods, null);
        Assert.Equal(3, noOverride.Count);
        Assert.True(noOverride.All(e => !e.Cancelled));
        Assert.Equal("08:00", noOverride[0].StartText);

        // 当天覆盖只有第 1、2 节（第 3 节被取消）→ 编辑行应包含第 3 节且标记为已取消，便于恢复
        var overridePeriods = new List<ClassPeriod>
        {
            new() { Index = 1, Name = "第1节", Start = new TimeSpan(8, 0, 0), End = new TimeSpan(8, 35, 0) },
            new() { Index = 2, Name = "第2节", Start = new TimeSpan(8, 40, 0), End = new TimeSpan(9, 15, 0) }
        };
        var withOverride = DayPeriodsMath.BuildEditList(basePeriods, overridePeriods);
        Assert.Equal(3, withOverride.Count);
        Assert.Equal("08:35", withOverride[0].EndText);
        Assert.False(withOverride[1].Cancelled);
        Assert.True(withOverride[2].Cancelled, "覆盖里缺少的节次 = 今天取消，仍显示以便恢复");

        // 覆盖为空列表 = 无覆盖（按契约）
        var emptyOverride = DayPeriodsMath.BuildEditList(basePeriods, new List<ClassPeriod>());
        Assert.Equal(3, emptyOverride.Count);
        Assert.True(emptyOverride.All(e => !e.Cancelled));

        // 覆盖里多出的节次（如临时加课）也进入编辑行
        var extra = DayPeriodsMath.BuildEditList(basePeriods, new List<ClassPeriod>
        {
            new() { Index = 1, Name = "第1节", Start = new TimeSpan(8, 0, 0), End = new TimeSpan(8, 40, 0) },
            new() { Index = 9, Name = "第9节", Start = new TimeSpan(17, 0, 0), End = new TimeSpan(17, 40, 0) }
        });
        Assert.Equal(4, extra.Count);
        Assert.False(extra.Last().Cancelled);
    }

    [BanjiTest(Name = "v1.7 生成 PeriodsOverride：排序/重排/校验错误")]
    public static void 生成时段覆盖()
    {
        var ok = DayPeriodsMath.TryBuildPeriods(new List<DayPeriodEdit>
        {
            Edit(1, "10:00", "10:40"),
            Edit(2, "08:00", "08:30"),
            Edit(3, "08:40", "09:10", cancelled: true)
        }, out var periods, out var error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(2, periods!.Count, "取消的节次不写入覆盖");
        Assert.Equal(1, periods[0].Index, "按开始时间排序后 Index 重排为 1..N");
        Assert.Equal(new TimeSpan(8, 0, 0), periods[0].Start);
        Assert.Equal(2, periods[1].Index);
        Assert.Equal(new TimeSpan(10, 0, 0), periods[1].Start);

        Assert.False(DayPeriodsMath.TryBuildPeriods(new List<DayPeriodEdit> { Edit(1, "08:00", "08:00") },
            out _, out var sameError));
        Assert.True((sameError ?? "").Contains("晚于开始"), "结束必须晚于开始");

        Assert.False(DayPeriodsMath.TryBuildPeriods(new List<DayPeriodEdit> { Edit(1, "abc", "08:40") },
            out _, out var formatError));
        Assert.True((formatError ?? "").Contains("格式"), "格式错误提示");

        Assert.False(DayPeriodsMath.TryBuildPeriods(new List<DayPeriodEdit>
        {
            Edit(1, "08:00", "09:00"),
            Edit(2, "08:30", "09:10")
        }, out _, out var overlapError));
        Assert.True((overlapError ?? "").Contains("重叠"), "重叠错误提示");

        Assert.False(DayPeriodsMath.TryBuildPeriods(new List<DayPeriodEdit>
        {
            Edit(1, "08:00", "08:40", cancelled: true)
        }, out _, out var allCancelledError));
        Assert.True((allCancelledError ?? "").Contains("至少要保留"), "全部取消错误提示");

        Assert.False(DayPeriodsMath.TryBuildPeriods(new List<DayPeriodEdit>(), out _, out var emptyError));
        Assert.True((emptyError ?? "").Contains("没有"), "空列表错误提示");
    }

    [BanjiTest(Name = "v1.7 清除时段：是否整天删除覆盖")]
    public static void 清除判断()
    {
        var onlyPeriods = new DayOverride
        {
            Date = DateOnly.FromDateTime(DateTime.Today),
            PeriodsOverride = new List<ClassPeriod>
            {
                new() { Index = 1, Name = "第1节", Start = new TimeSpan(8, 0, 0), End = new TimeSpan(8, 40, 0) }
            }
        };
        Assert.True(DayPeriodsMath.ShouldRemoveOverrideAfterClearingPeriods(onlyPeriods), "只有时段 → 清完删整天");

        onlyPeriods.PeriodsOverride = null;
        Assert.True(DayPeriodsMath.ShouldRemoveOverrideAfterClearingPeriods(onlyPeriods));

        var withReplacement = new DayOverride
        {
            Date = onlyPeriods.Date,
            PeriodsOverride = new List<ClassPeriod>(),
            CourseReplacements = { [3] = "数学" }
        };
        Assert.False(DayPeriodsMath.ShouldRemoveOverrideAfterClearingPeriods(withReplacement), "保留当天换课");

        var withCancelled = new DayOverride { CancelledPeriods = { 5 } };
        Assert.False(DayPeriodsMath.ShouldRemoveOverrideAfterClearingPeriods(withCancelled), "保留取消记录");

        var withNote = new DayOverride { Note = "提前放学" };
        Assert.False(DayPeriodsMath.ShouldRemoveOverrideAfterClearingPeriods(withNote), "保留备注");

        Assert.True(DayPeriodsMath.ShouldRemoveOverrideAfterClearingPeriods(null));
    }

    [BanjiTest(Name = "v1.7 临时时间表窗口可无参构造")]
    public static void 窗口无参构造()
    {
        var ctor = typeof(Banji.Views.DayPeriodsWindow).GetConstructor(Type.EmptyTypes);
        Assert.True(ctor is not null, "DayPeriodsWindow 必须保留无参构造（AppServices 直接 new）");
        Assert.Equal("Banji.Views.DayPeriodsWindow", ctor!.DeclaringType!.FullName);
    }
}
