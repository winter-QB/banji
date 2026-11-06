param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CliArgs
)

# 班机 QA 工具（Lead 维护）。为避免 PowerShell 位置参数/前缀匹配吞参数，只用一个剩余参数数组：
#   qa.ps1 restart
#   qa.ps1 cmd --cmd pick
#   qa.ps1 cmd --cmd notice start
#   qa.ps1 screenshot logs/xxx.png
#   qa.ps1 stop
#   qa.ps1 status
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\Banji\bin\Debug\net8.0-windows\Banji.exe"

$all = @($CliArgs)
$Action = if ($all.Count -gt 0) { $all[0].ToLowerInvariant() } else { "start" }
$Rest = if ($all.Count -gt 1) { @($all[1..($all.Count - 1)]) } else { @() }
$Rest = @($Rest)  # 防止单元素数组被 if 展开成标量（导致 $Rest[0] 取到字符串首字符）

function Get-BanjiProcesses {
    # 只统计由本工具启动的开发版实例（按可执行文件路径匹配）；权限受限的旧实例/安装版会被忽略，
    # 避免在 BANJI_INSTANCE_SUFFIX 隔离测试时误杀用户正在运行的班机。
    $devExe = $exe
    @(Get-Process Banji -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path -eq $devExe } catch { $false }
        } | Sort-Object StartTime)
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
    # 等应用真正完成启动再探测，避免探测进程抢在首实例创建互斥量之前“变成”首实例
    Start-Sleep -Milliseconds 2600
    for ($i = 0; $i -lt 6; $i++) {
        $procs = Get-BanjiProcesses
        if ($procs.Count -eq 1) {
            $probe = Start-Process -FilePath $exe -ArgumentList @("--cmd", "show") -PassThru -WindowStyle Hidden
            $exited = $probe.WaitForExit(4000)
            if (-not $exited) { try { $probe.Kill() } catch {} }
            if ($exited -and $probe.ExitCode -eq 0) { return $true }
        }
        elseif ($procs.Count -gt 1) {
            Start-Sleep -Milliseconds 800
            $procs = Get-BanjiProcesses
            if ($procs.Count -gt 1) {
                $procs | Select-Object -Skip 1 | Stop-Process -Force -ErrorAction SilentlyContinue
            }
        }
        Start-Sleep -Milliseconds 700
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
        if ($Rest.Count -eq 0) { throw "cmd 需要参数，例如: qa.ps1 cmd --cmd pick" }
        $procs = Get-BanjiProcesses
        if ($procs.Count -eq 0) {
            Write-Host "（未检测到班机实例，自动启动一个）"
            if (-not (Start-BanjiFresh)) { throw "无法启动班机实例" }
            $procs = Get-BanjiProcesses
        }
        if ($procs.Count -gt 1) {
            Write-Warning "发现 $($procs.Count) 个班机实例，保留最早的 PID $($procs[0].Id)"
            $procs | Select-Object -Skip 1 | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 400
        }
        $ok = Send-BanjiCommand -Arguments $Rest
        if (-not $ok) {
            Start-Sleep -Milliseconds 500
            $ok = Send-BanjiCommand -Arguments $Rest
        }
        if ($ok) { Write-Host "已发送: $($Rest -join ' ')" }
        else { Write-Warning "命令执行失败: $($Rest -join ' ')"; exit 1 }
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
        $out = if ($Rest.Count -gt 0) { $Rest[0] } else { "logs/screen.png" }
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "screenshot.ps1") -Out $out
    }
    "status" {
        $procs = Get-BanjiProcesses
        if ($procs.Count -eq 0) { Write-Host "没有运行中的班机实例" }
        else {
            $procs | Select-Object Id, StartTime, MainWindowTitle | Format-Table -AutoSize | Out-String | Write-Host
            Write-Host "实例数: $($procs.Count)"
        }
    }
    default { throw "未知动作: $Action（支持 start/cmd/stop/restart/screenshot/status）" }
}
