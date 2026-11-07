using Banji.Core.Models;
using Banji.Core.Settings;
using Banji.Core.WeekParityEngine;

namespace Banji.Tests;

/// <summary>
/// v1.4.0 单双周推算测试：锚点对齐、第几周、锚点前后/跨年、手动覆盖仅本周、
/// 跨周自动恢复 Auto、关闭功能时按 Odd 且不影响旧行为、事件。
/// 用真实日期计算（纯函数 + 注入时钟，不依赖真实时间）。
/// </summary>
public static class ParityWeekParityTests
{
    private static readonly DateOnly SemesterMonday = new(2026, 9, 7); // 周一

    private static (WeekParityService Service, ScheduleFakeSettingsService Settings) Create(
        DateTime now, Action<AppSettings>? setup = null)
    {
        var settings = new ScheduleFakeSettingsService();
        setup?.Invoke(settings.Current);
        var service = new WeekParityService(settings, () => now);
        return (service, settings);
    }

    private static Action<AppSettings> Enabled(
        DateOnly semesterMonday, bool firstWeekOdd = true, DateOnly? anchor = null)
        => s =>
        {
            s.EnableWeekParity = true;
            s.SemesterFirstWeekMonday = semesterMonday;
            s.FirstWeekIsOdd = firstWeekOdd;
            s.WeekParityAnchorDate = anchor;
        };

    [BanjiTest]
    public static void 锚点默认取开学第一周且对齐到周一()
    {
        var (svc, _) = Create(new DateTime(2026, 11, 7), Enabled(new DateOnly(2026, 9, 9))); // 周三
        Assert.Equal(new DateOnly(2026, 9, 7), svc.AnchorMonday);
        Assert.Equal(WeekParity.Odd, svc.GetParity(SemesterMonday));
    }

    [BanjiTest]
    public static void 单双周按周交替_首周为单()
    {
        var (svc, _) = Create(new DateTime(2026, 11, 7), Enabled(SemesterMonday));
        Assert.Equal(WeekParity.Odd, svc.GetParity(new DateOnly(2026, 9, 7)));   // 第 1 周
        Assert.Equal(WeekParity.Odd, svc.GetParity(new DateOnly(2026, 9, 13)));  // 第 1 周周日
        Assert.Equal(WeekParity.Even, svc.GetParity(new DateOnly(2026, 9, 14))); // 第 2 周
        Assert.Equal(WeekParity.Odd, svc.GetParity(new DateOnly(2026, 9, 21)));  // 第 3 周
        // 跨年：第 17 周（2026-12-28）单周，第 18 周（2027-01-04）双周
        Assert.Equal(WeekParity.Odd, svc.GetParity(new DateOnly(2026, 12, 28)));
        Assert.Equal(WeekParity.Even, svc.GetParity(new DateOnly(2027, 1, 4)));
    }

    [BanjiTest]
    public static void 首周为双时翻转()
    {
        var (svc, _) = Create(new DateTime(2026, 11, 7), Enabled(SemesterMonday, firstWeekOdd: false));
        Assert.Equal(WeekParity.Even, svc.GetParity(SemesterMonday));
        Assert.Equal(WeekParity.Odd, svc.GetParity(new DateOnly(2026, 9, 14)));
        Assert.Equal(WeekParity.Even, svc.GetParity(new DateOnly(2026, 9, 21)));
    }

    [BanjiTest]
    public static void 第几周计算与锚点前后安全值()
    {
        var (svc, _) = Create(new DateTime(2026, 11, 7), Enabled(SemesterMonday));
        Assert.Equal(1, svc.GetWeekNumber(SemesterMonday));
        Assert.Equal(1, svc.GetWeekNumber(new DateOnly(2026, 9, 13)));
        Assert.Equal(2, svc.GetWeekNumber(new DateOnly(2026, 9, 14)));
        Assert.Equal(9, svc.GetWeekNumber(new DateOnly(2026, 11, 2)));
        Assert.Equal(0, svc.GetWeekNumber(new DateOnly(2026, 8, 31)));   // 上一周
        Assert.Equal(-1, svc.GetWeekNumber(new DateOnly(2026, 8, 24)));  // 上两周
    }

    [BanjiTest]
    public static void 锚点日期优先于开学第一周()
    {
        var (svc, settings) = Create(new DateTime(2026, 11, 7), s =>
        {
            s.EnableWeekParity = true;
            s.SemesterFirstWeekMonday = SemesterMonday;
            s.WeekParityAnchorDate = new DateOnly(2026, 10, 5);
            s.FirstWeekIsOdd = true;
        });
        Assert.Equal(new DateOnly(2026, 10, 5), svc.AnchorMonday);
        Assert.Equal(1, svc.GetWeekNumber(new DateOnly(2026, 10, 5)));
        Assert.Equal(WeekParity.Odd, svc.GetParity(new DateOnly(2026, 10, 5)));
        Assert.Equal(WeekParity.Even, svc.GetParity(new DateOnly(2026, 10, 12)));
        Assert.Equal(new DateOnly(2026, 10, 5), settings.Current.WeekParityAnchorDate);
    }

