namespace Banji.Core.Quips;

/// <summary>
/// “摸一摸”台词库：与高中各科相关、温柔/幽默/轻吐槽、全部简体中文短句。
/// <para>
/// 规则：一轮内不重复（洗牌抽签式）；相邻两次绝不重复；
/// 另有 <see cref="ShanxiEasterEggProbability"/> 的极低概率抽到山西方言彩蛋
/// （“班机”在山西话里的谐音梗，轻松自黑，不含任何粗俗内容）。
/// </para>
/// </summary>
public sealed class QuipLibrary
{
    /// <summary>山西方言彩蛋概率（2%）。</summary>
    public const double ShanxiEasterEggProbability = 0.02;

    private static readonly string[] NormalQuips =
    {
        // ---- 语文 ----
        "语文课的灵魂，是那篇永远背不完的《赤壁赋》。",
        "作文八百字，开头写了三百字还在抒情。",
        "阅读理解选 C，作者本人来了也得选 C。",
        "我背会了《逍遥游》，但逍遥的只有庄子，不是我。",
        "默写错的不是字，是我对古人的理解。",
        "文言文翻译：字我都认识，连起来就不认识了。",
        // ---- 数学 ----
        "数学老师在黑板上写了个“显然”，我看了十分钟也没看出显然。",
        "这道函数题，我只会画个坐标轴表示尊重。",
        "圆锥曲线：画得很圆，错得很准。",
        "数学考试最后一题的第二问，是我和分数的分界线。",
        "数学老师说这题送分，我怀疑是送命题。",
        "导数的尽头是积分，积分的尽头是我的眼泪。",
        // ---- 英语 ----
        "英语听力一放，我就开始和耳机一起发呆。",
        "My English is fine, thank you, and you?",
        "完形填空全文都懂，就是选项都不熟。",
        "英语作文的万能句型，是我的救命稻草。",
        "单词书永远从 abandon 开始背，人生也是。",
        "听力里那位男士，永远在问一个我听不懂的问题。",
        // ---- 物理 ----
        "物理课上，小滑块总能滑得比我的人生顺畅。",
        "受力分析画了三根箭头，答案说方向反了。",
        "我：为什么物体会下落？牛顿：你先别问。",
        "电磁场一画，我的大脑也跟着感应出了涡流。",
        "物理实验课的数据，是用来凑出标准答案的。",
        "自由落体最快的一秒，是下课铃响的那一秒。",
        // ---- 化学 ----
        "化学方程式配平了，我的人生还没配平。",
        "化学老师讲硫酸的比喻味儿太足，我物理性后仰。",
        "有机化学：碳碳相连，我与知识不相连。",
        "实验现象：溶液变蓝。我的表情：也变得很蓝。",
        "摩尔质量算得清清楚楚，我的睡眠质量一塌糊涂。",
        // ---- 生物 ----
        "生物老师说细胞是生命的单位，我觉得课间是快乐单位。",
        "有丝分裂画到一半，我把染色体画成了毛线团。",
        "遗传题：爸妈都是双眼皮，孩子是单眼皮？先别急着怀疑人生。",
        "生态系统的能量流动，最后流向了我的黑眼圈。",
        "ATP 是直接能源物质，而我是间接犯困物质。",
        // ---- 历史 ----
        "历史课的年表比我的备忘录还长。",
        "背朝代歌的时候，总有个朝代来插队。",
        "历史老师：这节课划重点。我的书：全都是重点。",
        "从夏商周到明清，我的笔一直没停过。",
        "历史作业的论述题，我论述了半页“综上所述”。",
        "历史总是惊人的相似，比如每次考试前我都没复习完。",
        // ---- 地理 ----
        "地理课上，我终于知道了自己为什么总是水土不服。",
        "等压线画得像寿司卷，气压倒是没记住。",
        "我的方向感：上北下南，左右随缘。",
        "地理图册翻到第 87 页，我的心已经飞去了南半球。",
        "洋流一暖，我的分数一凉。",
        // ---- 思想政治 ----
        "思想政治：物质决定意识，作业决定睡眠。",
        "政治课的“是什么、为什么、怎么办”，简直是人生三步曲。",
        "矛盾的普遍性告诉我：作业和困意总是同时出现。",
        "价值规律说价格围绕价值波动，我的成绩也很有规律地波动。",
        // ---- 体育 ----
        "体育课热身跑两圈，我的灵魂先跑完了四百米。",
        "八百米起跑线前，我立下了期中考试前早睡的誓言。",
        "体育老师吹哨的那一刻，我决定热爱生活三秒。",
        "立定跳远的距离，是我与及格线的距离。",
        // ---- 音乐 / 美术 ----
        "音乐课唱歌，我主要负责用眼神和声。",
        "美术课画苹果，交上去被认成了土豆。",
        "学校合唱比赛，我们班的目标是：不跑调。",
        "美术老师：大胆用色。我：大胆用了黑色。",
        // ---- 信息技术 / 自习 ----
        "信息技术课上，键盘比我的思绪快。",
        "机房电脑一卡，我就拥有了半分钟的人生思考。",
        "自习课最安静的时候，是老师从后门进来的前一秒。",
        "自习课上，我学会了如何优雅地发呆。",
        // ---- 校园生活 / 温柔陪伴 ----
        "课间十分钟，七分钟在找下节课的教室。",
        "上课铃是我的闹钟，下课铃是我的重生。",
        "今天也要元气满满地……趴在课桌上。",
        "早读的声音有多大，困意就有多深。",
        "周一综合征是真实存在的，我本人就是证据。",
        "黑板上的倒计时，比我更懂紧迫感。",
        "我的文具盒里，永远少一支能写字的笔。",
        "数学作业写完了，语文作业还在书包里等我。",
        "课代表一声“交作业”，唤醒了我沉睡的灵魂。",
        "坐在第一排的好处是能看清黑板，坏处是也能被看清。",
        "今天的小目标：不迟到，不睡着，不被点名。",
        "老师说“这题上次讲过”，我心虚地翻开了笔记。",
        "教室后排的风，总是特别适合睡觉。",
        "期中考前：复习计划；考后：重来计划。",
        "别急，慢慢来，题会一道一道刷完的。",
        "已经上了三节课，我的能量条只剩一半。",
        "今天也是被三角函数和文言文包围的一天。",
        "困了就站起来听会儿，我可不想你被老师点名。",
        "题做不出来的时候，先深呼吸，再重新读一遍题。",
        "下课铃响之前，先别收拾书包。",
        "我在这里陪你上课，不许偷懒哦。",
        "摸鱼可以，别摸太久，我盯着的。"
    };

