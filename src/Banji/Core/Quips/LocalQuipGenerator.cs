namespace Banji.Core.Quips;

/// <summary>台词槽位里的“对象类型”：学科（可以谈题/考试）或活动课（只能谈活动/课堂，不能谈题/公式）。</summary>
public enum QuipSubjectKind
{
    /// <summary>学科：语文/数学/英语/物理/化学/生物/思想政治/历史/地理。</summary>
    Academic = 0,
    /// <summary>活动类：体育/美术/音乐/信息技术/通用技术/班会/自习/心理健康。</summary>
    Activity = 1
}

/// <summary>
/// 本地离线台词生成器（第三轮修复语义 bug）：
/// 按“学科模板 / 活动课模板 / 中性模板”分类生成，不再把体育、自习等代入“这道题/公式/和X好好相处”式句子；
/// 生成后还会过一遍黑名单校验（见 <see cref="IsValidQuip"/>），保证“体育这道题”“和自习好好相处”这类搭配为 0。
/// 完全离线、零网络、零外部依赖；随机源可注入，测试用固定序列即可得到确定性输出。
/// </summary>
public sealed class LocalQuipGenerator
{
    /// <summary>最近若干条输出不重复（避免“连着蹦同一句”）。</summary>
    public const int RecentWindow = 10;

    /// <summary>学科：可以谈题/作业/公式/考试。</summary>
    public static readonly IReadOnlyList<string> AcademicSubjects = new[]
    {
        "语文", "数学", "英语", "物理", "化学", "生物", "思想政治", "历史", "地理"
    };

    /// <summary>活动类：只能用活动课模板，禁止“这道题/公式/和X好好相处”等搭配。</summary>
    public static readonly IReadOnlyList<string> ActivitySubjects = new[]
    {
        "体育", "美术", "音乐", "信息技术", "通用技术", "班会", "自习", "心理健康"
    };

    /// <summary>明确禁止的固定搭配（回归用例）。</summary>
    private static readonly string[] HardBlacklist =
    {
        "体育这道题", "体育的题", "体育题", "体育公式", "体育作业", "体育默写", "体育错题",
        "美术的公式", "美术公式", "美术这道题", "音乐的公式", "音乐公式", "音乐的题",
        "信息技术公式", "自习这道题", "自习的题", "自习公式",
        "和自习好好相处", "和体育好好相处", "和美术好好相处", "和音乐好好相处",
        "和班会好好相处", "和信息技术好好相处", "和心理健康好好相处", "和通用技术好好相处",
        "体育和困意一起点名"
    };

    /// <summary>活动类科目句子里禁止出现的“学科味”词。</summary>
    private static readonly string[] ActivityForbiddenTokens =
    {
        "这道题", "的题", "错题", "题目", "公式", "默写", "背诵", "作业", "试卷", "复习", "好好相处"
    };

    private static readonly string[] Weekdays = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    // ---- 学科模板：{subject} 只会填入 AcademicSubjects ----
    private static readonly string[] AcademicTemplates =
    {
        "这道{subject}题，我盯着看了十分钟还是没思路。",
        "{subject}这科，越学越觉得里面全是坑。",
        "把{subject}错题本翻出来，今天消灭三道。",
        "{subject}考试前夜，先把公式过一遍。",
        "{subject}老师在黑板上写了个“显然”，我又没看出来。",
        "{subject}作业写完之前，先别想摸鱼。",
        "{timeOfDay}的光落在{subject}课本上，看着看着就困了。",
        "{weekday}的{subject}课，是本周最需要专注的一节。",
        "背{subject}知识点的时候，最适合配一杯温水。",
        "遇到不会的{subject}题，先写一个“解”字再想。",
        "{subject}课代表今天抱着一摞卷子走进了教室。",
        "别怕错，{subject}就是这样一道一道会的。",
        "{subject}老师说这题送分，我怀疑是送命题。",
        "{season}的{context}，{subject}笔记翻得哗哗响。",
        "{subject}小测前的十分钟，比一整节课都漫长。",
        "把{subject}的错题抄三遍，总有一遍能记住。"
    };

