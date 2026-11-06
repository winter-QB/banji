namespace Banji.Controls.Pet;

/// <summary>
/// 提醒气泡动画参数（v1.3.0）：灵动冒出 → 轻柔回弹（轻微 overshoot）→ 定住 → 收回。
/// 纯常量/纯函数，便于单测校验“动画曲线与时长符合契约”。
/// <para>spring 曲线用三次贝塞尔近似 iOS/鸿蒙的 KeySpline 控制点 (0.34,1.56,0.64,1)。</para>
/// </summary>
public static class NoticeBubbleAnimationSpec
{
    // ---- 进入 ----
    public const double EnterDurationMs = 360;
    public const double EnterStartScale = 0.62;
    public const double EnterOvershootScale = 1.05;
    /// <summary>overshoot 峰值出现的时间点（毫秒，相对进入开始）。</summary>
    public const double EnterOvershootAtMs = 240;
    public const double EnterEndScale = 1.0;
    /// <summary>进入时从下方上移的像素。</summary>
    public const double EnterTranslateY = 10;
    public const double FadeInMs = 200;

    // ---- 收回 ----
    public const double ExitDurationMs = 260;
    public const double ExitEndScale = 0.94;
    public const double ExitTranslateY = 6;
    public const double FadeOutMs = 220;

    // ---- spring KeySpline 控制点（x1,y1,x2,y2）----
    public const double SpringX1 = 0.34;
    public const double SpringY1 = 1.56;
    public const double SpringX2 = 0.64;
    public const double SpringY2 = 1.0;

    /// <summary>“轻柔回弹”的 KeySpline 控制点；WPF 里用 new KeySpline(SpringX1, SpringY1, SpringX2, SpringY2)。</summary>
    public static readonly (double X1, double Y1, double X2, double Y2) SpringControlPoints =
        (SpringX1, SpringY1, SpringX2, SpringY2);

    /// <summary>校验参数区间（测试/启动自检用）：返回 false 说明常量被改坏了。</summary>
    public static bool IsSane()
    {
        return EnterDurationMs >= 200 && EnterDurationMs <= 600
               && ExitDurationMs >= 120 && ExitDurationMs <= 400
               && EnterStartScale > 0.3 && EnterStartScale < 0.9
               && EnterOvershootScale > 1.0 && EnterOvershootScale < 1.2
               && Math.Abs(EnterEndScale - 1.0) < 1e-9
               && ExitEndScale > 0.8 && ExitEndScale < 1.0
               && EnterOvershootAtMs > 0 && EnterOvershootAtMs < EnterDurationMs
               && EnterTranslateY > 0 && ExitTranslateY >= 0
               && SpringX1 >= 0 && SpringX1 <= 1
               && Math.Abs(SpringY2 - 1.0) < 0.3;
    }
}

