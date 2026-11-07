#requires -version 5.1
<#
.SYNOPSIS
    发布版 AI 验证（便携版解压 → 干净数据目录 → 加载本地 0.5B 模型 → 检查 VC++ 运行库预加载）。

.DESCRIPTION
    v1.4.0 起构建会把新版 msvcp140.dll（14.4x+）随包放在 Banji.exe 同目录。
    本脚本用 dist\班机-便携版.zip 解压出一份“发布版”，在**全新的隔离数据目录**下：
      1) 检查 msvcp140.dll 存在且版本 >= 14.40；
      2) 启动 Banji.exe（BANJI_INSTANCE_SUFFIX=pkgfinal，数据目录 %APPDATA%\Banji-qa-pkgfinal）；
      3) --cmd ai-load <0.5B 模型>；
      4) 断言日志出现「已预加载 VC++ 运行库 msvcp140 14.x」和「AI 模型加载完成」；
      5) 结束后只结束自己启动的进程，并清理临时解压目录。

    用法（在 build\build.ps1 之后运行）：
      powershell -ExecutionPolicy Bypass -File build\verify-ai-release.ps1
      powershell -ExecutionPolicy Bypass -File build\verify-ai-release.ps1 -ModelPath "D:\models\qwen2.5-0.5b.gguf"
#>
param(
    [string]$PortableZip = "",
    [string]$ModelPath = "",
    [string]$Suffix = "pkgfinal",
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ([string]::IsNullOrWhiteSpace($PortableZip)) { $PortableZip = Join-Path $root "dist\班机-便携版.zip" }
$PortableZip = [System.IO.Path]::GetFullPath($PortableZip)
if (-not (Test-Path -LiteralPath $PortableZip)) {
    throw "找不到便携版：$PortableZip（请先运行 build\build.ps1）"
}
if ([string]::IsNullOrWhiteSpace($ModelPath)) {
    $ModelPath = Join-Path $env:APPDATA "Banji\models\qwen2.5-0.5b-instruct-q4_k_m.gguf"
}
$ModelPath = [System.IO.Path]::GetFullPath($ModelPath)
if (-not (Test-Path -LiteralPath $ModelPath)) {
    throw "找不到模型文件：$ModelPath（可先下载 0.5B 模型，或用 -ModelPath 指定）"
}

$script:PassCount = 0
$script:FailCount = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { $script:PassCount++; Write-Host ("  [PASS] " + $Message) -ForegroundColor Green }
    else { $script:FailCount++; Write-Host ("  [FAIL] " + $Message) -ForegroundColor Red }
}
function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1MB) { return ("{0:N1} MB" -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ("{0:N0} KB" -f ($Bytes / 1KB)) }
    return "$Bytes B"
}
function Get-DllVersionOrNull([string]$Path) {
    try {
        $raw = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return [Version]($raw.Split(' ')[0])
    } catch { return $null }
}