    // ---- 活动课模板：{activity} 只会填入 ActivitySubjects ----
    private static readonly string[] ActivityTemplates =
    {
        "这节{activity}课，终于可以起来活动一下了。",
        "{activity}时间，班机在教室后排看着你。",
        "上{activity}课的时候，别偷偷走神哦。",
        "{activity}课的老师今天心情好像不错。",
        "{activity}时间到，坐直一点会更精神。",
        "这节{activity}课，是我们班最期待的时段之一。",
        "{activity}课前，先把桌上的东西收一收。",
        "{weekday}的{activity}课，总能让人放松一点。",
        "{activity}课快开始了，记得带好要用的东西。",
        "老师说{activity}课要讲点有意思的，先别急着犯困。",
        "{season}的{activity}课，教室里的空气都轻快了些。",
        "{activity}时间，班里难得安静又热闹。",
        "这节{activity}课，班机也在旁边偷偷放松。"
    };

    // ---- 中性模板：不出现任何科目/活动名 ----
    private static readonly string[] NeutralTemplates =
    {
        "{timeOfDay}的教室，困意比粉笔灰还多。",
        "{weekday}的{context}，最适合把笔记补一补。",
        "{season}的风吹过窗台，下课铃也快响了。",
        "上课偷偷打哈欠的时候，我在旁边提醒你。",
        "{context}，要不要偷偷预习一下下一节？",
        "把今天的小目标完成，奖励自己一杯水。",
        "下课铃响之前，先别急着收拾书包。",
        "{timeOfDay}适合慢慢把错题整理一遍。",
        "黑板上的倒计时，比老师更懂紧迫感。",
        "坐在第一排的好处，是能看清黑板上的每一个字。",
        "{weekday}也要好好听课，别让周末太得意。",
        "{season}的课间，走廊里全是脚步声。"
    };

    private static readonly string[] FestivalNeutralTemplates =
    {
        "{festival}快乐！今天也要元气满满。",
        "{festival}到了，班机祝你{season}安好。",
        "今天是{festival}，连上课铃都温柔了一点。",
        "{festival}的小目标：一整天都不打瞌睡。"
    };

    private static readonly string[] FestivalAcademicTemplates =
    {
        "今天是{festival}，{subject}作业可以晚点写吗？",
        "{festival}也不耽误{subject}课代表收作业。"
    };

    private static readonly string[] FestivalActivityTemplates =
    {
        "今天是{festival}，{activity}课也要开开心心的。",
        "{festival}这天的{activity}课，大家都格外精神。"
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

    // =========================================================================
    // 公开查询/校验
    // =========================================================================

    public static bool IsAcademicSubject(string? name)
        => !string.IsNullOrWhiteSpace(name) && AcademicSubjects.Contains(name.Trim(), StringComparer.Ordinal);

    public static bool IsActivitySubject(string? name)
        => !string.IsNullOrWhiteSpace(name) && ActivitySubjects.Contains(name.Trim(), StringComparer.Ordinal);

    /// <summary>判断科目属于“学科”还是“活动课”；未知科目返回 null。</summary>
    public static QuipSubjectKind? SubjectKindOf(string? name)
    {
        if (IsAcademicSubject(name)) return QuipSubjectKind.Academic;
        if (IsActivitySubject(name)) return QuipSubjectKind.Activity;
        return null;
    }

    /// <summary>
    /// 台词语义校验：长度合理、没有明确黑名单搭配；若出现活动课科目，
    /// 禁止同句出现“题/公式/作业/背诵/好好相处”等学科味词。
    /// </summary>
    public static bool IsValidQuip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.Length < 4 || value.Length > 60) return false;
        if (value.Contains('\n') || value.Contains('\r')) return false;

        foreach (var bad in HardBlacklist)
        {
            if (value.Contains(bad, StringComparison.Ordinal)) return false;
        }

        foreach (var activity in ActivitySubjects)
        {
            if (!value.Contains(activity, StringComparison.Ordinal)) continue;
            foreach (var token in ActivityForbiddenTokens)
            {
                if (value.Contains(token, StringComparison.Ordinal)) return false;
            }
        }

