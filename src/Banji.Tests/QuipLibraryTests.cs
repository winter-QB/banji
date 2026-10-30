using Banji.Core.Quips;

namespace Banji.Tests;

/// <summary>台词库测试：非空、长度合理、一轮内不重复、相邻不重复、山西彩蛋概率极低。</summary>
public static class QuipLibraryTests
{
    [BanjiTest]
    public static void 台词库非空且数量达标()
    {
        var all = QuipLibrary.AllQuips;
        Assert.True(all.Count >= 60, $"台词至少 60 条，实际 {all.Count}");
        Assert.True(QuipLibrary.NormalCount >= 60, "普通台词至少 60 条");
        Assert.True(QuipLibrary.ShanxiCount >= 3, "山西方言彩蛋至少 3 条");
        Assert.True(QuipLibrary.ShanxiEasterEggProbability > 0 && QuipLibrary.ShanxiEasterEggProbability <= 0.05, "彩蛋概率应极低（≤5%）");
    }

    [BanjiTest]
    public static void 台词内容长度合理且不重复()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var quip in QuipLibrary.AllQuips)
        {
            Assert.False(string.IsNullOrWhiteSpace(quip), "台词不能为空");
            var text = quip.Trim();
            Assert.True(text.Length >= 4, $"台词太短：{text}");
            Assert.True(text.Length <= 40, $"台词太长（{text.Length} 字）：{text}");
            Assert.False(text.Contains('\n') || text.Contains('\r'), $"台词不能换行：{text}");
            Assert.True(seen.Add(text), $"台词重复：{text}");
        }
    }

    [BanjiTest]
    public static void 相邻两次不会抽到同一句()
    {
        var library = new QuipLibrary(Cycle(0.5, 0.87, 0.13, 0.42, 0.68, 0.31));
        var previous = "";
        for (var i = 0; i < 500; i++)
        {
            var quip = library.Next();
            Assert.False(string.IsNullOrWhiteSpace(quip), "抽到空白台词");
            Assert.False(quip == previous, $"连续两次抽到同一句：{quip}");
            previous = quip;
        }
    }

    [BanjiTest]
    public static void 一轮内台词不重复()
    {
        // 固定随机源不会触发 2% 彩蛋，因此连续 NormalCount 次恰好覆盖一轮普通台词。
        var library = new QuipLibrary(Cycle(0.5));
        var round = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < QuipLibrary.NormalCount; i++)
        {
            var quip = library.Next();
            Assert.True(round.Add(quip), $"一轮内重复：{quip}");
        }
        Assert.Equal(QuipLibrary.NormalCount, round.Count);
    }

    [BanjiTest]
    public static void 山西方言彩蛋可被触发且不连续重复()
    {
        // 随机数恒为 0 时每次都命中彩蛋分支。
        var library = new QuipLibrary(Cycle(0.0));
        var hits = new HashSet<string>(StringComparer.Ordinal);
        var previous = "";
        for (var i = 0; i < 60; i++)
        {
            var quip = library.Next();
            Assert.False(quip == previous, $"彩蛋连续重复：{quip}");
            Assert.True(QuipLibrary.AllQuips.Contains(quip), $"彩蛋不在台词库中：{quip}");
            hits.Add(quip);
            previous = quip;
        }
        Assert.True(hits.Count >= 3, "彩蛋应当能抽到多种台词");
    }

    [BanjiTest]
    public static void 不触发彩蛋时只出普通台词()
    {
        var library = new QuipLibrary(Cycle(0.5, 0.9, 0.75));
        for (var i = 0; i < 300; i++)
        {
            var quip = library.Next();
            Assert.True(!quip.Contains("山西", StringComparison.Ordinal) && !quip.Contains("咧", StringComparison.Ordinal),
                $"固定随机源下不应命中彩蛋：{quip}");
        }
    }

    /// <summary>循环使用给定随机值，避免依赖真实随机。</summary>
    private static Func<double> Cycle(params double[] values)
    {
        var index = 0;
        return () =>
        {
            var value = values.Length == 0 ? 0.5 : values[index % values.Length];
            index++;
            return value;
        };
    }
}

