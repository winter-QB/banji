param(
    [ValidateSet("start", "cmd", "stop", "restart", "screenshot", "status")]
    [string]$Action = "start",
    [string]$Out = "logs/screen.png",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandArgs
)

# 班机 QA 工具（Lead 维护）：
#   restart      = 杀掉所有实例 → 启动一个干净实例 → 验证 IPC 就绪（推荐每次验证前用）
#   cmd ...      = 向当前实例转发命令（自动清理多实例/重试一次）
#   stop         = 优雅退出所有实例，必要时强杀
#   screenshot   = 截主屏到 -Out
#   status       = 查看实例状态
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\Banji\bin\Debug\net8.0-windows\Banji.exe"

function Get-BanjiProcesses {
    @(Get-Process Banji -ErrorAction SilentlyContinue | Sort-Object StartTime)
}

function Kill-AllBanji {
    $procs = Get-BanjiProcesses
    if ($procs.Count -eq 0) { return }
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 5000) {
        if ((Get-BanjiProcesses).Count -eq 0) { return }
        Start-Sleep -Milliseconds 200
    }
}

function Send-BanjiCommand {
    param([string[]]$Arguments, [int]$TimeoutMs = 8000)
    $p = Start-Process -FilePath $exe -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit($TimeoutMs)) {
        try { $p.Kill() } catch {}
        return $false
    }
    return ($p.ExitCode -eq 0)
}

function Test-IpcReady {
    if ((Get-BanjiProcesses).Count -ne 1) { return $false }
    return (Send-BanjiCommand -Arguments @("--cmd", "show") -TimeoutMs 4000)
}

function Start-BanjiFresh {
    Start-Process -FilePath $exe -WindowStyle Normal | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 15000) {
        $procs = Get-BanjiProcesses
        if ($procs.Count -ge 1) {
            if ($procs.Count -eq 1 -and (Test-IpcReady)) {
                return $true
            }
            # 出现多实例：保留最早的，结束其余（不应发生，但自愈）
            $procs | Select-Object -Skip 1 | Stop-Process -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

switch ($Action) {
    "start" {
        $procs = Get-BanjiProcesses
        if ($procs.Count -eq 1 -and (Test-IpcReady)) {
            Write-Host "班机已在运行且 IPC 就绪 (PID $($procs[0].Id))"
            return
        }
        Kill-AllBanji
        Start-Sleep -Milliseconds 400
        if (Start-BanjiFresh) { Write-Host "班机已启动 (PID $((Get-BanjiProcesses)[0].Id))" }
        else { Write-Warning "班机启动后 IPC 未就绪"; exit 1 }
    }
    "restart" {
        Kill-AllBanji
        Start-Sleep -Milliseconds 500
        if (Start-BanjiFresh) { Write-Host "班机已干净重启 (PID $((Get-BanjiProcesses)[0].Id))" }
        else { Write-Warning "班机重启失败"; exit 1 }
    }
    "cmd" {
        if (-not $CommandArgs -or $CommandArgs.Count -eq 0) { throw "cmd 需要参数，例如: qa.ps1 cmd --cmd pick" }
        $procs = Get-BanjiProcesses
        if ($procs.Count -eq 0) { throw "当前没有运行中的班机实例，请先 qa.ps1 restart" }
        if ($procs.Count -gt 1) {
            Write-Warning "发现 $($procs.Count) 个班机实例，保留最早的 PID $($procs[0].Id)"
            $procs | Select-Object -Skip 1 | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 400
        }
        $ok = Send-BanjiCommand -Arguments $CommandArgs
        if (-not $ok) {
            Start-Sleep -Milliseconds 500
            $ok = Send-BanjiCommand -Arguments $CommandArgs
        }
        if ($ok) { Write-Host "已发送: $($CommandArgs -join ' ')" }
        else { Write-Warning "命令执行失败: $($CommandArgs -join ' ')"; exit 1 }
    }
    "stop" {
        if ((Get-BanjiProcesses).Count -gt 0) {
            Send-BanjiCommand -Arguments @("--cmd", "exit") -TimeoutMs 5000 | Out-Null
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($sw.ElapsedMilliseconds -lt 5000) {
                if ((Get-BanjiProcesses).Count -eq 0) { break }
                Start-Sleep -Milliseconds 300
            }
            $left = Get-BanjiProcesses
            if ($left.Count -gt 0) {
                Write-Host "强制结束剩余 $($left.Count) 个实例"
                Kill-AllBanji
            }
        }
        Write-Host "班机已停止"
    }
    "screenshot" {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "screenshot.ps1") -Out $Out
    }
    "status" {
        $procs = Get-BanjiProcesses
        if ($procs.Count -eq 0) { Write-Host "没有运行中的班机实例" }
        else {
            $procs | Select-Object Id, StartTime, MainWindowTitle | Format-Table -AutoSize | Out-String | Write-Host
            Write-Host "实例数: $($procs.Count)"
        }
    }
}
