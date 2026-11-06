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

    // =========================================================================
    // 第三轮：学科/活动课语义修复（黑名单 + 每科目 3000 条）
    // =========================================================================

    [BanjiTest]
    public static void 语义黑名单会被拒绝()
    {
        var bad = new[]
        {
            "体育这道题，我盯着看了十分钟还是没思路。",
            "今天也要和自习好好相处。",
            "美术的公式抄三遍。",
            "音乐的题不会做。",
            "和体育好好相处。",
            "信息技术公式背不下来。",
            "自习的题真难。"
        };
        foreach (var text in bad)
        {
            Assert.False(LocalQuipGenerator.IsValidQuip(text), $"应当被黑名单拒绝：{text}");
        }

        var good = new[]
        {
            "这节体育课，终于可以起来活动一下了。",
            "这道数学题，我盯着看了十分钟还是没思路。",
            "今天也要和语文好好相处。",
            "自习时间，班里难得安静又热闹。",
            "体育课上老师让我们跑了两圈。"
        };
        foreach (var text in good)
        {
            Assert.True(LocalQuipGenerator.IsValidQuip(text), $"不应当被误杀：{text}");
        }
    }

    [BanjiTest]
    public static void 学科科目各生成三千条无黑名单搭配()
    {
        var generator = new LocalQuipGenerator(() => new DateTime(2026, 11, 6, 10, 30, 0), Lcg(11));
        foreach (var subject in LocalQuipGenerator.AcademicSubjects)
        {
            Assert.True(LocalQuipGenerator.IsAcademicSubject(subject), $"{subject} 应属于学科");
            for (var i = 0; i < 3000; i++)
            {
                var text = generator.NextForSubject(subject);
                Assert.True(LocalQuipGenerator.IsValidQuip(text), $"学科台词不合法：[{subject}] {text}");
                Assert.True(text.Length is >= 4 and <= 60, $"长度不合理：{text}");
                Assert.True(text.Contains(subject, StringComparison.Ordinal), $"学科台词应当提到科目：{text}");
                foreach (var activity in LocalQuipGenerator.ActivitySubjects)
                    Assert.False(text.Contains(activity, StringComparison.Ordinal), $"学科台词里混入活动课名 [{activity}]：{text}");
            }
        }
    }

    [BanjiTest]
    public static void 活动课科目各生成三千条无学科味搭配()
    {
        var generator = new LocalQuipGenerator(() => new DateTime(2026, 11, 6, 10, 30, 0), Lcg(23));
        var forbidden = new[] { "这道题", "的题", "错题", "题目", "公式", "默写", "背诵", "作业", "试卷", "好好相处" };
        foreach (var activity in LocalQuipGenerator.ActivitySubjects)
        {
            Assert.True(LocalQuipGenerator.IsActivitySubject(activity), $"{activity} 应属于活动课");
            for (var i = 0; i < 3000; i++)
            {
                var text = generator.NextForSubject(activity);
                Assert.True(LocalQuipGenerator.IsValidQuip(text), $"活动课台词不合法：[{activity}] {text}");
                Assert.True(text.Length is >= 4 and <= 60, $"长度不合理：{text}");
                Assert.True(text.Contains(activity, StringComparison.Ordinal), $"活动课台词应当提到活动：{text}");
                foreach (var token in forbidden)
                    Assert.False(text.Contains(token, StringComparison.Ordinal), $"活动课台词出现学科味词 [{token}]：{text}");
            }
        }
    }

    [BanjiTest]
    public static void 默认生成三千条全部合法且相邻不重复()
    {
        var generator = new LocalQuipGenerator(() => new DateTime(2026, 6, 7, 15, 0, 0), Lcg(31));
        var previous = "";
        for (var i = 0; i < 3000; i++)
        {
            var text = generator.Next();
            Assert.True(LocalQuipGenerator.IsValidQuip(text), $"默认台词不合法：{text}");
            Assert.False(text == previous, $"相邻重复：{text}");
            previous = text;
        }
    }

    [BanjiTest]
    public static void 固定台词库同样通过语义校验()
    {
        foreach (var quip in QuipLibrary.AllQuips)
        {
            Assert.True(LocalQuipGenerator.IsValidQuip(quip), $"固定台词库命中语义黑名单：{quip}");
        }
    }

    [BanjiTest]
    public static void 科目分类判断()
    {
        Assert.Equal(QuipSubjectKind.Academic, LocalQuipGenerator.SubjectKindOf("数学")!.Value);
        Assert.Equal(QuipSubjectKind.Activity, LocalQuipGenerator.SubjectKindOf("体育")!.Value);
        Assert.True(LocalQuipGenerator.SubjectKindOf("物理") == QuipSubjectKind.Academic);
        Assert.True(LocalQuipGenerator.SubjectKindOf("自习") == QuipSubjectKind.Activity);
        Assert.Null(LocalQuipGenerator.SubjectKindOf("不存在的科目"));
        Assert.Throws<ArgumentException>(() => new LocalQuipGenerator().NextForSubject("不存在的科目"));
    }
}

