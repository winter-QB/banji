using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Banji.Controls.Pet;
using Banji.Core.Abstractions;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;
using Banji.Core.Models;
using Banji.Core.Quips;

namespace Banji.Views;

/// <summary>
/// v1.6.0 课表独立窗口（BanjiTimetable）：从 PetWindow 彻底拆出，宠物与课表互不联动。
/// 普通态 = 白底圆角卡片（可整窗拖动，位置存 PanelWindowLeft/Top）；
/// 上课专注态（AutoSimplifyTimetableDuringClass）= 贴屏幕边角的 9 种便签（FocusNotePlacement）。
/// </summary>
public partial class TimetablePanelWindow : Window
{
    private const double CardMargin = 24;      // 卡片四周留白（容纳阴影）
    private const double ScreenEdge = 24;      // 首次自动定位边距
    private const double DragThreshold = PanelDragMath.DragThreshold;

    private readonly FontFamily _fontThin;
    private readonly FontFamily _fontLight;
    private readonly FontFamily _fontRegular;
    private readonly FontFamily _fontMedium;
    private readonly FontFamily _fontBold;

    private static readonly SolidColorBrush PanelStrong = FrozenBrush("#111827");
    private static readonly SolidColorBrush PanelNormal = FrozenBrush("#1F2329");
    private static readonly SolidColorBrush PanelMuted = FrozenBrush("#5B6472");
    private static readonly SolidColorBrush PanelFaint = FrozenBrush("#8A94A6");
    private static readonly SolidColorBrush PanelBrand = FrozenBrush("#317FFF");
    private static readonly SolidColorBrush PanelOverrun = FrozenBrush("#D92D20");
    private static readonly SolidColorBrush ToolTipTextBrush = FrozenBrush("#1F2329");
    private static readonly SolidColorBrush StickyStripBrush = FrozenBrush(FocusNoteStickyStyle.StripColor);

    private readonly DispatcherTimer _topmostTimer;
    private SolidColorBrush _accentBrush = FrozenBrush("#FF3030");

    private ScheduleSnapshot _snapshot = ScheduleSnapshot.Empty(DateTime.Now);
    private DateTime? _previewTime;
    private DateTime? _overrunStartedAt;

    private bool _initialized;
    private bool _folded;
    private bool _focusActive;
    private bool _dragging;
    private bool _dragCandidate;
    private bool _suppressClick;
    private Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;
    private bool _hasWindowPosition;
    private FrameworkElement? _captureHost;
    private double _windowLeft;
    private double _windowTop;
    private DateTime _lastActivity = DateTime.Now;

    // 面板渲染签名缓存
    private string _timetableSignature = "";
    private string _foldedSignature = "";
    private string _dutySignature = "";
    private string _weekHeaderText = "";
    private string _weekShortText = "";
    private string _dutySummaryText = "";
    private DutyScheduleView? _dutySchedule;
    private bool _afterSchoolDutyActive;
    private IReadOnlyList<DutyTaskLine> _topDutyLines = Array.Empty<DutyTaskLine>();
    private double _lastCardDesignWidth = -1;
    private double _lastCardDesignHeight = -1;

    // 便签态
    private FocusNotePlacement? _qaPlacementOverride;
    private (double X, double Y)? _qaFreeOverride;
    private string _noteSignature = "";
    private Rect? _autoPlacePetRect;
    private bool _forceHaloForQa;

