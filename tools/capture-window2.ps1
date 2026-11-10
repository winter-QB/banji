param(
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Inset = 8,
    [string]$ProcessName = "Banji",
    [string]$ProcessPathLike = ""
)

# 用 PrintWindow 直接抓“窗口自身的内容”，不依赖屏幕截图/前台激活：
# - 不会拍到用户桌面或其他窗口（隐私安全）
# - 不会把窗口抢到前台（对正在用电脑的用户干扰最小）
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PrintCapDpi { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v); }
"@
[PrintCapDpi]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PrintCap {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr Find(string title, string processName, string pathLike, out string foundTitle) {
        IntPtr found = IntPtr.Zero;
        string ft = "";
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            var t = sb.ToString();
            if (t != title) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != 0) {
                try {
                    var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (!string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) return true;
                    if (!string.IsNullOrEmpty(pathLike)) {
                        var exePath = p.MainModule.FileName;
                        if (exePath.IndexOf(pathLike, StringComparison.OrdinalIgnoreCase) < 0) return true;
                    }
                } catch { return true; }
            }
            found = h; ft = t; return false;
        }, IntPtr.Zero);
        foundTitle = ft;
        return found;
    }
}
"@
$foundTitle = ""
$hwnd = [PrintCap]::Find($Title, $ProcessName, $ProcessPathLike, [ref]$foundTitle)
if ($hwnd -eq [IntPtr]::Zero) { throw "找不到班机窗口: $Title" }
$rect = New-Object PrintCap+RECT
[PrintCap]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = [int]$rect.Right - [int]$rect.Left
$h = [int]$rect.Bottom - [int]$rect.Top
if ($w -le 0 -or $h -le 0) { throw "窗口尺寸异常: ${w}x${h}" }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [PrintCap]::PrintWindow($hwnd, $hdc, 2)   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()
if (-not $ok) { throw "PrintWindow 失败（窗口可能不支持离屏渲染）: $foundTitle" }

# 裁掉窗口圆角/阴影边缘
if ($Inset -gt 0 -and $w -gt ([int]$Inset * 2) -and $h -gt ([int]$Inset * 2)) {
    $crop = New-Object System.Drawing.Rectangle([int]$Inset, [int]$Inset, [int]($w - [int]$Inset * 2), [int]($h - [int]$Inset * 2))
    $old = $bmp
    $bmp = $old.Clone($crop, $old.PixelFormat)
    $old.Dispose()
}
$full = if ([System.IO.Path]::IsPathRooted($Out)) { [System.IO.Path]::GetFullPath($Out) } else { [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Out)) }
$dir = [System.IO.Path]::GetDirectoryName($full)
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bw = $bmp.Width; $bh = $bmp.Height
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved PrintWindow [$foundTitle] -> $full (${bw}x${bh})"
