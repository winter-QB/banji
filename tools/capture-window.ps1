param(
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Padding = 0,
    [int]$Inset = 10,
    [string]$ProcessName = "Banji",
    [switch]$Contains
)

# 只截取指定应用窗口的矩形区域（不拍桌面全屏），用于保护用户桌面隐私。
# 找不到/被遮挡时：尽量把窗口提到前台后再截；若窗口是透明窗口，其背后内容仍会被拍进来（谨慎使用）。
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class WinCap {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static IntPtr Find(string title, bool contains, string processName) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            var t = sb.ToString();
            bool match = contains ? (t.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0) : t == title;
            if (!match) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != 0) {
                try {
                    var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (!string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) return true;
                } catch { return true; }
            }
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
}
"@
[WinCap]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null

$hwnd = [WinCap]::Find($Title, [bool]$Contains, $ProcessName)
if ($hwnd -eq [IntPtr]::Zero) { throw "找不到窗口: $Title" }
[WinCap]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
[WinCap]::SetForegroundWindow($hwnd) | Out-Null
# 临时置顶目标窗口：避免用户其它窗口遮挡导致截到用户内容；截完恢复
$topmost = [IntPtr]::new(-1)
$notTopmost = [IntPtr]::new(-2)
$topFlags = 0x0001 -bor 0x0002 -bor 0x0010 -bor 0x0040  # NOSIZE|NOMOVE|NOACTIVATE|SHOWWINDOW
[WinCap]::SetWindowPos($hwnd, $topmost, 0, 0, 0, 0, $topFlags) | Out-Null
Start-Sleep -Milliseconds 550
$rect = New-Object WinCap+RECT
[WinCap]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
Write-Host ("diag: hwnd={0} title={1} rect={2},{3},{4},{5}" -f $hwnd, $Title, $rect.Left, $rect.Top, $rect.Right, $rect.Bottom)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { throw "窗口尺寸异常: ${w}x${h}" }

$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$x = [Math]::Max($work.Left, $rect.Left - $Padding + $Inset)
$y = [Math]::Max($work.Top, $rect.Top - $Padding + $Inset)
$cw = [Math]::Min($work.Right - $x, $w + $Padding * 2 - $Inset * 2)
$ch = [Math]::Min($work.Bottom - $y, $h + $Padding * 2 - $Inset * 2)

$bmp = New-Object System.Drawing.Bitmap $cw, $ch
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($cw, $ch)))
if ([System.IO.Path]::IsPathRooted($Out)) { $full = [System.IO.Path]::GetFullPath($Out) } else { $full = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Out)) }
$dir = [System.IO.Path]::GetDirectoryName($full)
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
[WinCap]::SetWindowPos($hwnd, $notTopmost, 0, 0, 0, 0, $flags) | Out-Null
$g.Dispose(); $bmp.Dispose()
Write-Host "saved window [$Title] -> $full (${cw}x${ch})"
