param(
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [int]$ScreenshotDelayMs = 900
)

# 班机 v1.1 验收脚本（第二轮改动 + 新功能）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$dotnet = Join-Path $root ".dotnet\dotnet.exe"
$qa = Join-Path $root "tools\qa.ps1"
$outDir = Join-Path $root "logs\acceptance-v11"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Qa {
    param([string[]]$Arguments)
    & powershell -NoProfile -ExecutionPolicy Bypass -File $qa @Arguments
    if ($LASTEXITCODE -ne 0) { throw "qa.ps1 失败: $($Arguments -join ' ')" }
}
function Send-Banji { param([string[]]$CommandArguments) Qa -Arguments (@("cmd") + $CommandArguments) }
function Shot {
    param([string]$Name, [int]$DelayMs = -1)
    if ($DelayMs -lt 0) { $DelayMs = $ScreenshotDelayMs }
    Start-Sleep -Milliseconds $DelayMs
    Qa -Arguments @("screenshot", (Join-Path $outDir $Name))
}

# 窗口探测：测“全屏提示窗口”出现→消失的时长（验证自动关闭）
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class V11NoticeProbe {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int n);
  public static bool Exists(uint pid, string title) {
    bool found = false;
    EnumWindows((h,l) => { uint p; GetWindowThreadProcessId(h, out p); if (p==pid) { var sb=new StringBuilder(256); GetWindowText(h,sb,256); if (sb.ToString()==title && IsWindowVisible(h)) { found=true; return false; } } return true; }, IntPtr.Zero);
    return found;
  }
}
"@ -ErrorAction SilentlyContinue

function Measure-NoticeDuration {
    param([string]$Title, [int]$TimeoutMs = 15000)
    $procId = (Get-Process Banji -ErrorAction SilentlyContinue | Select-Object -First 1).Id
    if (-not $procId) { throw "没有运行中的班机实例" }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $seen = $false
    $appeared = 0
    $gone = 0
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $exists = [V11NoticeProbe]::Exists($procId, $Title)
        if ($exists -and -not $seen) { $seen = $true; $appeared = $sw.ElapsedMilliseconds }
        if ($seen -and -not $exists) { $gone = $sw.ElapsedMilliseconds; break }
        Start-Sleep -Milliseconds 80
    }
    if (-not $seen) { throw "「$Title」窗口没有出现" }
    if ($gone -eq 0) { throw "「$Title」窗口在 15 秒内没有自动关闭（自动关闭失败！）" }
    return [math]::Round(($gone - $appeared) / 1000.0, 2)
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

Step "停旧实例" { Qa -Arguments @("stop") }

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

Step "启动 + 演示数据 + 考试倒计时" {
    Qa -Arguments @("restart")
    Send-Banji @("--cmd", "seed-demo")
    Start-Sleep -Milliseconds 600
    Send-Banji @("--cmd", "exam-demo")
    Start-Sleep -Milliseconds 800
}

