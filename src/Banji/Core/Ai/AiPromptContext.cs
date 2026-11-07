using System.Text;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Core.Ai;

/// <summary>
/// v1.4.0：注入 system prompt 的动态上下文快照（纯数据，便于单测）。
/// 内容来自单双周服务与值日服务；任一服务未启用/未配置时对应字段为 null，拼装时自动省略。
/// </summary>
public sealed class AiPromptContext
{
    /// <summary>上下文对应的日期（默认今天）。</summary>
    public DateOnly Today { get; init; }

    /// <summary>日期/星期文案，如“今天是 2026年11月7日 星期六。”。</summary>
    public string? DateText { get; init; }

    /// <summary>单双周文案，如“本周是第 10 周 · 单周；下周是第 11 周 · 双周。”。</summary>
    public string? WeekParityText { get; init; }

    /// <summary>今日值日/本周值周摘要（组 + 任务成员），如“今日值日：第 2 组；任务：擦黑板（张三、李四）。”。</summary>
    public string? DutyText { get; init; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(DateText)
        && Today == default
        && string.IsNullOrWhiteSpace(WeekParityText)
        && string.IsNullOrWhiteSpace(DutyText);

    /// <summary>拼装成可直接追加到 system prompt 的文本块；没有可用信息时返回空串。</summary>
    public string ToSystemBlock() => AiPromptContextBuilder.BuildSystemContext(this);
}

/// <summary>
/// 动态上下文的纯函数拼装（日期/星期、单双周、值日摘要），全部不依赖真实服务，方便单测。
/// 长度受控：整块 ≤ <see cref="MaxContextChars"/>，值日摘要另受 <see cref="MaxDutyChars"/> 限制。
/// </summary>
public static class AiPromptContextBuilder
{
    /// <summary>注入 system prompt 的动态上下文最大字符数（超出截断加省略号）。</summary>
    public const int MaxContextChars = 640;

    /// <summary>值日摘要最多字符数。</summary>
    public const int MaxDutyChars = 280;

    /// <summary>值日摘要里最多列出的任务数（避免长列表挤爆上下文）。</summary>
    public const int MaxDutyTasks = 6;

    /// <summary>动态信息段的标题行（告诉模型这些是真实数据、别原样复述）。</summary>
    public const string SectionHeader =
        "【当前动态信息】（以下为真实实时数据，仅在与用户问题相关时使用；不要把整段信息原样复述，不确定就说不清楚，不要编造）";

    private static readonly string[] WeekdayNames =
        { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };

    /// <summary>值日相关场景关键词：本地台词 / AI 台词在这些场景下优先带上值日信息。</summary>
    private static readonly string[] DutyScenarioKeywords =
        { "值日", "值周", "轮值", "放学", "擦黑板", "黑板", "讲台", "卫生" };

    /// <summary>“今天是 2026年11月7日 星期六。”。</summary>
    public static string FormatDateText(DateOnly date)
        => $"今天是 {date.Year}年{date.Month}月{date.Day}日 {WeekdayNames[(int)date.DayOfWeek]}。";

    /// <summary>
    /// 单双周文案；周次 ≤ 0（未配置开学第一周）返回 null（调用方省略）。
    /// </summary>
    public static string? BuildWeekParityText(int weekNumber, WeekParity parity, bool overridden,
        int? nextWeekNumber = null, WeekParity? nextParity = null)
    {
        if (weekNumber <= 0) return null;
        var sb = new StringBuilder();
        sb.Append("本周是第 ").Append(weekNumber).Append(" 周 · ").Append(ParityName(parity));
        if (overridden) sb.Append("（本周手动覆盖）");
        if (nextWeekNumber is > 0 && nextParity is not null)
        {
            sb.Append("；下周是第 ").Append(nextWeekNumber.Value).Append(" 周 · ").Append(ParityName(nextParity.Value));
        }
        sb.Append('。');
        return sb.ToString();
    }

    /// <summary>
    /// 值日/值周摘要：组名 + 任务（成员）。未配置（组名与任务都空）返回 null 表示省略。
    /// </summary>
    public static string? BuildDutyText(DutyScheduleView? schedule)
    {
        if (schedule is null) return null;

        var prefix = schedule.Mode == DutyMode.Weekly ? "本周值周" : "今日值日";
        if (schedule.IsPaused) return $"{prefix}：本周暂停（手动覆盖）。";

        var group = schedule.GroupName?.Trim();
        var taskParts = new List<string>();
        if (schedule.Tasks is not null)
        {
            foreach (var task in schedule.Tasks)
            {
                if (taskParts.Count >= MaxDutyTasks) break;
                var taskName = task.TaskName?.Trim();
                if (string.IsNullOrWhiteSpace(taskName)) continue;
                var names = task.StudentNames?
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Trim())
                    .ToList() ?? new List<string>();
                taskParts.Add(names.Count == 0 ? taskName : $"{taskName}（{string.Join("、", names)}）");
            }
        }

        if (string.IsNullOrWhiteSpace(group) && taskParts.Count == 0) return null;

        var sb = new StringBuilder(prefix).Append('：');
        sb.Append(string.IsNullOrWhiteSpace(group) ? "未分组" : group);
        if (taskParts.Count > 0) sb.Append("；任务：").Append(string.Join("、", taskParts));
        sb.Append('。');
        return Truncate(sb.ToString(), MaxDutyChars);
    }

