using System.Windows;
using System.Windows.Controls;
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
/// 桌面宠物主窗口（第二轮改版）：透明无边框置顶、吸附屏幕右侧、可拖动、整体随 UiScale 无级缩放。
/// 承载：方形宠物本体、常驻信息（含考试 D-Day 行）、当天课表、头顶两个小三角、左下角功能区三角。
/// 第二轮起课表/信息**不再有卡片背景**，改为“白字 + 文字背后黑色重高斯模糊光晕”直接悬浮桌面。
/// 同时负责协调 <see cref="BubbleWindow"/>（台词/抽签/计时）与 <see cref="MenuFlyout"/>。
/// </summary>
public partial class PetWindow : Window
{
    private readonly QuipProvider _quips = new();
    private readonly BubbleWindow _bubble;
    private readonly DispatcherTimer _topmostTimer;

    private readonly FontFamily _fontThin;
    private readonly FontFamily _fontLight;
    private readonly FontFamily _fontRegular;
    private readonly FontFamily _fontMedium;
    private readonly FontFamily _fontBold;

    // 第二轮：课表/信息框去掉卡片背景，改为“白色文字 + 背后黑色重高斯模糊光晕”的悬浮文字方案。
    private static readonly SolidColorBrush WhiteBrush = FrozenBrush("#FFFFFF");
    private static readonly SolidColorBrush WhiteSoftBrush = FrozenBrush("#F2FFFFFF");
    private static readonly SolidColorBrush WhiteMutedBrush = FrozenBrush("#D9FFFFFF");
    private static readonly SolidColorBrush WhiteFaintBrush = FrozenBrush("#BFFFFFFF");
    private static readonly SolidColorBrush BrandLightBrush = FrozenBrush("#9CC3FF");
    private static readonly SolidColorBrush WarningBrush = FrozenBrush("#FF6B6B");
    private static readonly SolidColorBrush ToolTipTextBrush = FrozenBrush("#1F2329");

    private SolidColorBrush _accentBrush = FrozenBrush("#FF3030");
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

    // v1.2.0：宠物本体与课表/信息面板分离缩放 + 面板偏移
    private double _petRightInWindow;
    private double _petBottomInWindow;
    private double _lastPanelDesignHeight = -1;
    private bool _applyingLayout;
    private int _quipRequestId;

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
    // QA / 自动化入口（Lead 的 --cmd quip [text] / --cmd pat 会调用）
    // =========================================================================

