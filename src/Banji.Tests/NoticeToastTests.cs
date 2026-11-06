using Banji.Core.Abstractions;
using Banji.Core.Models;
using Banji.Core.Notices;
using Banji.Core.Settings;

namespace Banji.Tests;

/// <summary>
/// v1.3.0 宠物气泡提醒测试（纯文案/纯事件，不依赖 UI、不显示窗口）：
/// 上课/下课/大课间/放学文案、时间格式、默认 7 秒→7000ms、
/// 以及 NotificationService 改为抛 ToastRequested 事件（不再创建全屏窗口）。
/// </summary>
public static class NoticeToastTests
{
    private static readonly DateTime Day = new(2026, 11, 6); // 周五

    // =========================================================================
    // 构造辅助
    // =========================================================================

    private static CourseOccurrence Occ(
        string course, int startHour, int startMinute, int endHour, int endMinute,
        int index = 1, string? periodName = null)
        => new()
        {
            PeriodIndex = index,
            PeriodName = periodName ?? $"第{index}节",
            Course = course,
            Start = Day.AddHours(startHour).AddMinutes(startMinute),
            End = Day.AddHours(endHour).AddMinutes(endMinute)
        };

    private static ScheduleSnapshot Snap(params CourseOccurrence[] today)
        => new()
        {
            Now = Day.AddHours(8),
            Phase = SchedulePhase.InClass,
            HasTimetable = true,
            Current = today.Length > 0 ? today[0] : null,
            Today = today,
            FirstStart = today.Length > 0 ? today[0].Start : null,
            SchoolEnd = today.Length > 0 ? today[^1].End : null
        };

    private static ClassTransitionEventArgs Transition(
        ClassTransitionKind kind, CourseOccurrence? current, CourseOccurrence? next, ScheduleSnapshot? snapshot = null)
        => new()
        {
            Kind = kind,
            Time = current?.End ?? Day.AddHours(8),
            Current = current,
            Next = next,
            Snapshot = snapshot ?? Snap()
        };

    // =========================================================================
    // 保持时长
    // =========================================================================

    [BanjiTest]
    public static void 默认保持7秒换算为7000毫秒()
    {
        var settings = new AppSettings();
        Assert.Equal(7, settings.NoticeBubbleHoldSeconds);
        Assert.Equal(7000, NoticeToastText.DefaultHoldMilliseconds);
        Assert.Equal(7000, NoticeToastText.HoldMilliseconds(settings));
        Assert.Equal(7000, new NoticeToast().HoldMilliseconds);
    }

    [BanjiTest]
    public static void 保持时长按设置换算并夹取范围()
    {
        var settings = new AppSettings { NoticeBubbleHoldSeconds = 5 };
        Assert.Equal(5000, NoticeToastText.HoldMilliseconds(settings));

        Assert.Equal(2000, NoticeToastText.HoldMilliseconds(0));
        Assert.Equal(2000, NoticeToastText.HoldMilliseconds(1));
        Assert.Equal(7000, NoticeToastText.HoldMilliseconds(7));
        Assert.Equal(30000, NoticeToastText.HoldMilliseconds(99));

        // 设置对象为空时回退默认 7 秒
        Assert.Equal(7000, NoticeToastText.HoldMilliseconds((AppSettings?)null));
    }

    // =========================================================================
    // 上课
    // =========================================================================

    [BanjiTest]
    public static void 上课文案_科目与时间范围()
    {
        var occ = Occ("语文", 8, 0, 8, 45);
        var toast = NoticeToastText.ClassStart(occ, 7000);

        Assert.Equal("上课", toast.Title);
        Assert.Equal("语文 · 08:00–08:45", toast.Message);
        Assert.Equal(NoticeToastKind.ClassStart, toast.Kind);
        Assert.Equal(7000, toast.HoldMilliseconds);
    }

    [BanjiTest]
    public static void 上课文案_时间补零()
    {
        Assert.Equal("数学 · 08:05–08:50",
            NoticeToastText.ClassStart(Occ("数学", 8, 5, 8, 50), 7000).Message);
        Assert.Equal("体育 · 14:00–14:45",
            NoticeToastText.ClassStart(Occ("体育", 14, 0, 14, 45), 7000).Message);
    }

