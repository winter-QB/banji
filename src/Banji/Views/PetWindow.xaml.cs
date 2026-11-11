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
    // v1.5.3 上课专注态：深色玻璃材质 + 文字黑色投影（在纯白/纯黑/蓝底上都清晰）
    private static readonly Brush FocusGlassBrush = CreateFocusGlassBrush();
    private static readonly SolidColorBrush FocusGlassBorder = FrozenBrush("#4DFFFFFF");
    private static readonly DropShadowEffect FocusCardShadow = CreateFrozenShadow(Colors.Black, 16, 0, 0.26);
    private static readonly DropShadowEffect FocusTextShadow = CreateFrozenShadow(Colors.Black, 7, 0, 1.0);

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

    // v1.3.0：提醒气泡 / 右击菜单 / 隐藏弧线 / 空闲自动收起 / 课表折叠
    private readonly NoticeBubbleWindow _noticeBubble;
    private readonly IdleAutoHideLogic _idle;
    private readonly DispatcherTimer _idleTimer;
    private bool _petCollapsedToEdge;
    private bool _timetableFolded;
    private double _collapsedCenterY;
    private bool _lastHidePet;
    private string _foldedSignature = "";

    // v1.4.0：值周/值日 + 单双周面板
    private string _dutySignature = "";
    private DutyScheduleView? _dutySchedule;
    private bool _afterSchoolDutyActive;
    private string _weekHeaderText = "";
    private string _weekShortText = "";
    private string _dutySummaryText = "";
    private IReadOnlyList<DutyTaskLine> _topDutyLines = Array.Empty<DutyTaskLine>();

    // v1.5.0：白底卡片主题 / 课表拖动 / 上课专注模式
    private double _lastPanelDesignWidth = 160;
    private bool _panelWhiteCard = true;
    private DropShadowEffect? _panelCardShadow;
    private SolidColorBrush _panelStrong = FrozenBrush("#111827");
    private SolidColorBrush _panelNormal = FrozenBrush("#1F2329");
    private SolidColorBrush _panelMuted = FrozenBrush("#5B6472");
    private SolidColorBrush _panelFaint = FrozenBrush("#8A94A6");
    private SolidColorBrush _panelBrand = FrozenBrush("#317FFF");
    private SolidColorBrush _panelOverrun = FrozenBrush("#D92D20");
    private bool _panelDragCandidate;
    private bool _panelDragActive;
    private bool _suppressPanelClick;
    private Point _panelDragStartCursor;
    private double _panelDragBaseX;
    private double _panelDragBaseY;
    private double? _pendingPanelOffsetX;
    private double? _pendingPanelOffsetY;
    private bool _focusActive;

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

        PanelHost.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler(OnPanelRightButtonUp), true);
        PanelHost.MouseMove += (_, _) => TouchTimetableActivity();
        FunctionHost.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler(OnFunctionRightButtonUp), true);

        // v1.5.0：白底卡片阴影由代码持有，专注简化态可临时去掉
        _panelCardShadow = new DropShadowEffect
        {
            Color = Color.FromRgb(0x16, 0x30, 0x5C),
            BlurRadius = 18,
            ShadowDepth = 3,
            Direction = 270,
            Opacity = 0.20
        };
        PanelCard.Effect = _panelCardShadow;

        // v1.5.0：课表可拖动（4px 阈值区分点击）
        PanelHost.PreviewMouseLeftButtonDown += OnPanelPreviewLeftDown;
        PanelHost.PreviewMouseMove += OnPanelPreviewMove;
        PanelHost.PreviewMouseLeftButtonUp += OnPanelPreviewLeftUp;
        PanelHost.LostMouseCapture += OnPanelLostMouseCapture;

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

    /// <summary>v1.3.0：提醒气泡的锚点矩形（宠物收起/隐藏时用右侧弧线）。</summary>
    public Rect GetNoticeAnchorRect()
    {
        try
        {
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

    /// <summary>宠物是否处于隐藏/收起成弧线的状态。</summary>
    public bool IsPetHiddenOrCollapsed => _petCollapsedToEdge || Settings.HidePet;

    /// <summary>课表是否处于折叠态。</summary>
    public bool IsTimetableFolded => _timetableFolded;

    // =========================================================================
    // v1.3.0 QA 入口（Lead 的 --cmd collapse-pet/expand-pet/fold-timetable/unfold-timetable）
    // =========================================================================

    /// <summary>收进屏幕右侧，只留一道粗弧线（宠物+功能区+互动气泡隐藏）。</summary>
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
            Log.Info("宠物已自动收进屏幕侧边（弧线态）");
        }
        catch (Exception ex)
        {
            Log.Error("收起宠物失败", ex);
        }
    }

    /// <summary>从侧边弧线展开恢复宠物。</summary>
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

    /// <summary>课表进入折叠态（只显示下节课时间+科目；上课中带下课倒计时）。</summary>
    public void FoldTimetable()
    {
        try
        {
            if (_timetableFolded) return;
            _timetableFolded = true;
            _idle.MarkTimetableFolded();
            RefreshFoldedCard(force: true);
            ApplyLayout();
            Log.Info("课表已自动进入折叠态");
        }
        catch (Exception ex)
        {
            Log.Error("折叠课表失败", ex);
        }
    }

    /// <summary>展开课表（恢复完整课表+常驻信息）。</summary>
    public void UnfoldTimetable()
    {
        try
        {
            if (!_timetableFolded)
            {
                _idle.TouchTimetable(DateTime.Now);
                return;
            }
            _timetableFolded = false;
            _idle.MarkTimetableUnfolded(DateTime.Now);
            RefreshFoldedCard(force: true);
            ApplyLayout();
            Log.Info("课表已展开");
        }
        catch (Exception ex)
        {
            Log.Error("展开课表失败", ex);
        }
    }

    // =========================================================================
    // v1.3.0 空闲自动收起 / 课表自动折叠
    // =========================================================================

    private void TouchPetActivity()
    {
        try { _idle.TouchPet(DateTime.Now); } catch { /* ignore */ }
    }

    private void TouchTimetableActivity()
    {
        try { _idle.TouchTimetable(DateTime.Now); } catch { /* ignore */ }
    }

    private void TouchAllActivity()
    {
        try { _idle.TouchAll(DateTime.Now); } catch { /* ignore */ }
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

            var action = _idle.Evaluate(DateTime.Now, settings.PetIdleCollapseMinutes, settings.TimetableIdleFoldMinutes);
            switch (action)
            {
                case IdleHideAction.CollapsePet:
                    if (settings.HidePet || _petCollapsedToEdge) _idle.MarkPetCollapsed();
                    else CollapseToEdge();
                    break;
                case IdleHideAction.FoldTimetable:
                    if (!settings.ShowTimetablePanel) _idle.MarkTimetableFolded();
                    else FoldTimetable();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("空闲自动收起判定失败", ex);
        }
    }

    // =========================================================================
    // v1.3.0 右击菜单 / 弧线交互 / 折叠卡片交互
    // =========================================================================

    private void OnPetRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        TouchAllActivity();
        OpenMenuNearPet();
    }

    private void OnPanelRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        TouchAllActivity();
        OpenMenuNearPet();
    }

    private void OnFunctionRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        TouchAllActivity();
        OpenMenuNearPet();
    }

    /// <summary>右击宠物/功能区/课表：在宠物附近打开同一个 MenuFlyout。</summary>
    private void OpenMenuNearPet()
    {
        try
        {
            if (_openMenu is { IsVisible: true })
            {
                CloseMenuIfOpen(true);
                return;
            }
            var anchorRect = IsPetHiddenOrCollapsed ? GetNoticeAnchorRect() : GetPetBodyRect();
            var flyout = AppServices.Current.ShowMenuFlyout();
            _openMenu = flyout;
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_openMenu, flyout)) _openMenu = null;
            };
            flyout.PlaceNear(new Point(anchorRect.Left + anchorRect.Width * 0.5, anchorRect.Top));
            _idle.TouchAll(DateTime.Now);
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
        _idle.TouchAll(DateTime.Now);
    }

    private void OnEdgeArcRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        TouchAllActivity();
        OpenMenuNearPet();
    }

    private void OnFoldedCardClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_suppressPanelClick) return;
        UnfoldTimetable();
    }

    // =========================================================================
    // v1.3.0 提醒气泡（上课/下课/普通提醒统一走气泡；计时器超时仍全屏）
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
        try
        {
            _noticeBubble.Dismiss();
        }
        catch (Exception ex)
        {
            Log.Warn($"关闭提醒气泡失败：{ex.Message}");
        }
    }

    private void AnimateEdgeArcIn()
    {
        try
        {
            EdgeArcHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(220))
                {
                    From = 0,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        catch
        {
            // 动画失败不影响功能
        }
    }

    private void AnimatePetIn()
    {
        try
        {
            PetHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
                {
                    From = 0,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        catch
        {
            // 动画失败不影响功能
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
            UpdateTriangleStates();

            LoadPreviewTime();
            MigrateLegacyUiScale();
            UpdateLayout();
            InitializePosition();

            // v1.3.0：恢复隐藏/折叠状态并启动空闲自动收起计时
            _lastHidePet = settings.HidePet;
            if (settings.HidePet)
            {
                var petH = 134 * Math.Clamp(settings.PetScale, 0.2, 5.0);
                _collapsedCenterY = SystemParameters.WorkArea.Bottom - _bottomMargin - petH * 0.5;
                _idle.MarkPetCollapsed();
            }
            if (!settings.ShowTimetablePanel) _idle.MarkTimetableFolded();

            // v1.5.0：默认白底卡片主题
            ApplyPanelTheme(true);
            ApplyLayout();
            _snapshot = GetLiveSnapshot();
            RefreshFromSnapshot(force: true);
            UpdatePanelSizeIfNeeded();
            SubscribeServices();

            _idleTimer.Start();
            _topmostTimer.Start();
            ApplyQaStateFromEnvironment();
            EvaluateClassFocus();
            AnimateWindowIn();
            KeepOnTop();
        }
        catch (Exception ex)
        {
            Log.Error("初始化宠物窗口失败", ex);
        }
    }

    /// <summary>
    /// 开发/截图 QA：BANJI_PET_QA_STATE=collapsed|expanded|folded|unfolded（可逗号组合）
    /// 直接调用同名 public 方法切换状态，供隔离实例截图使用（正式运行不设置该环境变量即可）。
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
                    case "folded": FoldTimetable(); break;
                    case "unfolded": UnfoldTimetable(); break;
                    case "functions": ToggleFunctions(); break;
                    case "focus": EvaluateClassFocus(); break;
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
        services.Notices.ToastRequested += OnToastRequested;
        services.Notices.ToastDismissed += OnToastDismissed;
        services.Duty.Changed += OnDutyChanged;
        services.WeekParity.Changed += OnWeekParityChanged;
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
        _noticeBubble.RepositionToPet();
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

    // =========================================================================
    // v1.5.0：白底卡片主题 / 上课专注 / 功能区开关 / 课表拖动
    // =========================================================================

    /// <summary>切换面板主题：白底深字卡片（默认）或“专注简化态”的透明+白色光晕文字。</summary>
    private void ApplyPanelTheme(bool whiteCard)
    {
        _panelWhiteCard = whiteCard;
        if (whiteCard)
        {
            _panelStrong = FrozenBrush("#111827");
            _panelNormal = FrozenBrush("#1F2329");
            _panelMuted = FrozenBrush("#5B6472");
            _panelFaint = FrozenBrush("#8A94A6");
            _panelBrand = FrozenBrush("#317FFF");
            _panelOverrun = FrozenBrush("#D92D20");

            if (PanelCard is not null)
            {
                PanelCard.Background = FrozenBrush("#F5FFFFFF");
                PanelCard.BorderBrush = FrozenBrush("#14000000");
                PanelCard.BorderThickness = new Thickness(1);
                PanelCard.Effect = _panelCardShadow;
            }
            if (FoldedTopTask is not null) { FoldedTopTask.Foreground = FrozenBrush("#2F6BD8"); FoldedTopTask.Effect = null; }
            if (FoldedLabel is not null) { FoldedLabel.Foreground = FrozenBrush("#317FFF"); FoldedLabel.Effect = null; }
            if (FoldedRightText is not null) { FoldedRightText.Foreground = FrozenBrush("#A2ABBB"); FoldedRightText.Effect = null; }
            if (FoldedMain is not null) { FoldedMain.Foreground = FrozenBrush("#1F2329"); FoldedMain.Effect = null; }
            if (FoldedSecondary is not null) { FoldedSecondary.Foreground = FrozenBrush("#6B7280"); FoldedSecondary.Effect = null; }
            if (FoldedDuty is not null) { FoldedDuty.Foreground = FrozenBrush("#4A6FA5"); FoldedDuty.Effect = null; }
        }
        else
        {
            _panelStrong = WhiteBrush;
            _panelNormal = WhiteSoftBrush;
            _panelMuted = WhiteMutedBrush;
            _panelFaint = WhiteFaintBrush;
            _panelBrand = BrandLightBrush;
            _panelOverrun = WarningBrush;

            if (PanelCard is not null)
            {
                // v1.5.3：专注态用深色半透明玻璃底（纯白背景下白字也能读），而不是完全透明
                PanelCard.Background = FocusGlassBrush;
                PanelCard.BorderBrush = Brushes.Transparent;
                PanelCard.BorderThickness = new Thickness(0);
                PanelCard.Effect = FocusCardShadow;
            }
            if (FoldedTopTask is not null) { FoldedTopTask.Foreground = FrozenBrush("#BFD7FF"); FoldedTopTask.Effect = FocusTextShadow; }
            if (FoldedLabel is not null) { FoldedLabel.Foreground = FrozenBrush("#9CC3FF"); FoldedLabel.Effect = FocusTextShadow; }
            if (FoldedRightText is not null) { FoldedRightText.Foreground = FrozenBrush("#CCFFFFFF"); FoldedRightText.Effect = FocusTextShadow; }
            if (FoldedMain is not null) { FoldedMain.Foreground = WhiteBrush; FoldedMain.Effect = FocusTextShadow; }
            if (FoldedSecondary is not null) { FoldedSecondary.Foreground = WhiteMutedBrush; FoldedSecondary.Effect = FocusTextShadow; }
            if (FoldedDuty is not null) { FoldedDuty.Foreground = FrozenBrush("#9CC3FF"); FoldedDuty.Effect = FocusTextShadow; }
        }

        // 白底卡片上关闭文字光晕（深色字不需要黑色描边），透明专注态恢复光晕
        var haloEnabled = !whiteCard;
        foreach (var halo in GetAllPanelHaloControls())
        {
            if (halo is HaloTextLine line) line.HaloEnabled = haloEnabled;
            else if (halo is HaloTextStack stack) stack.HaloEnabled = haloEnabled;
        }
        _timetableSignature = "";
        _foldedSignature = "";
        _dutySignature = "";
    }

    private IEnumerable<FrameworkElement> GetAllPanelHaloControls()
    {
        yield return WeekHeaderLine;
        yield return TopTaskBody;
        yield return TimetableTitle;
        yield return TimetableBody;
        yield return TimetableEmpty;
        yield return DutyLine;
        yield return InfoBody;
    }

    /// <summary>根据当前时间/课表更新“上课专注模式”状态（每秒由 Clock.Tick 调用）。</summary>
    private void EvaluateClassFocus()
    {
        try
        {
            var settings = Settings;
            var enabled = settings.AutoHidePetDuringClass || settings.AutoSimplifyTimetableDuringClass;
            var focus = enabled && ClassFocusMode.IsInClassFocus(_snapshot);
            if (focus == _focusActive) return;

            _focusActive = focus;
            ApplyPanelTheme(!(focus && settings.AutoSimplifyTimetableDuringClass));
            ApplyLayout();
            RefreshFromSnapshot(force: true);
            UpdatePanelSizeIfNeeded();
            Log.Info(focus
                ? $"进入上课专注模式（隐藏宠物={settings.AutoHidePetDuringClass}，简化课表={settings.AutoSimplifyTimetableDuringClass}）"
                : "退出上课专注模式，恢复用户原状态");
        }
        catch (Exception ex)
        {
            Log.Error("更新上课专注模式失败", ex);
        }
    }

    /// <summary>当前是否处于上课专注窗口。</summary>
    public bool IsClassFocusActive => _focusActive;

    /// <summary>v1.5.0 QA：切换功能区展开/收起（Lead 的 --cmd 对接）。</summary>
    public void ToggleFunctions()
    {
        try
        {
            if (_focusActive || _petCollapsedToEdge || Settings.HidePet)
            {
                // 宠物可视元素隐藏时不允许展开（避免把功能区画在空窗口里）
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

    /// <summary>当前功能区是否展开（QA/测试用）。</summary>
    public bool IsFunctionsExpanded => _functionsExpanded;

    // ---------- 课表拖动 ----------

    private void OnPanelPreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_focusActive) return;
        _panelDragCandidate = true;
        _panelDragActive = false;
        _panelDragStartCursor = GetCursorDip();
        _panelDragBaseX = _pendingPanelOffsetX ?? Settings.PanelOffsetX;
        _panelDragBaseY = _pendingPanelOffsetY ?? Settings.PanelOffsetY;
        _suppressPanelClick = false;
    }

    private void OnPanelPreviewMove(object sender, MouseEventArgs e)
    {
        if (!_panelDragCandidate || e.LeftButton != MouseButtonState.Pressed)
        {
            if (_panelDragActive) EndPanelDrag(cancel: false);
            return;
        }
        try
        {
            var current = GetCursorDip();
            var dx = current.X - _panelDragStartCursor.X;
            var dy = current.Y - _panelDragStartCursor.Y;
            if (!_panelDragActive)
            {
                if (!PanelDragMath.ExceedsThreshold(dx, dy)) return;
                _panelDragActive = true;
                try { PanelHost.CaptureMouse(); } catch { /* ignore */ }
            }
            var (x, y) = PanelDragMath.ResolveOffsets(_panelDragBaseX, _panelDragBaseY, dx, dy);
            _pendingPanelOffsetX = x;
            _pendingPanelOffsetY = y;
            ApplyLayout();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"拖动课表失败：{ex.Message}");
            EndPanelDrag(cancel: true);
        }
    }

    private void OnPanelPreviewLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panelDragActive) { _panelDragCandidate = false; return; }
        _suppressPanelClick = true;
        e.Handled = true;
        EndPanelDrag(cancel: false);
        // 下一轮输入前解除点击抑制（避免拖动结束误触折叠卡/值日行点击）
        Dispatcher.BeginInvoke(new Action(() => _suppressPanelClick = false), DispatcherPriority.Input);
    }

    /// <summary>拖动过程中窗口会随面板移动，可能短暂丢鼠标捕获；拖动未结束就重新捕获，避免只拖到一半。</summary>
    private void OnPanelLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_panelDragActive)
        {
            _panelDragCandidate = false;
            return;
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_panelDragActive) return;
            try { PanelHost.CaptureMouse(); } catch { /* ignore */ }
        }), DispatcherPriority.Input);
    }

    private void EndPanelDrag(bool cancel)
    {
        var wasActive = _panelDragActive;
        _panelDragCandidate = false;
        _panelDragActive = false;
        try { PanelHost.ReleaseMouseCapture(); } catch { /* ignore */ }
        if (!wasActive) return;
        if (cancel)
        {
            _pendingPanelOffsetX = null;
            _pendingPanelOffsetY = null;
            ApplyLayout();
            return;
        }

        var x = Math.Round(_pendingPanelOffsetX ?? Settings.PanelOffsetX);
        var y = Math.Round(_pendingPanelOffsetY ?? Settings.PanelOffsetY);
        _pendingPanelOffsetX = null;
        _pendingPanelOffsetY = null;
        try
        {
            SettingsService.Update(s =>
            {
                s.PanelOffsetX = PanelDragMath.ClampOffset(x);
                s.PanelOffsetY = PanelDragMath.ClampOffset(y);
            });
            Log.Info($"课表位置已更新：X={x:0}, Y={y:0}");
        }
        catch (Exception ex)
        {
            Log.Error("保存课表位置失败", ex);
        }
        ApplyLayout();
    }
    /// <summary>
    /// v1.5.0 布局：宠物/课表可独立隐藏；课表为白底圆角卡片（专注简化态除外）。
    /// 隐藏宠物时窗口右缘精确贴工作区右缘（把手不留缝）；只剩把手时窗口缩到只有弧宽。
    /// 课表拖动使用 _pendingPanelOffsetX/Y 临时偏移，松手后持久化到 PanelOffsetX/Y。
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

            var focusSimplify = _focusActive && settings.AutoSimplifyTimetableDuringClass;
            var focusHidePet = _focusActive && settings.AutoHidePetDuringClass;
            var panelVisible = settings.ShowTimetablePanel;
            var petHidden = _petCollapsedToEdge || settings.HidePet || focusHidePet;
            var showFoldedCard = (_timetableFolded || focusSimplify) && panelVisible;

            // 可见性
            PetHost.Visibility = petHidden ? Visibility.Collapsed : Visibility.Visible;
            PanelHost.Visibility = panelVisible ? Visibility.Visible : Visibility.Collapsed;
            FoldedCard.Visibility = showFoldedCard ? Visibility.Visible : Visibility.Collapsed;
            PanelStack.Visibility = showFoldedCard ? Visibility.Collapsed : Visibility.Visible;
            TimetableSection.Visibility = panelVisible && !showFoldedCard
                ? Visibility.Visible
                : Visibility.Collapsed;
            FunctionHost.Visibility = (!petHidden && !_focusActive && _functionsExpanded)
                ? Visibility.Visible
                : Visibility.Collapsed;
            PanelHost.Opacity = focusSimplify ? 0.92 : 1.0;

            // 面板尺寸（卡片内边距/阴影不参与布局，直接量卡片）
            double panelDesignW = 0;
            double panelDesignH = 0;
            if (panelVisible)
            {
                PanelCard.Measure(new Size(320, double.PositiveInfinity));
                panelDesignW = Math.Max(120, PanelCard.DesiredSize.Width);
                panelDesignH = Math.Max(40, PanelCard.DesiredSize.Height);
                _lastPanelDesignWidth = panelDesignW;
                _lastPanelDesignHeight = panelDesignH;
                PanelHost.Width = Math.Ceiling(panelDesignW);
            }
            var panelW = panelDesignW * panelScale;
            var panelH = panelDesignH * panelScale;

            // 宠物区尺寸（隐藏时是弧线把手）
            double petBoxW;
            double petBoxH;
            if (petHidden)
            {
                // 专注模式连把手都不留，也不必给宠物区留空位（窗口只剩课表卡片）
                petBoxW = _focusActive ? 0 : 26;
                petBoxH = _focusActive ? 0 : 130;
            }
            else
            {
                var petClusterDesignW = _functionsExpanded ? 282 : 112;
                PetHost.Width = petClusterDesignW;
                PetHost.Height = 134;
                petBoxW = petClusterDesignW * petScale;
                petBoxH = 134 * petScale;
            }

            var handleOnly = petHidden && !panelVisible;
            double windowW;
            double windowH;
            double petRightInWindow;
            double petBottomInWindow;
            var work = SystemParameters.WorkArea;

            if (handleOnly)
            {
                if (_focusActive)
                {
                    // 专注模式连把手都不留：1x1 隐藏窗口（不挡任何东西）
                    EdgeArcHost.Visibility = Visibility.Collapsed;
                    windowW = 1;
                    windowH = 1;
                    petRightInWindow = 0;
                    petBottomInWindow = 0;
                }
                else
                {
                    // 只剩蓝色把手：窗口缩到弧宽，右缘精确贴工作区右缘
                    EdgeArcHost.Visibility = Visibility.Visible;
                    EdgeArcHost.Width = petBoxW;
                    EdgeArcHost.Height = petBoxH;
                    Canvas.SetLeft(EdgeArcHost, 0);
                    Canvas.SetTop(EdgeArcHost, 0);
                    windowW = petBoxW;
                    windowH = petBoxH;
                    petRightInWindow = windowW;
                    petBottomInWindow = windowH;
                }
            }
            else
            {
                // 专注模式连把手都不留
                EdgeArcHost.Visibility = (petHidden && !_focusActive) ? Visibility.Visible : Visibility.Collapsed;

                var offsetX = _pendingPanelOffsetX ?? settings.PanelOffsetX;
                var offsetY = _pendingPanelOffsetY ?? settings.PanelOffsetY;
                const double gap = 8;
                var panelRight = offsetX;
                var panelBottom = -petBoxH - gap + offsetY;
                var panelLeft = panelRight - panelW;
                var panelTop = panelBottom - panelH;

                double minX;
                double maxX;
                double minY;
                double maxY;
                if (panelVisible && !petHidden)
                {
                    minX = Math.Min(-petBoxW, panelLeft);
                    maxX = Math.Max(0, panelRight);
                    minY = Math.Min(-petBoxH, panelTop);
                    maxY = 0;
                }
                else if (panelVisible)
                {
                    minX = Math.Min(-petBoxW, panelLeft);
                    maxX = Math.Max(0, panelRight);
                    minY = Math.Min(-petBoxH, panelTop);
                    maxY = 0;
                }
                else
                {
                    minX = -petBoxW;
                    maxX = 0;
                    minY = -petBoxH;
                    maxY = 0;
                }

                const double leftPad = 24;
                // 隐藏宠物时右缘不留缝，让蓝色把手精确贴屏幕右缘
                var rightPad = petHidden ? 0 : 24;
                windowW = Math.Max(96, (maxX - minX) + leftPad + rightPad);
                windowH = Math.Max(120, (maxY - minY) + leftPad + rightPad);
                petRightInWindow = leftPad + (0 - minX);
                petBottomInWindow = leftPad + (0 - minY);

                if (petHidden)
                {
                    EdgeArcHost.Width = petBoxW;
                    EdgeArcHost.Height = petBoxH;
                    Canvas.SetLeft(EdgeArcHost, Math.Round(petRightInWindow - petBoxW));
                    Canvas.SetTop(EdgeArcHost, Math.Round(petBottomInWindow - petBoxH));
                }
                else
                {
                    Canvas.SetLeft(PetHost, Math.Round(petRightInWindow - petBoxW));
                    Canvas.SetTop(PetHost, Math.Round(petBottomInWindow - petBoxH));
                }
                if (panelVisible)
                {
                    Canvas.SetLeft(PanelHost, Math.Round(leftPad + (panelLeft - minX)));
                    Canvas.SetTop(PanelHost, Math.Round(leftPad + (panelTop - minY)));
                }
            }

            _petRightInWindow = petRightInWindow;
            _petBottomInWindow = petBottomInWindow;

            if (Math.Abs(Width - windowW) > 0.5) Width = windowW;
            if (Math.Abs(Height - windowH) > 0.5) Height = windowH;

            if (handleOnly && !_focusActive)
            {
                // 把手：Left = 工作区右缘 − 弧宽（DIP 精确、Round），垂直夹在工作区内
                var left = EdgeSnapMath.SnapLeftToWorkRight(work.Right, windowW);
                var desiredTop = _collapsedCenterY > 0
                    ? _collapsedCenterY - windowH * 0.5
                    : work.Bottom - _bottomMargin - windowH;
                Top = EdgeSnapMath.ClampTop(desiredTop, windowH, work.Top, work.Bottom);
                Left = left;
            }
            else
            {
                var rightMargin = petHidden ? 0 : _rightMargin;
                var desiredLeft = (work.Right - rightMargin) - petRightInWindow;
                double desiredTop;
                if (petHidden && _collapsedCenterY > 0)
                {
                    desiredTop = _collapsedCenterY - (petBottomInWindow - petBoxH * 0.5);
                }
                else
                {
                    desiredTop = (work.Bottom - _bottomMargin) - petBottomInWindow;
                }
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

    /// <summary>面板内容尺寸变化（展开/折叠、D-Day 行出现、切换折叠卡片等）时重新计算窗口尺寸，避免裁切。</summary>
    private void UpdatePanelSizeIfNeeded()
    {
        if (!_initialized) return;
        try
        {
            if (!Settings.ShowTimetablePanel) return;
            PanelCard.Measure(new Size(320, double.PositiveInfinity));
            var width = PanelCard.DesiredSize.Width;
            var height = PanelCard.DesiredSize.Height;
            if (Math.Abs(width - _lastPanelDesignWidth) > 0.5 || Math.Abs(height - _lastPanelDesignHeight) > 0.5)
                ApplyLayout();
        }
        catch (Exception ex)
        {
            Log.Warn($"更新课表面板尺寸失败：{ex.Message}");
        }
    }
    /// <summary>
    /// 刷新折叠态的“下节卡片”文案（上课中带下课倒计时）。
    /// v1.4.0：顶部压一行黑板类岗位、右侧显示单双周短文本、底部显示值周/值日行（可点击）。
    /// </summary>
    private void RefreshFoldedCard(bool force)
    {
        try
        {
            var now = _previewTime ?? DateTime.Now;
            var info = FoldedTimetableFormatter.Fold(_snapshot, now);

            var mode = _dutySchedule?.Mode ?? DutyMode.Weekly;
            // 折叠态空间有限：只压最靠前的一条黑板岗位，多于一条时提示“等 N 项”
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

            var dutyLink = _afterSchoolDutyActive
                ? "查看值日详情 ›"
                : _dutySummaryText;
            var weekRight = _weekShortText.Length > 0 ? _weekShortText : "点击展开";

            var signature = string.Join("|",
                label, main, secondary ?? "", topText, dutyLink, weekRight, _dutySchedule is not null);
            if (!force && signature == _foldedSignature) return;
            _foldedSignature = signature;

            FoldedTopTask.Text = topText;
            FoldedTopTask.Visibility = topText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            FoldedLabel.Text = label;
            FoldedRightText.Text = weekRight;
            FoldedMain.Text = main;
            FoldedSecondary.Text = secondary ?? "";
            FoldedSecondary.Visibility = string.IsNullOrEmpty(secondary)
                ? Visibility.Collapsed
                : Visibility.Visible;
            FoldedDuty.Text = dutyLink.Length > 0 && !_afterSchoolDutyActive ? dutyLink + " ›" : dutyLink;
            FoldedDuty.Visibility = dutyLink.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新课表折叠态失败：{ex.Message}");
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

    private void OnFunctionTriangleClick(object sender, RoutedEventArgs e) => ToggleFunctions();

    /// <summary>展开/收起同一个入口；会先同步 _functionsExpanded 状态再布局（修复点击后功能区不出现）。</summary>
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
            // 功能区首次展开时（之前为 Collapsed）需要一次布局，内部按钮才会按实际尺寸排布
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
            EvaluateClassFocus();
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

            // v1.5.0：白底主题随专注简化态切换；专注开关变化立即重新评估
            ApplyPanelTheme(!(_focusActive && settings.AutoSimplifyTimetableDuringClass));
            EvaluateClassFocus();

            // v1.3.0：隐藏宠物 / 隐藏课表 / 空闲分钟数变化时同步空闲状态机
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

            if (!settings.ShowTimetablePanel) _idle.MarkTimetableFolded();
            else if (!_timetableFolded) _idle.TouchTimetable(DateTime.Now);

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
            settings.ShowWeekNumberOnPet,
            settings.ShowDutyOnPet,
            settings.AutoHidePetDuringClass,
            settings.AutoSimplifyTimetableDuringClass,
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
                          w is not BubbleWindow && w is not MenuFlyout && w is not NoticeBubbleWindow) == true;
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
        RefreshDutyAndParity(force);
        RefreshInfo(_snapshot, overrun);
        RefreshTimetable(_snapshot, force);
        RefreshFoldedCard(force);
        UpdateTriangleStates();
        UpdatePanelSizeIfNeeded();
    }

    // =========================================================================
    // v1.4.0：值周/值日 + 单双周 面板
    // =========================================================================

    /// <summary>读取单双周与值日服务，刷新“周次标题 / 黑板置顶 / 值周行”，并决定放学后是否替换课表。</summary>
    private void RefreshDutyAndParity(bool force)
    {
        try
        {
            var settings = Settings;
            var snapshot = _snapshot;
            var date = DateOnly.FromDateTime(snapshot.Now);

            // 单双周标题（受 ShowWeekNumberOnPet + EnableWeekParity 控制）
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

            // 值日/值周（未配置时安静地不显示，避免宠物面板出现“未配置”噪音）
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
                _weekHeaderText,
                _weekShortText,
                _afterSchoolDutyActive,
                _dutySummaryText,
                string.Join(";", _topDutyLines.Select(l => l.Text)));
            if (!force && signature == _dutySignature) return;
            _dutySignature = signature;
            _timetableSignature = "";
            _foldedSignature = "";

            // 面板顶部周次标题
            if (_weekHeaderText.Length > 0)
            {
                WeekHeaderLine.SetText(_weekHeaderText, _fontLight, 9.5, _panelFaint);
                WeekHeaderLine.Visibility = Visibility.Visible;
            }
            else
            {
                WeekHeaderLine.Visibility = Visibility.Collapsed;
            }

            // 黑板类岗位置顶行
            if (_topDutyLines.Count > 0)
            {
                var lines = _topDutyLines
                    .Select(l => (IReadOnlyList<HaloSegment>)new List<HaloSegment>
                    {
                        new(l.TaskName, _fontMedium, 10.5, _panelBrand),
                        new("：", _fontLight, 10.5, _panelFaint),
                        new(l.MembersText, _fontLight, 10.5, _panelNormal)
                    })
                    .ToList();
                TopTaskBody.SetLines(lines);
                TopTaskBody.Visibility = Visibility.Visible;
            }
            else
            {
                TopTaskBody.Visibility = Visibility.Collapsed;
            }

            // 值周/值日行（点击打开“值日与轮值”）
            if (_dutySummaryText.Length > 0)
            {
                DutyLine.SetText(_dutySummaryText, _fontLight, 10, _panelBrand);
                DutyLineHost.Visibility = Visibility.Visible;
            }
            else
            {
                DutyLineHost.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Error("刷新值日/单双周面板失败", ex);
        }
    }

    private void OnDutyChanged(object? sender, EventArgs e) => DispatchDutyRefresh();

    private void OnWeekParityChanged(object? sender, EventArgs e) => DispatchDutyRefresh();

    private void DispatchDutyRefresh()
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshAfterDutyChange));
                return;
            }
            RefreshAfterDutyChange();
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新值日/单双周失败：{ex.Message}");
        }
    }

    private void RefreshAfterDutyChange()
    {
        try
        {
            _timetableSignature = "";
            _foldedSignature = "";
            _dutySignature = "";
            _snapshot = GetLiveSnapshot();
            RefreshFromSnapshot(force: true);
            UpdatePanelSizeIfNeeded();
        }
        catch (Exception ex)
        {
            Log.Error("响应值日/单双周变化失败", ex);
        }
    }

    private void OnDutyLineClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_suppressPanelClick) return;
        OpenDutyWindow();
    }

    private void OnFoldedDutyClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_suppressPanelClick) return;
        OpenDutyWindow();
    }

    private void OpenDutyWindow()
    {
        try
        {
            TouchAllActivity();
            AppServices.Current.ShowDutyWindow();
        }
        catch (Exception ex)
        {
            Log.Error("打开值日与轮值窗口失败", ex);
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

        // 行 1：距下课 / 距上课 / 已放学 / 下课超时
        var row1 = new List<HaloSegment>();
        if (!string.IsNullOrEmpty(primary.Label))
        {
            var labelBrush = primary.IsOverrun ? _panelOverrun : _panelMuted;
            row1.Add(new HaloSegment(primary.Label + " ", _fontLight, 10.5, labelBrush));
        }
        if (!string.IsNullOrEmpty(primary.Value))
        {
            var valueBrush = primary.IsOverrun ? _panelOverrun : _panelStrong;
            var size = string.IsNullOrEmpty(primary.Label) ? 14.5 : 16.5;
            row1.Add(new HaloSegment(primary.Value, _fontMedium, size, valueBrush));
        }
        else
        {
            row1.Add(new HaloSegment("—", _fontMedium, 14, _panelMuted));
        }
        lines.Add(row1);

        // 行 2：距放学 h:mm:ss + 剩余节数
        var row2 = new List<HaloSegment>();
        if (!string.IsNullOrEmpty(school.Label))
        {
            row2.Add(new HaloSegment(school.Label + " ", _fontLight, 9.5, _panelMuted));
            row2.Add(new HaloSegment(school.Value, _fontMedium, 11.5, _panelNormal));
            if (!string.IsNullOrEmpty(school.Suffix))
                row2.Add(new HaloSegment(school.Suffix, _fontLight, 9.5, _panelMuted));
        }
        else if (!string.IsNullOrEmpty(school.Value))
        {
            row2.Add(new HaloSegment(school.Value, _fontLight, 10.5, _panelMuted));
        }
        lines.Add(row2);

        // 行 3：现在 / 下节课程名
        var row3 = new List<HaloSegment>();
        if (!string.IsNullOrEmpty(course.Value1) || !string.IsNullOrEmpty(course.Value2))
        {
            if (!string.IsNullOrEmpty(course.Prefix1) && !string.IsNullOrEmpty(course.Value1))
            {
                row3.Add(new HaloSegment(course.Prefix1 + " ", _fontLight, 9.5, _panelMuted));
                row3.Add(new HaloSegment(course.Value1, _fontMedium, 11.5, _panelStrong));
            }
            else if (!string.IsNullOrEmpty(course.Value1))
            {
                row3.Add(new HaloSegment(course.Value1, _fontLight, 10.5, _panelMuted));
            }
            if (!string.IsNullOrEmpty(course.Prefix2) && !string.IsNullOrEmpty(course.Value2))
            {
                row3.Add(new HaloSegment(" " + course.Prefix2.TrimStart() + " ", _fontLight, 9.5, _panelMuted));
                row3.Add(new HaloSegment(course.Value2, _fontMedium, 11.5, _panelBrand));
            }
        }
        else
        {
            row3.Add(new HaloSegment("—", _fontLight, 10.5, _panelMuted));
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
                    new HaloSegment($"距{dday.Name} ", _fontLight, 10.5, _panelNormal),
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
        var afterSchoolLines = _afterSchoolDutyActive && _dutySchedule is not null
            ? DutyPanelFormatter.AfterSchoolLines(_dutySchedule.Mode, _dutySchedule)
            : Array.Empty<DutyTaskLine>();
        var signature = string.Join("|",
            expanded,
            snapshot.Phase,
            today.Count,
            highlighted?.PeriodIndex,
            _afterSchoolDutyActive,
            string.Join(";", afterSchoolLines.Select(l => l.Text)),
            string.Join(";", today.Select(c => $"{c.PeriodIndex}:{c.Course}:{(int)c.Status}")));
        if (!force && signature == _timetableSignature) return;
        _timetableSignature = signature;

        // v1.4.0：放学后 N 分钟内，用轮值信息替换课表内容（之后恢复“已放学/今天的课已上完”）
        if (_afterSchoolDutyActive && _dutySchedule is not null)
        {
            TimetableTitle.SetText("放学后", _fontLight, 10.5, _panelMuted);
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

        TimetableTitle.SetText(PetUiFormatter.TimetableTitle(_previewTime ?? DateTime.Now),
            _fontLight, 10.5, _panelMuted);

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

    /// <summary>把值日任务行渲染成课表样式的悬浮文字行（任务名 + 成员）。</summary>
    private List<IReadOnlyList<HaloSegment>> BuildDutyBodyLines(IReadOnlyList<DutyTaskLine> lines)
    {
        var result = new List<IReadOnlyList<HaloSegment>>(lines.Count);
        foreach (var line in lines)
        {
            result.Add(new List<HaloSegment>
            {
                new(line.TaskName, _fontMedium, 11.5, _panelNormal),
                new("：", _fontLight, 10.5, _panelFaint),
                new(line.MembersText, _fontLight, 10.5, _panelMuted)
            });
        }
        return result;
    }

    private void SetTimetableHint(string text)
    {
        TimetableEmpty.SetText(text, _fontLight, 10.5, _panelMuted);
        TimetableEmpty.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 课表一行：用全角空格做列对齐（节次 / 课程名 / 开始时间），纯文字无卡片背景；
    /// 强调行保留 #FF3030 红色（第三轮仍保留强调色）。
    /// </summary>
    private List<HaloSegment> BuildTimetableLine(CourseOccurrence course, bool isHighlight, int maxCourseChars)
    {
        var isPast = course.Status == CourseStatus.Past;
        var indexBrush = isHighlight ? _panelOverrun : _panelFaint;
        var courseBrush = isHighlight ? _accentBrush : (isPast ? _panelMuted : _panelNormal);

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
                isHighlight ? _panelOverrun : _panelFaint)
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
            services.Notices.ToastRequested -= OnToastRequested;
            services.Notices.ToastDismissed -= OnToastDismissed;
            services.Duty.Changed -= OnDutyChanged;
            services.WeekParity.Changed -= OnWeekParityChanged;
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
    }

    // =========================================================================
    // 小工具
    // =========================================================================


    private static Brush CreateFocusGlassBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 0, 0, 0), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x08, 0, 0, 0), 1.0));
        brush.Freeze();
        return brush;
    }

    private static DropShadowEffect CreateFrozenShadow(Color color, double blur, double depth, double opacity)
    {
        var effect = new DropShadowEffect { Color = color, BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity };
        effect.Freeze();
        return effect;
    }

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








































