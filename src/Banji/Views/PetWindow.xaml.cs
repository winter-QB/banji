using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Banji.Controls.Pet;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Quips;

namespace Banji.Views;

/// <summary>
/// 桌面宠物主窗口：透明无边框置顶、吸附屏幕右侧、可拖动、整体随 UiScale 无级缩放。
/// 承载：宠物本体、常驻信息框（3 行）、当天课表面板、头顶两个小三角、左下角功能区三角。
/// 同时负责协调 <see cref="BubbleWindow"/>（台词/抽签/计时）与 <see cref="MenuFlyout"/>。
/// </summary>
public partial class PetWindow : Window
{
    private readonly QuipLibrary _quips = new();
    private readonly BubbleWindow _bubble;
    private readonly DispatcherTimer _topmostTimer;

    private readonly FontFamily _fontThin;
    private readonly FontFamily _fontLight;
    private readonly FontFamily _fontRegular;
    private readonly FontFamily _fontMedium;
    private readonly FontFamily _fontBold;

    private readonly SolidColorBrush _textBrush = FrozenBrush("#2B3444");
    private readonly SolidColorBrush _secondaryBrush = FrozenBrush("#55617A");
    private readonly SolidColorBrush _mutedBrush = FrozenBrush("#8A94A6");
    private readonly SolidColorBrush _faintBrush = FrozenBrush("#A2ABBB");

    private SolidColorBrush _accentBrush = FrozenBrush("#FF3030");
    private SolidColorBrush _accentSoftBrush = FrozenBrush("#99FF3030");
    private SolidColorBrush _accentTintBrush = FrozenBrush("#1FFF3030");
    private SolidColorBrush _brandBrush = FrozenBrush("#317FFF");

    private ScheduleSnapshot _snapshot = ScheduleSnapshot.Empty(DateTime.Now);
    private string _timetableSignature = "";
    private string _settingsSignature = "";
    private DateTime? _overrunStartedAt;

    private double _rightMargin;
    private double _bottomMargin;
    private bool _initialized;
    private bool _dragging;
    private bool _functionsExpanded;
    private double _dragOriginLeft;
    private double _dragOriginTop;
    private Point _dragStartCursor;
    private MenuFlyout? _openMenu;
    private DateTime? _previewTime;

    public PetWindow()
    {
        InitializeComponent();

        _fontThin = (FontFamily)FindResource("Font.Thin");
        _fontLight = (FontFamily)FindResource("Font.Light");
        _fontRegular = (FontFamily)FindResource("Font.Regular");
        _fontMedium = (FontFamily)FindResource("Font.Medium");
        _fontBold = (FontFamily)FindResource("Font.Bold");

        Opacity = 0;

        _bubble = new BubbleWindow();

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(6) };
        _topmostTimer.Tick += (_, _) => KeepOnTop();