    private static readonly string[] ShanxiQuips =
    {
        "班机班机，山西话里可不能细琢磨，咱就当它是好词儿。",
        "额滴个乖乖，这道题咋又不会咧？",
        "娃呀，别慌，慢慢来，班机陪着你咧。",
        "今儿个状态不赖嘛，比昨儿个强多咧。",
        "俺这名字在山西可不敢大声叫，咱悄悄用就行。",
        "上课铃响咧，赶紧回座位坐好。"
    };

    private readonly Func<double> _random01;
    private readonly List<string> _normalBag = new();
    private readonly List<string> _shanxiBag = new();
    private string? _lastNormal;
    private string? _lastShanxi;

    /// <summary>使用默认随机源（CSPRNG 种子化的 Random.Shared 即可，台词不需要密码学随机）。</summary>
    public QuipLibrary()
        : this(null)
    {
    }

    /// <param name="random01">返回 [0,1) 的随机源，测试可注入固定值。</param>
    public QuipLibrary(Func<double>? random01)
    {
        _random01 = random01 ?? (() => Random.Shared.NextDouble());
    }

    /// <summary>全部台词（普通 + 山西方言彩蛋），测试与统计用。</summary>
    public static IReadOnlyList<string> AllQuips
    {
        get
        {
            var list = new List<string>(NormalQuips.Length + ShanxiQuips.Length);
            list.AddRange(NormalQuips);
            list.AddRange(ShanxiQuips);
            return list;
        }
    }

    public static int NormalCount => NormalQuips.Length;
    public static int ShanxiCount => ShanxiQuips.Length;

    /// <summary>抽下一句台词。</summary>
    public string Next()
    {
        if (_random01() < ShanxiEasterEggProbability)
            return Draw(_shanxiBag, ShanxiQuips, ref _lastShanxi);
        return Draw(_normalBag, NormalQuips, ref _lastNormal);
    }

    private string Draw(List<string> bag, string[] source, ref string? last)
    {
        if (bag.Count == 0) Refill(bag, source, last);

        var value = bag[^1];
        bag.RemoveAt(bag.Count - 1);
        last = value;
        return value;
    }

    private void Refill(List<string> bag, string[] source, string? last)
    {
        bag.Clear();
        bag.AddRange(source);
        // Fisher-Yates 洗牌：保证“一轮内不重复”。
        for (var i = bag.Count - 1; i > 0; i--)
        {
            var roll = _random01();
            if (double.IsNaN(roll)) roll = 0;
            var j = (int)Math.Floor(roll * (i + 1));
            if (j < 0) j = 0;
            if (j > i) j = i;
            (bag[i], bag[j]) = (bag[j], bag[i]);
        }
        // 新的一轮第一句不能和上一轮最后一句相同（“相邻不重复”）。
        if (bag.Count > 1 && last is not null && bag[^1] == last)
            (bag[^1], bag[0]) = (bag[0], bag[^1]);
    }
}
