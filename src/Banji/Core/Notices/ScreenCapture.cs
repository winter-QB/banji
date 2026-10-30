using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Banji.Core.Infrastructure;

namespace Banji.Core.Notices;

/// <summary>
/// 主屏截图（GDI BitBlt → BitmapSource），供全屏提示做「高斯模糊背景」。
/// 不依赖 System.Drawing，纯 WPF + Win32，发布时无需额外 NuGet 包。
/// </summary>
internal static class ScreenCapture
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint Srccopy = 0x00CC0020;

    /// <summary>截取主屏（物理像素），scale &lt; 1 时降采样以降低模糊开销。失败返回 null。</summary>
    public static BitmapSource? CapturePrimary(double scale = 1.0)
    {
        try
        {
            var width = GetSystemMetrics(SmCxScreen);
            var height = GetSystemMetrics(SmCyScreen);
            if (width <= 0 || height <= 0) return null;

            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            var memoryDc = CreateCompatibleDC(screenDc);
            var bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                ReleaseDC(IntPtr.Zero, screenDc);
                return null;
            }

            var oldBitmap = SelectObject(memoryDc, bitmap);
            try
            {
                if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, 0, 0, Srccopy))
                    return null;

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();

                if (scale is > 0 and < 1)
                {
                    var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                    scaled.Freeze();
                    return scaled;
                }
                return source;
            }
            finally
            {
                SelectObject(memoryDc, oldBitmap);
                DeleteObject(bitmap);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        catch (Exception ex)
        {
            Log.Error("主屏截图失败（全屏提示退化为纯色背景）", ex);
            return null;
        }
    }

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
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hDestDc, int x, int y, int width, int height, IntPtr hSrcDc, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);
}