# ---- 解压便携版到临时目录 ---------------------------------------------------------
$work = Join-Path $env:TEMP ("banji-ai-release-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$appProcess = $null
$dataDir = Join-Path $env:APPDATA ("Banji-qa-" + $Suffix)
try {
    Write-Host ""
    Write-Host "  ============================================================" -ForegroundColor DarkYellow
    Write-Host "     [班机] 发布版 AI 验证（干净数据目录 + 随包 VC++ 运行库）" -ForegroundColor Yellow
    Write-Host "     便携版：$PortableZip" -ForegroundColor DarkGray
    Write-Host "     模型  ：$ModelPath" -ForegroundColor DarkGray
    Write-Host "     数据  ：$dataDir" -ForegroundColor DarkGray
    Write-Host "  ============================================================" -ForegroundColor DarkYellow

    Write-Host "`n== 1. 解压便携版并检查随包运行库 ==" -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($PortableZip, $work)
    $exe = Join-Path $work "Banji.exe"
    Assert-True (Test-Path -LiteralPath $exe) "便携版解压出 Banji.exe（$([math]::Round((Get-Item $exe).Length/1MB,1)) MB）"
    $msvcp = Join-Path $work "msvcp140.dll"
    Assert-True (Test-Path -LiteralPath $msvcp) "便携版目录包含 msvcp140.dll"
    if (Test-Path -LiteralPath $msvcp) {
        $ver = Get-DllVersionOrNull $msvcp
        Write-Host ("   msvcp140.dll 版本：{0}（{1}）" -f $ver, (Format-Size (Get-Item $msvcp).Length)) -ForegroundColor DarkGray
        Assert-True (($null -ne $ver) -and ($ver -ge [Version]"14.40.0.0")) "msvcp140.dll 版本 >= 14.40"
    }

    Write-Host "`n== 2. 准备全新数据目录 ==" -ForegroundColor Cyan
    if (Test-Path -LiteralPath $dataDir) { Remove-Item -LiteralPath $dataDir -Recurse -Force }
    Assert-True (-not (Test-Path -LiteralPath $dataDir)) "已清空隔离数据目录（模拟干净机器）"

    # ---- 启动隔离实例 -------------------------------------------------------------
    $env:BANJI_INSTANCE_SUFFIX = $Suffix
    $appProcess = Start-Process -FilePath $exe -PassThru
    $logDir = Join-Path $dataDir "logs"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $logFile = $null
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $logDir) {
            $latest = Get-ChildItem $logDir -Filter "app-*.log" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($latest) { $logFile = $latest.FullName; break }
        }
        Start-Sleep -Milliseconds 500
    }
    Assert-True ($null -ne $logFile) "实例已启动并生成日志（$logFile）"
    if ($null -eq $logFile) { throw "实例没有生成日志，无法继续。" }

    # 等 IPC 就绪（日志出现“IPC 服务已启动”）
    $ipcReady = $false
    while ((Get-Date) -lt $deadline) {
        if (Select-String -LiteralPath $logFile -Pattern "IPC 服务已启动" -Quiet -ErrorAction SilentlyContinue) { $ipcReady = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Assert-True $ipcReady "隔离实例完成 IPC 初始化"

    # ---- AI 加载 ------------------------------------------------------------------
    Write-Host "`n== 3. 加载本地 0.5B 模型（验证 msvcp140 预加载）==" -ForegroundColor Cyan
    $argList = @("--cmd", "ai-load", $ModelPath)
    $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(60000)) { try { $p.Kill() } catch { } }
    Assert-True ($p.ExitCode -eq 0) "ai-load 命令已送达（第二实例退出码 0）"

    $vcppLine = $null
    $loadLine = $null
    $loadDeadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $loadDeadline) {
        $tail = @(Get-Content -LiteralPath $logFile -Tail 400 -ErrorAction SilentlyContinue)
        $vcpp = $tail | Select-String -Pattern "已预加载 VC\+\+ 运行库 msvcp140" | Select-Object -Last 1
        $load = $tail | Select-String -Pattern "AI 模型加载完成" | Select-Object -Last 1
        if ($vcpp) { $vcppLine = $vcpp.Line }
        if ($load) { $loadLine = $load.Line; break }
        Start-Sleep -Milliseconds 700
    }
    if ($vcppLine) { Write-Host ("   " + $vcppLine.Trim()) -ForegroundColor DarkGray }
    if ($loadLine) { Write-Host ("   " + $loadLine.Trim()) -ForegroundColor DarkGray }
    Assert-True ($null -ne $vcppLine) "日志出现「已预加载 VC++ 运行库 msvcp140 14.x」（随包 DLL 生效）"
    Assert-True ($null -ne $loadLine) "日志出现「AI 模型加载完成」（模型成功加载）"

    # 状态行
    $p2 = Start-Process -FilePath $exe -ArgumentList @("--cmd", "ai-status") -PassThru -WindowStyle Hidden
    if (-not $p2.WaitForExit(30000)) { try { $p2.Kill() } catch { } }
    Start-Sleep -Milliseconds 500
    $statusLine = @(Get-Content -LiteralPath $logFile -Tail 60 -ErrorAction SilentlyContinue) |
        Select-String -Pattern "AI 状态:" | Select-Object -Last 1
    if ($statusLine) { Write-Host ("   " + $statusLine.Line.Trim()) -ForegroundColor DarkGray }
    Assert-True (($null -ne $statusLine) -and ($statusLine.Line -match "Ready")) "AI 状态为 Ready"
} finally {
    # ---- 收尾 ---------------------------------------------------------------------
    if ($appProcess -and -not $appProcess.HasExited) {
        try { Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Remove-Item Env:\BANJI_INSTANCE_SUFFIX -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor DarkYellow
if ($script:FailCount -eq 0) {
    Write-Host ("     发布版 AI 验证通过：{0} 项全部 PASS（含随包 VC++ 运行库）" -f $script:PassCount) -ForegroundColor Green
} else {
    Write-Host ("     发布版 AI 验证失败：PASS {0}，FAIL {1}" -f $script:PassCount, $script:FailCount) -ForegroundColor Red
}
Write-Host "  ============================================================" -ForegroundColor DarkYellow
if ($script:FailCount -gt 0) { exit 1 }
exit 0
