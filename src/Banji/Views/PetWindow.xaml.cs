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
/// v1.6.0 宠物窗口：只负责宠物本体 / 头顶三角 / 功能区 / 侧边把手 / 气泡锚点 / 摸头台词。
/// 课表（白卡、折叠、值日、放学后、专注便签、拖动、缩放）全部迁到独立窗口 <see cref="TimetablePanelWindow"/>，
/// 拖动/隐藏/折叠互不联动，宠物窗口尺寸只按宠物群/功能区/把手计算。
/// </summary>
public partial class PetWindow : Window
{
    private readonly QuipProvider _quips = new();
    private readonly BubbleWindow _bubble;
    private readonly NoticeBubbleWindow _noticeBubble;
    private readonly DispatcherTimer _topmostTimer;
    private readonly IdleAutoHideLogic _idle;
    private readonly DispatcherTimer _idleTimer;

    private readonly FontFamily _fontThin;
    private readonly FontFamily _fontLight;
    private readonly FontFamily _fontRegular;
    private readonly FontFamily _fontMedium;
    private readonly FontFamily _fontBold;

    private static readonly SolidColorBrush ToolTipTextBrush = FrozenBrush("#1F2329");

    private ScheduleSnapshot _snapshot = ScheduleSnapshot.Empty(DateTime.Now);
    private DateTime? _previewTime;

    private double _rightMargin;
    private double _bottomMargin;
    private bool _initialized;
    private bool _dragging;
    private bool _functionsExpanded;
    private double _dragOriginLeft;
    private double _dragOriginTop;
    private Point _dragStartCursor;
    private MenuFlyout? _openMenu;
    private int _quipRequestId;

    private double _petRightInWindow;
    private double _petBottomInWindow;
    private bool _applyingLayout;
    private bool _petCollapsedToEdge;
    private double _collapsedCenterY;
    private bool _lastHidePet;
    private bool _focusWindowActive;
    private bool _focusHidesPet;

