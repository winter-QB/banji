using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Banji.Controls.Pet;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;

namespace Banji.Views;

/// <summary>
/// v1.3.0 提醒气泡窗：替代过去的全屏上课/下课提示（计时器超时仍走全屏）。
/// 白底圆角卡片 + 深色文字 + 轻阴影；灵动冒出 → 轻柔回弹（spring overshoot）→ 定住 N 秒 → 收回。
/// 独立置顶、不抢焦点、click-through（不挡 PPT 操作），窗口标题固定 <c>BanjiNotice</c>（自动化探测用）。
/// </summary>
public partial class NoticeBubbleWindow : Window
{
    private readonly DispatcherTimer _holdTimer;
    private readonly DispatcherTimer _topmostTimer;
    private bool _showing;
    private int _holdMilliseconds = 7000;

    public NoticeBubbleWindow()
    {
        InitializeComponent();
        Opacity = 0;

        _holdTimer = new DispatcherTimer(DispatcherPriority.Background);
        _holdTimer.Tick += (_, _) => BeginHide();

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();

        SizeChanged += (_, _) =>
        {
            if (_showing) PlaceNearPet();
        };
        Closed += (_, _) =>
        {
            _holdTimer.Stop();
            _topmostTimer.Stop();
            _showing = false;
        };
    }

    /// <summary>气泡当前是否正在展示（含动画）。</summary>
    public bool IsShowing => _showing && IsVisible;

    // =========================================================================
    // 对外接口（PetWindow 调用）
    // =========================================================================

    /// <summary>显示一条提醒；若当前已有提醒则直接替换内容并重启动画。</summary>
    public void ShowToast(NoticeToast toast)
    {
        if (toast is null) return;
        try
        {
            ApplyContent(toast);

            if (!IsVisible)
            {
                Opacity = 0;
                Show();
            }
            UpdateLayout();
            ApplyContentSize();
            UpdateLayout();
            PlaceNearPet();

            _showing = true;
            PlayEnter();
            RestartHold();
            ReassertTopmost();
            _topmostTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("显示提醒气泡失败", ex);
        }
    }

    /// <summary>立即收回（ToastDismissed / 应用退出时调用）。</summary>
    public void Dismiss()
    {
        if (!IsVisible || !_showing) return;
        BeginHide();
    }

    /// <summary>宠物窗移动/缩放/收起后重新贴合。</summary>
    public void RepositionToPet()
    {
        if (!IsVisible) return;
        try
        {
            UpdateLayout();
            PlaceNearPet();
        }
        catch (Exception ex)
        {
            Log.Warn($"重新定位提醒气泡失败：{ex.Message}");
        }
    }

    /// <summary>关闭并释放（PetWindow 关闭时调用）。</summary>
    public void CloseCleanup()
    {
        try
        {
            _showing = false;
            _holdTimer.Stop();
            _topmostTimer.Stop();
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("关闭提醒气泡失败", ex);
        }
    }

    // =========================================================================
    // 内容 / 尺寸 / 定位
    // =========================================================================

    private void ApplyContent(NoticeToast toast)
    {
        var isPreview = toast.Kind == NoticeToastKind.Preview;
        var kindLabel = toast.Kind switch
        {
            NoticeToastKind.ClassStart => "上课提醒",
            NoticeToastKind.ClassEnd => "下课提醒",
            NoticeToastKind.Preview => "提醒预览",
            _ => "班机提醒"
        };
        var accent = toast.Kind switch
        {
            NoticeToastKind.ClassStart => Color.FromRgb(0x31, 0x7F, 0xFF),
            NoticeToastKind.ClassEnd => Color.FromRgb(0xFF, 0x9F, 0x1C),
            NoticeToastKind.Preview => Color.FromRgb(0x9A, 0xA3, 0xAF),
            _ => Color.FromRgb(0x31, 0x7F, 0xFF)
        };
        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();

        var title = string.IsNullOrWhiteSpace(toast.Title) ? kindLabel : toast.Title.Trim();
        TitleText.Text = title;
        TitleText.Foreground = accentBrush;
        AccentBar.Background = accentBrush;

        var message = toast.Message?.Trim() ?? "";
        if (message.Length > 180) message = message[..180];
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (isPreview && message.Length == 0)
        {
            MessageText.Text = "这是一条提醒气泡预览";
            MessageText.Visibility = Visibility.Visible;
        }

        TimeText.Text = DateTime.Now.ToString("HH:mm");
        _holdMilliseconds = Math.Clamp(
            toast.HoldMilliseconds > 0 ? toast.HoldMilliseconds : SafeDefaultHoldMilliseconds(),
            1500, 60000);
    }

    private static int SafeDefaultHoldMilliseconds()
    {
        try { return Math.Clamp(AppServices.Current.Settings.Current.NoticeBubbleHoldSeconds, 2, 30) * 1000; }
        catch { return 7000; }
    }

