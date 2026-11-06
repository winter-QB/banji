using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Banji.Core.Infrastructure;
using Banji.Core.Settings;

namespace Banji.Controls.Pet;

/// <summary>
/// 宠物本体（第二轮用户要求改版）：<b>方形、扁扁的</b>小方块生物，
/// 两只小方形手 + 两只小方形脚，两只纯黑实心小圆点眼睛，无腮红/鼻子/嘴巴/高光；
/// 默认雾蓝色。保留呼吸浮动、周期眨眼、悬浮轻微放大提亮、被摸时的挤压反馈。
/// 交互（拖动窗口 / 点击对话）由 <see cref="Banji.Views.PetWindow"/> 负责，本控件只负责画和动。
/// </summary>
public partial class PetControl : UserControl
{
    private readonly Random _random = new();
    private readonly DispatcherTimer _blinkTimer;
    private bool _blinkEnabled = true;
    private bool _bobEnabled = true;
    private bool _animationsRunning;
    private bool _hovered;

    public PetControl()
    {
        InitializeComponent();
        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _blinkTimer.Tick += (_, _) => Blink();
        MouseEnter += (_, _) => SetHovered(true);
        MouseLeave += (_, _) => SetHovered(false);
        Loaded += (_, _) => StartAnimations();
        Unloaded += (_, _) => StopAnimations();
    }

    /// <summary>应用宠物外观设置；颜色立即生效，动画开关及时启停。</summary>
    public void ApplySettings(PetStyleSettings style, bool blinkEnabled, bool bobEnabled)
    {
        try
        {
            var body = ParseColor(style.BodyColor, Color.FromRgb(0x8F, 0xB6, 0xF2));
            var shade = ParseColor(style.BodyShadeColor, body);
            var eye = ParseColor(style.EyeColor, Color.FromRgb(0x23, 0x1F, 0x20));

            var outline = Darken(body, 0.30);
            var outlineBrush = new SolidColorBrush(outline);
            outlineBrush.Freeze();

            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            gradient.GradientStops.Add(new GradientStop(Lighten(body, 0.10), 0));
            gradient.GradientStops.Add(new GradientStop(body, 0.56));
            gradient.GradientStops.Add(new GradientStop(Darken(shade, 0.04), 1));
            gradient.Freeze();

            Body.Background = gradient;
            Body.BorderBrush = outlineBrush;

            var limbBrush = new SolidColorBrush(Darken(shade, 0.07));
            limbBrush.Freeze();
            var limbOutline = new SolidColorBrush(outline);
            limbOutline.Freeze();

            HandLeft.Background = limbBrush;
            HandRight.Background = limbBrush;
            FootLeft.Background = limbBrush;
            FootRight.Background = limbBrush;
            HandLeft.BorderBrush = limbOutline;
            HandRight.BorderBrush = limbOutline;
            FootLeft.BorderBrush = limbOutline;
            FootRight.BorderBrush = limbOutline;

            var eyeBrush = new SolidColorBrush(eye);
            eyeBrush.Freeze();
            EyeLeft.Fill = eyeBrush;
            EyeRight.Fill = eyeBrush;

            _blinkEnabled = blinkEnabled;
            _bobEnabled = bobEnabled;
            if (!blinkEnabled) ResetEyes();
            if (_animationsRunning) StartAnimations();
        }
        catch (Exception ex)
        {
            Log.Error("应用宠物外观设置失败", ex);
        }
    }

    /// <summary>被摸一下时的轻微反馈：身体轻轻一缩，眼睛跟着眨一下。</summary>
    public void PlayTouchReaction()
    {
        try
        {
            var squish = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(240)
            };
            squish.KeyFrames.Add(new EasingDoubleKeyFrame(0.94, KeyTime.FromPercent(0.32), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            squish.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));

            var squishY = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(240)
            };
            squishY.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromPercent(0.32), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            squishY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));

            squish.Completed += (_, _) => ApplyHoverAnimation();
            HoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, squish);
            HoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, squishY);
            Blink();
        }
        catch (Exception ex)
        {
            Log.Error("播放宠物点击反馈失败", ex);
        }
    }

    public void StartAnimations()
    {
        _animationsRunning = true;
        StartBob();
        ScheduleNextBlink();
    }

    public void StopAnimations()
    {
        _animationsRunning = false;
        _blinkTimer.Stop();
        try
        {
            BobOffset.BeginAnimation(TranslateTransform.YProperty, null);
            BobOffset.Y = 0;
            ResetEyes();
        }
        catch (Exception ex)
        {
            Log.Error("停止宠物动画失败", ex);
        }
    }

    private void StartBob()
    {
        if (!_bobEnabled || !_animationsRunning)
        {
            BobOffset.BeginAnimation(TranslateTransform.YProperty, null);
            BobOffset.Y = 0;
            return;
        }

        var bob = new DoubleAnimation
        {
            From = 0,
            To = -3.4,
            Duration = TimeSpan.FromSeconds(2.3),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        BobOffset.BeginAnimation(TranslateTransform.YProperty, bob);
    }

    private void ScheduleNextBlink()
    {
        if (!_animationsRunning || !_blinkEnabled)
        {
            _blinkTimer.Stop();
            return;
        }
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(3200 + _random.NextDouble() * 4200);
        _blinkTimer.Start();
    }

    private void Blink()
    {
        _blinkTimer.Stop();
        if (!_blinkEnabled || !_animationsRunning)
        {
            ResetEyes();
            return;
        }

        try
        {
            var squash = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(120),
                FillBehavior = FillBehavior.Stop
            };
            squash.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            squash.KeyFrames.Add(new EasingDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(56)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            EyeLeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
            EyeRightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
        }
        catch (Exception ex)
        {
            Log.Error("宠物眨眼动画失败", ex);
        }

        ScheduleNextBlink();
    }

    private void ResetEyes()
    {
        EyeLeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        EyeRightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        EyeLeftBlink.ScaleY = 1;
        EyeRightBlink.ScaleY = 1;
    }

    private void SetHovered(bool hovered)
    {
        if (_hovered == hovered) return;
        _hovered = hovered;
        ApplyHoverAnimation();
    }

    private void ApplyHoverAnimation()
    {
        try
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var scaleX = new DoubleAnimation(_hovered ? 1.05 : 1.0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
            var scaleY = new DoubleAnimation(_hovered ? 1.045 : 1.0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
            var glow = new DoubleAnimation(_hovered ? 0.12 : 0.0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
            HoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            HoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            HoverGlow.BeginAnimation(OpacityProperty, glow);
        }
        catch (Exception ex)
        {
            Log.Error("宠物悬浮动画失败", ex);
        }
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var value = (Color)ColorConverter.ConvertFromString(hex.Trim());
            return value;
        }
        catch
        {
            return fallback;
        }
    }

    private static Color Lighten(Color color, double amount)
        => Color.FromRgb(
            (byte)Math.Round(color.R + (255 - color.R) * amount),
            (byte)Math.Round(color.G + (255 - color.G) * amount),
            (byte)Math.Round(color.B + (255 - color.B) * amount));

    private static Color Darken(Color color, double amount)
        => Color.FromRgb(
            (byte)Math.Round(color.R * (1 - amount)),
            (byte)Math.Round(color.G * (1 - amount)),
            (byte)Math.Round(color.B * (1 - amount)));
}
