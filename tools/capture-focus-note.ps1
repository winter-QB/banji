param(
    [string]$Mode = "light",
    [string]$Placement = "TopLeftHorizontal",
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$ProcessPathLike = "workshop\banji\src\Banji\bin",
    [string]$PreviewTime = "2026-11-10 10:35"
)

# 专注态便签截图：先用局部中性背景盖住那块区域，再把便签窗口置顶，最后只截该区域。
# 不拍全屏桌面；仅用于开发/更新文档。
$ErrorActionPreference = "SilentlyContinue"
Set-Location (Split-Path -Parent $PSScriptRoot)
$env:BANJI_INSTANCE_SUFFIX = "focusnote-shot"
$env:BANJI_PET_PREVIEW_TIME = $PreviewTime
$env:BANJI_FOCUS_NOTE_QA = $Placement
Remove-Item "$env:APPDATA\Banji-qa-focusnote-shot" -Recurse -Force
Get-Process Banji | Where-Object { $_.Path -like "*workshop\banji\src\Banji\bin*" } | Stop-Process -Force
& powershell -NoProfile -ExecutionPolicy Bypass -File tools\qa.ps1 restart | Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File tools\qa.ps1 cmd --cmd seed-demo | Out-Null
Start-Sleep -Seconds 3

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class FocusNoteWin {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public struct RECT { public int L, T, R, B; }
  public static IntPtr Find(string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      var sb = new StringBuilder(256);
      GetWindowText(h, sb, 256);
      if (IsWindowVisible(h) && sb.ToString() == title) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@
$null = [FocusNoteWin]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))
$hwnd = [FocusNoteWin]::Find("BanjiTimetable")
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "找不到 BanjiTimetable 窗口"; exit 1 }
$r = New-Object FocusNoteWin+RECT
[FocusNoteWin]::GetWindowRect($hwnd, [ref]$r) | Out-Null
Write-Host "note rect: $($r.L),$($r.T) - $($r.R),$($r.B)"

$bx = [Math]::Max(0, $r.L - 120); $by = [Math]::Max(0, $r.T - 120)
$bw = [Math]::Min(2520 - $bx, ($r.R - $r.L) + 240); $bh = [Math]::Min(1680 - $by, ($r.B - $r.T) + 240)
$bd = Start-Process -FilePath "powershell" -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "tools\backdrop.ps1",
    "-Mode", $Mode, "-X", "$bx", "-Y", "$by", "-Width", "$bw", "-Height", "$bh"
) -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 1200
# 背景窗是 Topmost：把便签窗口再压回最上层
[FocusNoteWin]::SetWindowPos($hwnd, [IntPtr]::new(-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010 -bor 0x0040) | Out-Null
Start-Sleep -Milliseconds 700

$x = [Math]::Max(0, $r.L - 50); $y = [Math]::Max(0, $r.T - 50)
$cw = [Math]::Min(2520 - $x, ($r.R - $r.L) + 100); $ch = [Math]::Min(1680 - $y, ($r.B - $r.T) + 100)
$bmp = New-Object System.Drawing.Bitmap $cw, $ch
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($cw, $ch)))
$full = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path (Get-Location) $Out }
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
if (-not $bd.HasExited) { Stop-Process -Id $bd.Id -Force }
& powershell -NoProfile -ExecutionPolicy Bypass -File tools\qa.ps1 stop | Out-Null
Remove-Item Env:\BANJI_FOCUS_NOTE_QA, Env:\BANJI_PET_PREVIEW_TIME -ErrorAction SilentlyContinue
Write-Host "saved $full"
