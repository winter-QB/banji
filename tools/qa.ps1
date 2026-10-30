param(
    [ValidateSet("start", "cmd", "stop", "restart", "screenshot")]
    [string]$Action = "start",
    [string]$Out = "logs/screen.png",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Args
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\Banji\bin\Debug\net8.0-windows\Banji.exe"

function Send-BanjiCommand {
    param([string[]]$CommandArgs)
    $p = Start-Process -FilePath $exe -ArgumentList $CommandArgs -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(6000)) {
        Write-Warning "命令进程未在 6s 内退出，强制结束: $($CommandArgs -join ' ')"
        try { $p.Kill() } catch {}
    }
}

function Wait-BanjiReady {
    param([int]$TimeoutMs = 15000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $proc = Get-Process Banji -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) {
            # 通过 IPC 的 show 命令探测管道是否就绪
            $probe = Start-Process -FilePath $exe -ArgumentList @("--cmd", "show") -PassThru -WindowStyle Hidden
            $exited = $probe.WaitForExit(3000)
            if (-not $exited) { try { $probe.Kill() } catch {} }
            if ($exited -and $probe.ExitCode -eq 0) { return $true }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

switch ($Action) {
    "start" {
        $existing = Get-Process Banji -ErrorAction SilentlyContinue
        if ($existing) { Write-Host "班机已在运行 (PID $($existing[0].Id))"; return }
        Start-Process -FilePath $exe -WindowStyle Normal | Out-Null
        if (Wait-BanjiReady) { Write-Host "班机已启动并响应 IPC" } else { Write-Host "警告：班机 IPC 未就绪" }
    }
    "cmd" {
        if (-not $Args -or $Args.Count -eq 0) { throw "cmd 需要参数，例如: qa.ps1 cmd --cmd pick" }
        Send-BanjiCommand -CommandArgs $Args
        Write-Host "已发送: $($Args -join ' ')"
    }
    "stop" {
        Send-BanjiCommand -CommandArgs @("--cmd", "exit")
        $proc = Get-Process Banji -ErrorAction SilentlyContinue
        if ($proc -and -not $proc.WaitForExit(5000)) {
            Write-Warning "班机未在 5s 内退出，强制结束"
            $proc | Stop-Process -Force
        }
        Write-Host "班机已停止"
    }
    "restart" {
        & $PSCommandPath stop
        Start-Sleep -Milliseconds 800
        & $PSCommandPath start
    }
    "screenshot" {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "screenshot.ps1") -Out $Out
    }
}
