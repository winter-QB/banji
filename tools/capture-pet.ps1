param(
    [Parameter(Mandatory = $true)][string]$Out,
    [ValidateSet("dark", "light")][string]$Mode = "dark",
    [int]$Padding = 60,
    [int]$BackdropExtra = 240,
    [string[]]$ExtraTitle = @()
)

# 宠物窗是透明窗口：在宠物窗（及可选的额外窗口，如气泡/菜单）背后放一个“只覆盖该区域”的中性渐变小窗，
# 再局部截图；这样截图里不会出现用户桌面/浏览器内容，也不会全屏遮盖用户屏幕。
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PetCap2 {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static IntPtr Find(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
[PetCap2]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null

$rects = @()
$petHwnd = [PetCap2]::Find("班机")
if ($petHwnd -eq [IntPtr]::Zero) { throw "找不到班机宠物窗（Title=班机）" }
$pr = New-Object PetCap2+RECT
[PetCap2]::GetWindowRect($petHwnd, [ref]$pr) | Out-Null
$rects += $pr

$extraHwnds = @()
foreach ($t in $ExtraTitle) {
    $h = [PetCap2]::Find($t)
    if ($h -eq [IntPtr]::Zero) { continue }
    $extraHwnds += $h
    $r = New-Object PetCap2+RECT
    [PetCap2]::GetWindowRect($h, [ref]$r) | Out-Null
    $rects += $r
}

$left = ($rects | Measure-Object -Property Left -Minimum).Minimum
$top = ($rects | Measure-Object -Property Top -Minimum).Minimum
$right = ($rects | Measure-Object -Property Right -Maximum).Maximum
$bottom = ($rects | Measure-Object -Property Bottom -Maximum).Maximum
$w = $right - $left
$h = $bottom - $top
if ($w -le 0 -or $h -le 0) { throw "宠物窗尺寸异常: ${w}x${h}" }

$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
# 背景窗口覆盖思路：从宠物窗左侧/上方留出余量，一直铺到工作区右下，确保截取区域全部被中性背景覆盖
$bx = [Math]::Max($work.Left, $left - 400)
$by = [Math]::Max($work.Top, $top - 400)
$bw = [Math]::Max(1, $work.Right - $bx)
$bh = [Math]::Max(1, $work.Bottom - $by)

# 清理旧 BanjiBackdrop 残留，避免命中别人的背景窗
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*backdrop.ps1*' } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }
Start-Sleep -Milliseconds 300

$backdrop = Start-Process -FilePath "powershell" -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "tools\backdrop.ps1",
    "-Mode", $Mode, "-X", "$bx", "-Y", "$by", "-Width", "$bw", "-Height", "$bh"
) -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 2600  # pet-ui: backdrop(PowerShell+WinForms) 冷启动约 1.2~2.2s，900ms 太短会误报未启动

# 安全校验：背景窗口必须真的存在且置顶，否则截图可能拍到用户桌面（隐私风险）→ 直接中止
$topFlags = 0x0001 -bor 0x0002 -bor 0x0010 -bor 0x0040
$bdHwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $bdHwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 100
    $bdHwnd = [PetCap2]::Find("BanjiBackdrop")
}
if ($bdHwnd -eq [IntPtr]::Zero) {
    if (-not $backdrop.HasExited) { Stop-Process -Id $backdrop.Id -Force -ErrorAction SilentlyContinue }
    throw "中性背景窗口未启动（为避免泄露桌面，已中止截图）"
}
[PetCap2]::SetWindowPos($bdHwnd, [IntPtr]::new(-1), 0, 0, 0, 0, $topFlags) | Out-Null
# 背景窗置顶后，把宠物窗和附加窗口（气泡/菜单）压到它上面
[PetCap2]::SetWindowPos($petHwnd, [IntPtr]::new(-1), 0, 0, 0, 0, $topFlags) | Out-Null
foreach ($eh in $extraHwnds) {
    [PetCap2]::SetWindowPos($eh, [IntPtr]::new(-1), 0, 0, 0, 0, $topFlags) | Out-Null
}
Start-Sleep -Milliseconds 600

try {
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $x = [Math]::Max($work.Left, $left - $Padding)
    $y = [Math]::Max($work.Top, $top - $Padding)
    $cw = [Math]::Min($work.Right - $x, $w + $Padding * 2)
    $ch = [Math]::Min($work.Bottom - $y, $h + $Padding * 2)

    # 安全校验：背景窗必须完整覆盖截取区域，否则中止（绝不拍用户桌面）
    $br = New-Object PetCap2+RECT
    [PetCap2]::GetWindowRect($bdHwnd, [ref]$br) | Out-Null
    if ([int]$br.Left -gt $x -or [int]$br.Top -gt $y -or [int]$br.Right -lt ($x + $cw) -or [int]$br.Bottom -lt ($y + $ch)) {
        throw "中性背景窗口未覆盖截取区域（为避免泄露桌面，已中止截图）"
    }
    $bmp = New-Object System.Drawing.Bitmap $cw, $ch
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($cw, $ch)))
    if ([System.IO.Path]::IsPathRooted($Out)) { $full = [System.IO.Path]::GetFullPath($Out) } else { $full = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Out)) }
    $dir = [System.IO.Path]::GetDirectoryName($full)
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved pet capture -> $full (${cw}x${ch})"
}
finally {
    if (-not $backdrop.HasExited) { Stop-Process -Id $backdrop.Id -Force -ErrorAction SilentlyContinue }
}

