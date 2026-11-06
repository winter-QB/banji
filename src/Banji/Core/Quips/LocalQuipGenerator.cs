namespace Banji.Core.Quips;

/// <summary>
/// 本地离线台词生成器（第二轮新增，用户点名）：模板 + 槽位（科目 / 时段 / 星期 / 季节 / 节日），
/// 完全离线、零网络、零外部依赖；与固定台词库互补，输出更不容易重复。
/// <para>随机源可注入，测试用固定序列即可得到确定性输出。</para>
/// </summary>
public sealed class LocalQuipGenerator
{
    /// <summary>最近若干条输出不重复（避免“连着蹦同一句”）。</summary>
    public const int RecentWindow = 10;

    private static readonly string[] Subjects =
    {
        "语文", "数学", "英语", "物理", "化学", "生物", "历史", "地理",
        "思想政治", "体育", "音乐", "美术", "信息技术", "自习"
    };

    private static readonly string[] Weekdays = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    // {subject} {timeOfDay} {weekday} {season} {context} {festival}
    private static readonly string[] Templates =
    {
        "{timeOfDay}的{subject}课，最适合慢慢啃一道题。",
        "把{subject}当成一场小游戏，通关就在下课铃响的时候。",
        "{weekday}的{timeOfDay}，班机陪你一起上{subject}。",
        "困了就想想{subject}老师那句“这题很简单”。",
        "如果{subject}让你头大，先深呼吸三次。",
        "今天的{subject}作业，写完才能愉快地摸鱼。",
        "{context}，别忘了{subject}还差一页笔记。",
        "{season}的教室，{subject}和困意一起点名。",
        "做不出{subject}题的时候，先读三遍题干。",
        "{subject}老师的粉笔字，比我的字迹工整一百倍。",
        "今天的{subject}课代表格外认真。",
        "{weekday}也别松懈，{subject}老师在看着你。",
        "课本翻到{subject}那一页，今天把它拿下。",
        "{context}偷偷说一句：{subject}其实挺有意思的。",
        "把{subject}的公式抄三遍，总有一个会记住。",
        "{timeOfDay}的光落在课本上，{subject}好像也没那么难。",
        "班机提醒：{subject}错题本该更新啦。",
        "{weekday}的{context}，适合把{subject}知识过一遍。",
        "上{subject}课的时候，坐直一点会更清醒哦。",
        "别急着放弃，{subject}这道题就差最后一步。",
        "{season}的风吹过窗台，{subject}的铃声也快响了。",
        "今天也要和{subject}好好相处。",
        "{subject}老师说的重点，记得圈出来。",
        "{context}，要不要偷偷预习一下下一节{subject}？",
        "把{subject}练到会为止，剩下的交给时间。",
        "看到{subject}两个字，我的困意就自动退散了。",
        "{weekday}的{subject}，是我们班最安静的十分钟。",
        "班机在旁边看着你写{subject}作业呢。",
        "遇到不会的{subject}题，先写一个“解”字。",
        "{timeOfDay}适合背{subject}的重点。",
        "上课偷偷打哈欠的时候，我在旁边提醒你。",
        "把今天的{subject}小目标完成，奖励自己一杯水。",
        "老师转身写板书的瞬间，是{subject}课最刺激的时候。",
        "{season}的{context}，{subject}课本翻得哗哗响。",
        "别怕错，{subject}就是这样一道一道会的。",
        "今天的{subject}笔记，字迹好像比昨天好看。"
    };

    private static readonly string[] FestivalTemplates =
    {
        "{festival}快乐！今天的{subject}课也要元气满满。",
        "{festival}到了，班机祝你{season}安好。",
        "今天是{festival}，{subject}作业可以晚点写吗？",
        "{festival}这种日子，连{subject}老师都温柔了一点。",
        "{festival}的小目标：{subject}课不打瞌睡。"
    };

    private readonly Func<DateTime> _now;
    private readonly Func<double> _random01;
    private readonly Queue<string> _recent = new();

    public LocalQuipGenerator()
        : this(null, null)
    {
    }

