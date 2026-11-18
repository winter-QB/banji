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
/// 班级私有版：每天 12:50 的全屏强提醒（2 分钟自动关闭 + 循环播放 5 遍语音）。
/// 样式复用计时器超时全屏：截主屏 → 高斯模糊 + 暗色遮罩 + 居中大字。
/// </summary>
public partial class NapReminderWindow : Window
{
    private const int AutoCloseSeconds = 120;
    private const int VoiceRepeat = 5;

    private DispatcherTimer? _autoCloseTimer;
    private int _remaining = AutoCloseSeconds;
    private bool _closed;
    private Thread? _voiceThread;

    public NapReminderWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnPrimaryScreen();
        CaptureBlurredBackdrop();

        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            NativeMethods.SetToolWindow(hwnd, true);
            NativeMethods.ReassertTopmost(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("午间强提醒窗口置顶失败", ex);
        }

        _autoCloseTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _autoCloseTimer.Tick += OnAutoCloseTick;
        _autoCloseTimer.Start();
        HintText.Text = $"{_remaining} 秒后自动关闭";

        StartVoiceLoop();

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { NativeMethods.ReassertTopmost(hwnd); }
            catch { /* ignore */ }
        }));
    }

    private void StartVoiceLoop()
    {
        var wav = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "nap-1250.wav");
        if (!File.Exists(wav))
        {
            Log.Warn($"午间提醒语音文件缺失：{wav}");
            return;
        }
        _voiceThread = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < VoiceRepeat && !_closed; i++)
                {
                    if (_closed) break;
                    PlaySound(wav, IntPtr.Zero, SND_FILENAME | SND_SYNC);
                    for (var wait = 0; wait < 6 && !_closed; wait++)
                        Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"午间提醒语音播放失败：{ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "NapReminderVoice"
        };
        _voiceThread.Start();
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
        try { PlaySound(null, IntPtr.Zero, 0); } catch { /* ignore */ }
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
            Log.Error("午间强提醒全屏模糊背景生成失败（退化为暗色遮罩）", ex);
            BackdropImage.Visibility = Visibility.Collapsed;
            DimLayer.Opacity = 0.8;
        }
    }

    // =========================================================================
    // Win32
    // =========================================================================

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_SYNC = 0x0000;

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

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? sound, IntPtr hmod, uint flags);
}