    private TimetablePanelWindow? _timetablePanel;

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
        _noticeBubble = new NoticeBubbleWindow();
        _idle = new IdleAutoHideLogic(DateTime.Now);

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(6) };
        _topmostTimer.Tick += (_, _) => KeepOnTop();

        _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _idleTimer.Tick += (_, _) => EvaluateIdleAutoHide();

        Pet.MouseLeftButtonDown += OnPetMouseLeftButtonDown;
        Pet.MouseMove += OnPetMouseMove;
        Pet.MouseLeftButtonUp += OnPetMouseUp;
        Pet.LostMouseCapture += (_, _) => _dragging = false;
        Pet.MouseRightButtonUp += OnPetRightButtonUp;
        Pet.MouseEnter += (_, _) => TouchPetActivity();
        Pet.MouseMove += (_, _) => TouchPetActivity();
        FunctionHost.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler(OnFunctionRightButtonUp), true);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // =========================================================================
    // 供气泡窗定位使用
    // =========================================================================

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

    public Rect GetNoticeAnchorRect()
    {
        try
        {
            if (_focusHidesPet)
            {
                // 专注隐藏宠物时连把手也不显示：提醒气泡锚到屏幕右下角（原把手位置）
                var work = SystemParameters.WorkArea;
                return new Rect(work.Right - 26, work.Bottom - 130, 26, 130);
            }
            if (IsPetHiddenOrCollapsed)
            {
                var topLeft = EdgeArcHost.TranslatePoint(new Point(0, 0), this);
                var bottomRight = EdgeArcHost.TranslatePoint(new Point(EdgeArcHost.ActualWidth, EdgeArcHost.ActualHeight), this);
                return new Rect(Left + topLeft.X, Top + topLeft.Y,
                    Math.Max(1, bottomRight.X - topLeft.X), Math.Max(1, bottomRight.Y - topLeft.Y));
            }
            return GetPetBodyRect();
        }
        catch
        {
            return new Rect(Left, Top, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        }
    }

    public bool IsPetHiddenOrCollapsed => _petCollapsedToEdge || Settings.HidePet || _focusHidesPet;

    /// <summary>课表当前是否折叠（委托给独立课表窗口）。</summary>
    public bool IsTimetableFolded => _timetablePanel?.IsFolded == true;

    /// <summary>当前是否处于上课专注窗口（供 QA；宠物是否隐藏还看 AutoHidePetDuringClass）。</summary>
    public bool IsClassFocusActive => _focusWindowActive;

    public bool IsFunctionsExpanded => _functionsExpanded;

    // =========================================================================
    // QA / 自动化入口
    // =========================================================================

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

    private void RequestQuipBubble()
    {
        IAiService? ai = null;
        try { ai = AppServices.Current.Ai; } catch { /* ignore */ }
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
        if (requestId != _quipRequestId) return;
        var text = SanitizeAiQuip(generated);
        _bubble.ShowQuip(!string.IsNullOrEmpty(text) ? text : _quips.Next(Settings.UseLocalQuipGenerator));
    }

    private static string? SanitizeAiQuip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Replace("\r", " ").Replace("\n", " ").Trim().Trim('"', '“', '”', '\'');
        if (value.Length > 80) value = value[..80];
        return value.Length >= 2 ? value : null;
    }

    /// <summary>隐藏宠物（收进屏幕侧边只剩蓝把手）。</summary>
    public void CollapseToEdge()
    {
        try
        {
            if (_petCollapsedToEdge) return;
            var body = GetPetBodyRect();
            if (body.Height > 1) _collapsedCenterY = body.Top + body.Height * 0.5;
            _petCollapsedToEdge = true;
            _idle.MarkPetCollapsed();
            CloseMenuIfOpen(false);
            try { if (_bubble.IsShowing) _bubble.Hide(); } catch { /* ignore */ }
            ApplyLayout();
            AnimateEdgeArcIn();
            Log.Info("宠物已收进屏幕侧边（弧线态）");
        }
        catch (Exception ex)
        {
            Log.Error("收起宠物失败", ex);
        }
    }

    /// <summary>从侧边把手展开宠物。</summary>
    public void ExpandFromEdge()
    {
        try
        {
            if (!_petCollapsedToEdge && !Settings.HidePet)
            {
                _idle.TouchPet(DateTime.Now);
                return;
            }
            _petCollapsedToEdge = false;
            _idle.MarkPetExpanded(DateTime.Now);
            if (Settings.HidePet) SettingsService.Update(s => s.HidePet = false);
            ApplyLayout();
            AnimatePetIn();
            Log.Info("宠物已从屏幕侧边展开");
        }
        catch (Exception ex)
        {
            Log.Error("展开宠物失败", ex);
        }
    }

    /// <summary>折叠课表（委托独立课表窗口）。</summary>
    public void FoldTimetable()
    {
        try { _timetablePanel?.SetFolded(true); }
        catch (Exception ex) { Log.Error("折叠课表失败", ex); }
    }

    /// <summary>展开课表（委托独立课表窗口）。</summary>
    public void UnfoldTimetable()
    {
        try { _timetablePanel?.SetFolded(false); }
        catch (Exception ex) { Log.Error("展开课表失败", ex); }
    }

    /// <summary>v1.5/1.6 QA：切换功能区展开/收起。</summary>
    public void ToggleFunctions()
    {
        try
        {
            if (_focusWindowActive || _petCollapsedToEdge || Settings.HidePet)
            {
                Log.Info("宠物可视元素隐藏时忽略功能区切换");
                return;
            }
            if (_functionsExpanded) CollapseFunctions();
            else ExpandFunctions();
            Log.Info($"功能区已{(_functionsExpanded ? "展开" : "收起")}");
        }
        catch (Exception ex)
        {
            Log.Error("切换功能区失败", ex);
        }
    }

    // =========================================================================
    // 初始化 / 生命周期
    // =========================================================================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
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
            Pet.ApplySettings(settings.Pet, settings.PetBlinkEnabled, settings.PetBobEnabled);
            Topmost = settings.AlwaysOnTop;
            UpdateTriangleStates();

            LoadPreviewTime();
            MigrateLegacyUiScale();
            UpdateLayout();
            InitializePosition();

            _lastHidePet = settings.HidePet;
            if (settings.HidePet)
            {
                var petH = 134 * Math.Clamp(settings.PetScale, 0.2, 5.0);
                _collapsedCenterY = SystemParameters.WorkArea.Bottom - _bottomMargin - petH * 0.5;
                _idle.MarkPetCollapsed();
            }

            ApplyLayout();
            _snapshot = GetLiveSnapshot();
            EvaluateClassFocus();

            // v1.6.0：创建/持有独立课表窗口
            try
            {
                _timetablePanel = new TimetablePanelWindow();
                _timetablePanel.Show();
                UpdateLayout();
                _timetablePanel.PlaceNearPet(new Rect(Left, Top, ActualWidth, ActualHeight));
            }
            catch (Exception ex)
            {
                Log.Error("创建独立课表窗口失败", ex);
            }

            SubscribeServices();
            _idleTimer.Start();
            _topmostTimer.Start();
            ApplyQaStateFromEnvironment();
            AnimateWindowIn();
            KeepOnTop();
        }
        catch (Exception ex)
        {
            Log.Error("初始化宠物窗口失败", ex);
        }
    }

    /// <summary>
    /// 开发/截图 QA：BANJI_PET_QA_STATE=collapsed|expanded|functions|focus（可逗号组合）。
    /// </summary>
    private void ApplyQaStateFromEnvironment()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("BANJI_PET_QA_STATE");
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var token in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (token.Trim().ToLowerInvariant())
                {
                    case "collapsed": CollapseToEdge(); break;
                    case "expanded": ExpandFromEdge(); break;
                    case "functions": ToggleFunctions(); break;
                    case "focus": EvaluateClassFocus(); break;
                    case "folded": FoldTimetable(); break;
                    case "unfolded": UnfoldTimetable(); break;
                    default:
                        Log.Warn($"未知的 BANJI_PET_QA_STATE 项：{token}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"应用 QA 状态失败：{ex.Message}");
        }
    }

    private void InitializePosition()
    {
        var settings = Settings;
        _rightMargin = Math.Clamp(settings.PetRightMargin, 0, 4000);
        _bottomMargin = Math.Clamp(settings.PetBottomMargin, 0, 4000);
    }

    private void SubscribeServices()
    {
        var services = AppServices.Current;
        services.Clock.Tick += OnClockTick;
        services.Settings.Changed += OnSettingsChanged;
        services.Picker.Picked += OnPicked;
        services.Timers.Tick += OnTimerTick;
        services.Timers.StateChanged += OnTimerStateChanged;
        services.Notices.ToastRequested += OnToastRequested;
        services.Notices.ToastDismissed += OnToastDismissed;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    private void AnimateWindowIn()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void AnimateEdgeArcIn()
    {
        try
        {
            EdgeArcHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220))
            { From = 0, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        catch { /* ignore */ }
    }

    private void AnimatePetIn()
    {
        try
        {
            PetHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
            { From = 0, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        catch { /* ignore */ }
    }

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
                Log.Info($"宠物窗预览模式：固定时间 {parsed:yyyy-MM-dd HH:mm:ss}");
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
    // 专注模式（只负责隐藏宠物；便签由课表窗口负责）
    // =========================================================================

    private void EvaluateClassFocus()
    {
        try
        {
            var settings = Settings;
            var focus = ClassFocusMode.IsInClassFocus(_snapshot);
            var hidePet = focus && settings.AutoHidePetDuringClass;
            if (focus == _focusWindowActive && hidePet == _focusHidesPet) return;

            _focusWindowActive = focus;
            _focusHidesPet = hidePet;
            ApplyLayout();
            if (hidePet && _functionsExpanded) CollapseFunctions();
            Log.Info(focus
                ? $"进入上课专注窗口（隐藏宠物={hidePet}）"
                : "退出上课专注窗口，恢复宠物");
        }
        catch (Exception ex)
        {
            Log.Error("更新上课专注模式失败", ex);
        }
    }

    // =========================================================================
    // 布局：只有宠物群 / 功能区 / 侧边把手
    // =========================================================================

    private void ApplyLayout()
    {
        if (!_initialized) return;
        _applyingLayout = true;
        try
        {
            var settings = Settings;
            var petScale = Math.Clamp(settings.PetScale, 0.2, 5.0);
            PetScaleTransform.ScaleX = petScale;
            PetScaleTransform.ScaleY = petScale;

            var manualHidden = _petCollapsedToEdge || settings.HidePet;
            var handleOnly = manualHidden && !_focusHidesPet;
            var petHidden = manualHidden || _focusHidesPet;

            var petClusterDesignW = _functionsExpanded ? 282 : 112;
            PetHost.Width = petClusterDesignW;
            PetHost.Height = 134;
            var petBoxW = petClusterDesignW * petScale;
            var petBoxH = 134 * petScale;

            PetHost.Visibility = petHidden ? Visibility.Collapsed : Visibility.Visible;
            EdgeArcHost.Visibility = handleOnly ? Visibility.Visible : Visibility.Collapsed;
            FunctionHost.Visibility = (!petHidden && _functionsExpanded) ? Visibility.Visible : Visibility.Collapsed;

            double windowW;
            double windowH;
            if (handleOnly)
            {
                // v1.6：课表已独立，隐藏宠物时窗口缩到只有蓝把手，右缘精确贴工作区右缘
                EdgeArcHost.Width = 26;
                EdgeArcHost.Height = 130;
                Canvas.SetLeft(EdgeArcHost, 0);
                Canvas.SetTop(EdgeArcHost, 0);
                windowW = 26;
                windowH = 130;
                _petRightInWindow = windowW;
                _petBottomInWindow = windowH;
            }
            else if (_focusHidesPet)
            {
                // 专注隐藏：连把手都不留 → 1x1 隐藏窗口，不挡任何东西
                windowW = 1;
                windowH = 1;
                _petRightInWindow = 0;
                _petBottomInWindow = 0;
                Canvas.SetLeft(EdgeArcHost, 0);
                Canvas.SetTop(EdgeArcHost, 0);
            }
            else
            {
                const double margin = 24;
                windowW = petBoxW + margin * 2;
                windowH = petBoxH + margin * 2;
                _petRightInWindow = margin + petBoxW;
                _petBottomInWindow = margin + petBoxH;
                Canvas.SetLeft(PetHost, margin);
                Canvas.SetTop(PetHost, margin);
            }

            if (Math.Abs(Width - windowW) > 0.5) Width = windowW;
            if (Math.Abs(Height - windowH) > 0.5) Height = windowH;

            var work = SystemParameters.WorkArea;
            if (handleOnly)
            {
                var left = EdgeSnapMath.SnapLeftToWorkRight(work.Right, windowW);
                var desiredTop = _collapsedCenterY > 0
                    ? _collapsedCenterY - windowH * 0.5
                    : work.Bottom - _bottomMargin - windowH;
                Top = EdgeSnapMath.ClampTop(desiredTop, windowH, work.Top, work.Bottom);
                Left = left;
            }
            else if (_focusHidesPet)
            {
                Width = 1;
                Height = 1;
                Left = Math.Round(work.Right - 1);
                Top = Math.Round(work.Bottom - 1);
            }
            else
            {
                var desiredLeft = (work.Right - _rightMargin) - _petRightInWindow;
                var desiredTop = (work.Bottom - _bottomMargin) - _petBottomInWindow;
                var left = windowW <= work.Width
                    ? Math.Clamp(desiredLeft, work.Left, work.Right - windowW)
                    : work.Right - windowW;
                var top = windowH <= work.Height
                    ? Math.Clamp(desiredTop, work.Top, work.Bottom - windowH)
                    : work.Bottom - windowH;
                Left = Math.Round(left);
                Top = Math.Round(top);
            }
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
        _noticeBubble.RepositionToPet();
    }

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

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_applyingLayout || !_initialized) return;
        _bubble.RepositionAndRefresh();
        _noticeBubble.RepositionToPet();
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("WorkArea" or "PrimaryScreenWidth" or "PrimaryScreenHeight")) return;
        try { ApplyLayout(); }
        catch (Exception ex) { Log.Error("响应屏幕尺寸变化失败", ex); }
    }

    private void PersistPosition()
    {
        try
        {
            var work = SystemParameters.WorkArea;
            _rightMargin = Math.Clamp(Math.Round(work.Right - (Left + _petRightInWindow)), 0, 4000);
            _bottomMargin = Math.Clamp(Math.Round(work.Bottom - (Top + _petBottomInWindow)), 0, 4000);
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

    /// <summary>
    /// v1.7.0：托盘菜单/屏幕比例变化——显示宠物，并把宠物与课表窗口都拉回工作区。
    /// </summary>
    public void BringWindowsBackToScreen()
    {
        try
        {
            _rightMargin = 24;
            _bottomMargin = 24;
            SettingsService.Update(s =>
            {
                s.HidePet = false;
                s.ShowTimetablePanel = true;
                s.PetRightMargin = _rightMargin;
                s.PetBottomMargin = _bottomMargin;
            });
            ApplyLayout();
            _timetablePanel?.BringBackToScreen();
            if (IsVisible) Activate();
            Log.Info("托盘/屏幕变化：宠物与课表已拉回屏幕");
        }
        catch (Exception ex)
        {
            Log.Error("把宠物/课表拉回屏幕失败", ex);
        }
    }

    // =========================================================================
    // 宠物交互
    // =========================================================================

    private void OnPetMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) return;
        CloseMenuIfOpen(false);
        _timetablePanel?.EnsureBelowPet();
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
        if (moved) PersistPosition();
        else OnPetTouched();
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

    private void OnPetTouched() => TestPat();

    private void OnPetRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        TouchPetActivity();
        OpenMenuNearPet();
    }

    private void OnFunctionRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        TouchPetActivity();
        OpenMenuNearPet();
    }

    private void OpenMenuNearPet()
    {
        try
        {
            if (_openMenu is { IsVisible: true })
            {
                CloseMenuIfOpen(true);
                return;
            }
            // v1.7.0：右键菜单锚定鼠标当前屏幕 DIP 坐标（不再固定在宠物头顶）
            var cursor = GetCursorDip();
            var flyout = AppServices.Current.ShowMenuFlyout();
            _openMenu = flyout;
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_openMenu, flyout)) _openMenu = null;
            };
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
            Log.Error("打开右键菜单失败", ex);
        }
    }

    private void OnEdgeArcLeftClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ExpandFromEdge();
    }

    private void OnEdgeArcRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        TouchPetActivity();
        OpenMenuNearPet();
    }

    private void OnTimetableTriangleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            TouchPetActivity();
            _timetablePanel?.ToggleFold();
            UpdateTriangleStates();
            if (!Settings.ShowTimetablePanel)
            {
                SettingsService.Update(s => s.ShowTimetablePanel = true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换课表折叠失败", ex);
        }
    }

    private void OnMenuTriangleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var flyout = AppServices.Current.ShowMenuFlyout();
            _openMenu = flyout;
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

    private void OnFunctionTriangleClick(object sender, RoutedEventArgs e) => ToggleFunctions();

    private void ExpandFunctions()
    {
        try
        {
            _functionsExpanded = true;
            FunctionHost.BeginAnimation(OpacityProperty, null);
            FunctionHost.Visibility = Visibility.Visible;
            FunctionHost.Opacity = 0;
            FunctionHostOffset.X = 14;
            ApplyLayout();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    FunctionsBarControl.InvalidateMeasure();
                    FunctionsBarControl.UpdateLayout();
                }
                catch (Exception ex) { Log.Warn($"功能区首次布局失败：{ex.Message}"); }
            }), DispatcherPriority.Loaded);
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
            _functionsExpanded = false;
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
        var folded = _timetablePanel?.IsFolded == true;
        TimetableTriangleRotation.Angle = folded ? 180 : 0;
    }

    // =========================================================================
    // 服务事件
    // =========================================================================

    private void OnClockTick(object? sender, ScheduleSnapshot snapshot)
    {
        try
        {
            _snapshot = _previewTime is not null ? GetLiveSnapshot() : (snapshot ?? ScheduleSnapshot.Empty(DateTime.Now));
            EvaluateClassFocus();
        }
        catch (Exception ex)
        {
            Log.Error("刷新宠物信息失败", ex);
        }
    }

    private void OnPicked(object? sender, PickResult result)
    {
        try { _bubble.ShowPick(result); }
        catch (Exception ex) { Log.Error("显示抽签气泡失败", ex); }
    }

    private void OnTimerTick(object? sender, EventArgs e) => RefreshTimerBubble();
    private void OnTimerStateChanged(object? sender, EventArgs e) => RefreshTimerBubble();

    private void RefreshTimerBubble()
    {
        try { _bubble.UpdateTimer(AppServices.Current.Timers.Current); }
        catch (Exception ex) { Log.Error("刷新计时气泡失败", ex); }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            var settings = Settings;
            if (Topmost != settings.AlwaysOnTop) Topmost = settings.AlwaysOnTop;

            if (settings.HidePet != _lastHidePet)
            {
                _lastHidePet = settings.HidePet;
                if (settings.HidePet)
                {
                    var petH = 134 * Math.Clamp(settings.PetScale, 0.2, 5.0);
                    _collapsedCenterY = SystemParameters.WorkArea.Bottom - _bottomMargin - petH * 0.5;
                    _idle.MarkPetCollapsed();
                    try { if (_bubble.IsShowing) _bubble.Hide(); } catch { /* ignore */ }
                }
                else if (!_petCollapsedToEdge)
                {
                    _idle.MarkPetExpanded(DateTime.Now);
                }
            }
            else if (!settings.HidePet && !_petCollapsedToEdge)
            {
                _idle.TouchPet(DateTime.Now);
            }

            Pet.ApplySettings(settings.Pet, settings.PetBlinkEnabled, settings.PetBobEnabled);
            UpdateTriangleStates();
            EvaluateClassFocus();
            ApplyLayout();
        }
        catch (Exception ex)
        {
            Log.Error("响应宠物设置变化失败", ex);
        }
    }

    private void KeepOnTop()
    {
        try
        {
            if (!Settings.AlwaysOnTop) return;
            if (_bubble.IsShowing) return;
            if (_openMenu is { IsVisible: true }) return;
            var hasOtherTopmost = Application.Current?.Windows
                .OfType<Window>()
                .Any(w => w != this && w.IsVisible && w.Topmost &&
                          w is not BubbleWindow && w is not MenuFlyout &&
                          w is not NoticeBubbleWindow && w is not TimetablePanelWindow) == true;
            if (hasOtherTopmost) return;
            NativeMethods.ReassertTopmost(new WindowInteropHelper(this).Handle);
            // 宠物窗置顶后重新把课表窗压回宠物下方，保证重叠时点击宠物优先
            _timetablePanel?.EnsureBelowPet();
        }
        catch
        {
            // 置顶失败不影响使用
        }
    }

    // =========================================================================
    // 气泡提醒 / 空闲收起
    // =========================================================================

    private void OnToastRequested(object? sender, NoticeToast toast)
    {
        try
        {
            _idle.TouchAll(DateTime.Now);
            _noticeBubble.ShowToast(toast);
        }
        catch (Exception ex)
        {
            Log.Error("显示提醒气泡失败", ex);
        }
    }

    private void OnToastDismissed(object? sender, EventArgs e)
    {
        try { _noticeBubble.Dismiss(); }
        catch (Exception ex) { Log.Warn($"关闭提醒气泡失败：{ex.Message}"); }
    }

    private void TouchPetActivity()
    {
        try { _idle.TouchPet(DateTime.Now); } catch { /* ignore */ }
    }

    private void EvaluateIdleAutoHide()
    {
        if (!_initialized) return;
        try
        {
            var settings = Settings;
            if (_openMenu is { IsVisible: true } || _bubble.IsShowing || _noticeBubble.IsShowing)
            {
                _idle.TouchAll(DateTime.Now);
                return;
            }
            // 宠物空闲收起；课表折叠由独立课表窗口自己计时（互不联动）
            var action = _idle.Evaluate(DateTime.Now, settings.PetIdleCollapseMinutes, 0);
            if (action == IdleHideAction.CollapsePet)
            {
                if (settings.HidePet || _petCollapsedToEdge) _idle.MarkPetCollapsed();
                else CollapseToEdge();
            }
        }
        catch (Exception ex)
        {
            Log.Error("空闲自动收起判定失败", ex);
        }
    }

    // =========================================================================
    // 清理 / 小工具
    // =========================================================================

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            var services = AppServices.Current;
            services.Clock.Tick -= OnClockTick;
            services.Settings.Changed -= OnSettingsChanged;
            services.Picker.Picked -= OnPicked;
            services.Timers.Tick -= OnTimerTick;
            services.Timers.StateChanged -= OnTimerStateChanged;
            services.Notices.ToastRequested -= OnToastRequested;
            services.Notices.ToastDismissed -= OnToastDismissed;
        }
        catch (Exception ex)
        {
            Log.Warn($"解除宠物窗服务订阅失败：{ex.Message}");
        }

        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _topmostTimer.Stop();
        _idleTimer.Stop();
        try { Pet.StopAnimations(); } catch { /* ignore */ }
        try { _bubble.CloseCleanup(); } catch { /* ignore */ }
        try { _noticeBubble.CloseCleanup(); } catch { /* ignore */ }
        try { CloseMenuIfOpen(false); } catch { /* ignore */ }
        try { _timetablePanel?.CloseCleanup(); } catch { /* ignore */ }
    }

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex, Colors.Black));
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



