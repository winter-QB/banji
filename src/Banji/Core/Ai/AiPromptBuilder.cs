using System.Text;
using Banji.Core.Abstractions;

namespace Banji.Core.Ai;

/// <summary>
/// 端侧 AI 的提示词构造（Qwen2.5 / ChatML 格式）与历史截断。
/// 全部为纯函数，方便单测；运行期用模型真实 tokenizer 计数，测试可注入估算器。
/// </summary>
public static class AiPromptBuilder
{
    /// <summary>「班机」的系统提示词：方形雾蓝课堂宠物，中文、简短、温和幽默，不编造事实。</summary>
    public const string SystemPrompt =
        "你是「班机」，一个方形、雾蓝色的小方块，是同学们桌面上陪伴上课的课堂宠物。\n" +
        "请遵守以下规则：\n" +
        "1. 一律使用简体中文，语气温和、轻松、带一点点幽默，像坐在旁边的同学，而不是客服；\n" +
        "2. 回答简短：一般 1~3 句话、不超过 100 字，不要长篇大论；\n" +
        "3. 话题围绕高中课堂、学习、作息、校园生活和同学们的小情绪；\n" +
        "4. 不编造事实、数据和新闻，不知道就坦率说不知道；\n" +
        "5. 不使用 Markdown 标题、表格或长列表，除非用户明确要求。";

    /// <summary>生成宠物台词时的系统提示词（要求短句、符合课堂场景）。</summary>
    public const string QuipSystemPrompt =
        "你在替课堂桌面宠物「班机」写台词。班机是一个方形、雾蓝色的小方块，住在教室电脑里，性格温和、机灵、有点冷幽默。\n" +
        "台词要求：简体中文，每条不超过 24 个字，口语化、自然，不要解释、不要引号、不要表情符号。";

    /// <summary>生成开场白时的系统提示词。</summary>
    public const string GreetingSystemPrompt =
        "你是课堂桌面宠物「班机」，一个方形、雾蓝色的小方块。请用简体中文说一句 20 字以内的开场白，温和、轻松、有点幽默，可以顺带提一句课堂或学习，不要用引号。";

    /// <summary>ChatML 的开始标记。</summary>
    public const string ImStart = "<|im_start|>";

    /// <summary>ChatML 的结束标记。</summary>
    public const string ImEnd = "<|im_end|>";

    /// <summary>为回复预留的安全余量（tokens）：避免 prompt 顶满上下文导致无法生成。</summary>
    public const int SafetyMarginTokens = 32;

    /// <summary>上下文至少要有这么多 token 才值得生成。</summary>
    public const int MinPromptTokenBudget = 64;

    /// <summary>角色转 ChatML 角色名。</summary>
    public static string RoleName(AiChatRole role) => role switch
    {
        AiChatRole.System => "system",
        AiChatRole.Assistant => "assistant",
        _ => "user"
    };

    /// <summary>把一条消息格式化成 ChatML 片段（含结尾换行）。</summary>
    public static string FormatMessage(AiChatRole role, string? content)
        => $"{ImStart}{RoleName(role)}\n{(content ?? "").Trim()}{ImEnd}\n";

    /// <summary>ChatML 里“轮到助手说话”的结尾标记。</summary>
    public const string AssistantOpen = "<|im_start|>assistant\n";

    /// <summary>
    /// 按“上下文长度 − 回复预留 − 安全余量”计算 prompt 预算（token）。
    /// </summary>
    public static int ComputePromptBudget(int contextSize, int maxResponseTokens)
    {
        var budget = contextSize - maxResponseTokens - SafetyMarginTokens;
        return Math.Max(MinPromptTokenBudget, budget);
    }

    /// <summary>
    /// 构造 ChatML prompt：
    /// system 段 = 内置系统提示词 + 调用方传入的 system 消息（若有）；
    /// 对话段按时间顺序；从最新消息往前截断历史，放不下更早的消息就丢弃。
    /// 最新一条用户消息即使超预算也会被截断内容后保留（尽量保留开头）。
    /// </summary>
    /// <param name="history">对话历史（可含 System 消息；最后一条一般是当前用户输入）。</param>
    /// <param name="promptTokenBudget">prompt 允许的最大 token 数。</param>
    /// <param name="countTokens">token 计数函数；为 null 时用 <see cref="EstimateTokens"/>。</param>
    /// <param name="droppedMessages">被丢弃的历史消息条数。</param>
    public static string BuildChatPrompt(
        IReadOnlyList<AiChatMessage>? history,
        int promptTokenBudget,
        Func<string, int>? countTokens,
        out int droppedMessages)
    {
        droppedMessages = 0;
        var counter = countTokens ?? EstimateTokens;
        var budget = Math.Max(MinPromptTokenBudget, promptTokenBudget);

        var systemParts = new List<string> { SystemPrompt.Trim() };
        var dialog = new List<AiChatMessage>();
        if (history is not null)
        {
            foreach (var m in history)
            {
                if (m is null || string.IsNullOrWhiteSpace(m.Content)) continue;
                if (m.Role == AiChatRole.System)
                {
                    var text = m.Content.Trim();
                    if (!systemParts.Contains(text)) systemParts.Add(text);
                }
                else
                {
                    dialog.Add(m);
                }
            }
        }

        var systemBlock = BuildSystemBlock(systemParts);
        var used = counter(systemBlock) + counter(AssistantOpen) + 4;
        var remaining = Math.Max(0, budget - used);

        // 从最新往旧收集，直到超出预算
        var included = new List<AiChatMessage>();
        for (var i = dialog.Count - 1; i >= 0; i--)
        {
            var msg = dialog[i];
            var formatted = FormatMessage(msg.Role, msg.Content);
            var cost = counter(formatted);
            if (cost <= remaining)
            {
                included.Add(msg);
                remaining -= cost;
            }
            else
            {
                droppedMessages++;
                // 继续往前没必要了：更早的消息只会更长
                break;
            }
        }

        // 一条都放不下（或最新用户消息自身超预算）：截断最新一条用户消息保留开头
        if (included.Count == 0 && dialog.Count > 0)
        {
            var newest = dialog[^1];
            droppedMessages = dialog.Count - 1;
            var content = TruncateToTokenBudget(newest.Content, Math.Max(8, remaining), counter);
            included.Add(new AiChatMessage { Role = newest.Role, Content = content, Time = newest.Time });
            remaining = Math.Max(0, remaining - counter(FormatMessage(newest.Role, content)));
        }

        // included 是倒序收集的，恢复成时间顺序
        included.Reverse();

        var sb = new StringBuilder(systemBlock.Length + included.Count * 32 + AssistantOpen.Length);
        sb.Append(systemBlock);
        foreach (var msg in included)
        {
            sb.Append(FormatMessage(msg.Role, msg.Content));
        }
        sb.Append(AssistantOpen);
        return sb.ToString();
    }

