using Banji.Core.Models;

namespace Banji.Controls.Pet;

/// <summary>
/// v1.5.0 上课专注模式判定（纯函数）：`Phase==InClass && now >= Current.Start+30s && now <= Current.End-30s`。
/// 短课（时长 &lt; 2*保护时间）永远不进入专注，避免边界抖动。
/// </summary>
public static class ClassFocusMode
{
    /// <summary>上课开始/下课前的保护时间（秒）。</summary>
    public const int EdgeGuardSeconds = 30;

    /// <summary>给定一节课与当前时间，是否处于“专注窗口”。</summary>
    public static bool IsFocusWindow(DateTime now, DateTime start, DateTime end, int guardSeconds = EdgeGuardSeconds)
    {
        var guard = Math.Max(0, guardSeconds);
        if (end <= start) return false;
        var from = start.AddSeconds(guard);
        var to = end.AddSeconds(-guard);
        if (to < from) return false; // 短课：整节课都不进入专注
        return now >= from && now <= to;
    }

    /// <summary>根据课表快照判断是否处于专注窗口（非上课/无当前课返回 false）。</summary>
    public static bool IsInClassFocus(ScheduleSnapshot? snapshot, int guardSeconds = EdgeGuardSeconds)
    {
        if (snapshot is null || snapshot.Phase != SchedulePhase.InClass) return false;
        if (snapshot.Current is not { } current) return false;
        return IsFocusWindow(snapshot.Now, current.Start, current.End, guardSeconds);
    }
}