        return true;
    }

    // =========================================================================
    // 生成
    // =========================================================================

    /// <summary>生成一条不重复（最近 <see cref="RecentWindow"/> 条内）的台词。</summary>
    public string Next()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var text = Compose(null);
            if (!IsValidQuip(text)) continue;
            if (_recent.Contains(text)) continue;
            Remember(text);
            return text;
        }

        var fallback = Fallback();
        Remember(fallback);
        return fallback;
    }

    /// <summary>
    /// 指定科目生成一条台词（测试/校验用）：学科走学科模板，活动课走活动课模板，
    /// 绝不让活动课进入“题/公式/作业/好好相处”类模板。
    /// </summary>
    public string NextForSubject(string subject)
    {
        var trimmed = (subject ?? "").Trim();
        if (SubjectKindOf(trimmed) is null)
            throw new ArgumentException($"未知科目：{subject}", nameof(subject));

        for (var attempt = 0; attempt < 40; attempt++)
        {
            var text = Compose(trimmed);
            if (IsValidQuip(text)) return text;
        }
        return Fallback();
    }

    private string Compose(string? forcedSubject)
    {
        var now = _now();
        var festival = FestivalOf(DateOnly.FromDateTime(now));
        var useFestival = festival.Length > 0 && Roll() < 0.3;

        if (forcedSubject is null)
        {
            var categoryRoll = Roll();
            // 0~0.45 学科模板；0.45~0.70 活动课模板；其余中性模板（不出现科目名）
            if (categoryRoll < 0.45)
                return FillTemplate(useFestival ? FestivalAcademicTemplates : AcademicTemplates, null, now, festival);
            if (categoryRoll < 0.70)
                return FillTemplate(useFestival ? FestivalActivityTemplates : ActivityTemplates, null, now, festival);
            return FillTemplate(useFestival ? FestivalNeutralTemplates : NeutralTemplates, null, now, festival);
        }

        var kind = SubjectKindOf(forcedSubject);
        return kind == QuipSubjectKind.Activity
            ? FillTemplate(useFestival ? FestivalActivityTemplates : ActivityTemplates, forcedSubject, now, festival)
            : FillTemplate(useFestival ? FestivalAcademicTemplates : AcademicTemplates, forcedSubject, now, festival);
    }

    private string FillTemplate(string[] templates, string? forcedSubject, DateTime now, string festival)
    {
        if (templates.Length == 0) return Fallback();

        var text = templates[Pick(templates.Length)];

        if (text.Contains("{subject}", StringComparison.Ordinal))
        {
            var subject = forcedSubject is not null && IsAcademicSubject(forcedSubject)
                ? forcedSubject.Trim()
                : AcademicSubjects[Pick(AcademicSubjects.Count)];
            text = text.Replace("{subject}", subject, StringComparison.Ordinal);
        }

        if (text.Contains("{activity}", StringComparison.Ordinal))
        {
            var activity = forcedSubject is not null && IsActivitySubject(forcedSubject)
                ? forcedSubject.Trim()
                : ActivitySubjects[Pick(ActivitySubjects.Count)];
            text = text.Replace("{activity}", activity, StringComparison.Ordinal);
        }

        return text
            .Replace("{weekday}", Weekdays[(int)now.DayOfWeek == 0 ? 6 : (int)now.DayOfWeek - 1], StringComparison.Ordinal)
            .Replace("{timeOfDay}", TimeOfDayOf(now.Hour), StringComparison.Ordinal)
            .Replace("{context}", ContextOf(now), StringComparison.Ordinal)
            .Replace("{season}", SeasonOf(now.Month), StringComparison.Ordinal)
            .Replace("{festival}", festival, StringComparison.Ordinal);
    }
    /// <summary>兜底：中性模板一定不会命中黑名单。</summary>
    private string Fallback()
    {
        var now = _now();
        var template = NeutralTemplates[Pick(NeutralTemplates.Length)];
        var text = template
            .Replace("{weekday}", Weekdays[(int)now.DayOfWeek == 0 ? 6 : (int)now.DayOfWeek - 1], StringComparison.Ordinal)
            .Replace("{timeOfDay}", TimeOfDayOf(now.Hour), StringComparison.Ordinal)
            .Replace("{context}", ContextOf(now), StringComparison.Ordinal)
            .Replace("{season}", SeasonOf(now.Month), StringComparison.Ordinal)
            .Replace("{festival}", FestivalOf(DateOnly.FromDateTime(now)), StringComparison.Ordinal);
        return IsValidQuip(text) ? text : "下课铃响之前，先别急着收拾书包。";
    }

    private void Remember(string text)
    {
        _recent.Enqueue(text);
        while (_recent.Count > RecentWindow) _recent.Dequeue();
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

    // =========================================================================
    // 槽位辅助（公开给测试）
    // =========================================================================

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

