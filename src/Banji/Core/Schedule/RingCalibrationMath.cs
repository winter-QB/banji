using Banji.Core.Models;

namespace Banji.Core.Schedule;

/// <summary>
/// 铃声校准的纯计算逻辑（不依赖窗口/真实时钟，便于单元测试）。
/// <para>
/// 语义约定：程序显示的时间 = 课表时间 + BellOffsetSeconds。
/// 用户“听到铃声”的实际时刻记为 <c>listenedAt</c>，最近一个课表边界的显示时刻记为 <c>boundaryTime</c>，
/// 偏差 <c>delta = listenedAt - boundaryTime</c>；
/// 要让显示时间对齐真实铃声，新误差 = 当前误差 + delta，并夹在 ±60 秒。
/// （铃声比程序快 → delta 为负 → 建议负误差；铃声慢 → 正误差。）
/// </para>
/// </summary>
public static class RingCalibrationMath
{
    /// <summary>铃声误差允许范围（秒），与 AppSettings.Normalize 保持一致。</summary>
    public const int MaxOffsetSeconds = 60;

    /// <summary>寻找“最近的铃声边界”的容差：点按钮前后 2 分钟内的边界才认。</summary>
    public static readonly TimeSpan SearchTolerance = TimeSpan.FromSeconds(120);

    /// <summary>从候选边界里找离 listenedAt 最近的一个（默认容差 2 分钟）；没有则返回 null。</summary>
    public static ClassBoundary? FindNearest(
        IReadOnlyList<ClassBoundary>? boundaries, DateTime listenedAt, TimeSpan? tolerance = null)
    {
        if (boundaries is null || boundaries.Count == 0) return null;
        var limit = (tolerance ?? SearchTolerance).TotalSeconds;
        if (limit < 0) limit = 0;

        ClassBoundary? best = null;
        var bestDistance = double.MaxValue;
        foreach (var boundary in boundaries)
        {
            var distance = Math.Abs((listenedAt - boundary.Time).TotalSeconds);
            if (distance > limit || distance >= bestDistance) continue;
            best = boundary;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>实际听到铃声时刻与边界显示时刻的偏差（秒，四舍五入；正=听到得晚）。</summary>
    public static int DeltaSeconds(DateTime listenedAt, DateTime boundaryTime)
        => (int)Math.Round((listenedAt - boundaryTime).TotalSeconds, MidpointRounding.AwayFromZero);

    /// <summary>根据当前误差与本次偏差，计算建议的新误差（夹在 ±60 秒）。</summary>
    public static int SuggestOffsetSeconds(int currentOffsetSeconds, DateTime listenedAt, DateTime boundaryTime)
        => ClampOffset(ClampOffset(currentOffsetSeconds) + DeltaSeconds(listenedAt, boundaryTime));

    /// <summary>把任意误差值收敛到 [-60, 60]。</summary>
    public static int ClampOffset(int seconds) => Math.Clamp(seconds, -MaxOffsetSeconds, MaxOffsetSeconds);

    /// <summary>误差的文字说明（给用户看正负含义）。</summary>
    public static string DescribeOffset(int seconds)
    {
        var clamped = ClampOffset(seconds);
        return clamped switch
        {
            0 => "与课表时间一致",
            > 0 => $"比课表时间晚 {clamped} 秒",
            _ => $"比课表时间早 {-clamped} 秒"
        };
    }

    /// <summary>带符号的误差文本：+6 / -7 / 0。</summary>
    public static string FormatSigned(int seconds)
    {
        var clamped = ClampOffset(seconds);
        return clamped > 0 ? $"+{clamped}" : clamped.ToString();
    }
}
