param(
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [int]$ScreenshotDelayMs = 900
)

# 班机 v1.2.0 验收：分离缩放/课表位置 + 端侧 AI 聊天 + 回归。
# 隐私安全：不拍全屏桌面！宠物用“局部中性背景”截图，窗口用窗口级截图。
$ErrorActionPreference = "Stop"
$env:BANJI_INSTANCE_SUFFIX = "v12"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$dotnet = Join-Path $root ".dotnet\dotnet.exe"
$qa = Join-Path $root "tools\qa.ps1"
$exe = Join-Path $root "src\Banji\bin\Debug\net8.0-windows\Banji.exe"
$outDir = Join-Path $root "logs\acceptance-v12"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$qaDataRoot = Join-Path $env:APPDATA "Banji-qa-v12"
$logFile = Join-Path $qaDataRoot ("logs\app-" + (Get-Date -Format yyyyMMdd) + ".log")

function Qa {
    param([string[]]$Arguments)
    & powershell -NoProfile -ExecutionPolicy Bypass -File $qa @Arguments
    if ($LASTEXITCODE -ne 0) { throw "qa.ps1 失败: $($Arguments -join ' ')" }
}
function Send-Banji {
    param([string[]]$CommandArguments)
    $p = Start-Process -FilePath $exe -ArgumentList $CommandArguments -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(8000)) { try { $p.Kill() } catch {} }
}
function Close-Windows { Send-Banji @("--cmd", "close-windows"); Start-Sleep -Milliseconds 400 }
function ShotWindow {
    param([string]$TitleContains, [string]$Name, [int]$DelayMs = 1000)
    Send-Banji @("--cmd", "dismiss-notices") | Out-Null
    Start-Sleep -Milliseconds ($DelayMs + 250)
    & powershell -NoProfile -ExecutionPolicy Bypass -File "tools\capture-window.ps1" -Title $TitleContains -Out (Join-Path $outDir $Name) -ProcessName Banji
}
function ShotPet {
    param([string]$Name, [string[]]$ExtraTitles = @(), [int]$DelayMs = 900)
    Send-Banji @("--cmd", "dismiss-notices") | Out-Null
    Start-Sleep -Milliseconds ($DelayMs + 250)
    $a = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "tools\capture-pet.ps1", "-Out", (Join-Path $outDir $Name))
    foreach ($t in $ExtraTitles) { $a += "-ExtraTitle"; $a += $t }
    & powershell @a
}

$report = [System.Collections.Generic.List[string]]::new()
$script:fail = 0
function Step {
    param([string]$Label, [scriptblock]$Body)
    Write-Host "`n=== $Label ===" -ForegroundColor Cyan
    try { & $Body; $report.Add("PASS  $Label") }
    catch {
        $script:fail++
        Write-Warning "步骤失败: $Label :: $($_.Exception.Message)"
        $report.Add("FAIL  $Label :: $($_.Exception.Message)")
    }
}