    /// <summary>QA：显示一条摸头气泡；text 为空时随机出一条（长文本可用于验证多行排版）。</summary>
    public void ShowTestQuip(string? text = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                _bubble.ShowQuip(text.Trim());
                return;
            }
            RequestQuipBubble();
        }
        catch (Exception ex)
        {
            Log.Error("QA 显示摸头气泡失败", ex);
        }
    }

    /// <summary>QA：等价于摸头——播放点击反馈 + 触发台词气泡 + 养成统计摸头 +1。</summary>
    public void TestPat()
    {
        try
        {
            Pet.PlayTouchReaction();
            if (Settings.EnableGrowth)
            {
                try { AppServices.Current.Growth.RecordPat(); }
                catch (Exception ex) { Log.Error("记录摸头失败", ex); }
            }
            if (!Settings.EnableQuips) return;
            RequestQuipBubble();
        }
        catch (Exception ex)
        {
            Log.Error("摸摸宠物失败", ex);
        }
    }

    /// <summary>
    /// v1.2.0：摸头台词优先用端侧 AI（模型已加载且开启 AI 台词时），
    /// 1.5 秒内拿不到结果就回退本地台词生成器；全程异步，不阻塞 UI 线程。
    /// </summary>
    private void RequestQuipBubble()
    {
        IAiService? ai = null;
        try
        {
            ai = AppServices.Current.Ai;
        }
        catch
        {
            // 服务未就绪时走本地
        }

        var settings = Settings;
        if (ai is not null && settings.Ai.EnableAiQuips && ai.IsModelLoaded)
        {
            var requestId = ++_quipRequestId;
            _ = GenerateAiQuipAsync(ai, requestId);
            return;
        }

        _bubble.ShowQuip(_quips.Next(settings.UseLocalQuipGenerator));
    }

    private async Task GenerateAiQuipAsync(IAiService ai, int requestId)
    {
        string? generated = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var task = ai.TryGenerateQuipAsync("课堂桌面宠物的一句短台词", cts.Token);
            var finished = await Task.WhenAny(task, Task.Delay(1500)).ConfigureAwait(true);
            if (finished == task)
            {
                try { generated = await task.ConfigureAwait(true); }
                catch { generated = null; }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"AI 台词生成失败，回退本地台词：{ex.Message}");
        }

        if (requestId != _quipRequestId) return; // 期间又摸了一次，丢弃旧结果

        var text = SanitizeAiQuip(generated);
        if (!string.IsNullOrEmpty(text))
        {
            _bubble.ShowQuip(text);
            return;
        }
        _bubble.ShowQuip(_quips.Next(Settings.UseLocalQuipGenerator));
    }

    private static string? SanitizeAiQuip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Replace("\r", " ").Replace("\n", " ").Trim().Trim('"', '“', '”', '\'');
        if (value.Length > 80) value = value[..80];
        return value.Length >= 2 ? value : null;
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
            Topmost = settings.AlwaysOnTop;
            TimetableSection.Visibility = settings.ShowTimetablePanel ? Visibility.Visible : Visibility.Collapsed;
            UpdateTriangleStates();

            LoadPreviewTime();
            MigrateLegacyUiScale();
            UpdateLayout();
            InitializePosition();
            ApplyLayout();
            _snapshot = GetLiveSnapshot();
            RefreshFromSnapshot(force: true);
            UpdatePanelSizeIfNeeded();
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
        var settings = Settings;
        // 宠物群右下角锚定工作区右下；margin 即宠物群到右/下边缘的距离。
        _rightMargin = Math.Clamp(settings.PetRightMargin, 0, 4000);
        _bottomMargin = Math.Clamp(settings.PetBottomMargin, 0, 4000);
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
        if (_applyingLayout || !_initialized) return;
        _bubble.RepositionAndRefresh();
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("WorkArea" or "PrimaryScreenWidth" or "PrimaryScreenHeight")) return;
        try
        {
            ApplyLayout();
        }
        catch (Exception ex)
        {
            Log.Error("响应屏幕尺寸变化失败", ex);
        }
    }

    /// <summary>
    /// v1.2.0 核心布局：宠物群（PetScale）与课表/信息面板（PanelScale + PanelOffset）分别缩放/定位，
    /// 窗口尺寸取两者的并集（不裁切）；宠物群右下角锚定工作区右下 margin 处，并把窗口位置收进屏幕。
    /// </summary>
    private void ApplyLayout()
    {
        if (!_initialized) return;
        _applyingLayout = true;
        try
        {
            var settings = Settings;
            var petScale = Math.Clamp(settings.PetScale, 0.2, 5.0);
            var panelScale = Math.Clamp(settings.PanelScale, 0.4, 4.0);
            PetScaleTransform.ScaleX = petScale;
            PetScaleTransform.ScaleY = petScale;
            PanelScaleTransform.ScaleX = panelScale;
            PanelScaleTransform.ScaleY = panelScale;

            // 面板自然尺寸（未缩放）
            PanelStack.Measure(new Size(196, double.PositiveInfinity));
            var panelDesignHeight = Math.Max(40, PanelStack.DesiredSize.Height);
            _lastPanelDesignHeight = panelDesignHeight;
            var panelW = 196 * panelScale;
            var panelH = panelDesignHeight * panelScale;

            // 宠物群设计尺寸（功能区展开时向左多出 170 宽）
            var petClusterDesignW = _functionsExpanded ? 282 : 112;
            PetHost.Width = petClusterDesignW;
            PetHost.Height = 134;
            var petW = petClusterDesignW * petScale;
            var petH = 134 * petScale;

            // 面板默认位置：右缘与宠物群右缘对齐、底边在宠物群上方 8px；再叠加用户偏移
            var offsetX = settings.PanelOffsetX;
            var offsetY = settings.PanelOffsetY;
            const double gap = 8;
            var panelRight = offsetX;
            var panelBottom = -petH - gap + offsetY;
            var panelLeft = panelRight - panelW;
            var panelTop = panelBottom - panelH;

            // 局部坐标：宠物群右下角为 (0,0)，向上/向左为负
            var minX = Math.Min(-petW, panelLeft);
            var maxX = Math.Max(0, panelRight);
            var minY = Math.Min(-petH, panelTop);
            var maxY = 0;

            const double margin = 24;
            var windowW = Math.Max(140, (maxX - minX) + margin * 2);
            var windowH = Math.Max(180, (maxY - minY) + margin * 2);

            var petRightInWindow = margin + (0 - minX);
            var petBottomInWindow = margin + (0 - minY);
            _petRightInWindow = petRightInWindow;
            _petBottomInWindow = petBottomInWindow;

            Canvas.SetLeft(PetHost, Math.Round(petRightInWindow - petW));
            Canvas.SetTop(PetHost, Math.Round(petBottomInWindow - petH));
            Canvas.SetLeft(PanelHost, Math.Round(margin + (panelLeft - minX)));
            Canvas.SetTop(PanelHost, Math.Round(margin + (panelTop - minY)));

            if (Math.Abs(Width - windowW) > 0.5) Width = windowW;
            if (Math.Abs(Height - windowH) > 0.5) Height = windowH;

            var work = SystemParameters.WorkArea;
            var anchorX = work.Right - _rightMargin;
            var anchorY = work.Bottom - _bottomMargin;
            var desiredLeft = anchorX - petRightInWindow;
            var desiredTop = anchorY - petBottomInWindow;
            var left = windowW <= work.Width
                ? Math.Clamp(desiredLeft, work.Left, work.Right - windowW)
                : work.Right - windowW;
            var top = windowH <= work.Height
                ? Math.Clamp(desiredTop, work.Top, work.Bottom - windowH)
                : work.Bottom - windowH;
            Left = Math.Round(left);
            Top = Math.Round(top);
        }
        catch (Exception ex)
        {
            Log.Error("计算宠物窗布局失败", ex);
        }
        finally
        {
            _applyingLayout = false;
        }
        _bubble.RepositionAndRefresh();
    }

    /// <summary>课表面板内容高度变化（展开/折叠、D-Day 行出现等）时重新计算窗口尺寸，避免裁切。</summary>
    private void UpdatePanelSizeIfNeeded()
    {
        if (!_initialized) return;
        try
        {
            PanelStack.Measure(new Size(196, double.PositiveInfinity));
            var height = PanelStack.DesiredSize.Height;
            if (Math.Abs(height - _lastPanelDesignHeight) > 0.5) ApplyLayout();
        }
        catch (Exception ex)
        {
            Log.Warn($"更新课表面板尺寸失败：{ex.Message}");
        }
    }

    /// <summary>旧版 UiScale 兼容：升级到 v1.2.0 后把整体缩放拆到 PetScale / PanelScale。</summary>
    private void MigrateLegacyUiScale()
    {
        try
        {
            var settings = Settings;
            if (Math.Abs(settings.UiScale - 1.0) < 0.01) return;
            if (Math.Abs(settings.PetScale - 1.0) > 0.001 || Math.Abs(settings.PanelScale - 1.0) > 0.001) return;
            var legacy = settings.UiScale;
            SettingsService.Update(x =>
            {
                x.PetScale = Math.Clamp(legacy, 0.2, 5.0);
                x.PanelScale = Math.Clamp(legacy, 0.4, 4.0);
                x.UiScale = 1.0;
            });
            Log.Info($"旧版 UiScale={legacy:0.##} 已迁移为 PetScale/PanelScale");
        }
        catch (Exception ex)
        {
            Log.Warn($"UiScale 兼容迁移失败：{ex.Message}");
        }
    }

    private void PersistPosition()
    {
        try
        {
            var work = SystemParameters.WorkArea;
            _rightMargin = Math.Clamp(Math.Round(work.Right - (Left + _petRightInWindow)), 0, 4000);
            _bottomMargin = Math.Clamp(Math.Round(work.Bottom - (Top + _petBottomInWindow)), 0, 4000);
            // 贴近边缘时吸附。
            if (_rightMargin < 24) _rightMargin = 0;
            if (_bottomMargin < 24) _bottomMargin = 0;
            SettingsService.Update(s =>
            {
                s.PetRightMargin = _rightMargin;
                s.PetBottomMargin = _bottomMargin;
            });
            ApplyLayout();
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

    private void OnPetTouched() => TestPat();

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
            _functionsExpanded = true;
            ApplyLayout();
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
                    ApplyLayout();
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
            Foreground = ToolTipTextBrush
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
            if (Topmost != settings.AlwaysOnTop) Topmost = settings.AlwaysOnTop;

            var target = settings.ShowTimetablePanel ? Visibility.Visible : Visibility.Collapsed;
            if (TimetableSection.Visibility != target) TimetableSection.Visibility = target;

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

            ApplyLayout();
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
            settings.Buttons.IdleColor,
            settings.ShowCountdownOnPet,
            string.Join(";", settings.CountdownEvents.Select(c => $"{c.Id}:{c.Name}:{c.Date:yyyyMMdd}:{c.Enabled}:{c.Color}")));
    }

    private void RefreshAccentBrushes(Core.Settings.AppSettings settings)
    {
        _accentBrush = SolidBrush(settings.Buttons.AccentColor, Color.FromRgb(0xFF, 0x30, 0x30), 255);
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
        UpdatePanelSizeIfNeeded();
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

        // 行 1：距下课 / 距上课 / 已放学 / 下课超时
        var row1 = new List<HaloSegment>();
        if (!string.IsNullOrEmpty(primary.Label))
        {
            var labelBrush = primary.IsOverrun ? WarningBrush : WhiteMutedBrush;
            row1.Add(new HaloSegment(primary.Label + " ", _fontLight, 10.5, labelBrush));
        }
        if (!string.IsNullOrEmpty(primary.Value))
        {
            var valueBrush = primary.IsOverrun ? WarningBrush : WhiteBrush;
            var size = string.IsNullOrEmpty(primary.Label) ? 14.5 : 16.5;
            row1.Add(new HaloSegment(primary.Value, _fontMedium, size, valueBrush));
        }
        else
        {
            row1.Add(new HaloSegment("—", _fontMedium, 14, WhiteMutedBrush));
        }
        lines.Add(row1);

        // 行 2：距放学 h:mm:ss + 剩余节数
        var row2 = new List<HaloSegment>();
        if (!string.IsNullOrEmpty(school.Label))
        {
            row2.Add(new HaloSegment(school.Label + " ", _fontLight, 9.5, WhiteMutedBrush));
            row2.Add(new HaloSegment(school.Value, _fontMedium, 11.5, WhiteSoftBrush));
            if (!string.IsNullOrEmpty(school.Suffix))
                row2.Add(new HaloSegment(school.Suffix, _fontLight, 9.5, WhiteMutedBrush));
        }
        else if (!string.IsNullOrEmpty(school.Value))
        {
            row2.Add(new HaloSegment(school.Value, _fontLight, 10.5, WhiteMutedBrush));
        }
        lines.Add(row2);

        // 行 3：现在 / 下节课程名
        var row3 = new List<HaloSegment>();
        if (!string.IsNullOrEmpty(course.Value1) || !string.IsNullOrEmpty(course.Value2))
        {
            if (!string.IsNullOrEmpty(course.Prefix1) && !string.IsNullOrEmpty(course.Value1))
            {
                row3.Add(new HaloSegment(course.Prefix1 + " ", _fontLight, 9.5, WhiteMutedBrush));
                row3.Add(new HaloSegment(course.Value1, _fontMedium, 11.5, WhiteBrush));
            }
            else if (!string.IsNullOrEmpty(course.Value1))
            {
                row3.Add(new HaloSegment(course.Value1, _fontLight, 10.5, WhiteMutedBrush));
            }
            if (!string.IsNullOrEmpty(course.Prefix2) && !string.IsNullOrEmpty(course.Value2))
            {
                row3.Add(new HaloSegment(" " + course.Prefix2.TrimStart() + " ", _fontLight, 9.5, WhiteMutedBrush));
                row3.Add(new HaloSegment(course.Value2, _fontMedium, 11.5, BrandLightBrush));
            }
        }
        else
        {
            row3.Add(new HaloSegment("—", _fontLight, 10.5, WhiteMutedBrush));
        }
        lines.Add(row3);

        // 行 4：考试/重要日期倒计时（第二轮新增 D-Day）
        var settings = Settings;
        var countdown = settings.ShowCountdownOnPet
            ? PetUiFormatter.NextCountdown(settings.CountdownEvents, DateOnly.FromDateTime(_previewTime ?? DateTime.Now))
            : null;
        if (countdown is { } dday)
        {
            var accent = new SolidColorBrush(ParseColor(dday.Color, Color.FromRgb(0xFF, 0x6B, 0x6B)));
            accent.Freeze();
            if (dday.IsToday)
            {
                lines.Add(new[] { new HaloSegment(dday.Text, _fontBold, 13, accent, FontWeights.Bold) });
            }
            else
            {
                lines.Add(new[]
                {
                    new HaloSegment($"距{dday.Name} ", _fontLight, 10.5, WhiteSoftBrush),
                    new HaloSegment($"{dday.DaysLeft} 天", _fontBold, 13, accent, FontWeights.Bold)
                });
            }
        }

        InfoBody.SetLines(lines);
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

        TimetableTitle.SetText(PetUiFormatter.TimetableTitle(_previewTime ?? DateTime.Now),
            _fontLight, 10.5, WhiteMutedBrush);

        var hasToday = snapshot.HasTimetable && today.Count > 0;
        if (!hasToday)
        {
            TimetableBody.Clear();
            SetTimetableHint(snapshot.Phase == SchedulePhase.NoTimetable ? "暂无课表" : "今天没有课");
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

    private void SetTimetableHint(string text)
    {
        TimetableEmpty.SetText(text, _fontLight, 10.5, WhiteMutedBrush);
        TimetableEmpty.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 课表一行：用全角空格做列对齐（节次 / 课程名 / 开始时间），纯文字无卡片背景；
    /// 强调行保留 #FF3030 红色（第三轮仍保留强调色）。
    /// </summary>
    private List<HaloSegment> BuildTimetableLine(CourseOccurrence course, bool isHighlight, int maxCourseChars)
    {
        var isPast = course.Status == CourseStatus.Past;
        var indexBrush = isHighlight ? WarningBrush : WhiteFaintBrush;
        var courseBrush = isHighlight ? _accentBrush : (isPast ? WhiteMutedBrush : WhiteSoftBrush);

        var index = PetUiFormatter.PeriodTag(course).PadLeft(2, '　') + "　";

        var courseText = course.CourseDisplay;
        if (courseText.Length < maxCourseChars)
            courseText += new string('　', maxCourseChars - courseText.Length);

        return new List<HaloSegment>
        {
            new(index, _fontLight, 9, indexBrush),
            new(courseText, isHighlight ? _fontBold : _fontMedium,
                isHighlight ? 12.5 : 12, courseBrush,
                isHighlight ? FontWeights.Bold : FontWeights.Normal),
            new("　" + PetUiFormatter.CourseTime(course), _fontLight, 8.5,
                isHighlight ? WarningBrush : WhiteFaintBrush)
        };
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