    /// <summary>构造 ChatML prompt（用内置估算器计数，单测/无 tokenizer 场景用）。</summary>
    public static string BuildChatPrompt(IReadOnlyList<AiChatMessage>? history, int promptTokenBudget,
        out int droppedMessages)
        => BuildChatPrompt(history, promptTokenBudget, null, out droppedMessages);

    /// <summary>构造宠物台词生成 prompt（要求 5 条短句，一行一条）。</summary>
    public static string BuildQuipPrompt(string? contextHint = null, int maxLines = 5)
    {
        var hint = string.IsNullOrWhiteSpace(contextHint) ? "课堂日常" : contextHint.Trim();
        var user =
            $"请以「班机」的口吻写 {Math.Max(1, maxLines)} 条彼此不同的短台词，场景是：{hint}。\n" +
            "要求：每条不超过 24 个字，一行一条，只输出台词本身，不要编号、不要引号、不要解释。";
        return $"{ImStart}system\n{QuipSystemPrompt}{ImEnd}\n{ImStart}user\n{user}{ImEnd}\n{AssistantOpen}";
    }

    /// <summary>构造开场白生成 prompt。</summary>
    public static string BuildGreetingPrompt(string? contextHint = null)
    {
        var hint = string.IsNullOrWhiteSpace(contextHint) ? "" : $"\n（当前场景：{contextHint.Trim()}）";
        return $"{ImStart}system\n{GreetingSystemPrompt}{ImEnd}\n" +
               $"{ImStart}user\n请说一句开场白。{hint}{ImEnd}\n{AssistantOpen}";
    }

    private static string BuildSystemBlock(IReadOnlyList<string> parts)
    {
        var sb = new StringBuilder();
        sb.Append(ImStart).Append("system\n");
        sb.Append(string.Join("\n", parts.Select(p => p.Trim())));
        sb.Append(ImEnd).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// 把内容截断到大约 token 预算内（按字符二分，保留开头；中文用户问题一般重点在开头）。
    /// 返回的文本可能带省略号。
    /// </summary>
    public static string TruncateToTokenBudget(string? content, int tokenBudget, Func<string, int>? countTokens = null)
    {
        var text = (content ?? "").Trim();
        var counter = countTokens ?? EstimateTokens;
        if (string.IsNullOrEmpty(text) || tokenBudget <= 0) return "";
        if (counter(text) <= tokenBudget) return text;

        // 给结尾的“…”预留 1 个 token，保证截断结果仍在预算内
        var searchBudget = Math.Max(1, tokenBudget - 1);

        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (counter(text[..mid]) <= searchBudget) lo = mid;
            else hi = mid - 1;
        }

        // 不要从代理对中间截断
        if (lo > 0 && char.IsHighSurrogate(text[lo - 1])) lo--;
        if (lo <= 0) return "";
        return text[..lo].TrimEnd() + "…";
    }

    /// <summary>
    /// 粗略 token 估算（不依赖模型）：CJK 字符约 1 token，ASCII 约 3.5 字符/token，空白/标点更少。
    /// 仅用于截断上限的保守估计与单测；运行期优先用真实 tokenizer。
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var tokens = 0.0;
        foreach (var c in text)
        {
            if (c == '\n' || c == '\r') tokens += 0.3;
            else if (char.IsWhiteSpace(c)) tokens += 0.15;
            else if (IsCjk(c)) tokens += 1.0;
            else if (c < 0x80) tokens += 0.3;
            else tokens += 1.0;
        }
        return Math.Max(1, (int)Math.Ceiling(tokens));
    }

    private static bool IsCjk(char c)
        => (c >= 0x4E00 && c <= 0x9FFF)     // 常用汉字
           || (c >= 0x3400 && c <= 0x4DBF)  // 扩展 A
           || (c >= 0xF900 && c <= 0xFAFF)  // 兼容汉字
           || (c >= 0x3000 && c <= 0x303F)  // 中文标点
           || (c >= 0xFF00 && c <= 0xFFEF); // 全角字符
}