# 窗口探测（测全屏提示自动关闭时长，不截图，避免拍用户桌面）
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class V12Probe {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int n);
  public static bool Exists(string title) {
    bool found = false;
    EnumWindows((h,l) => { var sb=new StringBuilder(256); GetWindowText(h,sb,256); if (sb.ToString()==title && IsWindowVisible(h)) { found=true; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
"@ -ErrorAction SilentlyContinue
function Measure-Window {
    param([string]$Title, [int]$TimeoutMs = 30000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $seen = $false; $appeared = 0; $gone = 0
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $exists = [V12Probe]::Exists($Title)
        if ($exists -and -not $seen) { $seen = $true; $appeared = $sw.ElapsedMilliseconds }
        if ($seen -and -not $exists) { $gone = $sw.ElapsedMilliseconds; break }
        Start-Sleep -Milliseconds 80
    }
    if (-not $seen) { throw "窗口「$Title」没有出现" }
    if ($gone -eq 0) { throw "窗口「$Title」在 $TimeoutMs ms 内没有自动关闭" }
    return [math]::Round(($gone - $appeared) / 1000.0, 2)
}

Step "停旧实例（只停开发版）" { Qa -Arguments @("stop") }

if (-not $SkipBuild) {
    Step "构建" {
        & $dotnet build (Join-Path $root "Banji.sln") -c Debug 2>&1 | Select-String "error" | Select-Object -First 10 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "构建失败" }
    }
}
if (-not $SkipTests) {
    Step "全量测试" {
        & $dotnet run --project (Join-Path $root "src\Banji.Tests\Banji.Tests.csproj") -c Debug --no-build 2>&1 | Select-Object -Last 5 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "测试未全部通过" }
    }
}

Step "启动隔离实例 + 演示数据 + 考试倒计时" {
    Qa -Arguments @("restart")
    Send-Banji @("--cmd", "seed-demo")
    Start-Sleep -Milliseconds 700
    Send-Banji @("--cmd", "exam-demo")
    Start-Sleep -Milliseconds 800
}

Step "宠物：默认缩放" { ShotPet "01-pet-default.png" }
Step "宠物：大宠物 + 小课表" {
    Send-Banji @("--cmd", "set", "petscale", "1.6")
    Send-Banji @("--cmd", "set", "panelscale", "0.8")
    ShotPet "02-pet-big-panel-small.png" -DelayMs 1300
}
Step "宠物：小宠物 + 大课表" {
    Send-Banji @("--cmd", "set", "petscale", "0.7")
    Send-Banji @("--cmd", "set", "panelscale", "1.6")
    ShotPet "03-pet-small-panel-big.png" -DelayMs 1300
}
Step "课表位置：左移" {
    Send-Banji @("--cmd", "set", "petscale", "1")
    Send-Banji @("--cmd", "set", "panelscale", "1.1")
    Send-Banji @("--cmd", "set", "panelx", "-140")
    Send-Banji @("--cmd", "set", "panely", "0")
    ShotPet "04-panel-left.png" -DelayMs 1300
}
Step "课表位置：右移 + 下移" {
    Send-Banji @("--cmd", "set", "panelx", "160")
    Send-Banji @("--cmd", "set", "panely", "60")
    ShotPet "05-panel-right-down.png" -DelayMs 1300
}
Step "恢复默认缩放/位置" {
    Send-Banji @("--cmd", "set", "petscale", "1")
    Send-Banji @("--cmd", "set", "panelscale", "1")
    Send-Banji @("--cmd", "set", "panelx", "0")
    Send-Banji @("--cmd", "set", "panely", "0")
    Start-Sleep -Milliseconds 600
}
Step "宠物：长台词多行气泡" {
    Send-Banji @("--cmd", "quip", "今天的数学作业就像高斯的等差数列，看起来很多，其实只是把你的睡眠时间按差分递减了而已，加油吧少年。")
    ShotPet "06-pet-long-quip.png" -ExtraTitles @("BanjiBubble") -DelayMs 2400
    Start-Sleep -Seconds 4
}
Step "宠物：抽签气泡" {
    Send-Banji @("--cmd", "pick")
    ShotPet "07-pet-pick.png" -ExtraTitles @("BanjiBubble") -DelayMs 1200
    Start-Sleep -Seconds 10
}
Step "菜单（缩放/位置滑块 + 聊天入口）" {
    Send-Banji @("--cmd", "menu")
    ShotPet "08-pet-menu.png" -ExtraTitles @("BanjiMenu") -DelayMs 900
    Close-Windows
}
Step "AI 状态" {
    Send-Banji @("--cmd", "ai-status")
    Start-Sleep -Milliseconds 700
    $line = Get-Content $logFile -Tail 60 | Select-String "AI 状态" | Select-Object -Last 1
    if (-not $line) { throw "日志里没有 AI 状态" }
    $report.Add("INFO  " + $line.Line.Trim())
}
Step "加载 0.5B 模型（隔离实例）" {
    $model = Join-Path $env:APPDATA "Banji\models\qwen2.5-0.5b-instruct-q4_k_m.gguf"
    if (-not (Test-Path $model)) { throw "模型文件不存在: $model" }
    Send-Banji @("--cmd", "ai-load", $model)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ok = $false
    while ($sw.ElapsedMilliseconds -lt 120000) {
        Start-Sleep -Milliseconds 2000
        $tail = Get-Content $logFile -Tail 80 -ErrorAction SilentlyContinue
        if ($tail | Select-String "AI 加载结果: True") { $ok = $true; break }
        if ($tail | Select-String "AI 加载结果: False") { break }
        if ($tail | Select-String "AI 自动加载失败|AI 加载失败") { break }
    }
    if (-not $ok) { throw "模型加载失败（见日志）" }
    $report.Add("INFO  模型加载成功（耗时约 $([math]::Round($sw.Elapsed.TotalSeconds,1))s）")
}
Step "AI 聊天窗口" {
    Send-Banji @("--cmd", "chat")
    ShotWindow "和班机聊聊 - 班机" "09-chat.png" 1200
    Close-Windows
}
Step "AI 模型管理窗口" {
    Send-Banji @("--cmd", "ai-models")
    ShotWindow "AI 模型管理 - 班机" "10-ai-models.png" 1200
    Close-Windows
}
Step "AI 生成实测" {
    Send-Banji @("--cmd", "ai-say", "你好，用一句话介绍你自己")
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $reply = $null
    while ($sw.ElapsedMilliseconds -lt 180000) {
        Start-Sleep -Milliseconds 1500
        $tail = Get-Content $logFile -Tail 120 -ErrorAction SilentlyContinue
        $m = $tail | Select-String "AI 回复: " | Select-Object -Last 1
        if ($m) { $reply = $m.Line; break }
    }
    if (-not $reply) { throw "180 秒内没有 AI 回复" }
    $report.Add("INFO  " + $reply.Trim())
    Write-Host $reply
}
Step "AI 宠物台词实测" {
    Send-Banji @("--cmd", "ai-quip")
    Start-Sleep -Seconds 6
    $tail = Get-Content $logFile -Tail 80 | Select-String "AI 台词: " | Select-Object -Last 1
    if (-not $tail) { throw "没有 AI 台词日志" }
    $report.Add("INFO  " + $tail.Line.Trim())
}
Step "设置窗口：宠物缩放/位置" {
    Send-Banji @("--cmd", "settings", "pet")
    ShotWindow "设置 - 班机" "11-settings-pet.png" 1300
    Close-Windows
}
Step "设置窗口：端侧 AI" {
    Send-Banji @("--cmd", "settings", "ai")
    ShotWindow "设置 - 班机" "12-settings-ai.png" 1300
    Close-Windows
}
Step "仪表盘（回归）" {
    Send-Banji @("--cmd", "dashboard")
    ShotWindow "仪表盘 - 班机" "13-dashboard.png"
    Close-Windows
}
Step "花名册积分（回归）" {
    Send-Banji @("--cmd", "rosters")
    ShotWindow "花名册管理 - 班机" "14-rosters.png"
    Close-Windows
}
Step "随机分组（回归）" {
    Send-Banji @("--cmd", "grouping")
    ShotWindow "随机分组 - 班机" "15-grouping.png"
    Close-Windows
}
Step "铃声校准（回归）" {
    Send-Banji @("--cmd", "ring-calibration")
    ShotWindow "铃声校准 — 班机" "16-ring-calibration.png"
    Close-Windows
}
Step "上课提示自动关闭（回归，不截图避免拍桌面）" {
    Send-Banji @("--cmd", "notice", "start")
    $secs = Measure-Window -Title "上课提示"
    $report.Add("INFO  上课提示停留 ${secs}s（自动关闭）")
    if ($secs -lt 2 -or $secs -gt 8) { throw "上课提示停留异常: ${secs}s" }
    Start-Sleep -Milliseconds 500
}
Step "计时器超时自动关闭（回归，不截图）" {
    Send-Banji @("--cmd", "notice", "timeout")
    $secs = Measure-Window -Title "计时器超时" -TimeoutMs 30000
    $report.Add("INFO  计时器超时窗口停留 ${secs}s（自动关闭）")
    if ($secs -lt 3 -or $secs -gt 25) { throw "计时器超时停留异常: ${secs}s" }
}
Step "备份导出/检查/恢复（回归）" {
    $backup = Join-Path $outDir "roundtrip.banji"
    if (Test-Path $backup) { Remove-Item $backup -Force }
    Send-Banji @("--cmd", "backup-export", $backup)
    Start-Sleep -Milliseconds 700
    if (-not (Test-Path $backup)) { throw "备份文件未生成" }
    Send-Banji @("--cmd", "backup-inspect", $backup)
    Start-Sleep -Milliseconds 700
}
Step "退出隔离实例" { Qa -Arguments @("stop") }

Write-Host "`n================ v1.2.0 验收汇总 ================" -ForegroundColor Cyan
$report | Out-Host
Get-ChildItem $outDir -Filter *.png | Sort-Object Name | Select-Object Name, Length | Out-Host
if ($script:fail -gt 0) { Write-Host "`n有 $script:fail 个步骤失败" -ForegroundColor Red; exit 1 }
Write-Host "`n全部步骤通过 ✅" -ForegroundColor Green