    public LocalQuipGenerator(Func<DateTime>? now = null, Func<double>? random01 = null)
    {
        _now = now ?? (() => DateTime.Now);
        _random01 = random01 ?? (() => Random.Shared.NextDouble());
    }

    /// <summary>生成一条不重复（最近 <see cref="RecentWindow"/> 条内）的台词。</summary>
    public string Next()
    {
        var text = "";
        for (var attempt = 0; attempt < 24; attempt++)
        {
            text = Compose();
            if (!string.IsNullOrWhiteSpace(text) && !_recent.Contains(text)) break;
        }

        _recent.Enqueue(text);
        while (_recent.Count > RecentWindow) _recent.Dequeue();
        return text;
    }

    private string Compose()
    {
        var now = _now();
        var festival = FestivalOf(DateOnly.FromDateTime(now));
        var useFestival = festival.Length > 0 && Roll() < 0.35;
        var templates = useFestival ? FestivalTemplates : Templates;
        var template = templates[Pick(templates.Length)];
        return template
            .Replace("{subject}", Subjects[Pick(Subjects.Length)], StringComparison.Ordinal)
            .Replace("{weekday}", Weekdays[(int)now.DayOfWeek == 0 ? 6 : (int)now.DayOfWeek - 1], StringComparison.Ordinal)
            .Replace("{timeOfDay}", TimeOfDayOf(now.Hour), StringComparison.Ordinal)
            .Replace("{context}", ContextOf(now), StringComparison.Ordinal)
            .Replace("{season}", SeasonOf(now.Month), StringComparison.Ordinal)
            .Replace("{festival}", festival, StringComparison.Ordinal);
    }

    private int Pick(int count)
    {
        if (count <= 0) return 0;
        var roll = Roll();
        var index = (int)Math.Floor(roll * count);
        return Math.Clamp(index, 0, count - 1);
    }

    private double Roll()
    {
        var roll = _random01();
        if (double.IsNaN(roll)) return 0.5;
        return Math.Clamp(roll, 0, 0.999999);
    }

    /// <summary>时段槽位（按小时）。</summary>
    public static string TimeOfDayOf(int hour) => hour switch
    {
        >= 0 and < 5 => "深夜",
        >= 5 and < 8 => "清晨",
        >= 8 and < 11 => "上午",
        >= 11 and < 14 => "中午",
        >= 14 and < 17 => "下午",
        >= 17 and < 19 => "傍晚",
        >= 19 and < 22 => "晚上",
        _ => "深夜"
    };

    /// <summary>课堂场景/节奏槽位。</summary>
    public static string ContextOf(DateTime now)
    {
        var week = now.DayOfWeek;
        if (week is DayOfWeek.Saturday or DayOfWeek.Sunday) return "周末";
        var time = now.TimeOfDay;
        if (time < TimeSpan.FromHours(8)) return "早读";
        if (time < TimeSpan.FromHours(11.5)) return "上课";
        if (time < TimeSpan.FromHours(14)) return "午休";
        if (time < TimeSpan.FromHours(17)) return "上课";
        if (time < TimeSpan.FromHours(19)) return "傍晚";
        return "晚自习";
    }

    public static string SeasonOf(int month) => month switch
    {
        3 or 4 or 5 => "春天",
        6 or 7 or 8 => "夏天",
        9 or 10 or 11 => "秋天",
        _ => "冬天"
    };

    /// <summary>固定公历节日/重要日子（不依赖农历，离线可算）；没有则返回空串。</summary>
    public static string FestivalOf(DateOnly date) => (date.Month, date.Day) switch
    {
        (1, 1) => "元旦",
        (5, 1) or (5, 2) or (5, 3) => "劳动节",
        (5, 4) => "青年节",
        (6, 1) => "儿童节",
        (6, 7) or (6, 8) => "高考",
        (9, 10) => "教师节",
        (10, 1) or (10, 2) or (10, 3) => "国庆节",
        (12, 31) => "跨年夜",
        _ => ""
    };
}
