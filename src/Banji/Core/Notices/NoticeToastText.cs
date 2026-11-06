using Banji.Core.Abstractions;
using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Core.Notices;

/// <summary>
/// v1.3.0 宠物气泡提醒的文案与保持时长（纯函数，无 UI、无真实时钟，便于单元测试）。
/// <para>
/// 用户要求：上课/下课不再全屏紫底挡 PPT，统一改成气泡；文案带“科目与时间范围”，
/// 课后/放学附带下一节信息。气泡窗口由 PetWindow（pet-ui）订阅 <see cref="INotificationService.ToastRequested"/> 后展示。
/// </para>
/// </summary>
public static class NoticeToastText
{
    /// <summary>设置默认值：气泡保持 7 秒。</summary>
    public const int DefaultHoldSeconds = 7;
    public const int MinHoldSeconds = 2;
    public const int MaxHoldSeconds = 30;

    /// <summary>默认保持毫秒数（7 秒 = 7000ms）。</summary>
    public const int DefaultHoldMilliseconds = DefaultHoldSeconds * 1000;

    /// <summary>标题与正文之间的分隔符：“ · ”。</summary>
    public const string Dot = " · ";

    /// <summary>时间范围分隔符（en dash）：“–”。</summary>
    public const string Dash = "–";

    /// <summary>把设置里的 NoticeBubbleHoldSeconds 换算成毫秒（夹在 2~30 秒）。</summary>
    public static int HoldMilliseconds(int holdSeconds)
        => Math.Clamp(holdSeconds, MinHoldSeconds, MaxHoldSeconds) * 1000;

    /// <summary>从设置取保持毫秒数；settings 为空时用默认 7 秒。</summary>
    public static int HoldMilliseconds(AppSettings? settings)
        => HoldMilliseconds(settings?.NoticeBubbleHoldSeconds ?? DefaultHoldSeconds);

    /// <summary>课程显示名：有课程名用课程名，否则用“第N节”。</summary>
    public static string CourseName(CourseOccurrence? occurrence)
    {
        if (occurrence is null) return "";
        return occurrence.HasCourse ? occurrence.Course.Trim() : occurrence.PeriodName;
    }

    /// <summary>时间范围文本：“08:00–08:45”。</summary>
    public static string TimeRange(CourseOccurrence occurrence)
        => $"{occurrence.Start:HH:mm}{Dash}{occurrence.End:HH:mm}";

    /// <summary>下一节提示：“下节 15:50 美术”。</summary>
    public static string NextText(CourseOccurrence? next)
        => next is null ? "" : $"下节 {next.Start:HH:mm} {CourseName(next)}";

    /// <summary>上课气泡：标题“上课”，正文“语文 · 08:00–08:45”。</summary>
    public static NoticeToast ClassStart(CourseOccurrence? target, int holdMilliseconds)
    {
        var message = target is null
            ? "新的一节课开始了"
            : $"{CourseName(target)}{Dot}{TimeRange(target)}";
        return new NoticeToast
        {
            Title = "上课",
            Message = message,
            Kind = NoticeToastKind.ClassStart,
            HoldMilliseconds = holdMilliseconds
        };
    }

    /// <summary>下课气泡：标题“下课”，正文“下节 15:50 美术”（最后一节则“今天的课结束了”）。</summary>
    public static NoticeToast ClassEnd(CourseOccurrence? ending, CourseOccurrence? next, int holdMilliseconds)
    {
        var message = next is not null ? NextText(next) : "今天的课结束了";
        return new NoticeToast
        {
            Title = "下课",
            Message = message,
            Kind = NoticeToastKind.ClassEnd,
            HoldMilliseconds = holdMilliseconds
        };
    }

    /// <summary>大课间气泡：标题“大课间”，正文“下节 15:50 美术”或“课间休息一下”。</summary>
    public static NoticeToast BigBreak(CourseOccurrence? next, int holdMilliseconds)
    {
        var message = next is not null ? NextText(next) : "课间休息一下";
        return new NoticeToast
        {
            Title = "大课间",
            Message = message,
            Kind = NoticeToastKind.ClassEnd,
            HoldMilliseconds = holdMilliseconds
        };
    }

    /// <summary>放学气泡：标题“放学”，正文“今天 17:30 放学 · 共 8 节课”。</summary>
    public static NoticeToast SchoolEnd(DateTime? schoolEnd, int periodCount, int holdMilliseconds)
    {
        var message = schoolEnd is null
            ? "今天的课结束了"
            : $"今天 {schoolEnd.Value:HH:mm} 放学" + (periodCount > 0 ? $" · 共 {periodCount} 节课" : "");
        return new NoticeToast
        {
            Title = "放学",
            Message = message,
            Kind = NoticeToastKind.ClassEnd,
            HoldMilliseconds = holdMilliseconds
        };
    }

    /// <summary>通用气泡（如设置预览里的普通提醒）。</summary>
    public static NoticeToast Generic(string title, string message, int holdMilliseconds)
        => new()
        {
            Title = string.IsNullOrWhiteSpace(title) ? "提醒" : title,
            Message = message ?? "",
            Kind = NoticeToastKind.Generic,
            HoldMilliseconds = holdMilliseconds
        };

    /// <summary>根据课表过渡事件生成气泡（ClassClock.Transition / 设置预览共用）。</summary>
    public static NoticeToast ForTransition(ClassTransitionEventArgs transition, int holdMilliseconds)
    {
        if (transition is null) return Generic("提醒", "", holdMilliseconds);
        var snapshot = transition.Snapshot;
        switch (transition.Kind)
        {
            case ClassTransitionKind.ClassStarted:
                return ClassStart(transition.Current ?? transition.Next, holdMilliseconds);

            case ClassTransitionKind.BreakStarted:
                return BigBreak(transition.Next, holdMilliseconds);

            case ClassTransitionKind.SchoolEnded:
                return SchoolEnd(snapshot?.SchoolEnd ?? transition.Time, snapshot?.Today.Count ?? 0, holdMilliseconds);

            case ClassTransitionKind.ClassEnded:
                if (EndsAtSchoolEnd(transition))
                    return SchoolEnd(snapshot?.SchoolEnd ?? transition.Time, snapshot?.Today.Count ?? 0, holdMilliseconds);
                return ClassEnd(transition.Current, transition.Next, holdMilliseconds);

            default:
                return Generic("提醒", NextText(transition.Next), holdMilliseconds);
        }
    }

    /// <summary>这次“下课”是否就是下午放学（最后一节下课）。</summary>
    public static bool EndsAtSchoolEnd(ClassTransitionEventArgs transition)
    {
        if (transition is null) return false;
        if (transition.Kind == ClassTransitionKind.SchoolEnded) return true;
        if (transition.Kind != ClassTransitionKind.ClassEnded) return false;

        var ending = transition.Current;
        var snapshot = transition.Snapshot;
        if (ending is null || snapshot?.SchoolEnd is not { } schoolEnd) return false;
        if (snapshot.Next is not null) return false; // 后面还有课，只是普通下课
        return ending.End >= schoolEnd.AddSeconds(-1);
    }
}