    /// <summary>
    /// 拼装动态上下文块。顺序：日期 → 单双周 → 值日摘要；整体超长时截断。
    /// 无任何可用信息时返回空串（保证不改变原有 system prompt 行为）。
    /// </summary>
    public static string BuildSystemContext(AiPromptContext? context)
    {
        if (context is null || context.IsEmpty) return "";

        var lines = new List<string>();
        var dateLine = !string.IsNullOrWhiteSpace(context.DateText)
            ? context.DateText!.Trim()
            : context.Today != default ? FormatDateText(context.Today) : null;
        if (!string.IsNullOrWhiteSpace(dateLine)) lines.Add(dateLine!);

        if (!string.IsNullOrWhiteSpace(context.WeekParityText)) lines.Add(context.WeekParityText!.Trim());
        if (!string.IsNullOrWhiteSpace(context.DutyText)) lines.Add(Truncate(context.DutyText!.Trim(), MaxDutyChars)!);

        if (lines.Count == 0) return "";
        var block = SectionHeader + "\n" + string.Join("\n", lines);
        return Truncate(block, MaxContextChars) ?? "";
    }

    /// <summary>提示词/场景是否与值日相关（供 AI 台词优先走值日缓存）。</summary>
    public static bool IsDutyScenarioHint(string? contextHint)
    {
        if (string.IsNullOrWhiteSpace(contextHint)) return false;
        return DutyScenarioKeywords.Any(k => contextHint.Contains(k, StringComparison.Ordinal));
    }

    /// <summary>按字符截断（不切断代理对），超长时末尾补“…”；null/空返回 null。</summary>
    public static string? Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (maxChars <= 0) return null;
        if (value.Length <= maxChars) return value;

        var cut = Math.Max(0, maxChars - 1);
        if (cut > 0 && cut < value.Length && char.IsHighSurrogate(value[cut - 1])) cut--;
        if (cut <= 0) return "…";
        return value[..cut].TrimEnd() + "…";
    }

    private static string ParityName(WeekParity parity) => parity == WeekParity.Odd ? "单周" : "双周";
}

/// <summary>
/// 运行期动态上下文采集：从已初始化的 <see cref="AppServices"/> 读取单双周/值日信息。
/// 任何服务缺失、未配置或抛异常都降级为省略对应部分，绝不影响聊天/台词主流程。
/// </summary>
public static class AiPromptContextProvider
{
    /// <summary>
    /// 采集今天的动态上下文；AppServices 未初始化或全部服务都不可用时返回 null。
    /// </summary>
    public static AiPromptContext? TryCapture(DateOnly? todayOverride = null)
    {
        try
        {
            if (!AppServices.IsInitialized) return null;
            var services = AppServices.Current;
            if (services is null) return null;

            var today = todayOverride ?? DateOnly.FromDateTime(DateTime.Now);
            var dateText = AiPromptContextBuilder.FormatDateText(today);

            var weekParityText = TryBuildWeekParity(services, today);
            var dutyText = TryBuildDuty(services, today);

            return new AiPromptContext
            {
                Today = today,
                DateText = dateText,
                WeekParityText = weekParityText,
                DutyText = dutyText
            };
        }
        catch (Exception ex)
        {
            Log.Warn("采集 AI 动态上下文失败（已忽略，不影响生成）：" + ex.Message);
            return null;
        }
    }

    /// <summary>采集并拼装成 system prompt 文本块；不可用时返回 null。</summary>
    public static string? TryCaptureSystemBlock(DateOnly? todayOverride = null)
    {
        try
        {
            var block = AiPromptContextBuilder.BuildSystemContext(TryCapture(todayOverride));
            return string.IsNullOrWhiteSpace(block) ? null : block;
        }
        catch (Exception ex)
        {
            Log.Warn("拼装 AI 动态上下文失败（已忽略）：" + ex.Message);
            return null;
        }
    }

    private static string? TryBuildWeekParity(AppServices services, DateOnly today)
    {
        try
        {
            var parity = services.WeekParity;
            if (parity is null || !parity.Enabled) return null;
            if (parity.AnchorMonday is null) return null; // 还没配置开学第一周，不编造周次

            var weekNumber = parity.GetWeekNumber(today);
            if (weekNumber <= 0) return null;

            var nextWeekDate = today.AddDays(7);
            var nextWeekNumber = parity.GetWeekNumber(nextWeekDate);
            var nextParity = parity.GetParity(nextWeekDate);
            return AiPromptContextBuilder.BuildWeekParityText(
                weekNumber, parity.GetParity(today), parity.IsCurrentWeekOverridden,
                nextWeekNumber, nextParity);
        }
        catch (Exception ex)
        {
            Log.Warn("读取单双周上下文失败（已忽略）：" + ex.Message);
            return null;
        }
    }

    private static string? TryBuildDuty(AppServices services, DateOnly today)
    {
        try
        {
            var duty = services.Duty;
            if (duty is null || !duty.IsConfigured) return null;
            var schedule = duty.GetSchedule(today);
            return AiPromptContextBuilder.BuildDutyText(schedule);
        }
        catch (Exception ex)
        {
            Log.Warn("读取值日上下文失败（已忽略）：" + ex.Message);
            return null;
        }
    }
}