        Pet.MouseLeftButtonDown += OnPetMouseLeftButtonDown;
        Pet.MouseMove += OnPetMouseMove;
        Pet.MouseLeftButtonUp += OnPetMouseUp;
        Pet.LostMouseCapture += (_, _) => _dragging = false;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Closed += OnClosed;
    }

    // =========================================================================
    // 供气泡窗定位使用
    // =========================================================================

    /// <summary>宠物本体在屏幕上的矩形（DIP）。</summary>
    public Rect GetPetBodyRect()
    {
        try
        {
            var topLeft = Pet.TranslatePoint(new Point(0, 0), this);
            var bottomRight = Pet.TranslatePoint(new Point(Pet.ActualWidth, Pet.ActualHeight), this);
            return new Rect(Left + topLeft.X, Top + topLeft.Y,
                Math.Max(1, bottomRight.X - topLeft.X), Math.Max(1, bottomRight.Y - topLeft.Y));
        }
        catch
        {
            return new Rect(Left, Top, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        }
    }

    /// <summary>气泡的理想锚点：宠物眼睛附近（屏幕 DIP）。</summary>
    public Point GetBubbleAnchor()
    {
        try
        {
            var point = Pet.TranslatePoint(new Point(Pet.ActualWidth * 0.5, Pet.ActualHeight * 0.44), this);
            return new Point(Left + point.X, Top + point.Y);
        }
        catch
        {
            return new Point(Left, Top);
        }
    }

    /// <summary>
    /// 气泡可占用的右边界（屏幕 DIP）：功能区展开时，气泡要让到功能区左侧，
    /// 否则贴着宠物身体左侧。
    /// </summary>
    public double GetBubbleRightBoundary()
    {
        try
        {
            if (_functionsExpanded && FunctionHost.IsVisible && FunctionHost.ActualWidth > 1)
            {
                var topLeft = FunctionHost.TranslatePoint(new Point(0, 0), this);
                return Left + topLeft.X;
            }
            return GetPetBodyRect().Left;
        }
        catch
        {
            return Left;
        }
    }

    // =========================================================================
    // 初始化
    // =========================================================================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            // 不进 Alt-Tab、不抢焦点，但鼠标点击照常工作。
            NativeMethods.SetToolWindow(handle, true);
            NativeMethods.SetNoActivate(handle, true);
        }
        catch (Exception ex)
        {
            Log.Error("设置宠物窗口扩展样式失败", ex);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var settings = Settings;
            _settingsSignature = BuildSettingsSignature(settings);
            RefreshAccentBrushes(settings);
            Pet.ApplySettings(settings.Pet, settings.PetBlinkEnabled, settings.PetBobEnabled);
            ApplyScale(settings.UiScale);
            Topmost = settings.AlwaysOnTop;
            TimetableCard.Visibility = settings.ShowTimetablePanel ? Visibility.Visible : Visibility.Collapsed;
            UpdateTriangleStates();

            LoadPreviewTime();
            UpdateLayout();
            InitializePosition();
            _snapshot = GetLiveSnapshot();
            RefreshFromSnapshot(force: true);
            SubscribeServices();

            _topmostTimer.Start();
            AnimateWindowIn();
            KeepOnTop();
        }
        catch (Exception ex)
        {
            Log.Error("初始化宠物窗口失败", ex);
        }
    }

    private void InitializePosition()
    {
        var work = SystemParameters.WorkArea;
        var settings = Settings;
        if (settings.PetRightMargin <= 0.5 && settings.PetBottomMargin <= 0.5)
        {
            // 首次启动：吸附屏幕右侧、任务栏上方，整体位于屏幕中部偏下。
            _rightMargin = 0;
            _bottomMargin = Math.Round(work.Height * 0.10);
        }
        else
        {
            _rightMargin = Math.Clamp(settings.PetRightMargin, 0, 4000);
            _bottomMargin = Math.Clamp(settings.PetBottomMargin, 0, 4000);
        }
        AnchorToMargins();
    }

    private void SubscribeServices()
    {
        var services = AppServices.Current;
        services.Clock.Tick += OnClockTick;
        services.Clock.Transition += OnClassTransition;
        services.Settings.Changed += OnSettingsChanged;
        services.Picker.Picked += OnPicked;
        services.Timers.Tick += OnTimerTick;
        services.Timers.StateChanged += OnTimerStateChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    private void AnimateWindowIn()
    {
        var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // =========================================================================
    // 开发/截图预览：BANJI_PET_PREVIEW_TIME="2026-10-30 10:30" 时用该时间生成课表快照
    // （仅开发与 UI 验收用；未设置环境变量时完全走真实时钟，不影响正式使用）
    // =========================================================================

    private void LoadPreviewTime()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("BANJI_PET_PREVIEW_TIME");
            if (string.IsNullOrWhiteSpace(raw)) return;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
                DateTime.TryParse(raw, out parsed))
            {
                _previewTime = parsed;
                Log.Info($"宠物窗预览模式：改用固定时间 {parsed:yyyy-MM-dd HH:mm:ss} 生成课表快照");
            }
            else
            {
                Log.Warn($"无法解析 BANJI_PET_PREVIEW_TIME={raw}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取宠物窗预览时间失败：{ex.Message}");
        }
    }

    private ScheduleSnapshot GetLiveSnapshot()
    {
        if (_previewTime is { } preview)
        {
            try { return AppServices.Current.Schedule.SnapshotAt(preview); }
            catch (Exception ex) { Log.Warn($"预览快照生成失败：{ex.Message}"); }
        }
        return AppServices.Current.Clock.Current ?? ScheduleSnapshot.Empty(DateTime.Now);
    }

    private static ISettingsService SettingsService => AppServices.Current.Settings;
    private static Core.Settings.AppSettings Settings => AppServices.Current.Settings.Current;

    // =========================================================================
    // 位置 / 缩放
    // =========================================================================

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_initialized || _dragging) return;
        AnchorToMargins();
        _bubble.RepositionAndRefresh();
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("WorkArea" or "PrimaryScreenWidth" or "PrimaryScreenHeight")) return;
        try
        {
            UpdateLayout();
            AnchorToMargins();
            _bubble.RepositionAndRefresh();
        }
        catch (Exception ex)
        {
            Log.Error("响应屏幕尺寸变化失败", ex);
        }
    }

    private void AnchorToMargins()
    {
        try
        {
            var work = SystemParameters.WorkArea;
            var width = ActualWidth;
            var height = ActualHeight;
            if (double.IsNaN(width) || width <= 0 || double.IsNaN(height) || height <= 0) return;

            var left = work.Right - _rightMargin - width;
            var top = work.Bottom - _bottomMargin - height;
            // 拖出屏幕太远时至少保留一部分可见。
            left = Math.Clamp(left, work.Left - width + 80, work.Right - 80);
            top = Math.Clamp(top, work.Top - height + 80, work.Bottom - 80);
            Left = Math.Round(left);
            Top = Math.Round(top);
        }
        catch (Exception ex)
        {
            Log.Error("定位宠物窗口失败", ex);
        }
    }

    private void ApplyScale(double scale)
    {
        if (double.IsNaN(scale) || scale <= 0) scale = 1;
        scale = Math.Clamp(scale, 0.05, 20.0);
        if (Math.Abs(UiScaleTransform.ScaleX - scale) < 1e-9) return;
        UiScaleTransform.ScaleX = scale;
        UiScaleTransform.ScaleY = scale;
    }

    private void PersistPosition()
    {
        try
        {
            var work = SystemParameters.WorkArea;
            var right = work.Right - (Left + ActualWidth);
            var bottom = work.Bottom - (Top + ActualHeight);
            _rightMargin = Math.Clamp(Math.Round(right), 0, 4000);
            _bottomMargin = Math.Clamp(Math.Round(bottom), 0, 4000);
            // 贴近边缘时吸附。
            if (_rightMargin < 24) _rightMargin = 0;
            if (_bottomMargin < 24) _bottomMargin = 0;
            SettingsService.Update(s =>
            {
                s.PetRightMargin = _rightMargin;
                s.PetBottomMargin = _bottomMargin;
            });
            AnchorToMargins();
            _bubble.RepositionAndRefresh();
        }
        catch (Exception ex)
        {
            Log.Error("保存宠物位置失败", ex);
        }
    }

    // =========================================================================
    // 宠物交互：拖动 / 摸一摸
    // =========================================================================

    private void OnPetMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) return;
        CloseMenuIfOpen(false);
        _dragging = true;
        _dragOriginLeft = Left;
        _dragOriginTop = Top;
        _dragStartCursor = GetCursorDip();
        try { Pet.CaptureMouse(); } catch { /* ignore */ }
        e.Handled = true;
    }

    private void OnPetMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        try
        {
            var current = GetCursorDip();
            Left = _dragOriginLeft + (current.X - _dragStartCursor.X);
            Top = _dragOriginTop + (current.Y - _dragStartCursor.Y);
        }
        catch (Exception ex)
        {
            Log.Warn($"拖动宠物失败：{ex.Message}");
            _dragging = false;
        }
    }

    private void OnPetMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        try { Pet.ReleaseMouseCapture(); } catch { /* ignore */ }
        e.Handled = true;

        var moved = Math.Abs(Left - _dragOriginLeft) > 1.5 || Math.Abs(Top - _dragOriginTop) > 1.5;
        if (moved)
        {
            PersistPosition();
        }
        else
        {
            OnPetTouched();
        }
    }

    private Point GetCursorDip()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            var fallback = Mouse.GetPosition(this);
            return new Point(Left + fallback.X, Top + fallback.Y);
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return new Point(point.X / scaleX, point.Y / scaleY);
    }

    private void OnPetTouched()
    {
        try
        {
            Pet.PlayTouchReaction();
            if (!Settings.EnableQuips) return;
            _bubble.ShowQuip(_quips.Next());
        }
        catch (Exception ex)
        {
            Log.Error("摸摸宠物失败", ex);
        }
    }

    // =========================================================================
    // 小三角 & 功能区
    // =========================================================================

    private void OnTimetableTriangleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Settings.ShowTimetablePanel)
            {
                SettingsService.Update(s => s.ShowTimetablePanel = true);
                return;
            }
            SettingsService.Update(s => s.TimetableExpanded = !s.TimetableExpanded);
        }
        catch (Exception ex)
        {
            Log.Error("切换课表展开状态失败", ex);
        }
    }

    private void OnMenuTriangleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_openMenu is { IsVisible: true })
            {
                CloseMenuIfOpen(true);
                return;
            }
            var flyout = AppServices.Current.ShowMenuFlyout();
            _openMenu = flyout;
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_openMenu, flyout)) _openMenu = null;
            };
            var point = MenuTriangle.TranslatePoint(
                new Point(MenuTriangle.ActualWidth * 0.5, MenuTriangle.ActualHeight), this);
            flyout.PlaceNear(new Point(Left + point.X, Top + point.Y));
        }
        catch (Exception ex)
        {
            Log.Error("打开菜单失败", ex);
        }
    }

    private void CloseMenuIfOpen(bool toggle)
    {
        try
        {
            if (_openMenu is not { IsVisible: true }) return;
            _openMenu.Close();
            _openMenu = null;
        }
        catch (Exception ex)
        {
            Log.Warn($"关闭菜单失败：{ex.Message}");
        }
    }

    private void OnFunctionTriangleClick(object sender, RoutedEventArgs e)
    {
        _functionsExpanded = !_functionsExpanded;
        if (_functionsExpanded)
        {
            ExpandFunctions();
        }
        else
        {
            CollapseFunctions();
        }
    }

    private void ExpandFunctions()
    {
        try
        {
            FunctionHost.Visibility = Visibility.Visible;
            FunctionHost.Opacity = 0;
            FunctionHostOffset.X = 14;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            FunctionHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
            var move = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            move.Completed += (_, _) => _bubble.RepositionAndRefresh();
            FunctionHostOffset.BeginAnimation(TranslateTransform.XProperty, move);
            FunctionTriangleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            FunctionTriangleRotation.Angle = 180;
            SetTriangleToolTip(FunctionTriangle, "收起功能区");
        }
        catch (Exception ex)
        {
            Log.Error("展开功能区失败", ex);
        }
    }

    private void CollapseFunctions()
    {
        try
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                if (!_functionsExpanded)
                {
                    FunctionHost.Visibility = Visibility.Collapsed;
                    _bubble.RepositionAndRefresh();
                }
            };
            FunctionHost.BeginAnimation(OpacityProperty, fade);
            FunctionHostOffset.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(14, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            FunctionTriangleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            FunctionTriangleRotation.Angle = 0;
            SetTriangleToolTip(FunctionTriangle, "展开功能区");
        }
        catch (Exception ex)
        {
            Log.Error("收起功能区失败", ex);
        }
    }

    private void SetTriangleToolTip(Button button, string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = _fontRegular,
            FontSize = 12,
            Foreground = _textBrush
        };
        button.ToolTip = new ToolTip { Content = block, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
    }

    private void UpdateTriangleStates()
    {
        var expanded = Settings.TimetableExpanded;
        TimetableTriangleRotation.Angle = expanded ? 0 : 180;
    }

    // =========================================================================
    // 服务事件
    // =========================================================================

    private void OnClockTick(object? sender, ScheduleSnapshot snapshot)
    {
        try
        {
            _snapshot = _previewTime is not null ? GetLiveSnapshot() : (snapshot ?? ScheduleSnapshot.Empty(DateTime.Now));
            RefreshFromSnapshot(force: false);
        }
        catch (Exception ex)
        {
            Log.Error("刷新宠物信息失败", ex);
        }
    }

    private void OnClassTransition(object? sender, ClassTransitionEventArgs e)
    {
        try
        {
            if (e.Kind == ClassTransitionKind.ClassEnded)
            {
                // 下课 15 秒内把第一行换成“下课超时 mm:ss”。
                _overrunStartedAt = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            Log.Error("处理下课转换失败", ex);
        }
    }

    private void OnPicked(object? sender, PickResult result)
    {
        try
        {
            _bubble.ShowPick(result);
        }
        catch (Exception ex)
        {
            Log.Error("显示抽签气泡失败", ex);
        }
    }

    private void OnTimerTick(object? sender, EventArgs e) => RefreshTimerBubble();

    private void OnTimerStateChanged(object? sender, EventArgs e) => RefreshTimerBubble();

    private void RefreshTimerBubble()
    {
        try
        {
            _bubble.UpdateTimer(AppServices.Current.Timers.Current);
        }
        catch (Exception ex)
        {
            Log.Error("刷新计时气泡失败", ex);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            var settings = Settings;
            ApplyScale(settings.UiScale);
            if (Topmost != settings.AlwaysOnTop) Topmost = settings.AlwaysOnTop;

            var target = settings.ShowTimetablePanel ? Visibility.Visible : Visibility.Collapsed;
            if (TimetableCard.Visibility != target) TimetableCard.Visibility = target;

            var signature = BuildSettingsSignature(settings);
            if (signature != _settingsSignature)
            {
                _settingsSignature = signature;
                RefreshAccentBrushes(settings);
                Pet.ApplySettings(settings.Pet, settings.PetBlinkEnabled, settings.PetBobEnabled);
                UpdateTriangleStates();
                _timetableSignature = "";
                RefreshFromSnapshot(force: true);
            }

            _bubble.RepositionAndRefresh();
        }
        catch (Exception ex)
        {
            Log.Error("响应宠物设置变化失败", ex);
        }
    }

    private static string BuildSettingsSignature(Core.Settings.AppSettings settings)
    {
        return string.Join("|",
            settings.TimetableExpanded,
            settings.ShowTimetablePanel,
            settings.PetBlinkEnabled,
            settings.PetBobEnabled,
            settings.Pet.BodyColor,
            settings.Pet.BodyShadeColor,
            settings.Pet.EyeColor,
            settings.Buttons.AccentColor,
            settings.Buttons.IdleColor);
    }

    private void RefreshAccentBrushes(Core.Settings.AppSettings settings)
    {
        _accentBrush = SolidBrush(settings.Buttons.AccentColor, Color.FromRgb(0xFF, 0x30, 0x30), 255);
        _accentSoftBrush = SolidBrush(settings.Buttons.AccentColor, Color.FromRgb(0xFF, 0x30, 0x30), 160);
        _accentTintBrush = SolidBrush(settings.Buttons.AccentColor, Color.FromRgb(0xFF, 0x30, 0x30), 26);
        _brandBrush = SolidBrush(settings.Buttons.IdleColor, Color.FromRgb(0x31, 0x7F, 0xFF), 255);
    }

    private void KeepOnTop()
    {
        try
        {
            if (!Settings.AlwaysOnTop) return;
            if (_bubble.IsShowing) return;
            if (_openMenu is { IsVisible: true }) return;
            // 有其它置顶窗（全屏提示 / 计时器超时等）正在显示时让位。
            var hasOtherTopmost = Application.Current?.Windows
                .OfType<Window>()
                .Any(w => w != this && w.IsVisible && w.Topmost &&
                          w is not BubbleWindow && w is not MenuFlyout) == true;
            if (hasOtherTopmost) return;
            NativeMethods.ReassertTopmost(new WindowInteropHelper(this).Handle);
        }
        catch
        {
            // 置顶失败不影响使用
        }
    }

    // =========================================================================
    // 刷新信息框 / 课表面板
    // =========================================================================

    private void RefreshFromSnapshot(bool force)
    {
        var overrun = ResolveOverrunElapsed();
        RefreshInfo(_snapshot, overrun);
        RefreshTimetable(_snapshot, force);
        UpdateTriangleStates();
    }

    private TimeSpan? ResolveOverrunElapsed()
    {
        if (_overrunStartedAt is not { } startedAt) return null;
        var elapsed = DateTime.Now - startedAt;
        var limit = Math.Max(3, Settings.EndOverrunSeconds);
        if (elapsed.TotalSeconds >= 0 && elapsed.TotalSeconds < limit) return elapsed;
        _overrunStartedAt = null;
        return null;
    }

    private void RefreshInfo(ScheduleSnapshot snapshot, TimeSpan? overrun)
    {
        var primary = PetUiFormatter.Primary(snapshot, overrun);
        var school = PetUiFormatter.School(snapshot);
        var course = PetUiFormatter.Course(snapshot);

        // 行 1：距下课 / 距上课 / 已放学 / 下课超时
        InfoRow1.Inlines.Clear();
        if (!string.IsNullOrEmpty(primary.Label))
        {
            var labelBrush = primary.IsOverrun ? _accentBrush : _mutedBrush;
            InfoRow1.Inlines.Add(MakeRun(primary.Label + " ", _fontLight, 10, labelBrush));
        }
        if (!string.IsNullOrEmpty(primary.Value))
        {
            var valueBrush = primary.IsOverrun ? _accentBrush : _textBrush;
            var size = string.IsNullOrEmpty(primary.Label) ? 14 : 15.5;
            InfoRow1.Inlines.Add(MakeRun(primary.Value, _fontMedium, size, valueBrush));
        }
        else
        {
            InfoRow1.Inlines.Add(MakeRun("—", _fontMedium, 13, _mutedBrush));
        }

        // 行 2：距放学 h:mm:ss + 剩余节数
        InfoRow2.Inlines.Clear();
        if (!string.IsNullOrEmpty(school.Label))
        {
            InfoRow2.Inlines.Add(MakeRun(school.Label + " ", _fontLight, 9.5, _mutedBrush));
            InfoRow2.Inlines.Add(MakeRun(school.Value, _fontMedium, 11.5, _secondaryBrush));
            if (!string.IsNullOrEmpty(school.Suffix))
                InfoRow2.Inlines.Add(MakeRun(school.Suffix, _fontLight, 9.5, _mutedBrush));
        }
        else if (!string.IsNullOrEmpty(school.Value))
        {
            InfoRow2.Inlines.Add(MakeRun(school.Value, _fontLight, 10.5, _mutedBrush));
        }

        // 行 3：现在 / 下节课程名
        InfoRow3.Inlines.Clear();
        if (!string.IsNullOrEmpty(course.Value1) || !string.IsNullOrEmpty(course.Value2))
        {
            if (!string.IsNullOrEmpty(course.Prefix1) && !string.IsNullOrEmpty(course.Value1))
            {
                InfoRow3.Inlines.Add(MakeRun(course.Prefix1 + " ", _fontLight, 9.5, _mutedBrush));
                InfoRow3.Inlines.Add(MakeRun(course.Value1, _fontMedium, 11.5, _textBrush));
            }
            else if (!string.IsNullOrEmpty(course.Value1))
            {
                InfoRow3.Inlines.Add(MakeRun(course.Value1, _fontLight, 10.5, _mutedBrush));
            }
            if (!string.IsNullOrEmpty(course.Prefix2) && !string.IsNullOrEmpty(course.Value2))
            {
                InfoRow3.Inlines.Add(MakeRun(" " + course.Prefix2.TrimStart() + " ", _fontLight, 9.5, _mutedBrush));
                InfoRow3.Inlines.Add(MakeRun(course.Value2, _fontMedium, 11.5, _brandBrush));
            }
        }
        else
        {
            InfoRow3.Inlines.Add(MakeRun("—", _fontLight, 10.5, _mutedBrush));
        }
    }

    private void RefreshTimetable(ScheduleSnapshot snapshot, bool force)
    {
        var expanded = Settings.TimetableExpanded;
        var today = snapshot.Today ?? Array.Empty<CourseOccurrence>();
        var highlighted = snapshot.Highlighted;
        var signature = string.Join("|",
            expanded,
            snapshot.Phase,
            today.Count,
            highlighted?.PeriodIndex,
            string.Join(";", today.Select(c => $"{c.PeriodIndex}:{c.Course}:{(int)c.Status}")));
        if (!force && signature == _timetableSignature) return;
        _timetableSignature = signature;

        TimetableTitle.Text = PetUiFormatter.TimetableTitle(_previewTime ?? DateTime.Now);
        TimetableRows.Children.Clear();

        var hasToday = snapshot.HasTimetable && today.Count > 0;
        TimetableMeta.Text = hasToday ? $"共 {today.Count} 节" : "";
        if (!hasToday)
        {
            TimetableEmpty.Visibility = Visibility.Visible;
            TimetableEmpty.Text = snapshot.Phase == SchedulePhase.NoTimetable ? "暂无课表" : "今天没有课";
            return;
        }

        IReadOnlyList<CourseOccurrence> rows;
        if (expanded)
        {
            rows = today;
        }
        else if (highlighted is not null)
        {
            rows = new[] { highlighted };
        }
        else
        {
            var last = today.LastOrDefault();
            if (last is null || snapshot.Phase == SchedulePhase.AfterSchool)
            {
                TimetableEmpty.Visibility = Visibility.Visible;
                TimetableEmpty.Text = "今天的课已上完";
                return;
            }
            rows = new[] { last };
        }

        TimetableEmpty.Visibility = Visibility.Collapsed;
        foreach (var course in rows)
        {
            var isHighlight = highlighted is not null && course.PeriodIndex == highlighted.PeriodIndex;
            TimetableRows.Children.Add(BuildTimetableRow(course, isHighlight));
        }
    }

    private Border BuildTimetableRow(CourseOccurrence course, bool isHighlight)
    {
        var row = new Border
        {
            Height = 21,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(3, 0, 5, 0),
            Margin = new Thickness(0, 0, 0, 1),
            Background = isHighlight ? _accentTintBrush : Brushes.Transparent
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = new Border
        {
            Width = 2.5,
            CornerRadius = new CornerRadius(1.25),
            Background = isHighlight ? _accentBrush : Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);

        var isPast = course.Status == CourseStatus.Past;
        var indexBrush = isHighlight ? _accentSoftBrush : _faintBrush;
        var courseBrush = isHighlight ? _accentBrush : (isPast ? _faintBrush : _textBrush);

        var index = new TextBlock
        {
            Text = PetUiFormatter.PeriodTag(course),
            FontFamily = _fontLight,
            FontSize = 9,
            Foreground = indexBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(index, 1);
        grid.Children.Add(index);

        var courseText = new TextBlock
        {
            Text = course.CourseDisplay,
            FontFamily = isHighlight ? _fontBold : _fontMedium,
            FontWeight = isHighlight ? FontWeights.Bold : FontWeights.Normal,
            FontSize = isHighlight ? 12.5 : 12,
            Foreground = courseBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(courseText, 2);
        grid.Children.Add(courseText);

        var time = new TextBlock
        {
            Text = PetUiFormatter.CourseTime(course),
            FontFamily = _fontLight,
            FontSize = 8.5,
            Foreground = isHighlight ? _accentSoftBrush : _faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(time, 3);
        grid.Children.Add(time);

        row.Child = grid;
        return row;
    }

    // =========================================================================
    // 清理
    // =========================================================================

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            var services = AppServices.Current;
            services.Clock.Tick -= OnClockTick;
            services.Clock.Transition -= OnClassTransition;
            services.Settings.Changed -= OnSettingsChanged;
            services.Picker.Picked -= OnPicked;
            services.Timers.Tick -= OnTimerTick;
            services.Timers.StateChanged -= OnTimerStateChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"解除宠物窗服务订阅失败：{ex.Message}");
        }

        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _topmostTimer.Stop();
        try { Pet.StopAnimations(); } catch { /* ignore */ }
        try { _bubble.CloseCleanup(); } catch { /* ignore */ }
        try { CloseMenuIfOpen(false); } catch { /* ignore */ }
    }

    // =========================================================================
    // 小工具
    // =========================================================================

    private Run MakeRun(string text, FontFamily family, double size, Brush brush)
        => new(text)
        {
            FontFamily = family,
            FontSize = size,
            Foreground = brush
        };

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex, Colors.Black));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush SolidBrush(string? hex, Color fallback, byte alpha)
    {
        var color = ParseColor(hex, fallback);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex.Trim()); }
        catch { return fallback; }
    }
}



