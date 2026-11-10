using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;

namespace Banji.Views;

/// <summary>
/// Win11 风格圆角无边框置顶菜单：设置/仪表盘/花名册/课表/课堂时段/显示隐藏课表/
/// 宠物大小（滑块+数值框）/按钮透明度/始终置顶/开机自启/杀死整个程序。
/// 点击外部或按 ESC 关闭。
/// </summary>
public partial class MenuFlyout : Window
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Banji";

    private const int VkLeftButton = 0x01;
    private const int VkEscape = 0x1B;

    private readonly TranslateTransform _popOffset = new(0, 0);
    private readonly DispatcherTimer _outsideClickWatch;
    // 默认 true：InitializeComponent 里 Slider.Value 被 Minimum 强制修正时会触发 ValueChanged，
    // 不能在构造阶段把用户设置改掉（历史上把 UiScale 改成了 0.1）。
    private bool _syncing = true;
    private bool _anchorSet;
    private bool _wasActivated;
    private bool _leftWasDown;
    private bool _placing;
    private Point _anchor;

    public MenuFlyout()
    {
        InitializeComponent();
        Opacity = 0;

        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(1, 1));
        group.Children.Add(_popOffset);
        Outer.RenderTransformOrigin = new Point(0.92, 0.2);
        Outer.RenderTransform = group;

        _outsideClickWatch = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _outsideClickWatch.Tick += (_, _) => WatchOutsideClick();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        Activated += (_, _) => _wasActivated = true;
        Deactivated += (_, _) =>
        {
            if (_wasActivated && IsVisible) Close();
        };
        Loaded += (_, _) =>
        {
            try
            {
                // 小屏幕下菜单不越出工作区：内容超出时内部滚动。
                MenuScroll.MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height - 120);
            }
            catch (Exception ex)
            {
                Log.Warn($"设置菜单滚动高度失败：{ex.Message}");
            }
            SyncFromSettings();
            // 从菜单小三角打开时贴着三角；QA/命令直接 ShowMenuFlyout 时给一个屏幕右下角的默认位置。
            if (_anchorSet)
                PlaceNear(_anchor);
            else
                PlaceNear(new Point(SystemParameters.WorkArea.Right - 70, SystemParameters.WorkArea.Bottom - 240));
            AnimateIn();
            _outsideClickWatch.Start();
        };
        Closed += (_, _) =>
        {
            _outsideClickWatch.Stop();
            try { AppServices.Current.Growth.Changed -= OnGrowthChanged; } catch { /* ignore */ }
        };
        try { AppServices.Current.Growth.Changed += OnGrowthChanged; } catch { /* 服务未就绪时忽略 */ }
        // 内容最终高度可能晚于第一次 PlaceNear 才确定，尺寸变化时重新贴边，确保不越出工作区。
        SizeChanged += (_, _) =>
        {
            if (_anchorSet && IsVisible && !_placing) PlaceNear(_anchor);
        };
    }

    /// <summary>贴着触发点（屏幕 DIP 坐标）弹出，自动避让屏幕边缘。</summary>
    public void PlaceNear(Point anchor)
    {
        _anchor = anchor;
        _anchorSet = true;
        if (_placing) return;
        _placing = true;
        try
        {
            UpdateLayout();
            var work = SystemParameters.WorkArea;
            var width = ActualWidth > 0 ? ActualWidth : 300;
            var height = ActualHeight > 0 ? ActualHeight : 440;
            const double edge = 8;

            var left = anchor.X - width - 14;      // 优先放在触发点左侧
            var top = anchor.Y - height * 0.28;
            if (left < work.Left + edge) left = anchor.X + 14; // 左边放不下就放右侧
            left = Math.Clamp(left, work.Left + edge, work.Right - edge - width);
            top = Math.Clamp(top, work.Top + edge, Math.Max(work.Top + edge, work.Bottom - edge - height));

            Left = Math.Round(left);
            Top = Math.Round(top);
        }
        catch (Exception ex)
        {
            Log.Warn($"定位菜单失败：{ex.Message}");
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            // 不进 Alt-Tab；菜单需要能拿焦点以支持 ESC。
            NativeMethods.SetToolWindow(new WindowInteropHelper(this).Handle, true);
            NativeMethods.ReassertTopmost(new WindowInteropHelper(this).Handle);
        }
        catch (Exception ex)
        {
            Log.Error("设置菜单窗口扩展样式失败", ex);
        }
    }

    /// <summary>
    /// 点击菜单外部 / 按 ESC 关闭。宠物窗是不抢焦点的窗口，点击它不会让本窗口 Deactivated，
    /// 所以额外轮询一次“鼠标左键按下时是否在菜单外”，保证任何情况下都能关掉。
    /// </summary>
    private void WatchOutsideClick()
    {
        if (!IsVisible) return;
        try
        {
            if ((GetAsyncKeyState(VkEscape) & 0x8000) != 0)
            {
                Close();
                return;
            }
            var down = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;
            if (down && !_leftWasDown && !IsCursorInsideWindow())
            {
                Close();
                return;
            }
            _leftWasDown = down;
        }
        catch
        {
            // 轮询失败不影响菜单使用
        }
    }

    private bool IsCursorInsideWindow()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var point)) return true;
            if (ActualWidth <= 0 || ActualHeight <= 0) return true;
            var topLeft = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            return point.X >= topLeft.X && point.X <= bottomRight.X &&
                   point.Y >= topLeft.Y && point.Y <= bottomRight.Y;
        }
        catch
        {
            return true;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private void AnimateIn()
    {
        try
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
            _popOffset.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { From = 8, EasingFunction = ease });
        }
        catch (Exception ex)
        {
            Log.Error("菜单弹出动画失败", ex);
        }
    }

    // =========================================================================
    // 设置同步
    // =========================================================================

    private void SyncFromSettings()
    {
        try
        {
            var settings = AppServices.Current.Settings.Current;
            _syncing = true;
            PetScaleSlider.Value = Math.Clamp(settings.PetScale, PetScaleSlider.Minimum, PetScaleSlider.Maximum);
            PetScaleValue.Text = settings.PetScale.ToString("0.00") + "×";
            PanelScaleSlider.Value = Math.Clamp(settings.PanelScale, PanelScaleSlider.Minimum, PanelScaleSlider.Maximum);
            PanelScaleValue.Text = settings.PanelScale.ToString("0.00") + "×";

            ButtonOpacitySlider.Value = settings.Buttons.IdleOpacityPercent;
            ButtonOpacityValue.Text = $"{settings.Buttons.IdleOpacityPercent}%";
            ToggleTimetableText.Text = settings.ShowTimetablePanel ? "隐藏课表" : "显示课表";
            var petWindow = AppServices.Current.PetWindow;
            FoldTimetableText.Text = petWindow?.IsTimetableFolded == true ? "展开课表（折叠中）" : "折叠课表";
            SetSwitch(HidePetSwitch, HidePetKnobOffset, settings.HidePet, animate: false);
            SetSwitch(CountdownSwitch, CountdownKnobOffset, settings.ShowCountdownOnPet, animate: false);
            SetSwitch(GrowthSwitch, GrowthKnobOffset, settings.EnableGrowth, animate: false);
            SetSwitch(AlwaysOnTopSwitch, AlwaysOnTopKnobOffset, settings.AlwaysOnTop, animate: false);
            SetSwitch(AutoStartSwitch, AutoStartKnobOffset, settings.AutoStartWithWindows, animate: false);
            RefreshGrowthDisplay();
        }
        catch (Exception ex)
        {
            Log.Error("同步菜单设置失败", ex);
        }
        finally
        {
            _syncing = false;
        }
    }

    // =========================================================================
    // 缩放 / 课表位置（v1.2.0：宠物与课表分别调节）
    // =========================================================================

    private static string FormatOffset(double value)
    {
        var rounded = (int)Math.Round(value);
        return rounded > 0 ? $"+{rounded}" : rounded.ToString(CultureInfo.InvariantCulture);
    }

    private void OnPetScaleSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        ApplyLayoutSetting(
            value => AppServices.Current.Settings.Update(s => s.PetScale = Math.Clamp(value, 0.2, 5.0)),
            e.NewValue,
            value => PetScaleValue.Text = value.ToString("0.00") + "×");
    }

    private void OnPanelScaleSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        ApplyLayoutSetting(
            value => AppServices.Current.Settings.Update(s => s.PanelScale = Math.Clamp(value, 0.4, 4.0)),
            e.NewValue,
            value => PanelScaleValue.Text = value.ToString("0.00") + "×");
    }


    /// <summary>滑块实时生效：写设置 + 更新数值显示（设置变更会驱动 PetWindow 重新布局）。</summary>
    private void ApplyLayoutSetting(Action<double> apply, double value, Action<double> updateLabel)
    {
        try
        {
            apply(value);
            _syncing = true;
            updateLabel(value);
            _syncing = false;
        }
        catch (Exception ex)
        {
            Log.Error("调整宠物/课表布局失败", ex);
        }
        finally
        {
            _syncing = false;
        }
    }
    // =========================================================================
    // 按钮透明度
    // =========================================================================

    private void OnButtonOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        try
        {
            var percent = (int)Math.Round(e.NewValue);
            percent = Math.Clamp(percent, 0, 100);
            AppServices.Current.Settings.Update(s => s.Buttons.IdleOpacityPercent = percent);
            _syncing = true;
            ButtonOpacityValue.Text = $"{percent}%";
            _syncing = false;
        }
        catch (Exception ex)
        {
            Log.Error("调整按钮透明度失败", ex);
        }
    }

    // =========================================================================
    // 开关与菜单项
    // =========================================================================

    // =========================================================================
    // 考试倒计时 / 陪伴养成（第二轮新增）
    // =========================================================================

    private void OnCountdownToggleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = !AppServices.Current.Settings.Current.ShowCountdownOnPet;
            AppServices.Current.Settings.Update(s => s.ShowCountdownOnPet = enabled);
            SetSwitch(CountdownSwitch, CountdownKnobOffset, enabled, animate: true);
        }
        catch (Exception ex)
        {
            Log.Error("切换考试倒计时显示失败", ex);
        }
    }

    private void OnGrowthToggleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = !AppServices.Current.Settings.Current.EnableGrowth;
            AppServices.Current.Settings.Update(s => s.EnableGrowth = enabled);
            SetSwitch(GrowthSwitch, GrowthKnobOffset, enabled, animate: true);
            RefreshGrowthDisplay();
        }
        catch (Exception ex)
        {
            Log.Error("切换养成统计失败", ex);
        }
    }

    private void OnGrowthChanged(object? sender, EventArgs e) => RefreshGrowthDisplay();

    private void RefreshGrowthDisplay()
    {
        try
        {
            var services = AppServices.Current;
            var enabled = services.Settings.Current.EnableGrowth;
            var stats = services.Growth.Stats;

            GrowthDaysValue.Text = enabled ? $"陪伴 {stats.DaysUsed} 天" : "已关闭";
            GrowthStatsText.Text = $"摸头 {stats.PatCount} 次 · 抽签 {stats.PickCount} 次 · 分组 {stats.GroupCount} 次";

            var unlocked = services.Growth.Achievements;
            var total = Core.Growth.PetGrowthService.Definitions.Count;
            GrowthAchievementsText.Text = unlocked.Count == 0
                ? $"成就 0/{total}：再摸摸头试试"
                : $"成就 {unlocked.Count}/{total}：{string.Join("、", unlocked.Select(a => a.Title))}";
            GrowthAchievementsText.ToolTip = unlocked.Count == 0
                ? "摸头、抽签、分组、上下课都会慢慢解锁成就"
                : string.Join("\n", unlocked.Select(a => $"{a.Title} —— {a.Description}"));
        }
        catch (Exception ex)
        {
            Log.Error("刷新养成统计显示失败", ex);
        }
    }

    private void OnAlwaysOnTopClick(object sender, RoutedEventArgs e) => ToggleSwitch(
        AlwaysOnTopSwitch, AlwaysOnTopKnobOffset,
        AppServices.Current.Settings.Current.AlwaysOnTop,
        value => AppServices.Current.Settings.Update(s => s.AlwaysOnTop = value));

    private void OnAutoStartClick(object sender, RoutedEventArgs e) => ToggleSwitch(
        AutoStartSwitch, AutoStartKnobOffset,
        AppServices.Current.Settings.Current.AutoStartWithWindows,
        value =>
        {
            AppServices.Current.Settings.Update(s => s.AutoStartWithWindows = value);
            ApplyAutoStart(value);
        });

    private void ToggleSwitch(Border track, TranslateTransform knob, bool current, Action<bool> apply)
    {
        var next = !current;
        try
        {
            apply(next);
            SetSwitch(track, knob, next, animate: true);
        }
        catch (Exception ex)
        {
            Log.Error("切换菜单开关失败", ex);
            SetSwitch(track, knob, current, animate: false);
        }
    }

    private void SetSwitch(Border track, TranslateTransform knob, bool on, bool animate)
    {
        var targetColor = on ? Color.FromRgb(0x31, 0x7F, 0xFF) : Color.FromRgb(0xD7, 0xDE, 0xEA);
        var knobTarget = on ? 15.0 : 0.0;
        try
        {
            if (!animate)
            {
                var brush = new SolidColorBrush(targetColor);
                brush.Freeze();
                track.Background = brush;
                knob.BeginAnimation(TranslateTransform.XProperty, null);
                knob.X = knobTarget;
                return;
            }

            var animatedBrush = new SolidColorBrush(((SolidColorBrush)track.Background).Color);
            track.Background = animatedBrush;
            animatedBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(130)));
            knob.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(knobTarget, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        catch (Exception ex)
        {
            Log.Error("刷新菜单开关样式失败", ex);
        }
    }

    private void OnToggleTimetableClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppServices.Current.Settings.Current;
            var show = !settings.ShowTimetablePanel;
            AppServices.Current.Settings.Update(s => s.ShowTimetablePanel = show);
            ToggleTimetableText.Text = show ? "隐藏课表" : "显示课表";
        }
        catch (Exception ex)
        {
            Log.Error("切换课表显示失败", ex);
        }
    }

    private void OnFoldTimetableClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var pet = AppServices.Current.PetWindow;
            if (pet is null) return;
            if (pet.IsTimetableFolded) pet.UnfoldTimetable();
            else pet.FoldTimetable();
            FoldTimetableText.Text = pet.IsTimetableFolded ? "展开课表（折叠中）" : "折叠课表";
        }
        catch (Exception ex)
        {
            Log.Error("切换课表折叠态失败", ex);
        }
    }

    private void OnHidePetToggleClick(object sender, RoutedEventArgs e) => ToggleSwitch(
        HidePetSwitch, HidePetKnobOffset,
        AppServices.Current.Settings.Current.HidePet,
        value =>
        {
            AppServices.Current.Settings.Update(s => s.HidePet = value);
            if (!value) AppServices.Current.PetWindow?.ExpandFromEdge();
        });

    private void OnSettingsClick(object sender, RoutedEventArgs e) => CloseThen(() => AppServices.Current.ShowSettings());

    private void OnDashboardClick(object sender, RoutedEventArgs e) => CloseThen(() => AppServices.Current.ShowDashboard());

    private void OnChatClick(object sender, RoutedEventArgs e) => CloseThen(() => AppServices.Current.ShowChatWindow());

    private void OnDutyClick(object sender, RoutedEventArgs e) => CloseThen(() => AppServices.Current.ShowDutyWindow());

    private void OnRosterClick(object sender, RoutedEventArgs e) => CloseThen(() => AppServices.Current.ShowRosterManager());

    private void OnTimetableClick(object sender, RoutedEventArgs e) => CloseThen(() => AppServices.Current.ShowTimetableEditor());

    private void OnPeriodsClick(object sender, RoutedEventArgs e) => CloseThen(() => AppServices.Current.ShowPeriodSettings());

    private void OnExitClick(object sender, RoutedEventArgs e) => CloseThen(() => AppServices.Current.Shutdown());

    private void CloseThen(Action action)
    {
        try { Close(); } catch { /* ignore */ }
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("执行菜单命令失败", ex);
        }
    }

    // =========================================================================
    // 开机自启（HKCU Run，无需管理员）
    // =========================================================================

    private static void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return;
                key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
        }
    }
}













