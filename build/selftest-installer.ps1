#requires -version 5.1
<#
.SYNOPSIS
    《班机》安装器端到端自测：静默安装 → 校验文件/注册表/快捷方式/字体 → 启动 → 覆盖安装 → 静默卸载 → 校验清理。

.DESCRIPTION
    用法（先运行 build\build.ps1 生成 dist\班机安装程序.exe）：
      powershell -ExecutionPolicy Bypass -File build\selftest-installer.ps1
      powershell -ExecutionPolicy Bypass -File build\selftest-installer.ps1 -InstallDir D:\temp\banji-test
      powershell -ExecutionPolicy Bypass -File build\selftest-installer.ps1 -KeepInstalled   # 测完不卸载（留着看）

    退出码 0 = 全部通过，1 = 有失败项。
#>
param(
    [string]$Installer = "dist\班机安装程序.exe",
    [string]$InstallDir = ".tmp-install",
    [switch]$KeepInstalled,
    [switch]$SkipLaunchTest
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$installerFull = [System.IO.Path]::GetFullPath((Join-Path $root $Installer))
$installFull = [System.IO.Path]::GetFullPath((Join-Path $root $InstallDir))
if (-not (Test-Path -LiteralPath $installerFull)) {
    throw "找不到安装程序：$installerFull（请先运行 build\build.ps1）"
}

# 期望版本号以主工程 csproj 为准（构建脚本会把它同步到安装器）
$appVersion = "1.0.0"
try {
    [xml]$mainProjectXml = Get-Content -LiteralPath (Join-Path $root "src\Banji\Banji.csproj") -Raw
    $versionNode = $mainProjectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    if ($versionNode -and $versionNode.Version) { $appVersion = [string]$versionNode.Version }
} catch { }
Write-Host "  期望程序版本：$appVersion" -ForegroundColor DarkGray

$script:PassCount = 0
$script:FailCount = 0
$script:FailedItems = @()

function Write-Title([string]$Text) {
    Write-Host ""
    Write-Host "== $Text" -ForegroundColor Cyan
    Write-Host ("   " + ("-" * 58)) -ForegroundColor DarkGray
}

function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) {
        $script:PassCount++
        Write-Host ("   [PASS] " + $Message) -ForegroundColor Green
    } else {
        $script:FailCount++
        $script:FailedItems += $Message
        Write-Host ("   [FAIL] " + $Message) -ForegroundColor Red
    }
}

function Assert-Exists([string]$Path, [string]$Message) {
    Assert-True (Test-Path -LiteralPath $Path) $Message
}

function Assert-Missing([string]$Path, [string]$Message) {
    Assert-True (-not (Test-Path -LiteralPath $Path)) $Message
}

function Get-RegValue([string]$Path, [string]$Name) {
    try {
        $item = Get-ItemProperty -Path $Path -Name $Name -ErrorAction Stop
        return $item.$Name
    } catch {
        return $null
    }
}

function Assert-RegValue([string]$Path, [string]$Name, [string]$Expected, [string]$Message) {
    $actual = Get-RegValue $Path $Name
    Assert-True (($null -ne $actual) -and ($actual -eq $Expected)) "$Message（期望：$Expected，实际：$actual）"
}

$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Banji"
$AppKey = "HKCU:\Software\Banji"
$FontKey = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Fonts"
$UserFontsDir = Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Fonts"
$DesktopLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "班机.lnk"
$StartMenuFolder = Join-Path ([Environment]::GetFolderPath("Programs")) "班机"
$FontFiles = @(
    "HarmonyOS_Sans_SC_Thin.ttf", "HarmonyOS_Sans_SC_Light.ttf", "HarmonyOS_Sans_SC_Regular.ttf",
    "HarmonyOS_Sans_SC_Medium.ttf", "HarmonyOS_Sans_SC_Bold.ttf", "HarmonyOS_Sans_SC_Black.ttf"
)
$FontDisplayNames = @(
    "HarmonyOS Sans SC Thin (TrueType)", "HarmonyOS Sans SC Light (TrueType)", "HarmonyOS Sans SC (TrueType)",
    "HarmonyOS Sans SC Medium (TrueType)", "HarmonyOS Sans SC Bold (TrueType)", "HarmonyOS Sans SC Black (TrueType)"
)

