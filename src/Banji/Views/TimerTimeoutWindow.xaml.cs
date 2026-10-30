using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Banji.Core.Boot;
using Banji.Core.Infrastructure;

namespace Banji.Views;

/// <summary>
/// 计时器超时全屏强提醒（开发文档第 4 章）：
/// 截取主屏 → 高斯模糊背景 + 居中“计时器超时” + 下方“关闭”按钮，15 秒自动关闭。
/// 置顶、不抢焦点；关闭（含自动关闭）时调用 <see cref="Banji.Core.Models.TimerSnapshot"/> 对应服务的
/// <c>AcknowledgeExpiry()</c> 复位计时器（幂等，core-schedule 的 NotificationService 可直接集成本窗口）。
/// </summary>
public partial class TimerTimeoutWindow : Window
{
    private const int AutoCloseSeconds = 15;

    private DispatcherTimer? _autoCloseTimer;
    private int _remaining = AutoCloseSeconds;
    private bool _closed;

    public TimerTimeoutWindow()
    {
        InitializeComponent();
        ApplyStyleFromSettings();
        Loaded += OnLoaded;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
    }

    // =========================================================================
    // 样式（超时字号 / 遮罩不透明度来自设置）
    // =========================================================================

    private void ApplyStyleFromSettings()
    {
        var fontSize = 96.0;
        var coverOpacity = 0.45;
        try
        {
            if (AppServices.IsInitialized)
            {
                var notice = AppServices.Current.Settings.Current.Notice;
                fontSize = Math.Clamp(notice.TimerTimeoutFontSize, 40, 300);
                coverOpacity = Math.Clamp(notice.BlurBackdropOpacityPercent, 0, 100) / 100.0;
            }
        }
        catch
        {
            // 使用默认值
        }

        TitleText.FontSize = fontSize;
        DimLayer.Opacity = coverOpacity;
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnPrimaryScreen();
        CaptureBlurredBackdrop();

        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            NativeMethods.SetToolWindow(hwnd, true); // 不进 Alt-Tab
            NativeMethods.ReassertTopmost(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("计时器超时窗口置顶失败", ex);
        }

        _autoCloseTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _autoCloseTimer.Tick += OnAutoCloseTick;
        _autoCloseTimer.Start();
        HintText.Text = $"{_remaining} 秒后自动关闭";

        // 稍后再压一次顶层，避免被 PPT 放映等抢层
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { NativeMethods.ReassertTopmost(hwnd); }
            catch { /* ignore */ }
        }));
    }

    private void OnAutoCloseTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            Close();
            return;
        }
        HintText.Text = $"{_remaining} 秒后自动关闭";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _autoCloseTimer?.Stop();
        _autoCloseTimer = null;
        try
        {
            if (AppServices.IsInitialized)
                AppServices.Current.Timers.AcknowledgeExpiry();
        }
        catch (Exception ex)
        {
            Log.Error("计时器超时关闭后复位计时器失败", ex);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter or Key.Space)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // =========================================================================
    // 全屏定位 + 高斯模糊背景
    // =========================================================================

    private void PositionOnPrimaryScreen()
    {
        WindowState = WindowState.Normal;
        Left = 0;
        Top = 0;
        Width = Math.Max(1, SystemParameters.PrimaryScreenWidth);
        Height = Math.Max(1, SystemParameters.PrimaryScreenHeight);
    }

    /// <summary>截主屏并提供给背景图（半分辨率 + 大模糊半径，性能友好）。</summary>
    private void CaptureBlurredBackdrop()
    {
        try
        {
            var width = GetSystemMetrics(SM_CXSCREEN);
            var height = GetSystemMetrics(SM_CYSCREEN);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("无法获取主屏尺寸");

            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("无法获取屏幕设备上下文");

            var memoryDc = CreateCompatibleDC(screenDc);
            var hBitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || hBitmap == IntPtr.Zero)
            {
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                ReleaseDC(IntPtr.Zero, screenDc);
                throw new InvalidOperationException("无法创建屏幕截图缓冲区");
            }

            var oldBitmap = SelectObject(memoryDc, hBitmap);
            try
            {
                if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, 0, 0, SRCCOPY))
                    throw new InvalidOperationException("屏幕截图失败");

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();

                // 半分辨率足够模糊背景用，模糊半径开销降到 1/4
                var scaled = new TransformedBitmap(source, new ScaleTransform(0.5, 0.5));
                scaled.Freeze();

                BackdropImage.Source = scaled;
                BackdropImage.Effect = new BlurEffect
                {
                    Radius = 24,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance
                };
            }
            finally
            {
                SelectObject(memoryDc, oldBitmap);
                DeleteObject(hBitmap);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        catch (Exception ex)
        {
            Log.Error("计时器超时全屏模糊背景生成失败（退化为暗色遮罩）", ex);
            BackdropImage.Visibility = Visibility.Collapsed;
            DimLayer.Opacity = 0.72;
        }
    }

    // =========================================================================
    // Win32
    // =========================================================================

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destDc, int x, int y, int width, int height,
        IntPtr srcDc, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);
}
