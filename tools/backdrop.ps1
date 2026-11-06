param(
    [ValidateSet("dark", "light")]
    [string]$Mode = "dark"
)

# 发布用干净截图：铺一层无任何隐私内容的纯渐变全屏背景（覆盖任务栏与桌面图标）
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$form.TopMost = $false
$form.ShowInTaskbar = $false
$form.Text = "BanjiBackdrop"

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
