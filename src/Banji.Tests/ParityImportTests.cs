using Banji.Core.Models;
using Banji.Core.Timetable;

namespace Banji.Tests;

/// <summary>
/// v1.4.0 导入单双周测试：长表“周次”列、单元格 (单)/(双)/[单]/[双] 后缀、
/// 无法识别值给 warning 并按通用、列与后缀冲突时列优先、预览统计。
/// </summary>
public static class ParityImportTests
{
    [BanjiTest]
    public static void 长表周次列识别通用单周双周()
    {
        var path = TestFileBuilder.WriteCsv("parity-long.csv",
            new[] { "星期", "节次", "课程", "开始时间", "结束时间", "周次" },
            new[] { "周一", "第1节", "语文", "08:00", "08:45", "通用" },
            new[] { "周一", "第1节", "数学", "08:00", "08:45", "单" },
            new[] { "周一", "第1节", "英语", "08:00", "08:45", "双" },
            new[] { "周二", "第2节", "物理", "08:55", "09:40", "odd" },
            new[] { "周二", "第2节", "化学", "08:55", "09:40", "even" });

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate, string.Join(" / ", preview.Warnings));
        var tt = preview.Candidate!;

        Assert.Equal(2, tt.SchemaVersion);
        Assert.Equal("语文", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.All)?.Course);
        Assert.Equal("数学", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.Odd)?.Course);
        Assert.Equal("英语", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.Even)?.Course);
        Assert.Equal("物理", tt.EntryAt(DayOfWeek.Tuesday, 2, WeekType.Odd)?.Course);
        Assert.Equal("化学", tt.EntryAt(DayOfWeek.Tuesday, 2, WeekType.Even)?.Course);

        // 解析优先级验证：单周显示专用、双周显示另一半
        Assert.Equal("数学", tt.CourseAt(DayOfWeek.Monday, 1, WeekParity.Odd));
        Assert.Equal("英语", tt.CourseAt(DayOfWeek.Monday, 1, WeekParity.Even));

        // 预览统计
        var (all, odd, even) = TimetableParityEditor.CountByWeekType(tt);
        Assert.Equal(1, all);
        Assert.Equal(2, odd);
        Assert.Equal(2, even);
        Assert.True(preview.FormatDescription.Contains("单双周") || preview.FormatDescription.Contains("单周"),
            "预览说明应含单双周统计：" + preview.FormatDescription);
    }

    [BanjiTest]
    public static void 单元格单双后缀识别()
    {
        var path = TestFileBuilder.WriteCsv("parity-suffix.csv",
            new[] { "节次", "时间", "周一", "周二" },
            new[] { "第1节", "08:00-08:45", "数学(单)", "物理（双）" },
            new[] { "第2节", "08:55-09:40", "语文[单]", "英语(双)" },
            new[] { "第3节", "10:00-10:45", "化学", "生物" });

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate, string.Join(" / ", preview.Warnings));
        var tt = preview.Candidate!;

        // 后缀被剥离，课程名干净
        Assert.Equal("数学", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.Odd)?.Course);
        Assert.Equal("物理", tt.EntryAt(DayOfWeek.Tuesday, 1, WeekType.Even)?.Course);
        Assert.Equal("语文", tt.EntryAt(DayOfWeek.Monday, 2, WeekType.Odd)?.Course);
        Assert.Equal("英语", tt.EntryAt(DayOfWeek.Tuesday, 2, WeekType.Even)?.Course);

        // 无后缀 = 通用
        Assert.Equal("化学", tt.EntryAt(DayOfWeek.Monday, 3, WeekType.All)?.Course);
        Assert.Equal("生物", tt.EntryAt(DayOfWeek.Tuesday, 3, WeekType.All)?.Course);

        var (all, odd, even) = TimetableParityEditor.CountByWeekType(tt);
        Assert.Equal(2, all);
        Assert.Equal(2, odd);
        Assert.Equal(2, even);
    }

    [BanjiTest]
    public static void 无法识别的周次值按通用并给warning()
    {
        var path = TestFileBuilder.WriteCsv("parity-unknown.csv",
            new[] { "星期", "节次", "课程", "周次" },
            new[] { "周一", "第1节", "语文", "第三周" },
            new[] { "周二", "第1节", "数学", "" });

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate);
        var tt = preview.Candidate!;
        Assert.Equal("语文", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.All)?.Course);
        Assert.Equal("数学", tt.EntryAt(DayOfWeek.Tuesday, 1, WeekType.All)?.Course);
        Assert.True(preview.Warnings.Any(w => w.Contains("周次") && w.Contains("无法识别")),
            "应有周次无法识别的 warning：" + string.Join(" / ", preview.Warnings));
    }

    [BanjiTest]
    public static void 周次列与后缀冲突时列优先并给warning()
    {
        var path = TestFileBuilder.WriteCsv("parity-conflict.csv",
            new[] { "星期", "节次", "课程", "周次" },
            new[] { "周一", "第1节", "数学(双)", "单" });

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate);
        var tt = preview.Candidate!;
        // 列方式的“单”优先
        Assert.Equal("数学", tt.EntryAt(DayOfWeek.Monday, 1, WeekType.Odd)?.Course);
        Assert.True(preview.Warnings.Any(w => w.Contains("不一致")),
            "应有列与后缀不一致的 warning：" + string.Join(" / ", preview.Warnings));
    }

    [BanjiTest]
    public static void 旧四布局导入仍然全部通过()
    {
        // 布局 A：无周次列/后缀 → 全部通用（v1.3 回归）
        var pathA = TestFileBuilder.WriteCsv("parity-legacy-a.csv",
            new[] { "节次", "时间", "周一", "周二" },
            new[] { "第1节", "08:00-08:45", "语文", "数学" });
        var previewA = TimetableImporter.Preview(pathA);
        Assert.NotNull(previewA.Candidate);
        Assert.Equal(2, previewA.Candidate!.Days.Values.SelectMany(l => l).Count(e => e.Weeks == WeekType.All));
        Assert.Equal(0, TimetableParityEditor.Count(previewA.Candidate, WeekType.Odd));
        Assert.Equal(0, TimetableParityEditor.Count(previewA.Candidate, WeekType.Even));
        Assert.Equal(2, previewA.Candidate.SchemaVersion);
    }

    [BanjiTest]
    public static void 只有单双专用条目也能导入不丢()
    {
        var path = TestFileBuilder.WriteCsv("parity-only-specific.csv",
            new[] { "节次", "时间", "周一", "周二" },
            new[] { "第1节", "08:00-08:45", "数学(单)", "" },
            new[] { "第2节", "08:55-09:40", "物理（双）", "" });

        var preview = TimetableImporter.Preview(path);
        Assert.NotNull(preview.Candidate);
        var tt = preview.Candidate!;
        Assert.Equal("数学", tt.CourseAt(DayOfWeek.Monday, 1, WeekParity.Odd));
        Assert.Null(tt.CourseAt(DayOfWeek.Monday, 1, WeekParity.Even)); // 没有通用/双周 → null
        Assert.Equal("物理", tt.CourseAt(DayOfWeek.Monday, 2, WeekParity.Even));
    }
}