function Stop-BanjiFromDir([string]$Dir) {
    Get-Process -Name "Banji" -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $path = $_.Path
            if ($path -and $path.StartsWith(($Dir.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
}

function Clear-TestEnvironment {
    # 只结束测试安装目录里的班机，以及安装器自己的进程；不碰开发调试实例
    Stop-BanjiFromDir $installFull
    foreach ($name in @("BanjiInstaller", "班机安装程序", "卸载班机")) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 600
    if (Test-Path -LiteralPath $installFull) { Remove-Item -LiteralPath $installFull -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $AppKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $DesktopLnk -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $StartMenuFolder) { Remove-Item -LiteralPath $StartMenuFolder -Recurse -Force -ErrorAction SilentlyContinue }
}

function Run-SilentInstaller([string]$TargetDir) {
    $p = Start-Process -FilePath $installerFull -ArgumentList @("/S", "/D=$TargetDir") -PassThru -Wait
    return $p.ExitCode
}

function Run-SilentUninstaller([string]$Dir) {
    $uninstaller = Join-Path $Dir "卸载班机.exe"
    if (-not (Test-Path -LiteralPath $uninstaller)) { throw "找不到卸载程序：$uninstaller" }
    $p = Start-Process -FilePath $uninstaller -ArgumentList @("/uninstall", "/S") -PassThru -Wait
    return $p.ExitCode
}

function Wait-DirectoryGone([string]$Dir, [int]$TimeoutSeconds = 25) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (-not (Test-Path -LiteralPath $Dir)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return (-not (Test-Path -LiteralPath $Dir))
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor DarkYellow
Write-Host "     [班机] 安装器端到端自测" -ForegroundColor Yellow
Write-Host "     安装程序：$installerFull" -ForegroundColor DarkGray
Write-Host "     临时安装目录：$installFull" -ForegroundColor DarkGray
Write-Host "  ============================================================" -ForegroundColor DarkYellow

# ---------------------------------------------------------------------------
Write-Title "0. 清理测试环境"
Clear-TestEnvironment
Assert-Missing $installFull "测试目录已清空"

# ---------------------------------------------------------------------------
Write-Title "1. 静默安装（/S /D=…）"
$installExit = Run-SilentInstaller $installFull
Assert-True ($installExit -eq 0) "静默安装退出码为 0（实际：$installExit）"
Assert-Exists $installFull "安装目录已创建"

if (-not (Test-Path -LiteralPath $installFull)) {
    Write-Host ""
    Write-Host "  [中止] 安装没有成功，后续检查无法进行。请查看日志：$env:TEMP\BanjiInstaller.log" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
Write-Title "2. 校验安装内容"
Assert-Exists (Join-Path $installFull "Banji.exe") "主程序 Banji.exe 已安装"
Assert-Exists (Join-Path $installFull "卸载班机.exe") "卸载程序 卸载班机.exe 已生成"
Assert-Exists (Join-Path $installFull "README.md") "README.md 已复制"
Assert-Exists (Join-Path $installFull "HarmonyOS_Sans_LICENSE.txt") "字体许可证已复制"
Assert-Exists (Join-Path $installFull "安装信息.txt") "安装信息.txt 已生成"
Assert-Exists (Join-Path $installFull "Assets\Templates\花名册示例.xlsx") "样例模板（花名册）已复制到安装目录"
Assert-Exists (Join-Path $installFull "Assets\Templates\课程表示例.xlsx") "样例模板（课程表）已复制到安装目录"
foreach ($f in $FontFiles) {
    Assert-Exists (Join-Path $installFull "Fonts\$f") "安装目录字体：$f"
}

# ---------------------------------------------------------------------------
Write-Title "3. 校验卸载信息注册表"
Assert-RegValue $UninstallKey "DisplayName" "班机" "卸载信息 DisplayName"
Assert-RegValue $UninstallKey "DisplayVersion" $appVersion "卸载信息 DisplayVersion"
Assert-RegValue $UninstallKey "Publisher" "班机" "卸载信息 Publisher"
Assert-RegValue $UninstallKey "InstallLocation" $installFull "卸载信息 InstallLocation"
$displayIcon = Get-RegValue $UninstallKey "DisplayIcon"
Assert-True ($displayIcon -eq (Join-Path $installFull "Banji.exe")) "卸载信息 DisplayIcon（实际：$displayIcon）"
$uninstallString = Get-RegValue $UninstallKey "UninstallString"
Assert-True ($uninstallString -like "*卸载班机.exe*") "卸载信息 UninstallString（实际：$uninstallString）"
Assert-RegValue $AppKey "InstallPath" $installFull "HKCU\Software\Banji 记录安装路径"

# ---------------------------------------------------------------------------
Write-Title "4. 校验快捷方式"
Assert-Exists $DesktopLnk "桌面快捷方式 班机.lnk"
Assert-Exists (Join-Path $StartMenuFolder "班机.lnk") "开始菜单 班机.lnk"
Assert-Exists (Join-Path $StartMenuFolder "卸载班机.lnk") "开始菜单 卸载班机.lnk"

# ---------------------------------------------------------------------------
Write-Title "5. 校验字体安装（当前用户）"
foreach ($f in $FontFiles) {
    Assert-Exists (Join-Path $UserFontsDir $f) "用户字体目录：$f"
}
foreach ($name in $FontDisplayNames) {
    $value = Get-RegValue $FontKey $name
    Assert-True ((-not [string]::IsNullOrEmpty($value)) -and (Test-Path -LiteralPath $value)) "字体注册项：$name"
}

# ---------------------------------------------------------------------------
Write-Title "6. 启动安装后的程序"
if ($SkipLaunchTest) {
    Write-Host "   （已跳过 -SkipLaunchTest）" -ForegroundColor Yellow
} else {
    $otherBanji = @(Get-Process -Name "Banji" -ErrorAction SilentlyContinue | Where-Object {
        try { -not $_.Path -or -not $_.Path.StartsWith(($installFull.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) }
        catch { $true }
    })
    if ($otherBanji.Count -gt 0) {
        Write-Host "   检测到已有班机实例在运行（开发调试实例）。单实例机制下，安装后的 exe 会转发命令后自行退出，属预期行为；跳过启动检查。" -ForegroundColor Yellow
    } else {
        Stop-BanjiFromDir $installFull
        Start-Sleep -Milliseconds 400
        $appProcess = Start-Process -FilePath (Join-Path $installFull "Banji.exe") -PassThru
        Start-Sleep -Seconds 4
        $appProcess.Refresh()
        Assert-True (-not $appProcess.HasExited) "安装后的 Banji.exe 能正常启动并保持运行"
        if (-not $appProcess.HasExited) {
            Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 600
        }
    }
}

# ---------------------------------------------------------------------------
Write-Title "7. 覆盖安装（幂等性）"
$reinstallExit = Run-SilentInstaller $installFull
Assert-True ($reinstallExit -eq 0) "重复安装退出码为 0（实际：$reinstallExit）"
Assert-Exists (Join-Path $installFull "Banji.exe") "覆盖安装后主程序仍在"
Assert-Exists $DesktopLnk "覆盖安装后桌面快捷方式仍在"

# ---------------------------------------------------------------------------
Write-Title "8. 静默卸载"
if ($KeepInstalled) {
    Write-Host "   （-KeepInstalled：跳过卸载，保留安装）" -ForegroundColor Yellow
} else {
    $uninstallExit = Run-SilentUninstaller $installFull
    Assert-True ($uninstallExit -eq 0) "静默卸载退出码为 0（实际：$uninstallExit）"
    Assert-True (Wait-DirectoryGone $installFull) "安装目录已被清理（含延迟自删除）"

    Write-Title "9. 校验卸载是否清理干净"
    Assert-Missing $installFull "安装目录已删除"
    Assert-Missing $DesktopLnk "桌面快捷方式已删除"
    Assert-Missing $StartMenuFolder "开始菜单文件夹已删除"
    Assert-True ($null -eq (Get-RegValue $UninstallKey "DisplayName")) "卸载信息注册表已删除"
    Assert-True ($null -eq (Get-RegValue $AppKey "InstallPath")) "应用注册表已删除"
    foreach ($f in $FontFiles) {
        Assert-Missing (Join-Path $UserFontsDir $f) "用户字体文件已删除：$f"
    }
    foreach ($name in $FontDisplayNames) {
        Assert-True ($null -eq (Get-RegValue $FontKey $name)) "字体注册项已删除：$name"
    }
    $dataDir = Join-Path $env:APPDATA "Banji"
    Assert-Exists $dataDir "用户数据目录 %APPDATA%\Banji 被保留（卸载不删数据）"
}

# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  ============================================================" -ForegroundColor DarkYellow
if ($script:FailCount -eq 0) {
    Write-Host ("     自测通过：{0} 项全部 PASS" -f $script:PassCount) -ForegroundColor Green
} else {
    Write-Host ("     自测失败：PASS {0} 项，FAIL {1} 项" -f $script:PassCount, $script:FailCount) -ForegroundColor Red
    foreach ($item in $script:FailedItems) { Write-Host ("       - " + $item) -ForegroundColor Red }
}
Write-Host "  ============================================================" -ForegroundColor DarkYellow
Write-Host ""

if ($script:FailCount -gt 0) { exit 1 }
exit 0