    [BanjiTest]
    public static void 上课文案_无课程名用节次名()
    {
        var occ = Occ("", 10, 0, 10, 45, index: 3, periodName: "第3节");
        Assert.Equal("第3节 · 10:00–10:45", NoticeToastText.ClassStart(occ, 7000).Message);
    }

    [BanjiTest]
    public static void 上课文案_无目标兜底()
    {
        var toast = NoticeToastText.ClassStart(null, 7000);
        Assert.Equal("上课", toast.Title);
        Assert.Equal("新的一节课开始了", toast.Message);
    }

    [BanjiTest]
    public static void 上课事件分发为上课气泡()
    {
        var occ = Occ("物理", 10, 55, 11, 40, index: 4);
        var toast = NoticeToastText.ForTransition(
            Transition(ClassTransitionKind.ClassStarted, occ, null, Snap(occ)), 7000);
        Assert.Equal("上课", toast.Title);
        Assert.Equal("物理 · 10:55–11:40", toast.Message);
        Assert.Equal(NoticeToastKind.ClassStart, toast.Kind);
    }

    // =========================================================================
    // 下课
    // =========================================================================

    [BanjiTest]
    public static void 下课文案_提示下一节()
    {
        var ending = Occ("语文", 8, 0, 8, 45, index: 1);
        var next = Occ("美术", 15, 50, 16, 35, index: 8);
        var toast = NoticeToastText.ClassEnd(ending, next, 7000);
        Assert.Equal("下课", toast.Title);
        Assert.Equal("下节 15:50 美术", toast.Message);
        Assert.Equal(NoticeToastKind.ClassEnd, toast.Kind);
    }

    [BanjiTest]
    public static void 下课文案_最后一节无下一节()
    {
        var ending = Occ("班会", 16, 45, 17, 30, index: 8);
        var toast = NoticeToastText.ClassEnd(ending, null, 7000);
        Assert.Equal("下课", toast.Title);
        Assert.Equal("今天的课结束了", toast.Message);
    }

    [BanjiTest]
    public static void 下课事件分发为下课气泡()
    {
        var ending = Occ("历史", 16, 45, 17, 30, index: 8);
        var next = Occ("自习", 18, 0, 18, 45, index: 9);
        var snapshot = Snap(ending, next);
        var toast = NoticeToastText.ForTransition(
            Transition(ClassTransitionKind.ClassEnded, ending, next, snapshot), 7000);
        Assert.Equal("下课", toast.Title);
        Assert.Equal("下节 18:00 自习", toast.Message);
    }

    // =========================================================================
    // 大课间 / 放学
    // =========================================================================

    [BanjiTest]
    public static void 大课间文案()
    {
        var next = Occ("化学", 10, 0, 10, 45, index: 3);
        var toast = NoticeToastText.BigBreak(next, 7000);
        Assert.Equal("大课间", toast.Title);
        Assert.Equal("下节 10:00 化学", toast.Message);
        Assert.Equal(7000, toast.HoldMilliseconds);

        var empty = NoticeToastText.BigBreak(null, 7000);
        Assert.Equal("大课间", empty.Title);
        Assert.Equal("课间休息一下", empty.Message);
    }

    [BanjiTest]
    public static void 大课间事件分发为大课间气泡()
    {
        var next = Occ("化学", 10, 0, 10, 45, index: 3);
        var toast = NoticeToastText.ForTransition(
            Transition(ClassTransitionKind.BreakStarted, null, next, Snap(next)), 7000);
        Assert.Equal("大课间", toast.Title);
        Assert.Equal("下节 10:00 化学", toast.Message);
    }

    [BanjiTest]
    public static void 放学文案()
    {
        var toast = NoticeToastText.SchoolEnd(Day.AddHours(17).AddMinutes(30), 8, 7000);
        Assert.Equal("放学", toast.Title);
        Assert.Equal("今天 17:30 放学 · 共 8 节课", toast.Message);
        Assert.Equal(NoticeToastKind.ClassEnd, toast.Kind);

        var unknown = NoticeToastText.SchoolEnd(null, 0, 7000);
        Assert.Equal("放学", unknown.Title);
        Assert.Equal("今天的课结束了", unknown.Message);
    }

