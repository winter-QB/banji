param(
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [int]$ScreenshotDelayMs = 900
)

# 班机最终验收脚本（Lead 维护）：构建 → 测试 → 干净重启 → 逐项命令+截图 → 汇总
# 依赖升级版 tools\qa.ps1（restart 会强杀所有实例并验证 IPC）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$dotnet = Join-Path $root ".dotnet\dotnet.exe"
$qa = Join-Path $root "tools\qa.ps1"
$outDir = Join-Path $root "logs\acceptance"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Qa {
    param([string[]]$Arguments)
    & powershell -NoProfile -ExecutionPolicy Bypass -File $qa @Arguments
    if ($LASTEXITCODE -ne 0) { throw "qa.ps1 失败: $($Arguments -join ' ')" }
}

function Send-Banji {
    param([string[]]$CommandArguments)
    Qa -Arguments (@("cmd") + $CommandArguments)
}

function Shot {
    param([string]$Name)
    Start-Sleep -Milliseconds $ScreenshotDelayMs
    Qa -Arguments @("screenshot", (Join-Path $outDir $Name))
}

$report = [System.Collections.Generic.List[string]]::new()
$script:fail = 0
function Step {
    param([string]$Label, [scriptblock]$Body)
    Write-Host "`n=== $Label ===" -ForegroundColor Cyan
    try {
        & $Body
        $report.Add("PASS  $Label")
    }
    catch {
        $script:fail++
        Write-Warning "步骤失败: $Label :: $($_.Exception.Message)"
        $report.Add("FAIL  $Label :: $($_.Exception.Message)")
    }
}

Step "停止旧实例" { Qa -Arguments @("stop") }

if (-not $SkipBuild) {
    Step "构建 Debug" {
        $output = & $dotnet build (Join-Path $root "Banji.sln") -c Debug 2>&1
        $output | Select-String "error" | Select-Object -First 10 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "构建失败" }
    }
}
if (-not $SkipTests) {
    Step "运行核心测试" {
        & $dotnet run --project (Join-Path $root "src\Banji.Tests\Banji.Tests.csproj") -c Debug --no-build 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "测试未全部通过" }
    }
}

Step "干净启动班机" { Qa -Arguments @("restart") }

Step "演示数据 + 字体自检" {
    Send-Banji @("--cmd", "seed-demo")
    Start-Sleep -Milliseconds 700
    Send-Banji @("--cmd", "font-check")
    Start-Sleep -Milliseconds 500
}

Step "截图：宠物默认状态" { Shot "01-pet-default.png" }

Step "截图：折叠课表" {
    Send-Banji @("--cmd", "expand", "false")
    Shot "02-pet-collapsed.png"
    Send-Banji @("--cmd", "expand", "true")
}

Step "截图：抽签气泡" {
    Send-Banji @("--cmd", "pick")
    Shot "03-pet-pick-bubble.png"
}

Step "截图：计时器气泡" {
    Send-Banji @("--cmd", "timer", "countdown", "120")
    Shot "04-pet-timer.png"
    Send-Banji @("--cmd", "timer", "cancel")
}

Step "截图：菜单 flyout" {
    Send-Banji @("--cmd", "menu")
    Shot "05-pet-menu.png"
    Send-Banji @("--cmd", "close-windows")
}

Step "截图：花名册选择 flyout" {
    Send-Banji @("--cmd", "roster-picker")
    Shot "05b-roster-picker.png"
    Send-Banji @("--cmd", "close-windows")
}

Step "截图：设置窗口" {
    Send-Banji @("--cmd", "settings")
    Shot "06-settings.png"
    Send-Banji @("--cmd", "close-windows")
}

Step "截图：仪表盘" {
    Send-Banji @("--cmd", "dashboard")
    Shot "07-dashboard.png"
    Send-Banji @("--cmd", "close-windows")
}

Step "截图：花名册管理" {
    Send-Banji @("--cmd", "rosters")
    Shot "08-rosters.png"
    Send-Banji @("--cmd", "close-windows")
}

Step "截图：课表管理" {
    Send-Banji @("--cmd", "timetable")
    Shot "09-timetable.png"
    Send-Banji @("--cmd", "close-windows")
}

Step "截图：课堂时段设置" {
    Send-Banji @("--cmd", "periods")
    Shot "09b-periods.png"
    Send-Banji @("--cmd", "close-windows")
}

Step "截图：上课全屏提示" {
    Send-Banji @("--cmd", "notice", "start")
    Shot "10-notice-start.png"
    Start-Sleep -Seconds 5
}

Step "截图：下课全屏提示" {
    Send-Banji @("--cmd", "notice", "end")
    Shot "11-notice-end.png"
    Start-Sleep -Seconds 5
}

Step "截图：计时器超时模糊" {
    Send-Banji @("--cmd", "notice", "timeout")
    Shot "12-timer-timeout.png"
    Start-Sleep -Seconds 16
}

Step "退出班机" { Qa -Arguments @("stop") }

Write-Host "`n================ 验收汇总 ================" -ForegroundColor Cyan
$report | Out-Host
Write-Host "截图目录: $outDir"
Get-ChildItem $outDir -Filter *.png | Sort-Object Name | Select-Object Name, Length | Out-Host
if ($script:fail -gt 0) {
    Write-Host "`n有 $script:fail 个步骤失败" -ForegroundColor Red
    exit 1
}
Write-Host "`n全部步骤通过 ✅" -ForegroundColor Green
