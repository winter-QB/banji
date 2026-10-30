param(
    [string]$Out = "logs/screen.png"
)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$sig = @"
using System;
using System.Runtime.InteropServices;
public static class DpiHelper {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    public static void Enable() { try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch {} }
}
"@
Add-Type -TypeDefinition $sig
[DpiHelper]::Enable()

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)

# 支持绝对路径与相对路径
if ([System.IO.Path]::IsPathRooted($Out)) {
    $full = [System.IO.Path]::GetFullPath($Out)
} else {
    $full = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Out))
}
$dir = [System.IO.Path]::GetDirectoryName($full)
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bmp.Dispose()
Write-Host "saved $full ($($bounds.Width)x$($bounds.Height))"