    public TimetablePanelWindow()
    {
        InitializeComponent();

        _fontThin = (FontFamily)FindResource("Font.Thin");
        _fontLight = (FontFamily)FindResource("Font.Light");
        _fontRegular = (FontFamily)FindResource("Font.Regular");
        _fontMedium = (FontFamily)FindResource("Font.Medium");
        _fontBold = (FontFamily)FindResource("Font.Bold");

        Opacity = 0;

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _topmostTimer.Tick += (_, _) => ReassertBelowPet();

        // 整窗拖动（普通态 & Free 便签态）；4px 阈值区分点击
        foreach (var host in new FrameworkElement[] { PanelCard, NoteHost })
        {
            host.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            host.PreviewMouseMove += OnPreviewMouseMove;
            host.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
            host.LostMouseCapture += OnLostMouseCapture;
        }
        PanelCard.MouseRightButtonUp += OnPanelRightButtonUp;
        NoteHost.MouseRightButtonUp += OnPanelRightButtonUp;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // =========================================================================
    // 对外（PetWindow / 菜单 / QA）
    // =========================================================================

    /// <summary>折叠/展开课表（运行期状态，不改设置）。</summary>
    public void ToggleFold()
    {
        SetFolded(!_folded);
    }

    public void SetFolded(bool folded)
    {
        if (_folded == folded) return;
        _folded = folded;
        TouchActivity();
        ApplyPanelMode();
        RefreshFoldedCard(force: true);
        Log.Info(folded ? "课表已折叠（独立窗口）" : "课表已展开（独立窗口）");
    }

    public bool IsFolded => _folded;

    /// <summary>当前是否处于上课专注便签态。</summary>
    public bool IsFocusNoteActive => _focusActive;

    /// <summary>PetWindow 关闭时调用。</summary>
    public void CloseCleanup()
    {
        try { Close(); } catch { /* ignore */ }
    }

    /// <summary>
    /// 首次自动定位：贴在宠物窗口上方（底边 = 宠物窗顶 − 8，右缘与宠物窗对齐并夹在工作区内）。
    /// 仅当用户尚未保存过 PanelWindowLeft/Top 时生效，保证“两只宠物”互不遮挡。
    /// </summary>
    public void PlaceNearPet(Rect petWindowRect)
    {
        _autoPlacePetRect = petWindowRect;
        // 首次布局时窗口尺寸可能还没稳定，放到下一轮 Loaded 再贴到宠物上方
        Dispatcher.BeginInvoke(new Action(ApplyAutoPlaceNearPet), DispatcherPriority.Loaded);
    }

    private void ApplyAutoPlaceNearPet()
    {
        try
        {
            if (!_initialized || _focusActive || _autoPlacePetRect is not { } petWindowRect) return;
            var settings = Settings;
            if (settings.PanelWindowLeft is not null || settings.PanelWindowTop is not null) return;
            if (!IsVisible || Settings.ShowTimetablePanel == false) return;
            // 先确保卡片尺寸已按内容更新
            UpdateCardSizeIfNeeded();
            var work = SystemParameters.WorkArea;
            var w = ActualWidth > 0 ? ActualWidth : Width;
            var h = ActualHeight > 0 ? ActualHeight : Height;
            if (w <= 1 || h <= 1) return;
            var right = Math.Min(petWindowRect.Right, work.Right - CardMargin);
            var left = right - w;
            var top = petWindowRect.Top - 8 - h;
            left = Math.Clamp(left, work.Left + CardMargin, Math.Max(work.Left + CardMargin, work.Right - CardMargin - w));
            top = Math.Clamp(top, work.Top + CardMargin, Math.Max(work.Top + CardMargin, work.Bottom - CardMargin - h));
            Left = Math.Round(left);
            Top = Math.Round(top);
            _windowLeft = Left;
            _windowTop = Top;
            _hasWindowPosition = true;
            Log.Info($"课表窗口自动定位：({Left:0},{Top:0})，尺寸 {w:0}x{h:0}（贴宠物上方，宠物顶={petWindowRect.Top:0}）");
        }
        catch (Exception ex)
        {
            Log.Warn($"课表窗口自动定位失败：{ex.Message}");
        }
    }

    /// <summary>v1.7.0：托盘菜单/屏幕比例变化——把课表窗口夹回当前工作区并保存位置。</summary>
    public void BringBackToScreen()
    {
        try
        {
            if (!IsVisible) Show();
            UpdateCardSizeIfNeeded();
            var work = SystemParameters.WorkArea;
            var w = ActualWidth > 0 ? ActualWidth : Width;
            var h = ActualHeight > 0 ? ActualHeight : Height;
            var left = Math.Clamp(Left, work.Left + CardMargin, Math.Max(work.Left + CardMargin, work.Right - CardMargin - w));
            var top = Math.Clamp(Top, work.Top + CardMargin, Math.Max(work.Top + CardMargin, work.Bottom - CardMargin - h));
            Left = Math.Round(left);
            Top = Math.Round(top);
            _windowLeft = Left;
            _windowTop = Top;
            _hasWindowPosition = true;
            SettingsService.Update(s =>
            {
                s.PanelWindowLeft = Left;
                s.PanelWindowTop = Top;
            });
            Log.Info($"课表窗口已拉回屏幕：({Left:0},{Top:0})");
        }
        catch (Exception ex)
        {
            Log.Error("把课表窗口拉回屏幕失败", ex);
        }
    }

    public void RefreshNow()
    {
        try
        {
            _snapshot = GetLiveSnapshot();
            RefreshAll(force: true);
            ApplyPanelMode();
        }
        catch (Exception ex)
        {
            Log.Error("刷新课表窗口失败", ex);
        }
    }

    // =========================================================================
    // 生命周期 / 服务订阅
    // =========================================================================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetToolWindow(handle, true);
            NativeMethods.SetNoActivate(handle, true);
            ReassertBelowPet();
        }
        catch (Exception ex)
        {
            Log.Error("设置课表窗口扩展样式失败", ex);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            LoadPreviewTime();
            LoadQaNoteOverrides();
            LoadQaHaloOverride();
            ApplyPanelHalo();
            _hasWindowPosition = false;
            _snapshot = GetLiveSnapshot();
            RefreshAll(force: true);
            ApplyPanelMode();
            SubscribeServices();
            _topmostTimer.Start();
            ReassertBelowPet();
            AnimateIn();
        }
        catch (Exception ex)
        {
            Log.Error("初始化课表窗口失败", ex);
        }
    }

    private void SubscribeServices()
    {
        var services = AppServices.Current;
        services.Clock.Tick += OnClockTick;
        services.Clock.Transition += OnClassTransition;
        services.Settings.Changed += OnSettingsChanged;
        services.Duty.Changed += OnDutyChanged;
        services.WeekParity.Changed += OnWeekParityChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _topmostTimer.Stop();
        try
        {
            var services = AppServices.Current;
            services.Clock.Tick -= OnClockTick;
            services.Clock.Transition -= OnClassTransition;
            services.Settings.Changed -= OnSettingsChanged;
            services.Duty.Changed -= OnDutyChanged;
            services.WeekParity.Changed -= OnWeekParityChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"解除课表窗服务订阅失败：{ex.Message}");
        }
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    private void AnimateIn()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // =========================================================================
    // 时钟 / 专注态 / 设置
    // =========================================================================

    private void OnClockTick(object? sender, ScheduleSnapshot snapshot)
    {
        try
        {
            _snapshot = _previewTime is not null ? GetLiveSnapshot() : (snapshot ?? ScheduleSnapshot.Empty(DateTime.Now));
            EvaluateFocusState();
            RefreshAll(force: false);
            UpdateCardSizeIfNeeded();
            ApplyNoteText();
            EvaluateIdleFold();
        }
        catch (Exception ex)
        {
            Log.Error("刷新课表窗口失败", ex);
        }
    }

    private void OnClassTransition(object? sender, ClassTransitionEventArgs e)
    {
        try
        {
            if (e.Kind == ClassTransitionKind.ClassEnded) _overrunStartedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            Log.Warn($"处理课表下课转换失败：{ex.Message}");
        }
    }

    private void EvaluateFocusState()
    {
        if (!_initialized) return;
        var settings = Settings;
        var focus = settings.AutoSimplifyTimetableDuringClass && ClassFocusMode.IsInClassFocus(_snapshot);
        if (focus == _focusActive) return;
        _focusActive = focus;
        ApplyPanelHalo();
        ApplyPanelMode();
        RefreshAll(force: true);
        Log.Info(focus ? "课表进入专注便签态" : "课表退出专注便签态，恢复原位置/白底");
    }

    private void EvaluateIdleFold()
    {
        try
        {
            var settings = Settings;
            if (_focusActive || _folded || settings.TimetableIdleFoldMinutes <= 0) return;
            if ((DateTime.Now - _lastActivity).TotalMinutes >= settings.TimetableIdleFoldMinutes)
            {
                SetFolded(true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"课表空闲折叠判定失败：{ex.Message}");
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            if (!_initialized) return;
            _timetableSignature = "";
            _foldedSignature = "";
            _dutySignature = "";
            _noteSignature = "";
            _accentBrush = SolidBrush(Settings.Buttons.AccentColor, Color.FromRgb(0xFF, 0x30, 0x30), 255);
            EvaluateFocusState();
            RefreshAll(force: true);
            ApplyPanelMode();
        }
        catch (Exception ex)
        {
            Log.Error("响应课表设置变化失败", ex);
        }
    }

    private void OnDutyChanged(object? sender, EventArgs e) => DispatchRefresh();
    private void OnWeekParityChanged(object? sender, EventArgs e) => DispatchRefresh();

    private void DispatchRefresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => { _dutySignature = ""; _timetableSignature = ""; _foldedSignature = ""; RefreshAll(force: true); ApplyPanelMode(); })); return; }
        _dutySignature = "";
        _timetableSignature = "";
        _foldedSignature = "";
        RefreshAll(force: true);
        ApplyPanelMode();
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("WorkArea" or "PrimaryScreenWidth" or "PrimaryScreenHeight")) return;
        try { ApplyPanelMode(); } catch (Exception ex) { Log.Warn($"响应屏幕变化失败：{ex.Message}"); }
    }

    private void OnPanelRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            e.Handled = true;
            TouchActivity();
            // v1.7.0：右键菜单锚定鼠标当前屏幕 DIP 坐标（不再用窗口固定点/宠物头顶）
            var cursor = GetCursorDip();
            var flyout = AppServices.Current.ShowMenuFlyout();
            flyout.PlaceNear(cursor);
            // ShowMenuFlyout 内部 Loaded 还会 PlaceNear 一次，这里在 Loaded 之后再校正，避免被覆盖
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (flyout.IsVisible) flyout.PlaceNear(cursor);
                }
                catch (Exception ex)
                {
                    Log.Warn($"校正右键菜单位置失败：{ex.Message}");
                }
            }), DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Log.Error("打开课表右键菜单失败", ex);
        }
    }

    private void OnDutyLineClick(object sender, MouseButtonEventArgs e)
    {
        if (_suppressClick) return;
        e.Handled = true;
        TouchActivity();
        try { AppServices.Current.ShowDutyWindow(); }
        catch (Exception ex) { Log.Error("打开值日与轮值窗口失败", ex); }
    }

    private void OnFoldedDutyClick(object sender, MouseButtonEventArgs e)
    {
        if (_suppressClick) return;
        e.Handled = true;
        TouchActivity();
        try { AppServices.Current.ShowDutyWindow(); }
        catch (Exception ex) { Log.Error("打开值日与轮值窗口失败", ex); }
    }

    private void OnFoldedCardClick(object sender, MouseButtonEventArgs e)
    {
        if (_suppressClick) return;
        e.Handled = true;
        SetFolded(false);
    }

    private void TouchActivity() => _lastActivity = DateTime.Now;

    // =========================================================================
    // 拖动（普通态整窗 / Free 便签）
    // =========================================================================

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TimetablePanelPolicy.CanDragPanel(_focusActive, CurrentPlacement(), _qaFreeOverride.HasValue)) return;
        _dragCandidate = true;
        _dragging = false;
        _suppressClick = false;
        _dragStartCursor = GetCursorDip();
        _dragStartLeft = Left;
        _dragStartTop = Top;
        TouchActivity();
        // 点击面板不允许把面板抬到宠物窗口上方
        Dispatcher.BeginInvoke(new Action(ReassertBelowPet), DispatcherPriority.Input);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed)
        {
            if (_dragging) EndDrag(cancel: false);
            return;
        }
        try
        {
            var current = GetCursorDip();
            var dx = current.X - _dragStartCursor.X;
            var dy = current.Y - _dragStartCursor.Y;
            if (!_dragging)
            {
                if (!PanelDragMath.ExceedsThreshold(dx, dy)) return;
                _dragging = true;
                _captureHost = sender as FrameworkElement;
                try { _captureHost?.CaptureMouse(); } catch { /* ignore */ }
            }
            Left = Math.Round(_dragStartLeft + dx);
            Top = Math.Round(_dragStartTop + dy);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"拖动课表窗口失败：{ex.Message}");
            EndDrag(cancel: true);
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            _dragCandidate = false;
            Dispatcher.BeginInvoke(new Action(ReassertBelowPet), DispatcherPriority.Input);
            return;
        }
        _suppressClick = true;
        e.Handled = true;
        EndDrag(cancel: false);
        Dispatcher.BeginInvoke(new Action(() => _suppressClick = false), DispatcherPriority.Input);
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging) { _dragCandidate = false; return; }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_dragging) return;
            try { _captureHost?.CaptureMouse(); } catch { /* ignore */ }
        }), DispatcherPriority.Input);
    }

    private void EndDrag(bool cancel)
    {
        _dragCandidate = false;
        var moved = _dragging;
        _dragging = false;
        // v1.7.0：必须释放拖动宿主的鼠标捕获；否则之后点宠物的事件仍会被面板劫持（表现为“拖宠物却动课表”）
        try
        {
            _captureHost?.ReleaseMouseCapture();
            if (Mouse.Captured is not null) Mouse.Capture(null);
        }
        catch { /* ignore */ }
        _captureHost = null;
        // v1.7.0：拖动会通过鼠标激活把窗口抬到 topmost 组顶部，结束后立即压回宠物下方
        ReassertBelowPet();
        Dispatcher.BeginInvoke(new Action(ReassertBelowPet), DispatcherPriority.Background);
        if (!moved) return;

        var work = SystemParameters.WorkArea;
        if (cancel)
        {
            Left = _dragStartLeft;
            Top = _dragStartTop;
            return;
        }

        if (_focusActive)
        {
            var (x, y) = FocusNoteLayout.ClampFreePosition(Left, Top, ActualWidth, ActualHeight,
                work.Left, work.Top, work.Right, work.Bottom);
            Left = x;
            Top = y;
            SettingsService.Update(s =>
            {
                s.FocusNoteFreeLeft = x;
                s.FocusNoteFreeTop = y;
            });
            Log.Info($"自由便签位置已保存：X={x:0}, Y={y:0}");
        }
        else
        {
            var clampedLeft = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - ActualWidth));
            var clampedTop = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
            Left = Math.Round(clampedLeft);
            Top = Math.Round(clampedTop);
            _windowLeft = Left;
            _windowTop = Top;
            _hasWindowPosition = true;
            SettingsService.Update(s =>
            {
                s.PanelWindowLeft = Math.Round(Left);
                s.PanelWindowTop = Math.Round(Top);
            });
            Log.Info($"课表窗口位置已保存：X={Left:0}, Y={Top:0}");
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
        var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return new Point(point.X / sx, point.Y / sy);
    }

    // =========================================================================
    // 布局（普通卡片 / 专注便签）
    // =========================================================================

    private void ApplyPanelMode()
    {
        if (!_initialized) return;
        try
        {
            var settings = Settings;
            if (!settings.ShowTimetablePanel)
            {
                Hide();
                return;
            }

            if (!IsVisible) Show();
            ReassertBelowPet();

            if (_focusActive)
            {
                ApplyNoteMode(settings);
                return;
            }

            NoteHost.Visibility = Visibility.Collapsed;
            CardMarginHost.Visibility = Visibility.Visible;
            ApplyCardLayout(settings);
            // 首次测量可能早于文本/模板完全布局，下一轮输入前再核对一次尺寸
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_focusActive) return;
                UpdateCardSizeIfNeeded();
            }), DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Log.Error("课表窗口布局失败", ex);
        }
    }

    /// <summary>普通卡片内容尺寸变化时重算窗口大小（避免裁切/留白）。</summary>
    private void UpdateCardSizeIfNeeded()
    {
        if (!_initialized || _focusActive || !IsVisible) return;
        try
        {
            if (!Settings.ShowTimetablePanel) return;
            PanelCard.Measure(new Size(280, double.PositiveInfinity));
            var w = Math.Max(120, PanelCard.DesiredSize.Width);
            var h = Math.Max(48, PanelCard.DesiredSize.Height);
            if (Math.Abs(w - _lastCardDesignWidth) > 0.5 || Math.Abs(h - _lastCardDesignHeight) > 0.5)
            {
                ApplyCardLayout(Settings);
                if (_autoPlacePetRect is { } petRect
                    && Settings.PanelWindowLeft is null && Settings.PanelWindowTop is null)
                {
                    ApplyAutoPlaceNearPet();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"更新课表窗口尺寸失败：{ex.Message}");
        }
    }

    private void ApplyCardLayout(Core.Settings.AppSettings settings)
    {
        var scale = Math.Clamp(settings.PanelScale, 0.4, 4.0);
        PanelScaleTransform.ScaleX = scale;
        PanelScaleTransform.ScaleY = scale;

        PanelStack.Visibility = _folded ? Visibility.Collapsed : Visibility.Visible;
        FoldedCard.Visibility = _folded ? Visibility.Visible : Visibility.Collapsed;
        TimetableSection.Visibility = _folded ? Visibility.Collapsed : Visibility.Visible;

        PanelCard.Measure(new Size(280, double.PositiveInfinity));
        var designW = Math.Max(120, PanelCard.DesiredSize.Width);
        var designH = Math.Max(48, PanelCard.DesiredSize.Height);
        _lastCardDesignWidth = designW;
        _lastCardDesignHeight = designH;

        var windowW = Math.Ceiling(designW * scale) + CardMargin * 2;
        var windowH = Math.Ceiling(designH * scale) + CardMargin * 2;
        CardMarginHost.Width = designW;
        CardMarginHost.Height = designH;
        Canvas.SetLeft(CardMarginHost, CardMargin);
        Canvas.SetTop(CardMarginHost, CardMargin);

        var sizeChanged = Math.Abs(Width - windowW) > 0.5 || Math.Abs(Height - windowH) > 0.5;
        if (sizeChanged)
        {
            Width = windowW;
            Height = windowH;
        }

        // 位置：优先用户拖动保存的位置；首次自动放在工作区右下、宠物上方
        var work = SystemParameters.WorkArea;
        if (!_hasWindowPosition)
        {
            var savedLeft = settings.PanelWindowLeft;
            var savedTop = settings.PanelWindowTop;
            if (savedLeft is { } sl && savedTop is { } st)
            {
                _windowLeft = sl;
                _windowTop = st;
            }
            else
            {
                var petH = 134 * Math.Clamp(settings.PetScale, 0.2, 5.0);
                _windowLeft = work.Right - windowW - ScreenEdge;
                // 面板底边 = 宠物窗顶边 − 24；宠物窗顶边 = 工作区底 − petBoxH − 48（宠物窗 24 边距留白 ×2）\n                _windowTop = work.Bottom - petH - 48 - ScreenEdge - windowH;
            }
            _hasWindowPosition = true;
        }

        var left = Math.Clamp(_windowLeft, work.Left, Math.Max(work.Left, work.Right - windowW));
        var top = Math.Clamp(_windowTop, work.Top, Math.Max(work.Top, work.Bottom - windowH));
        Left = Math.Round(left);
        Top = Math.Round(top);
        _windowLeft = Left;
        _windowTop = Top;
    }

    private void ApplyNoteMode(Core.Settings.AppSettings settings)
    {
        CardMarginHost.Visibility = Visibility.Collapsed;
        NoteHost.Visibility = Visibility.Visible;

        var placement = CurrentPlacement();
        var isFree = placement == FocusNotePlacement.Free;
        if (!isFree && (_qaFreeOverride is null))
        {
            // 非 Free 时禁止拖动
        }

        // 先按比例估算长度，用文本量出厚度后再算最终几何（便笺纸：紧凑 1~2 行）
        var work = SystemParameters.WorkArea;
        var estimatedLength = FocusNoteLayout.ComputeLength(work.Width, work.Height, settings.FocusNoteLengthPercent);
        var textW = Math.Max(60, estimatedLength - FocusNoteStickyStyle.TextPadding * 2);
        NoteTextPanel.Measure(new Size(textW, double.PositiveInfinity));
        var thickness = Math.Clamp(NoteTextPanel.DesiredSize.Height + 12, 46, FocusNoteLayout.MaxThickness);

        var (freeX, freeY) = ResolveFreePosition(settings, work);
        var geometry = FocusNoteLayout.Compute(placement, work.Left, work.Top, work.Right, work.Bottom,
            thickness, settings.FocusNoteLengthPercent, freeX, freeY);

        Width = Math.Max(1, Math.Round(geometry.Width));
        Height = Math.Max(1, Math.Round(geometry.Height));
        Left = Math.Round(geometry.Left);
        Top = Math.Round(geometry.Top);
        _windowLeft = Left;
        _windowTop = Top;

        // Canvas 子元素不会自动拉伸：便签层必须显式铺满窗口
        NoteHost.Width = Width;
        NoteHost.Height = Height;

        LayoutNoteLayers(geometry);
        ApplyNoteText();
    }

    private FocusNotePlacement CurrentPlacement()
    {
        if (_qaPlacementOverride is { } qa) return qa;
        return Settings.FocusNotePlacement;
    }

    private (double? X, double? Y) ResolveFreePosition(Core.Settings.AppSettings settings, Rect work)
    {
        if (_qaFreeOverride is { } qa) return (qa.X, qa.Y);
        return (settings.FocusNoteFreeLeft, settings.FocusNoteFreeTop);
    }

    /// <summary>
    /// v1.6.0 定稿：Windows 便笺纸样式——实心白底方角、无模糊/渐变/压暗/阴影；
    /// 贴屏幕边那侧一条主题蓝细条；深色文字清晰不淡化。
    /// </summary>
    private void LayoutNoteLayers(FocusNoteGeometry geometry)
    {
        NoteTextLayer.OpacityMask = null;
        NoteTextPanel.Opacity = FocusNoteStickyStyle.TextOpacity;

        ApplyStickyAccentStrip(FocusNoteStickyStyle.StripEdge(CurrentPlacement()));

        // 文字靠近“实边”一侧（Free/竖直便签取左上），深色字直接显示
        var textAlignment = geometry.IsVertical || geometry.SolidAtStart
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        NoteTextPanel.HorizontalAlignment = textAlignment;
        NoteLine1.TextAlignment = TextAlignment.Left;
        NoteLine2.TextAlignment = TextAlignment.Left;

        var rotation = geometry.TextRotationDegrees;
        // 竖直便签：文字内容盒子为 长×厚，LayoutTransform 旋转 ±90° 后正好铺满 厚×长 的窗口
        NoteTextHost.Width = geometry.IsVertical ? geometry.Height : geometry.Width;
        NoteTextHost.Height = geometry.IsVertical ? geometry.Width : geometry.Height;
        NoteTextHost.HorizontalAlignment = HorizontalAlignment.Center;
        NoteTextHost.VerticalAlignment = VerticalAlignment.Center;
        NoteTextHost.RenderTransform = null;
        NoteTextHost.LayoutTransform = rotation == 0 ? Transform.Identity : new RotateTransform(rotation);
    }

    /// <summary>主题蓝细条贴在便签贴近屏幕边缘的那一侧（水平便签上/下，竖直便签左/右）。</summary>
    private void ApplyStickyAccentStrip(FocusNoteStripEdge edge)
    {
        NoteAccentStrip.Width = double.NaN;
        NoteAccentStrip.Height = double.NaN;
        NoteAccentStrip.Background = StickyStripBrush;
        switch (edge)
        {
            case FocusNoteStripEdge.Bottom:
                NoteAccentStrip.HorizontalAlignment = HorizontalAlignment.Stretch;
                NoteAccentStrip.VerticalAlignment = VerticalAlignment.Bottom;
                NoteAccentStrip.Height = FocusNoteStickyStyle.StripThickness;
                break;
            case FocusNoteStripEdge.Left:
                NoteAccentStrip.HorizontalAlignment = HorizontalAlignment.Left;
                NoteAccentStrip.VerticalAlignment = VerticalAlignment.Stretch;
                NoteAccentStrip.Width = FocusNoteStickyStyle.StripThickness;
                break;
            case FocusNoteStripEdge.Right:
                NoteAccentStrip.HorizontalAlignment = HorizontalAlignment.Right;
                NoteAccentStrip.VerticalAlignment = VerticalAlignment.Stretch;
                NoteAccentStrip.Width = FocusNoteStickyStyle.StripThickness;
                break;
            default:
                NoteAccentStrip.HorizontalAlignment = HorizontalAlignment.Stretch;
                NoteAccentStrip.VerticalAlignment = VerticalAlignment.Top;
                NoteAccentStrip.Height = FocusNoteStickyStyle.StripThickness;
                break;
        }
    }

    private void ApplyNoteText()
    {
        if (!_focusActive) return;
        try
        {
            string line1;
            string line2;
            if (_afterSchoolDutyActive && _dutySchedule is not null)
            {
                line1 = DutyPanelFormatter.DutyLabel(_dutySchedule.Mode) + "：" +
                        (string.IsNullOrWhiteSpace(_dutySchedule.GroupName) ? "未分组" : _dutySchedule.GroupName!.Trim());
                var tasks = DutyPanelFormatter.NonTopTaskLines(_dutySchedule.Tasks);
                line2 = tasks.Count > 0 ? tasks[0].Text : (_weekShortText.Length > 0 ? _weekShortText : "");
            }
            else
            {
                var now = _previewTime ?? DateTime.Now;
                var info = FoldedTimetableFormatter.Fold(_snapshot, now);
                var label = string.IsNullOrEmpty(info.Label) ? "" : info.Label + " ";
                line1 = (label + info.MainText).Trim();
                var secondary = info.Secondary;
                if (_topDutyLines.Count > 0 && !string.IsNullOrEmpty(_topDutyLines[0].Text))
                    secondary = _topDutyLines[0].Text;
                else if (string.IsNullOrEmpty(secondary) && !string.IsNullOrEmpty(_dutySummaryText))
                    secondary = _dutySummaryText;
                line2 = secondary ?? _weekShortText;
            }

            var signature = line1 + "|" + line2;
            if (signature == _noteSignature && NoteLine1.Text == line1) return;
            _noteSignature = signature;
            NoteLine1.Text = line1;
            NoteLine2.Text = line2;
            NoteLine2.Visibility = string.IsNullOrEmpty(line2) ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新专注便签文字失败：{ex.Message}");
        }
    }

    // =========================================================================
    // 面板内容刷新（从 PetWindow 迁移）
    // =========================================================================

    private void RefreshAll(bool force)
    {
        _snapshot = GetLiveSnapshot();
        RefreshDutyAndParity(force);
        RefreshInfo(_snapshot, ResolveOverrunElapsed());
        RefreshTimetable(_snapshot, force);
        RefreshFoldedCard(force);
    }

    private void RefreshDutyAndParity(bool force)
    {
        try
        {
            var settings = Settings;
            var snapshot = _snapshot;
            var date = DateOnly.FromDateTime(snapshot.Now);

            var parity = AppServices.Current.WeekParity;
            if (settings.ShowWeekNumberOnPet && parity.Enabled)
            {
                var week = parity.GetWeekNumber(date);
                var current = parity.GetParity(date);
                _weekHeaderText = DutyPanelFormatter.WeekHeaderText(week, current, true, true);
                _weekShortText = DutyPanelFormatter.WeekShortText(week, current, true, true);
            }
            else
            {
                _weekHeaderText = "";
                _weekShortText = "";
            }

            var duty = AppServices.Current.Duty;
            Core.Models.DutyConfig? config = null;
            var dutyEnabled = false;
            try
            {
                config = duty.Config;
                dutyEnabled = settings.ShowDutyOnPet && duty.IsConfigured && config.Display.ShowOnPet;
            }
            catch (Exception ex)
            {
                Log.Warn($"读取值日配置失败：{ex.Message}");
            }

            if (dutyEnabled && config is not null)
            {
                _dutySchedule = duty.GetSchedule(date);
                var mode = config.Mode;
                _dutySummaryText = DutyPanelFormatter.DutySummaryText(mode, _dutySchedule);
                _topDutyLines = config.Display.ShowBlackboardTop
                    ? DutyPanelFormatter.TopTaskLines(_dutySchedule?.Tasks)
                    : Array.Empty<DutyTaskLine>();
                _afterSchoolDutyActive = config.Display.ShowAfterSchoolInfo
                    && DutyPanelFormatter.IsAfterSchoolDutyWindow(snapshot, config.Display.AfterSchoolMinutes, true);
            }
            else
            {
                _dutySchedule = null;
                _dutySummaryText = "";
                _topDutyLines = Array.Empty<DutyTaskLine>();
                _afterSchoolDutyActive = false;
            }

            var signature = string.Join("|",
                _weekHeaderText, _weekShortText, _afterSchoolDutyActive, _dutySummaryText,
                string.Join(";", _topDutyLines.Select(l => l.Text)));
            if (!force && signature == _dutySignature) return;
            _dutySignature = signature;
            _timetableSignature = "";
            _foldedSignature = "";

            if (_weekHeaderText.Length > 0)
            {
                WeekHeaderLine.SetText(_weekHeaderText, _fontLight, 9.5, PanelFaint);
                WeekHeaderLine.Visibility = Visibility.Visible;
            }
            else WeekHeaderLine.Visibility = Visibility.Collapsed;

            if (_topDutyLines.Count > 0)
            {
                var lines = _topDutyLines
                    .Select(l => (IReadOnlyList<HaloSegment>)new List<HaloSegment>
                    {
                        new(l.TaskName, _fontMedium, 10.5, PanelBrand),
                        new("：", _fontLight, 10.5, PanelFaint),
                        new(l.MembersText, _fontLight, 10.5, PanelNormal)
                    })
                    .ToList();
                TopTaskBody.SetLines(lines);
                TopTaskBody.Visibility = Visibility.Visible;
            }
            else TopTaskBody.Visibility = Visibility.Collapsed;

            if (_dutySummaryText.Length > 0)
            {
                DutyLine.SetText(_dutySummaryText, _fontLight, 10, PanelBrand);
                DutyLineHost.Visibility = Visibility.Visible;
            }
            else DutyLineHost.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error("刷新值日/单双周面板失败", ex);
        }
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
        var lines = new List<IReadOnlyList<HaloSegment>>();

        var row1 = new List<HaloSegment>();
        if (!string.IsNullOrEmpty(primary.Label))
        {
            var labelBrush = primary.IsOverrun ? PanelOverrun : PanelMuted;
            row1.Add(new HaloSegment(primary.Label + " ", _fontLight, 10.5, labelBrush));
        }
        if (!string.IsNullOrEmpty(primary.Value))
        {
            var valueBrush = primary.IsOverrun ? PanelOverrun : PanelStrong;
            var size = string.IsNullOrEmpty(primary.Label) ? 14.5 : 16.5;
            row1.Add(new HaloSegment(primary.Value, _fontMedium, size, valueBrush));
        }
        else row1.Add(new HaloSegment("—", _fontMedium, 14, PanelMuted));
        lines.Add(row1);

        var row2 = new List<HaloSegment>();
        if (!string.IsNullOrEmpty(school.Label))
        {
            row2.Add(new HaloSegment(school.Label + " ", _fontLight, 9.5, PanelMuted));
            row2.Add(new HaloSegment(school.Value, _fontMedium, 11.5, PanelNormal));
            if (!string.IsNullOrEmpty(school.Suffix))
                row2.Add(new HaloSegment(school.Suffix, _fontLight, 9.5, PanelMuted));
        }
        else if (!string.IsNullOrEmpty(school.Value))
            row2.Add(new HaloSegment(school.Value, _fontLight, 10.5, PanelMuted));
        lines.Add(row2);

        var row3 = new List<HaloSegment>();
        if (!string.IsNullOrEmpty(course.Value1) || !string.IsNullOrEmpty(course.Value2))
        {
            if (!string.IsNullOrEmpty(course.Prefix1) && !string.IsNullOrEmpty(course.Value1))
            {
                row3.Add(new HaloSegment(course.Prefix1 + " ", _fontLight, 9.5, PanelMuted));
                row3.Add(new HaloSegment(course.Value1, _fontMedium, 11.5, PanelStrong));
            }
            else if (!string.IsNullOrEmpty(course.Value1))
                row3.Add(new HaloSegment(course.Value1, _fontLight, 10.5, PanelMuted));

            if (!string.IsNullOrEmpty(course.Prefix2) && !string.IsNullOrEmpty(course.Value2))
            {
                row3.Add(new HaloSegment(" " + course.Prefix2.TrimStart() + " ", _fontLight, 9.5, PanelMuted));
                row3.Add(new HaloSegment(course.Value2, _fontMedium, 11.5, PanelBrand));
            }
        }
        else row3.Add(new HaloSegment("—", _fontLight, 10.5, PanelMuted));
        lines.Add(row3);

        var settings = Settings;
        var countdown = settings.ShowCountdownOnPet
            ? PetUiFormatter.NextCountdown(settings.CountdownEvents, DateOnly.FromDateTime(_previewTime ?? DateTime.Now))
            : null;
        if (countdown is { } dday)
        {
            var accent = new SolidColorBrush(ParseColor(dday.Color, Color.FromRgb(0xFF, 0x6B, 0x6B)));
            accent.Freeze();
            if (dday.IsToday)
                lines.Add(new[] { new HaloSegment(dday.Text, _fontBold, 13, accent, FontWeights.Bold) });
            else
                lines.Add(new[]
                {
                    new HaloSegment($"距{dday.Name} ", _fontLight, 10.5, PanelNormal),
                    new HaloSegment($"{dday.DaysLeft} 天", _fontBold, 13, accent, FontWeights.Bold)
                });
        }

        InfoBody.SetLines(lines);
    }

    private void RefreshTimetable(ScheduleSnapshot snapshot, bool force)
    {
        var expanded = Settings.TimetableExpanded;
        var today = snapshot.Today ?? Array.Empty<CourseOccurrence>();
        var highlighted = snapshot.Highlighted;
        var afterSchoolLines = _afterSchoolDutyActive && _dutySchedule is not null
            ? DutyPanelFormatter.AfterSchoolLines(_dutySchedule.Mode, _dutySchedule)
            : Array.Empty<DutyTaskLine>();
        var signature = string.Join("|",
            expanded, snapshot.Phase, today.Count, highlighted?.PeriodIndex, _afterSchoolDutyActive,
            string.Join(";", afterSchoolLines.Select(l => l.Text)),
            string.Join(";", today.Select(c => $"{c.PeriodIndex}:{c.Course}:{(int)c.Status}")));
        if (!force && signature == _timetableSignature) return;
        _timetableSignature = signature;

        if (_afterSchoolDutyActive && _dutySchedule is not null)
        {
            TimetableTitle.SetText("放学后", _fontLight, 10.5, PanelMuted);
            var dutyLines = BuildDutyBodyLines(afterSchoolLines);
            if (dutyLines.Count > 0)
            {
                TimetableEmpty.Visibility = Visibility.Collapsed;
                TimetableBody.SetLines(dutyLines);
            }
            else
            {
                TimetableBody.Clear();
                SetTimetableHint("值日任务已显示在顶部");
            }
            return;
        }

        TimetableTitle.SetText(PetUiFormatter.TimetableTitle(_previewTime ?? DateTime.Now), _fontLight, 10.5, PanelMuted);

        var hasToday = snapshot.HasTimetable && today.Count > 0;
        if (!hasToday)
        {
            TimetableBody.Clear();
            SetTimetableHint(snapshot.Phase == SchedulePhase.NoTimetable ? "暂无课表" : "今天没有课");
            return;
        }

        IReadOnlyList<CourseOccurrence> rows;
        if (expanded) rows = today;
        else if (highlighted is not null) rows = new[] { highlighted };
        else
        {
            var last = today.LastOrDefault();
            if (last is null || snapshot.Phase == SchedulePhase.AfterSchool)
            {
                TimetableBody.Clear();
                SetTimetableHint("今天的课已上完");
                return;
            }
            rows = new[] { last };
        }

        var maxCourseChars = Math.Clamp(rows.Max(c => c.CourseDisplay.Length), 2, 6);
        var lines = new List<IReadOnlyList<HaloSegment>>(rows.Count);
        foreach (var course in rows)
        {
            var isHighlight = highlighted is not null && course.PeriodIndex == highlighted.PeriodIndex;
            lines.Add(BuildTimetableLine(course, isHighlight, maxCourseChars));
        }

        TimetableEmpty.Visibility = Visibility.Collapsed;
        TimetableBody.SetLines(lines);
    }

    private List<IReadOnlyList<HaloSegment>> BuildDutyBodyLines(IReadOnlyList<DutyTaskLine> lines)
    {
        var result = new List<IReadOnlyList<HaloSegment>>(lines.Count);
        foreach (var line in lines)
        {
            result.Add(new List<HaloSegment>
            {
                new(line.TaskName, _fontMedium, 11.5, PanelNormal),
                new("：", _fontLight, 10.5, PanelFaint),
                new(line.MembersText, _fontLight, 10.5, PanelMuted)
            });
        }
        return result;
    }

    private void SetTimetableHint(string text)
    {
        TimetableEmpty.SetText(text, _fontLight, 10.5, PanelMuted);
        TimetableEmpty.Visibility = Visibility.Visible;
    }

    private List<HaloSegment> BuildTimetableLine(CourseOccurrence course, bool isHighlight, int maxCourseChars)
    {
        var isPast = course.Status == CourseStatus.Past;
        var indexBrush = isHighlight ? PanelOverrun : PanelFaint;
        var courseBrush = isHighlight ? _accentBrush : (isPast ? PanelMuted : PanelNormal);
        var index = PetUiFormatter.PeriodTag(course).PadLeft(2, '　') + "　";
        var courseText = course.CourseDisplay;
        if (courseText.Length < maxCourseChars)
            courseText += new string('　', maxCourseChars - courseText.Length);

        return new List<HaloSegment>
        {
            new(index, _fontLight, 9, indexBrush),
            new(courseText, isHighlight ? _fontBold : _fontMedium, isHighlight ? 12.5 : 12, courseBrush,
                isHighlight ? FontWeights.Bold : FontWeights.Normal),
            new("　" + PetUiFormatter.CourseTime(course), _fontLight, 8.5, isHighlight ? PanelOverrun : PanelFaint)
        };
    }

    private void RefreshFoldedCard(bool force)
    {
        try
        {
            var now = _previewTime ?? DateTime.Now;
            var info = FoldedTimetableFormatter.Fold(_snapshot, now);
            var mode = _dutySchedule?.Mode ?? DutyMode.Weekly;
            var topText = _topDutyLines.Count > 0
                ? _topDutyLines[0].Text + (_topDutyLines.Count > 1 ? $" 等{_topDutyLines.Count}项" : "")
                : "";
            var showAfterSchoolDuty = _afterSchoolDutyActive && _dutySchedule is not null;

            string label;
            string main;
            string? secondary;
            if (showAfterSchoolDuty && _dutySchedule is not null)
            {
                label = DutyPanelFormatter.DutyLabel(mode);
                main = _dutySchedule.IsPaused
                    ? (mode == DutyMode.Weekly ? "本周暂停" : "今日暂停")
                    : string.IsNullOrWhiteSpace(_dutySchedule.GroupName) ? "未分组" : _dutySchedule.GroupName!.Trim();
                var rest = DutyPanelFormatter.NonTopTaskLines(_dutySchedule.Tasks);
                secondary = rest.Count > 0 ? rest[0].Text : null;
            }
            else
            {
                label = string.IsNullOrEmpty(info.Label) ? "今天" : info.Label;
                main = info.MainText;
                secondary = info.Secondary;
            }

            var dutyLink = _afterSchoolDutyActive ? "查看值日详情 ›" : _dutySummaryText;
            var weekRight = _weekShortText.Length > 0 ? _weekShortText : "点击展开";
            var signature = string.Join("|", label, main, secondary ?? "", topText, dutyLink, weekRight, _dutySchedule is not null);
            if (!force && signature == _foldedSignature) return;
            _foldedSignature = signature;

            FoldedTopTask.Text = topText;
            FoldedTopTask.Visibility = topText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            FoldedLabel.Text = label;
            FoldedRightText.Text = weekRight;
            FoldedMain.Text = main;
            FoldedSecondary.Text = secondary ?? "";
            FoldedSecondary.Visibility = string.IsNullOrEmpty(secondary) ? Visibility.Collapsed : Visibility.Visible;
            FoldedDuty.Text = dutyLink.Length > 0 && !_afterSchoolDutyActive ? dutyLink + " ›" : dutyLink;
            FoldedDuty.Visibility = dutyLink.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新课表折叠态失败：{ex.Message}");
        }
    }

    // =========================================================================
    // 时钟预览 / 工具
    // =========================================================================

    private void LoadPreviewTime()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("BANJI_PET_PREVIEW_TIME");
            if (string.IsNullOrWhiteSpace(raw)) return;
            if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed)
                || DateTime.TryParse(raw, out parsed))
            {
                _previewTime = parsed;
                Log.Info($"课表窗预览模式：固定时间 {parsed:yyyy-MM-dd HH:mm:ss}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取课表窗预览时间失败：{ex.Message}");
        }
    }

    /// <summary>仅开发/QA：BANJI_FOCUS_NOTE_QA=TopLeftHorizontal… / BANJI_FOCUS_NOTE_FREE_QA="x,y" 覆盖便签位置。</summary>
    private void LoadQaNoteOverrides()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("BANJI_FOCUS_NOTE_QA");
            if (!string.IsNullOrWhiteSpace(raw)
                && Enum.TryParse<FocusNotePlacement>(raw.Trim(), ignoreCase: true, out var parsed))
            {
                _qaPlacementOverride = parsed;
                Log.Info($"QA：专注便签位置覆盖为 {parsed}");
            }
            var freeRaw = Environment.GetEnvironmentVariable("BANJI_FOCUS_NOTE_FREE_QA");
            if (!string.IsNullOrWhiteSpace(freeRaw))
            {
                var parts = freeRaw.Split(',');
                if (parts.Length == 2
                    && double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var fx)
                    && double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var fy))
                {
                    _qaFreeOverride = (fx, fy);
                    Log.Info($"QA：自由便签位置覆盖为 ({fx:0},{fy:0})");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 QA 便签覆盖失败：{ex.Message}");
        }
    }

    private ScheduleSnapshot GetLiveSnapshot()
    {
        if (_previewTime is { } preview)
        {
            try { return AppServices.Current.Schedule.SnapshotAt(preview); }
            catch (Exception ex) { Log.Warn($"课表窗预览快照生成失败：{ex.Message}"); }
        }
        return AppServices.Current.Clock.Current ?? ScheduleSnapshot.Empty(DateTime.Now);
    }

    /// <summary>
    /// v1.7.0：课表窗永远插到宠物窗下方（两窗都 Topmost；用 SetWindowPos insertAfter=宠物窗句柄）。
    /// 这样两窗重叠时鼠标命中优先宠物，拖宠物不会被课表窗透明区域截获。
    /// </summary>
    public void EnsureBelowPet() => ReassertBelowPet();

    private void ReassertBelowPet()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var petWindow = AppServices.Current.PetWindow;
            var petHandle = petWindow is null ? IntPtr.Zero : new WindowInteropHelper(petWindow).Handle;
            if (petHandle == IntPtr.Zero)
            {
                NativeMethods.ReassertTopmost(handle);
                return;
            }
            NativeMethods.SetWindowPos(handle, petHandle, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER);
        }
        catch (Exception ex)
        {
            Log.Warn($"把课表窗口压到宠物窗下方失败：{ex.Message}");
        }
    }

    /// <summary>QA：BANJI_PANEL_FORCE_HALO=1 复现 v1.6 白卡黑边 bug 用于前后对比截图。</summary>
    private void LoadQaHaloOverride()
    {
        try
        {
            _forceHaloForQa = string.Equals(
                Environment.GetEnvironmentVariable("BANJI_PANEL_FORCE_HALO"), "1", StringComparison.Ordinal);
            if (_forceHaloForQa) Log.Info("QA：强制开启 Halo（复现白卡黑边）");
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 QA Halo 覆盖失败：{ex.Message}");
        }
    }

    /// <summary>v1.7.0：白卡/便签都关闭 Halo 黑色光晕/描边（白卡是深色字，开着会糊成黑边）。</summary>
    private void ApplyPanelHalo()
    {
        try
        {
            var surface = _focusActive ? TimetablePanelSurface.FocusNote : TimetablePanelSurface.WhiteCard;
            var enabled = _forceHaloForQa || TimetablePanelPolicy.ShouldEnableHalo(surface);
            foreach (var halo in EnumeratePanelHaloControls())
            {
                switch (halo)
                {
                    case HaloTextLine line: line.HaloEnabled = enabled; break;
                    case HaloTextStack stack: stack.HaloEnabled = enabled; break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"切换课表 Halo 失败：{ex.Message}");
        }
    }

    private IEnumerable<FrameworkElement> EnumeratePanelHaloControls()
    {
        yield return WeekHeaderLine;
        yield return TopTaskBody;
        yield return TimetableTitle;
        yield return TimetableBody;
        yield return TimetableEmpty;
        yield return DutyLine;
        yield return InfoBody;
    }

    private static ISettingsService SettingsService => AppServices.Current.Settings;
    private static Core.Settings.AppSettings Settings => AppServices.Current.Settings.Current;

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











