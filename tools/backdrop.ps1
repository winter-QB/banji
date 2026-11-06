param(
    [ValidateSet("dark", "light")]
    [string]$Mode = "dark",
    [int]$X = -1,
    [int]$Y = -1,
    [int]$Width = 0,
    [int]$Height = 0
)

# 中性渐变背景窗口：默认全屏；指定 X/Y/Width/Height 时只覆盖局部（用于宠物窗背后的干净背景）。
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class BackdropDpi { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v); }
"@
[BackdropDpi]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.TopMost = $true
$form.ShowInTaskbar = $false
$form.Text = "BanjiBackdrop"

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
if ($Width -gt 0 -and $Height -gt 0 -and $X -ge 0 -and $Y -ge 0) {
    $form.Bounds = New-Object System.Drawing.Rectangle($X, $Y, $Width, $Height)
} else {
    $form.Bounds = $screen
}

$form.add_Paint({
    param($sender, $e)
    $rect = $sender.ClientRectangle
    if ($Mode -eq "light") {
        $c1 = [System.Drawing.Color]::FromArgb(247, 249, 253)
        $c2 = [System.Drawing.Color]::FromArgb(206, 220, 242)
    } else {
        $c1 = [System.Drawing.Color]::FromArgb(15, 20, 32)
        $c2 = [System.Drawing.Color]::FromArgb(38, 51, 76)
    }
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 45.0)
    $e.Graphics.FillRectangle($brush, $rect)
    $brush.Dispose()
})

[System.Windows.Forms.Application]::Run($form)
