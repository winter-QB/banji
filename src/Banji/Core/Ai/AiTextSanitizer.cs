using System.Text;
using System.Text.RegularExpressions;

namespace Banji.Core.Ai;

/// <summary>
/// 生成文本清洗（纯函数）：去掉 ChatML 特殊标记、清理空行、从批量输出里拆出宠物台词。
/// 模型偶尔会“多说一句”或带上编号/引号，这里统一兜底。
/// </summary>
public static class AiTextSanitizer
{
    /// <summary>宠物台词最大字数（用户要求 ≤30 字；生成提示词里要求 ≤24 字）。</summary>
    public const int DefaultQuipMaxChars = 30;

    private static readonly Regex SpecialTokenRegex =
        new(@"<\|[^|>]{1,40}\|>", RegexOptions.Compiled);

    private static readonly Regex NumberingPrefixRegex =
        new(@"^\s*(?:[-*•·]+\s*|\d{1,2}\s*[.、)）:：]\s*|[（(]\d{1,2}[)）]\s*)", RegexOptions.Compiled);

    private static readonly Regex QuoteWrapRegex =
        new(@"^[\s""'“”‘’「」『』《》]+|[\s""'“”‘’「」『』《》]+$", RegexOptions.Compiled);

    private static readonly Regex InlineNumberingRegex =
        new(@"(?:^|\s)\d{1,2}\s*[.、)）]\s*", RegexOptions.Compiled);

    /// <summary>去掉 ChatML 标记、控制字符，压缩空行并 trim。</summary>
    public static string CleanReply(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = SpecialTokenRegex.Replace(raw, "");
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        // 去掉控制字符（保留换行/制表）
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
        }
        text = sb.ToString();
        // 连续 3 个以上换行压成 2 个
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        // 行尾空格
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        return text.Trim();
    }

    /// <summary>
    /// 去掉模型可能自带的角色前缀（如 “assistant:”），并清理首尾引号。
    /// </summary>
    public static string CleanAssistantText(string? raw)
    {
        var text = CleanReply(raw);
        if (text.Length == 0) return "";
        var firstLineBreak = text.IndexOf('\n');
        var firstLine = firstLineBreak < 0 ? text : text[..firstLineBreak];
        var lower = firstLine.Trim().ToLowerInvariant();
        if (lower is "assistant" or "assistant:" or "班机:" or "班机：")
            text = firstLineBreak < 0 ? "" : text[(firstLineBreak + 1)..].Trim();
        return text;
    }

    /// <summary>
    /// 从“批量生成台词”的原始输出里解析台词列表：
    /// 按行拆分 → 去编号/项目符号/包裹引号 → 过滤过长/过短/重复 → 最多取 maxLines 条。
    /// </summary>
    public static List<string> ParseQuipLines(string? raw, int maxLines = 5, int maxChars = DefaultQuipMaxChars)
    {
        var result = new List<string>();
        var text = CleanReply(raw);
        if (text.Length == 0) return result;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 模型有时把 5 条挤在一行（“1. xxx 2. yyy”），行数不足时按编号规则再拆一次
        var candidateLines = lines.Length >= Math.Max(2, maxLines / 2)
            ? lines
            : SplitInlineNumbered(text).ToArray();

        foreach (var rawLine in candidateLines)
        {
            if (result.Count >= maxLines) break;
            var line = NormalizeQuipLine(rawLine, maxChars);
            if (line.Length == 0) continue;
            if (result.Any(l => string.Equals(l, line, StringComparison.Ordinal))) continue;
            result.Add(line);
        }
        return result;
    }

    /// <summary>把单行台词规范成“可直接显示”的样子；不合规返回空串。</summary>
    public static string NormalizeQuipLine(string? rawLine, int maxChars = DefaultQuipMaxChars)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return "";
        var line = CleanReply(rawLine).Replace('\n', ' ').Trim();
        line = NumberingPrefixRegex.Replace(line, "");
        line = QuoteWrapRegex.Replace(line, "");
        line = line.Trim();
        // 去掉结尾的“——班机”之类署名
        line = Regex.Replace(line, @"\s*[—\-]{1,2}\s*班机\s*$", "").Trim();
        if (line.Length < 2 || line.Length > maxChars) return "";
        // 必须包含中文（防止模型输出英文/符号）
        if (!line.Any(c => c >= 0x4E00 && c <= 0x9FFF)) return "";
        return line;
    }

    /// <summary>把 <paramref name="text"/> 压到 maxChars 内（超出则截断并去掉尾部标点）。</summary>
    public static string LimitLength(string? text, int maxChars)
    {
        var value = (text ?? "").Trim();
        if (maxChars <= 0 || value.Length <= maxChars) return value;
        var cut = value[..maxChars];
        return cut.TrimEnd('，', '。', '；', '、', ',', '.', ';', ' ', '…') + "…";
    }

    /// <summary>是否像“模型没给出有效内容”（空/只有标点/过长）。</summary>
    public static bool IsUsableReply(string? text) => !string.IsNullOrWhiteSpace(text) && text.Trim().Length >= 2;

    private static List<string> SplitInlineNumbered(string text)
    {
        var parts = new List<string>();
        // 把 "1. xxx 2. yyy 3. zzz" 这类拆开：在 “… 2. ” 这种编号前插入换行
        var normalized = Regex.Replace(text, @"(?<=\S)\s+(?=\d{1,2}\s*[.、)）])", "\n");
        if (normalized != text)
        {
            parts.AddRange(normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            parts.Add(text);
        }
        return parts;
    }
}