    [BanjiTest]
    public static void 最后一节下课自动变成放学气泡()
    {
        var last = Occ("班会", 16, 45, 17, 30, index: 8);
        var snapshot = Snap(last); // Today 只有这一节，SchoolEnd = 17:30
        var transition = Transition(ClassTransitionKind.ClassEnded, last, null, snapshot);

        Assert.True(NoticeToastText.EndsAtSchoolEnd(transition), "最后一节下课应识别为放学");
        var toast = NoticeToastText.ForTransition(transition, 7000);
        Assert.Equal("放学", toast.Title);
        Assert.Equal("今天 17:30 放学 · 共 1 节课", toast.Message);
    }

    [BanjiTest]
    public static void 放学事件分发为放学气泡()
    {
        var last = Occ("班会", 16, 45, 17, 30, index: 8);
        var snapshot = Snap(last);
        var toast = NoticeToastText.ForTransition(new ClassTransitionEventArgs
        {
            Kind = ClassTransitionKind.SchoolEnded,
            Time = last.End,
            Current = last,
            Next = null,
            Snapshot = snapshot
        }, 7000);
        Assert.Equal("放学", toast.Title);
        Assert.Equal("今天 17:30 放学 · 共 1 节课", toast.Message);
    }

    [BanjiTest]
    public static void 普通下课不会误判为放学()
    {
        var ending = Occ("语文", 8, 0, 8, 45, index: 1);
        var next = Occ("数学", 8, 55, 9, 40, index: 2);
        var snapshot = Snap(ending, next);
        var transition = Transition(ClassTransitionKind.ClassEnded, ending, next, snapshot);
        Assert.False(NoticeToastText.EndsAtSchoolEnd(transition));
        Assert.Equal("下课", NoticeToastText.ForTransition(transition, 7000).Title);
    }

    // =========================================================================
    // NotificationService：改为抛气泡事件，不再创建全屏提示窗口
    // =========================================================================

    [BanjiTest]
    public static void 上课提醒抛气泡事件且不再全屏()
    {
        var service = new NotificationService();
        var toasts = new List<NoticeToast>();
        var dismissed = 0;
        service.ToastRequested += (_, toast) => toasts.Add(toast);
        service.ToastDismissed += (_, _) => dismissed++;

        var occ = Occ("语文", 8, 0, 8, 45);
        service.ShowClassStart(Transition(ClassTransitionKind.ClassStarted, occ, occ, Snap(occ)));

        Assert.Equal(1, toasts.Count);
        Assert.Equal("上课", toasts[0].Title);
        Assert.Equal("语文 · 08:00–08:45", toasts[0].Message);
        Assert.Equal(NoticeToastKind.ClassStart, toasts[0].Kind);
        Assert.Equal(7000, toasts[0].HoldMilliseconds);
        Assert.True(dismissed >= 1, "弹出新气泡前应先收回旧气泡（ToastDismissed）");
    }

    [BanjiTest]
    public static void 下课提醒抛气泡事件()
    {
        var service = new NotificationService();
        var toasts = new List<NoticeToast>();
        service.ToastRequested += (_, toast) => toasts.Add(toast);

        var ending = Occ("英语", 8, 55, 9, 40, index: 2);
        var next = Occ("物理", 10, 0, 10, 45, index: 3);
        service.ShowClassEnd(Transition(ClassTransitionKind.ClassEnded, ending, next, Snap(ending, next)));

        Assert.Equal(1, toasts.Count);
        Assert.Equal("下课", toasts[0].Title);
        Assert.Equal("下节 10:00 物理", toasts[0].Message);
    }

    [BanjiTest]
    public static void ShowToast与DismissAll事件契约()
    {
        var service = new NotificationService();
        NoticeToast? received = null;
        var dismissed = 0;
        service.ToastRequested += (_, toast) => received = toast;
        service.ToastDismissed += (_, _) => dismissed++;

        service.ShowToast(NoticeToastText.Generic("提醒", "测试正文", 3000));
        Assert.NotNull(received);
        Assert.Equal("提醒", received!.Title);
        Assert.Equal("测试正文", received.Message);
        Assert.Equal(3000, received.HoldMilliseconds);

        service.DismissAll();
        Assert.True(dismissed >= 1, "DismissAll 应触发 ToastDismissed");
    }
}