    private void ApplyContentSize()
    {
        Card.Measure(new Size(380, double.PositiveInfinity));
        var size = Card.DesiredSize;
        Width = Math.Ceiling(Math.Clamp(size.Width, 190, 340)) + 40;
        Height = Math.Ceiling(Math.Max(56, size.Height)) + 38;
    }

    private void PlaceNearPet()
    {
        try
        {
            var pet = AppServices.Current.PetWindow;
            if (pet is null || !pet.IsVisible) return;
            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            var work = SystemParameters.WorkArea;
            var width = ActualWidth;
            var height = ActualHeight;
            var anchor = pet.GetNoticeAnchorRect();
            const double edge = 8;
            const double gap = 10;

            // 首选：贴在宠物/面板上方（水平对齐宠物中心）；上方放不下就放到宠物左侧。
            var left = anchor.Left + anchor.Width * 0.5 - width * 0.5;
            var top = pet.Top - height - gap;
            if (top < work.Top + edge)
            {
                left = anchor.Left - width - 12;
                top = anchor.Top + anchor.Height * 0.5 - height * 0.5;
            }

            left = Math.Clamp(left, work.Left + edge, Math.Max(work.Left + edge, work.Right - edge - width));
            top = Math.Clamp(top, work.Top + edge, Math.Max(work.Top + edge, work.Bottom - edge - height));
            Left = Math.Round(left);
            Top = Math.Round(top);
        }
        catch (Exception ex)
        {
            Log.Warn($"定位提醒气泡失败：{ex.Message}");
        }
    }

    // =========================================================================
    // 动画
    // =========================================================================

    private void PlayEnter()
    {
        var spring = new KeySpline(
            NoticeBubbleAnimationSpec.SpringX1, NoticeBubbleAnimationSpec.SpringY1,
            NoticeBubbleAnimationSpec.SpringX2, NoticeBubbleAnimationSpec.SpringY2);

        PopScale.BeginAnimation(ScaleTransform.ScaleXProperty, BuildEnterScale(spring));
        PopScale.BeginAnimation(ScaleTransform.ScaleYProperty, BuildEnterScale(spring));
        PopOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Ms(NoticeBubbleAnimationSpec.EnterDurationMs))
        {
            From = NoticeBubbleAnimationSpec.EnterTranslateY,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(NoticeBubbleAnimationSpec.FadeInMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static DoubleAnimationUsingKeyFrames BuildEnterScale(KeySpline spring)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = Ms(NoticeBubbleAnimationSpec.EnterDurationMs)
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(
            NoticeBubbleAnimationSpec.EnterStartScale, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            NoticeBubbleAnimationSpec.EnterOvershootScale,
            KeyTime.FromTimeSpan(Ms(NoticeBubbleAnimationSpec.EnterOvershootAtMs)), spring));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            NoticeBubbleAnimationSpec.EnterEndScale,
            KeyTime.FromTimeSpan(Ms(NoticeBubbleAnimationSpec.EnterDurationMs)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        return animation;
    }

    private void BeginHide()
    {
        if (!_showing) return;
        _showing = false;
        _holdTimer.Stop();
        _topmostTimer.Stop();

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var scale = new DoubleAnimation(NoticeBubbleAnimationSpec.ExitEndScale, Ms(NoticeBubbleAnimationSpec.ExitDurationMs))
        {
            EasingFunction = ease
        };
        PopScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        PopScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale.Clone());
        PopOffset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(NoticeBubbleAnimationSpec.ExitTranslateY, Ms(NoticeBubbleAnimationSpec.ExitDurationMs))
            {
                From = 0,
                EasingFunction = ease
            });

        var fade = new DoubleAnimation(0, Ms(NoticeBubbleAnimationSpec.FadeOutMs))
        {
            EasingFunction = ease
        };
        fade.Completed += (_, _) =>
        {
            if (!_showing)
            {
                try { Hide(); } catch { /* ignore */ }
            }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void RestartHold()
    {
        _holdTimer.Stop();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(_holdMilliseconds);
        _holdTimer.Start();

        // 进度条匀速缩短（先快后慢会显得拖沓，这里保持线性）
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, Ms(_holdMilliseconds)) { From = 1 });
    }

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private void ReassertTopmost()
    {
        try
        {
            NativeMethods.ReassertTopmost(new WindowInteropHelper(this).Handle);
        }
        catch
        {
            // 置顶失败不影响展示
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetToolWindow(handle, true);   // 不进 Alt-Tab
            NativeMethods.SetNoActivate(handle, true);   // 不抢焦点
            NativeMethods.SetClickThrough(handle, true); // 不挡 PPT 点击
            NativeMethods.ReassertTopmost(handle);
        }
        catch (Exception ex)
        {
            Log.Error("设置提醒气泡窗口扩展样式失败", ex);
        }
    }

}


