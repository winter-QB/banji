using Banji.Core.Quips;

namespace Banji.Tests;

/// <summary>本地离线台词生成器 + 台词来源选择器测试（第二轮新增）。</summary>
public static class QuipGeneratorTests
{
    private static Func<double> Lcg(int seed)
    {
        var state = (uint)seed;
        return () =>
        {
            state = state * 1664525u + 1013904223u;
            return state / 4294967296.0;
        };
    }

    [BanjiTest]
    public static void 生成器输出非空且槽位全部填充()
    {
        var generator = new LocalQuipGenerator(
            () => new DateTime(2026, 11, 6, 10, 30, 0), Lcg(20261106));
        for (var i = 0; i < 300; i++)
        {
            var text = generator.Next();
            Assert.False(string.IsNullOrWhiteSpace(text), "生成器输出了空台词");
            Assert.False(text.Contains('{') || text.Contains('}'), $"槽位未填充：{text}");
            Assert.True(text.Length >= 6, $"台词太短：{text}");
            Assert.True(text.Length <= 60, $"台词太长（{text.Length} 字）：{text}");
            Assert.False(text.Contains('\n') || text.Contains('\r'), $"台词不应换行：{text}");
        }
    }

    [BanjiTest]
    public static void 生成器相邻两次不重复且有一定多样性()
    {
        var generator = new LocalQuipGenerator(() => new DateTime(2026, 11, 6, 10, 30, 0), Lcg(7));
        var previous = "";
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 400; i++)
        {
            var text = generator.Next();
            Assert.False(text == previous, $"连续两次同一句：{text}");
            previous = text;
            distinct.Add(text);
        }
        Assert.True(distinct.Count >= 30, $"台词多样性不足，仅 {distinct.Count} 种");
    }

    [BanjiTest]
    public static void 生成器确定性可复现()
    {
        var a = new LocalQuipGenerator(() => new DateTime(2026, 11, 6, 9, 0, 0), Lcg(42));
        var b = new LocalQuipGenerator(() => new DateTime(2026, 11, 6, 9, 0, 0), Lcg(42));
        for (var i = 0; i < 50; i++)
            Assert.Equal(a.Next(), b.Next());
    }

    [BanjiTest]
    public static void 生成器会用到科目和时段槽位()
    {
        var generator = new LocalQuipGenerator(() => new DateTime(2026, 11, 6, 10, 30, 0), Lcg(99));
        var subjects = new[] { "语文", "数学", "英语", "物理", "化学", "生物", "历史", "地理", "思想政治", "体育", "音乐", "美术", "信息技术", "自习" };
        var joined = string.Join("\n", Enumerable.Range(0, 200).Select(_ => generator.Next()));
        Assert.True(subjects.Any(s => joined.Contains(s, StringComparison.Ordinal)), "200 条台词里居然没有出现任何科目");
        Assert.True(joined.Contains("上午", StringComparison.Ordinal) || joined.Contains("周", StringComparison.Ordinal),
            "应当出现时段/星期槽位");
    }

    [BanjiTest]
    public static void 时段季节节日辅助函数()
    {
        Assert.Equal("深夜", LocalQuipGenerator.TimeOfDayOf(2));
        Assert.Equal("清晨", LocalQuipGenerator.TimeOfDayOf(6));
        Assert.Equal("上午", LocalQuipGenerator.TimeOfDayOf(9));
        Assert.Equal("中午", LocalQuipGenerator.TimeOfDayOf(12));
        Assert.Equal("下午", LocalQuipGenerator.TimeOfDayOf(15));
        Assert.Equal("傍晚", LocalQuipGenerator.TimeOfDayOf(18));
        Assert.Equal("晚上", LocalQuipGenerator.TimeOfDayOf(20));
        Assert.Equal("深夜", LocalQuipGenerator.TimeOfDayOf(23));

        Assert.Equal("春天", LocalQuipGenerator.SeasonOf(4));
        Assert.Equal("夏天", LocalQuipGenerator.SeasonOf(7));
        Assert.Equal("秋天", LocalQuipGenerator.SeasonOf(10));
        Assert.Equal("冬天", LocalQuipGenerator.SeasonOf(1));

        Assert.Equal("元旦", LocalQuipGenerator.FestivalOf(new DateOnly(2027, 1, 1)));
        Assert.Equal("高考", LocalQuipGenerator.FestivalOf(new DateOnly(2027, 6, 7)));
        Assert.Equal("教师节", LocalQuipGenerator.FestivalOf(new DateOnly(2027, 9, 10)));
        Assert.Equal("国庆节", LocalQuipGenerator.FestivalOf(new DateOnly(2027, 10, 1)));
        Assert.Equal("", LocalQuipGenerator.FestivalOf(new DateOnly(2027, 4, 15)));

        Assert.Equal("周末", LocalQuipGenerator.ContextOf(new DateTime(2026, 11, 7, 10, 0, 0)));
        Assert.Equal("早读", LocalQuipGenerator.ContextOf(new DateTime(2026, 11, 6, 7, 20, 0)));
        Assert.Equal("午休", LocalQuipGenerator.ContextOf(new DateTime(2026, 11, 6, 12, 30, 0)));
        Assert.Equal("晚自习", LocalQuipGenerator.ContextOf(new DateTime(2026, 11, 6, 20, 0, 0)));
    }

    [BanjiTest]
    public static void 节日台词会带上节日名()
    {
        // 元旦当天、随机数偏小时会走节日模板分支；多抽几次总会出现节日词。
        var generator = new LocalQuipGenerator(() => new DateTime(2027, 1, 1, 10, 0, 0), Lcg(2027));
        var joined = string.Join("\n", Enumerable.Range(0, 120).Select(_ => generator.Next()));
        Assert.True(joined.Contains("元旦", StringComparison.Ordinal), "元旦当天应当能生成节日台词");
    }

    [BanjiTest]
    public static void 台词来源选择器跨来源也不连续重复()
    {
        var library = new QuipLibrary(Lcg(3));
        var generator = new LocalQuipGenerator(() => new DateTime(2026, 11, 6, 10, 0, 0), Lcg(5));
        var provider = new QuipProvider(library, generator);

        var previous = "";
        for (var i = 0; i < 200; i++)
        {
            var useGenerator = i % 2 == 0; // 交替使用两种来源
            var text = provider.Next(useGenerator);
            Assert.False(string.IsNullOrWhiteSpace(text), "台词为空");
            Assert.False(text == previous, $"跨来源连续重复：{text}");
            Assert.Equal(text, provider.Last);
            previous = text;
        }
    }
}
