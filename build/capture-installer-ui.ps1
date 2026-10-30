#requires -version 5.1
<#
.SYNOPSIS
    自动走一遍安装器界面并截图（README 用），可选完整安装流程。

.DESCRIPTION
    默认只截欢迎页；加 -AllPages 会依次点「下一步 →」「开始安装」，
    截图目录页 / 进度页 / 完成页，并把班机静默卸载清掉临时安装。

    用法：
      powershell -ExecutionPolicy Bypass -File build\capture-installer-ui.ps1
      powershell -ExecutionPolicy Bypass -File build\capture-installer-ui.ps1 -AllPages
#>
param(
    [string]$Installer = "dist\班机安装程序.exe",
    [string]$OutDir = "assets\screenshots",
    [string]$InstallDir = "",
    [switch]$AllPages,
    [switch]$KeepRunning
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$installerFull = [System.IO.Path]::GetFullPath((Join-Path $root $Installer))
if (-not (Test-Path -LiteralPath $installerFull)) {
    throw "找不到安装程序：$installerFull（请先运行 build\build.ps1）"
}
if (-not $InstallDir) { $InstallDir = Join-Path $root "build\ui-install-test" }
$outFull = [System.IO.Path]::GetFullPath((Join-Path $root $OutDir))
if (-not (Test-Path -LiteralPath $outFull)) { New-Item -ItemType Directory -Path $outFull -Force | Out-Null }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class UiWin32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
}
"@
try { [UiWin32]::SetProcessDpiAwareness(2) | Out-Null } catch { }

function Get-MainWindow([int]$TargetProcessId) {
    $rootElement = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetProcessId)
    for ($i = 0; $i -lt 60; $i++) {
        $window = $rootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($window) { return $window }
        Start-Sleep -Milliseconds 250
    }
    throw "安装器窗口没有出现（进程可能已退出）"
}

function Find-ElementByName($rootElement, [string]$Name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $found = $rootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $found) { throw "界面上找不到元素：$Name" }
    return $found
}

function Invoke-Element($element) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Set-EditValue($rootElement, [string]$Value) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $edit = $rootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $edit) { throw "界面上找不到路径输入框" }
    $pattern = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}

function Uncheck-CheckBoxIfChecked($rootElement, [string]$Name) {
    $box = Find-ElementByName $rootElement $Name
    $pattern = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) {
        $pattern.Toggle()
    }
}

function Save-WindowScreenshot($process, [string]$OutPath) {
    $handle = $process.MainWindowHandle
    for ($i = 0; $i -lt 20 -and $handle -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        $handle = $process.MainWindowHandle
    }
    if ($handle -eq [IntPtr]::Zero) { throw "拿不到安装器窗口句柄" }

    [UiWin32]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 350

    $rect = New-Object UiWin32+RECT
    if (-not [UiWin32]::GetWindowRect($handle, [ref]$rect)) { throw "GetWindowRect 失败" }
    $margin = 20
    $x = $rect.Left - $margin
    $y = $rect.Top - $margin
    $width = ($rect.Right - $rect.Left) + $margin * 2
    $height = ($rect.Bottom - $rect.Top) + $margin * 2

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $width, $height))
    $bitmap.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Host "截图：$OutPath（$width x $height）"
}

function Wait-Installed([string]$Dir, [int]$TimeoutSeconds = 120) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $key = Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Banji" -ErrorAction SilentlyContinue
        if ($key -and $key.InstallLocation -and (Test-Path -LiteralPath (Join-Path $key.InstallLocation "Banji.exe"))) {
            return $key.InstallLocation
        }
        Start-Sleep -Milliseconds 500
    }
    throw "等待安装完成超时"
}

# ---------------------------------------------------------------------------
# 开始
# ---------------------------------------------------------------------------
# 只结束安装器自己的进程；不碰正在运行的班机（安装器只会结束自己安装目录里的班机）
foreach ($name in @("BanjiInstaller", "班机安装程序", "卸载班机")) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 400

$process = Start-Process -FilePath $installerFull -PassThru
$window = Get-MainWindow $process.Id
Start-Sleep -Milliseconds 1200

Save-WindowScreenshot $process (Join-Path $outFull "installer-welcome.png")

if ($AllPages) {
    Invoke-Element (Find-ElementByName $window "下一步 →")
    Start-Sleep -Milliseconds 800
    Set-EditValue $window $InstallDir
    Start-Sleep -Milliseconds 300
    Save-WindowScreenshot $process (Join-Path $outFull "installer-directory.png")

    Invoke-Element (Find-ElementByName $window "开始安装")
    Start-Sleep -Milliseconds 1400
    Save-WindowScreenshot $process (Join-Path $outFull "installer-progress.png")

    $installedTo = Wait-Installed $InstallDir
    Write-Host "安装完成：$installedTo"
    Start-Sleep -Milliseconds 900
    Save-WindowScreenshot $process (Join-Path $outFull "installer-done.png")

    Uncheck-CheckBoxIfChecked $window "立即启动班机"
    Invoke-Element (Find-ElementByName $window "完成")
    Start-Sleep -Milliseconds 800

    # 静默卸载，清理临时安装
    $uninstaller = Join-Path $installedTo "卸载班机.exe"
    if (Test-Path -LiteralPath $uninstaller) {
        $p = Start-Process -FilePath $uninstaller -ArgumentList "/uninstall", "/S" -PassThru -Wait
        Start-Sleep -Seconds 4
        Write-Host "已卸载临时安装（退出码 $($p.ExitCode)）"
    }
} else {
    if ($KeepRunning) { Write-Host "安装器保持运行（PID $($process.Id)）" }
    else { $process | Stop-Process -Force }
}