Step "宠物：方形 + 无背景课表 + D-Day（展开）" { Shot "01-pet-default.png" }
Step "宠物：折叠" {
    Send-Banji @("--cmd", "expand", "false")
    Shot "02-pet-collapsed.png"
    Send-Banji @("--cmd", "expand", "true")
}
Step "宠物：摸头气泡" {
    Send-Banji @("--cmd", "pat")
    Shot "03-pet-pat-bubble.png"
    Start-Sleep -Seconds 3
}
Step "宠物：长台词多行气泡" {
    Send-Banji @("--cmd", "quip", "今天的数学作业就像高斯的等差数列，看起来很多，其实只是把你的睡眠时间按差分递减了而已，加油吧少年。")
    Shot "04-pet-long-quip.png"
    Start-Sleep -Seconds 4
}
Step "宠物：抽签气泡" {
    Send-Banji @("--cmd", "pick")
    Shot "05-pet-pick-bubble.png"
}
Step "宠物：计时气泡" {
    Send-Banji @("--cmd", "timer", "countdown", "120")
    Shot "06-pet-timer.png"
    Send-Banji @("--cmd", "timer", "cancel")
}
Step "菜单（含养成统计）" {
    Send-Banji @("--cmd", "menu")
    Shot "07-pet-menu.png"
    Send-Banji @("--cmd", "close-windows")
}
Step "设置窗口（备份/考试/校准分区）" {
    Send-Banji @("--cmd", "settings")
    Shot "08-settings.png"
    Send-Banji @("--cmd", "close-windows")
}
Step "设置-备份分区" {
    Send-Banji @("--cmd", "settings", "backup")
    Shot "08b-settings-backup.png"
    Send-Banji @("--cmd", "close-windows")
}
Step "仪表盘（积分/养成/D-Day）" {
    Send-Banji @("--cmd", "dashboard")
    Shot "09-dashboard.png"
    Send-Banji @("--cmd", "close-windows")
}
Step "花名册管理（积分列）" {
    Send-Banji @("--cmd", "rosters")
    Shot "10-rosters.png"
    Send-Banji @("--cmd", "close-windows")
}
Step "随机分组器" {
    Send-Banji @("--cmd", "grouping")
    Shot "11-grouping.png"
    Send-Banji @("--cmd", "close-windows")
}
Step "铃声校准向导" {
    Send-Banji @("--cmd", "ring-calibration")
    Shot "12-ring-calibration.png"
    Send-Banji @("--cmd", "close-windows")
}
Step "上课提示自动关闭实测" {
    Send-Banji @("--cmd", "notice", "start")
    $secs = Measure-NoticeDuration -Title "上课提示"
    Write-Host "上课提示停留: ${secs}s"
    $report.Add("INFO  上课提示停留 ${secs}s（自动关闭）")
    if ($secs -lt 2.0 -or $secs -gt 8.0) { throw "上课提示停留时长异常: ${secs}s" }
    Start-Sleep -Milliseconds 500
}
Step "上课提示进度条截图" {
    Send-Banji @("--cmd", "notice", "start")
    Shot "13-notice-start.png" 700
    Start-Sleep -Seconds 5
}
Step "下课提示自动关闭实测" {
    Send-Banji @("--cmd", "notice", "end")
    $secs = Measure-NoticeDuration -Title "下课提示"
    Write-Host "下课提示停留: ${secs}s"
    $report.Add("INFO  下课提示停留 ${secs}s（自动关闭）")
    if ($secs -lt 2.0 -or $secs -gt 8.0) { throw "下课提示停留时长异常: ${secs}s" }
    Start-Sleep -Milliseconds 500
}
Step "下课提示进度条截图" {
    Send-Banji @("--cmd", "notice", "end")
    Shot "14-notice-end.png" 700
    Start-Sleep -Seconds 5
}
Step "计时器超时模糊" {
    Send-Banji @("--cmd", "notice", "timeout")
    Shot "15-timer-timeout.png" 1000
    Start-Sleep -Seconds 16
}
Step "备份导出/检查/恢复" {
    $backup = Join-Path $outDir "roundtrip.banji"
    if (Test-Path $backup) { Remove-Item $backup -Force }
    Send-Banji @("--cmd", "backup-export", $backup)
    Start-Sleep -Milliseconds 600
    if (-not (Test-Path $backup)) { throw "备份文件未生成" }
    Send-Banji @("--cmd", "backup-inspect", $backup)
    Start-Sleep -Milliseconds 600
    Send-Banji @("--cmd", "backup-import", $backup)
    Start-Sleep -Milliseconds 800
}
Step "退出" { Qa -Arguments @("stop") }

Write-Host "`n================ v1.1 验收汇总 ================" -ForegroundColor Cyan
$report | Out-Host
Get-ChildItem $outDir -Filter *.png | Sort-Object Name | Select-Object Name, Length | Out-Host
if ($script:fail -gt 0) { Write-Host "`n有 $script:fail 个步骤失败" -ForegroundColor Red; exit 1 }
Write-Host "`n全部步骤通过 ✅" -ForegroundColor Green
