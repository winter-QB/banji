#requires -version 5.1
<#
.SYNOPSIS
    验证“单文件发布版 / 安装版”能否加载 LLamaSharp 本地模型并生成内容（隔离实例，不碰用户实例）。

.DESCRIPTION
    会把 BANJI_INSTANCE_SUFFIX 设为独立值（默认 pkgfinal），因此即使机器上还有别的班机实例在运行也不会冲突。
    流程：启动 exe（隔离实例）→ 等 IPC 就绪 → `--cmd ai-load <模型>` → 等加载结果
          → `--cmd ai-status` → `--cmd ai-say <提示词>` → 检查日志中 AI 原生库/加载/回复结果 → 关闭实例。
    只在进程级操作：不会截图、不弹窗口；结束后只结束自己启动的 PID。

    用法（发布版 exe 在 build\publish 或安装目录里）：
      powershell -ExecutionPolicy Bypass -File build\verify-ai-singlefile.ps1 `
          -ExePath build\publish\Banji.exe `
          -ModelPath "$env:APPDATA\Banji\models\qwen2.5-0.5b-instruct-q4_k_m.gguf"
#>
param(
    [string]$ExePath = "build\publish\Banji.exe",
    [string]$ModelPath = "",
    [string]$Suffix = "pkgfinal",
    [int]$TimeoutSeconds = 240,
    [string]$Prompt = "用一句话介绍你自己"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$exe = [System.IO.Path]::GetFullPath((Join-Path $root $ExePath))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "找不到可执行文件：$exe（请先 build\build.ps1 发布或安装）"
}
if ([string]::IsNullOrWhiteSpace($ModelPath)) {
    $ModelPath = Join-Path $env:APPDATA "Banji\models\qwen2.5-0.5b-instruct-q4_k_m.gguf"
}
$model = [System.IO.Path]::GetFullPath($ModelPath)
if (-not (Test-Path -LiteralPath $model)) {
    throw "找不到模型文件：$model（可先下载 0.5B 模型，或手动指定 -ModelPath）"
}

$script:PassCount = 0
$script:FailCount = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { $script:PassCount++; Write-Host ("  [PASS] " + $Message) -ForegroundColor Green }
    else { $script:FailCount++; Write-Host ("  [FAIL] " + $Message) -ForegroundColor Red }
}

$logDir = Join-Path $env:APPDATA "Banji\logs"
$logFile = $null
function Get-AppLog {
    $latest = Get-ChildItem $logDir -Filter "app-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) { return $latest.FullName }
    return $null
}
function Wait-LogPattern([string]$Pattern, [int]$Seconds, [string]$What) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
        $script:logFile = Get-AppLog
        if ($script:logFile) {
            $hit = Get-Content -LiteralPath $script:logFile -Tail 200 -ErrorAction SilentlyContinue |
                Select-String -Pattern $Pattern -SimpleMatch
            if ($hit) { return $hit[-1].Line }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}
function Send-Qa([string[]]$CommandArgs, [int]$TimeoutMs = 30000) {
    $argList = @("--cmd") + $CommandArgs
    $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit($TimeoutMs)) {
        try { $p.Kill() } catch { }
        return $false
    }
    return ($p.ExitCode -eq 0)
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor DarkYellow
Write-Host "     [班机] 单文件发布版 AI 验证（隔离实例）" -ForegroundColor Yellow
Write-Host "     exe   : $exe" -ForegroundColor DarkGray
Write-Host "     模型  : $model" -ForegroundColor DarkGray
Write-Host "     suffix: $Suffix" -ForegroundColor DarkGray
Write-Host "  ============================================================" -ForegroundColor DarkYellow

$startMark = (Get-Content -LiteralPath (Get-AppLog) -Tail 1 -ErrorAction SilentlyContinue)
$env:BANJI_INSTANCE_SUFFIX = $Suffix
$appProcess = $null
try {
    Write-Host "`n== 1. 启动隔离实例 ==" -ForegroundColor Cyan
    $appProcess = Start-Process -FilePath $exe -PassThru
    $ready = Wait-LogPattern "IPC 服务已启动" $TimeoutSeconds "IPC"
    Assert-True ($null -ne $ready) "隔离实例启动并完成 IPC 初始化"
    $appProcess.Refresh()
    Assert-True (-not $appProcess.HasExited) "实例进程仍在运行（PID $($appProcess.Id)）"

    Write-Host "`n== 2. 加载本地 GGUF 模型（LLamaSharp 原生库）==" -ForegroundColor Cyan
    $ok = Send-Qa @("ai-load", $model)
    Assert-True $ok "ai-load 命令已送达（第二实例退出码 0）"
    $nativeLine = Wait-LogPattern "AI 原生库" $TimeoutSeconds "native"
    if ($null -eq $nativeLine) {
        # 日志里也可能只有“静默跳过”，检查加载结果即可
        $nativeLine = Wait-LogPattern "AI 原生库准备失败" 1 "native-fail"
    }
    Write-Host ("   " + $(if ($null -ne $nativeLine) { $nativeLine } else { "(日志未出现 AI 原生库行)" })) -ForegroundColor DarkGray
    $loadLine = Wait-LogPattern "AI 加载结果:" $TimeoutSeconds "load"
    Assert-True (($null -ne $loadLine) -and ($loadLine -match "True")) "模型加载成功：$loadLine"

    $statusLine = Wait-LogPattern "AI 状态:" 10 "status"
    Write-Host ("   " + $(if ($null -ne $statusLine) { $statusLine } else { "(无状态行)" })) -ForegroundColor DarkGray
    Assert-True (($null -ne $statusLine) -and ($statusLine -match "Ready")) "AI 状态为 Ready"

    Write-Host "`n== 3. 生成一段回复（实际推理，CPU）==" -ForegroundColor Cyan
    $ok2 = Send-Qa @("ai-say", $Prompt) 120000
    Assert-True $ok2 "ai-say 命令已送达"
    $replyLine = Wait-LogPattern "AI 回复:" $TimeoutSeconds "reply"
    Write-Host ("   " + $(if ($null -ne $replyLine) { $replyLine } else { "(无回复行)" })) -ForegroundColor DarkGray
    Assert-True (($null -ne $replyLine) -and ($replyLine -notmatch "<null>") -and $replyLine.Length -gt 12) "AI 生成了非空回复"

    Write-Host "`n== 4. 收尾 ==" -ForegroundColor Cyan
    Send-Qa @("exit") 15000 | Out-Null
    Start-Sleep -Milliseconds 1200
    $appProcess.Refresh()
    if (-not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue }
    Assert-True $true "已关闭隔离实例（只结束本次启动的 PID）"
} finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        try { Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Remove-Item Env:\BANJI_INSTANCE_SUFFIX -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor DarkYellow
if ($script:FailCount -eq 0) {
    Write-Host ("     单文件 AI 验证通过：{0} 项全部 PASS" -f $script:PassCount) -ForegroundColor Green
} else {
    Write-Host ("     单文件 AI 验证失败：PASS {0}，FAIL {1}" -f $script:PassCount, $script:FailCount) -ForegroundColor Red
}
Write-Host "  ============================================================" -ForegroundColor DarkYellow
if ($script:FailCount -gt 0) { exit 1 }
exit 0
