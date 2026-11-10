using Banji.Controls.Pet;
using Banji.Core.Models;

namespace Banji.Tests;

/// <summary>v1.5.0 宠物/课表交互纯逻辑测试：上课专注窗口、课表拖动阈值/夹取、右缘吸附。</summary>
public static class PetUiV15Tests
{
    private static CourseOccurrence Course(DateTime start, DateTime end) => new()
    {
        PeriodIndex = 1,
        PeriodName = "第1节",
        Course = "数学",
        Start = start,
        End = end
    };

    private static ScheduleSnapshot InClass(DateTime now, DateTime start, DateTime end) => new()
    {
        Now = now,
        Phase = SchedulePhase.InClass,
        HasTimetable = true,
        Current = Course(start, end),
        Today = new[] { Course(start, end) }
    };

    // =========================================================================
    // 上课专注窗口
    // =========================================================================

    [BanjiTest]
    public static void 专注窗口为上课后三十秒到下课前三十秒()
    {
        var start = new DateTime(2026, 11, 6, 10, 0, 0);
        var end = start.AddMinutes(45);

        Assert.False(ClassFocusMode.IsFocusWindow(start, start, end), "刚上课不应进入专注");
        Assert.False(ClassFocusMode.IsFocusWindow(start.AddSeconds(29), start, end), "上课后 29 秒不应进入专注");
        Assert.True(ClassFocusMode.IsFocusWindow(start.AddSeconds(30), start, end), "上课后 30 秒应进入专注");
        Assert.True(ClassFocusMode.IsFocusWindow(start.AddMinutes(20), start, end), "上课中段应处于专注");
        Assert.True(ClassFocusMode.IsFocusWindow(end.AddSeconds(-30), start, end), "下课前 30 秒仍处于专注");
        Assert.False(ClassFocusMode.IsFocusWindow(end.AddSeconds(-29), start, end), "下课前 29 秒应退出专注");
        Assert.False(ClassFocusMode.IsFocusWindow(end, start, end), "下课时应退出专注");
    }

    [BanjiTest]
    public static void 短课不会进入专注且非上课返回否()
    {
        var start = new DateTime(2026, 11, 6, 10, 0, 0);
        Assert.False(ClassFocusMode.IsFocusWindow(start.AddSeconds(29), start, start.AddSeconds(50)), "50 秒短课不应进入专注");
        Assert.False(ClassFocusMode.IsFocusWindow(start, start, start), "零时长不应进入专注");
        Assert.True(ClassFocusMode.IsFocusWindow(start.AddSeconds(30), start, start.AddSeconds(60)), "60 秒课在正中间为专注");

        var end = start.AddMinutes(45);
        Assert.False(ClassFocusMode.IsInClassFocus(null));
        Assert.False(ClassFocusMode.IsInClassFocus(new ScheduleSnapshot { Now = start, Phase = SchedulePhase.Break }));
        Assert.False(ClassFocusMode.IsInClassFocus(new ScheduleSnapshot { Now = start, Phase = SchedulePhase.InClass }));
        Assert.True(ClassFocusMode.IsInClassFocus(InClass(start.AddMinutes(10), start, end)));
        Assert.False(ClassFocusMode.IsInClassFocus(InClass(start.AddSeconds(10), start, end)));
    }

    [BanjiTest]
    public static void 保护时间可配置()
    {
        var start = new DateTime(2026, 11, 6, 10, 0, 0);
        var end = start.AddMinutes(45);
        Assert.False(ClassFocusMode.IsFocusWindow(start.AddSeconds(30), start, end, 60), "保护 60 秒时上课后 30 秒还不算专注");
        Assert.True(ClassFocusMode.IsFocusWindow(start.AddSeconds(60), start, end, 60));
        Assert.Equal(30, ClassFocusMode.EdgeGuardSeconds);
    }

    // =========================================================================
    // 课表拖动
    // =========================================================================

    [BanjiTest]
    public static void 拖动阈值区分点击与拖动()
    {
        Assert.False(PanelDragMath.ExceedsThreshold(0, 0));
        Assert.False(PanelDragMath.ExceedsThreshold(3.9, 3.9));
        Assert.False(PanelDragMath.ExceedsThreshold(2, -2));
        Assert.True(PanelDragMath.ExceedsThreshold(4, 0));
        Assert.True(PanelDragMath.ExceedsThreshold(0, -4));
        Assert.True(PanelDragMath.ExceedsThreshold(10, 0));
        Assert.Equal(4, PanelDragMath.DragThreshold);
    }

    [BanjiTest]
    public static void 拖动偏移按基准累加并夹取()
    {
        var (x, y) = PanelDragMath.ResolveOffsets(40, -20, 10, 5);
        Assert.Equal(50, x);
        Assert.Equal(-15, y);

        var (cx, cy) = PanelDragMath.ResolveOffsets(0, 0, 5000, -5000);
        Assert.Equal(800, cx);
        Assert.Equal(-800, cy);

        Assert.Equal(-800, PanelDragMath.ResolveOffsets(-790, 0, -100, 0).X);
        Assert.Equal(800, PanelDragMath.ClampOffset(900));
        Assert.Equal(-800, PanelDragMath.ClampOffset(-900));
        Assert.Equal(12, PanelDragMath.ClampOffset(12));
    }

    // =========================================================================
    // 右缘吸附
    // =========================================================================

    [BanjiTest]
    public static void 把手窗口左缘贴工作区右缘()
    {
        var workRight = 1440.0;
        var left = EdgeSnapMath.SnapLeftToWorkRight(workRight, 26);
        Assert.Equal(1414, left);
        Assert.True(EdgeSnapMath.IsFlushRight(left, 26, workRight), "窗口右缘应精确贴工作区右缘");
        Assert.False(EdgeSnapMath.IsFlushRight(left - 2, 26, workRight), "有 2px 缝时不应判定为贴齐");

        var fractional = EdgeSnapMath.SnapLeftToWorkRight(1439.6, 16);
        Assert.True(EdgeSnapMath.IsFlushRight(fractional, 16, 1439.6, 0.6), "DPI 小数右缘允许半像素容差");
    }

    [BanjiTest]
    public static void 把手窗口垂直方向夹在工作区内()
    {
        var workTop = 0.0;
        var workBottom = 912.0;
        Assert.Equal(100, EdgeSnapMath.ClampTop(100, 130, workTop, workBottom));
        Assert.Equal(0, EdgeSnapMath.ClampTop(-50, 130, workTop, workBottom));
        Assert.Equal(782, EdgeSnapMath.ClampTop(1000, 130, workTop, workBottom));
        Assert.True(EdgeSnapMath.ClampTop(1000, 130, workTop, workBottom) + 130 <= workBottom);
        Assert.Equal(workTop, EdgeSnapMath.ClampTop(10, 130, workTop, workTop + 100), "工作区比把手矮时贴顶");
    }
}
