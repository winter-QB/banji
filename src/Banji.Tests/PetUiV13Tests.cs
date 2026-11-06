using Banji.Controls.Pet;
using Banji.Core.Models;

namespace Banji.Tests;

/// <summary>v1.3.0 宠物交互纯逻辑测试：空闲自动收起状态机 / 气泡动画常量 / 课表折叠文案。</summary>
public static class PetUiV13Tests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 20, 0, 0);

    private static ScheduleSnapshot Snapshot(
        SchedulePhase phase,
        CourseOccurrence? current = null,
        CourseOccurrence? next = null,
        TimeSpan? untilCurrentEnd = null)
        => new()
        {
            Now = T0,
            Phase = phase,
            HasTimetable = phase != SchedulePhase.NoTimetable,
            Current = current,
            Next = next,
            UntilCurrentEnd = untilCurrentEnd,
            Today = current is null && next is null
                ? Array.Empty<CourseOccurrence>()
                : new[] { current, next }.Where(c => c is not null).Select(c => c!).ToList()
        };

    private static CourseOccurrence Course(int index, string name, int hour, int minute = 0)
        => new()
        {
            PeriodIndex = index,
            PeriodName = $"第{index}节",
            Course = name,
            Start = new DateTime(2026, 11, 6, hour, minute, 0),
            End = new DateTime(2026, 11, 6, hour, minute, 0).AddMinutes(45)
        };

    // =========================================================================
    // 空闲状态机
    // =========================================================================

    [BanjiTest]
    public static void 空闲到点收起宠物()
    {
        var logic = new IdleAutoHideLogic(T0);
        Assert.Equal(IdleHideAction.None, logic.Evaluate(T0.AddMinutes(9), 10, 0));
        Assert.Equal(IdleHideAction.CollapsePet, logic.Evaluate(T0.AddMinutes(10), 10, 0));
        Assert.Equal(IdleHideAction.CollapsePet, logic.Evaluate(T0.AddMinutes(30), 10, 0));

        logic.MarkPetCollapsed();
        Assert.Equal(IdleHideAction.None, logic.Evaluate(T0.AddMinutes(30), 10, 0));
    }

    [BanjiTest]
    public static void 空闲到点折叠课表()
    {
        var logic = new IdleAutoHideLogic(T0);
        Assert.Equal(IdleHideAction.None, logic.Evaluate(T0.AddMinutes(5), 0, 10));
        Assert.Equal(IdleHideAction.FoldTimetable, logic.Evaluate(T0.AddMinutes(10), 0, 10));

        logic.MarkTimetableFolded();
        Assert.Equal(IdleHideAction.None, logic.Evaluate(T0.AddMinutes(20), 0, 10));
    }

    [BanjiTest]
    public static void 交互会重置空闲计时()
    {
        var logic = new IdleAutoHideLogic(T0);
        logic.TouchPet(T0.AddMinutes(8));
        Assert.Equal(IdleHideAction.None, logic.Evaluate(T0.AddMinutes(15), 10, 0));
        Assert.Equal(IdleHideAction.CollapsePet, logic.Evaluate(T0.AddMinutes(18), 10, 0));

        logic.MarkPetCollapsed();
        logic.TouchPet(T0.AddMinutes(25)); // 点弧线展开
        Assert.False(logic.PetCollapsed, "交互后应视为已展开状态");
        Assert.Equal(IdleHideAction.None, logic.Evaluate(T0.AddMinutes(34), 10, 0));
        Assert.Equal(IdleHideAction.CollapsePet, logic.Evaluate(T0.AddMinutes(35), 10, 0));
    }

    [BanjiTest]
    public static void 设置零表示关闭自动收起()
    {
        var logic = new IdleAutoHideLogic(T0);
        Assert.Equal(IdleHideAction.None, logic.Evaluate(T0.AddHours(5), 0, 0));
        Assert.Null(logic.PetIdleRemaining(T0.AddHours(5), 0));
        Assert.Null(logic.TimetableIdleRemaining(T0.AddHours(5), 0));
    }

    [BanjiTest]
    public static void 宠物收起与课表折叠可以先后触发()
    {
        var logic = new IdleAutoHideLogic(T0);
        // 宠物 5 分钟、课表 8 分钟：5 分钟先收起宠物
        Assert.Equal(IdleHideAction.CollapsePet, logic.Evaluate(T0.AddMinutes(5), 5, 8));
        logic.MarkPetCollapsed();
        Assert.Equal(IdleHideAction.None, logic.Evaluate(T0.AddMinutes(7), 5, 8));
        Assert.Equal(IdleHideAction.FoldTimetable, logic.Evaluate(T0.AddMinutes(8), 5, 8));
    }

    [BanjiTest]
    public static void 空闲剩余时间计算()
    {
        var logic = new IdleAutoHideLogic(T0);
        Assert.Equal(TimeSpan.FromMinutes(10), logic.PetIdleRemaining(T0, 10));
        Assert.Equal(TimeSpan.FromMinutes(4), logic.PetIdleRemaining(T0.AddMinutes(6), 10));
        Assert.Equal(TimeSpan.Zero, logic.PetIdleRemaining(T0.AddMinutes(30), 10));
        logic.MarkPetCollapsed();
        Assert.Null(logic.PetIdleRemaining(T0, 10));
    }

    [BanjiTest]
    public static void 展开恢复后重新计时()
    {
        var logic = new IdleAutoHideLogic(T0);
        logic.MarkPetCollapsed();
        logic.MarkTimetableFolded();
        logic.MarkPetExpanded(T0.AddMinutes(20));
        logic.MarkTimetableUnfolded(T0.AddMinutes(20));
        Assert.Equal(IdleHideAction.None, logic.Evaluate(T0.AddMinutes(29), 10, 10));
        Assert.Equal(IdleHideAction.CollapsePet, logic.Evaluate(T0.AddMinutes(30), 10, 10));
    }

    // =========================================================================
    // 气泡动画常量
    // =========================================================================

    [BanjiTest]
    public static void 气泡动画参数符合契约()
    {
        Assert.True(NoticeBubbleAnimationSpec.IsSane(), "气泡动画常量不合法");
        Assert.InRange(NoticeBubbleAnimationSpec.EnterDurationMs, 280, 450);
        Assert.InRange(NoticeBubbleAnimationSpec.ExitDurationMs, 180, 320);
        Assert.True(NoticeBubbleAnimationSpec.EnterOvershootScale > 1.0, "进入应有轻微回弹 overshoot");
        Assert.True(NoticeBubbleAnimationSpec.EnterStartScale < 1.0);
        Assert.True(NoticeBubbleAnimationSpec.ExitEndScale < 1.0);
        Assert.True(NoticeBubbleAnimationSpec.EnterTranslateY > 0, "进入应上移");
    }

    [BanjiTest]
    public static void 气泡动画使用Spring曲线()
    {
        var (x1, y1, x2, y2) = NoticeBubbleAnimationSpec.SpringControlPoints;
        Assert.InRange(x1, 0.0, 1.0);
        Assert.InRange(x2, 0.0, 1.0);
        Assert.True(y1 > 1.0, $"spring 第一控制点 y 应大于 1 才有回弹：{y1}");
        Assert.InRange(y2, 0.8, 1.2);
    }

    // =========================================================================
    // 课表折叠态文案
    // =========================================================================

    [BanjiTest]
    public static void 折叠态显示下节课时间和科目()
    {
        var info = FoldedTimetableFormatter.Fold(
            Snapshot(SchedulePhase.Break, next: Course(6, "美术", 15, 50)), T0);
        Assert.True(info.HasData);
        Assert.Equal("下节", info.Label);
        Assert.Equal("15:50 美术", info.MainText);
        Assert.Null(info.Secondary);
    }

    [BanjiTest]
    public static void 上课时折叠态带下课倒计时和下节信息()
    {
        var info = FoldedTimetableFormatter.Fold(
            Snapshot(SchedulePhase.InClass,
                current: Course(5, "体育", 14, 0),
                next: Course(6, "美术", 15, 50),
                untilCurrentEnd: TimeSpan.FromSeconds(42 * 60 + 54)),
            T0);
        Assert.True(info.HasData);
        Assert.Equal("下课", info.Label);
        Assert.Equal("42:54", info.MainText);
        Assert.Equal("下节 15:50 美术", info.Secondary);
    }

    [BanjiTest]
    public static void 折叠态无课表与放学后()
    {
        var none = FoldedTimetableFormatter.Fold(null, T0);
        Assert.False(none.HasData);
        Assert.Equal("暂无课表", none.MainText);

        var empty = FoldedTimetableFormatter.Fold(Snapshot(SchedulePhase.NoTimetable), T0);
        Assert.False(empty.HasData);

        var after = FoldedTimetableFormatter.Fold(Snapshot(SchedulePhase.AfterSchool), T0);
        Assert.False(after.HasData);
        Assert.Equal("今天的课已上完", after.MainText);
    }

    [BanjiTest]
    public static void 折叠态在最后一节课时提示最后一节()
    {
        var info = FoldedTimetableFormatter.Fold(
            Snapshot(SchedulePhase.InClass, current: Course(8, "历史", 16, 45),
                untilCurrentEnd: TimeSpan.FromSeconds(90)),
            T0);
        Assert.Equal("01:30", info.MainText);
        Assert.Equal("最后一节课", info.Secondary);
    }
}