    [BanjiTest]
    public static void 手动覆盖只影响本周()
    {
        var currentMonday = new DateOnly(2026, 9, 7); // 自动为单周（第 1 周）
        var (svc, settings) = Create(new DateTime(2026, 9, 9), Enabled(SemesterMonday));
        settings.Update(x =>
        {
            x.WeekParityOverride = WeekParityOverrideMode.Even; // 覆盖成双周
            x.WeekParityOverrideWeekStart = currentMonday;
        });

        Assert.Equal(WeekParity.Even, svc.GetParity(currentMonday));
        Assert.Equal(WeekParity.Even, svc.GetParity(new DateOnly(2026, 9, 13))); // 本周日仍是覆盖
        Assert.Equal(WeekParity.Even, svc.GetParity(new DateOnly(2026, 9, 14))); // 下周自动=双（恰好相同）
        Assert.Equal(WeekParity.Odd, svc.GetParity(new DateOnly(2026, 9, 21)));  // 下下周恢复自动单周
        Assert.True(svc.IsCurrentWeekOverridden);
        Assert.Equal(WeekParityOverrideMode.Even, svc.OverrideMode);
        Assert.Equal(WeekParity.Even, svc.CurrentParity);
    }

    [BanjiTest]
    public static void 跨周后手动覆盖自动恢复Auto并写回设置()
    {
        var staleMonday = new DateOnly(2026, 8, 31); // 上一周
        var (svc, settings) = Create(new DateTime(2026, 9, 7), Enabled(SemesterMonday));
        settings.Update(x =>
        {
            x.WeekParityOverride = WeekParityOverrideMode.Even;
            x.WeekParityOverrideWeekStart = staleMonday;
        });

        // 构造时已清理：恢复 Auto、清空覆盖周
        Assert.Equal(WeekParityOverrideMode.Auto, settings.Current.WeekParityOverride);
        Assert.Null(settings.Current.WeekParityOverrideWeekStart);
        Assert.False(svc.IsCurrentWeekOverridden);
        Assert.Equal(WeekParity.Odd, svc.GetParity(new DateOnly(2026, 9, 7))); // 自动单周
    }

    [BanjiTest]
    public static void 覆盖缺少周起始时按本周补齐()
    {
        var (svc, settings) = Create(new DateTime(2026, 9, 9), Enabled(SemesterMonday));
        settings.Update(x =>
        {
            x.WeekParityOverride = WeekParityOverrideMode.Odd;
            x.WeekParityOverrideWeekStart = null;
        });
        // 再次刷新触发清理逻辑
        svc.Refresh();
        Assert.Equal(WeekParityOverrideMode.Odd, settings.Current.WeekParityOverride);
        Assert.Equal(new DateOnly(2026, 9, 7), settings.Current.WeekParityOverrideWeekStart);
        Assert.True(svc.IsCurrentWeekOverridden);
    }

    [BanjiTest]
    public static void 覆盖周不是周一时自动对齐()
    {
        var (svc, settings) = Create(new DateTime(2026, 9, 9), Enabled(SemesterMonday));
        settings.Update(x =>
        {
            x.WeekParityOverride = WeekParityOverrideMode.Even;
            x.WeekParityOverrideWeekStart = new DateOnly(2026, 9, 9); // 周三
        });
        svc.Refresh();
        Assert.Equal(new DateOnly(2026, 9, 7), settings.Current.WeekParityOverrideWeekStart);
        Assert.Equal(WeekParity.Even, svc.GetParity(new DateOnly(2026, 9, 7)));
    }

    [BanjiTest]
    public static void 关闭单双周时按奇数周且忽略覆盖()
    {
        var settings = new ScheduleFakeSettingsService();
        settings.Current.EnableWeekParity = false;
        settings.Current.SemesterFirstWeekMonday = SemesterMonday;
        settings.Current.FirstWeekIsOdd = false; // 若生效第 1 周应为双
        settings.Current.WeekParityOverride = WeekParityOverrideMode.Even;
        settings.Current.WeekParityOverrideWeekStart = null;
        var svc = new WeekParityService(settings, () => new DateTime(2026, 9, 7));

        Assert.False(svc.Enabled);
        Assert.Equal(WeekParity.Odd, svc.GetParity(SemesterMonday));
        Assert.Equal(WeekParity.Odd, svc.GetParity(new DateOnly(2026, 9, 14)));
        Assert.False(svc.IsCurrentWeekOverridden);
    }

    [BanjiTest]
    public static void 未设置锚点时安全回退()
    {
        var settings = new ScheduleFakeSettingsService();
        settings.Current.EnableWeekParity = true;
        var svc = new WeekParityService(settings, () => new DateTime(2026, 9, 7));
        Assert.Null(svc.AnchorMonday);
        Assert.Equal(0, svc.GetWeekNumber(new DateOnly(2026, 9, 7)));
        Assert.Equal(WeekParity.Odd, svc.GetParity(new DateOnly(2026, 9, 7)));
    }

    [BanjiTest]
    public static void 设置变化会广播Changed事件()
    {
        var (svc, settings) = Create(new DateTime(2026, 9, 7), Enabled(SemesterMonday));
        var count = 0;
        svc.Changed += (_, _) => count++;
        settings.Update(x => x.EnableWeekParity = false);
        Assert.True(count >= 1, "设置变化应触发 Changed");
        var before = count;
        svc.Refresh();
        Assert.True(count > before, "Refresh 应触发 Changed");
    }

    [BanjiTest]
    public static void ToMonday对齐规则()
    {
        Assert.Equal(new DateOnly(2026, 9, 7), WeekParityService.ToMonday(new DateOnly(2026, 9, 7)));
        Assert.Equal(new DateOnly(2026, 9, 7), WeekParityService.ToMonday(new DateOnly(2026, 9, 9)));
        Assert.Equal(new DateOnly(2026, 9, 7), WeekParityService.ToMonday(new DateOnly(2026, 9, 13)));
    }
}
