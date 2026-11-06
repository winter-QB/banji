#requires -version 5.1
<#
.SYNOPSIS
    《班机》一键构建脚本：Debug 构建 + 测试 → Release 单文件 publish → 组装负载 → 构建 WPF 安装程序 → 输出 dist。

.DESCRIPTION
    产物：
      dist\班机安装程序.exe    （单文件安装程序，内含主程序、鸿蒙字体、README、字体许可证，可安装/卸载）
      dist\班机-便携版.zip     （免安装绿色版，解压即用）

    用法（在仓库根目录）：
      powershell -ExecutionPolicy Bypass -File build\build.ps1
      powershell -ExecutionPolicy Bypass -File build\build.ps1 -SkipTests     # 跳过测试（开发时快速出包）
      powershell -ExecutionPolicy Bypass -File build\build.ps1 -SkipPortable  # 不生成便携版
      powershell -ExecutionPolicy Bypass -File build\build.ps1 -KeepTemp      # 保留 build\publish 等中间产物
      powershell -ExecutionPolicy Bypass -File build\build.ps1 -SmokeTest     # 构建完自动跑安装器端到端自测

    说明：
      - 统一使用仓库内本地 .NET SDK（.dotnet\dotnet.exe），不依赖系统 dotnet。
      - 构建前会结束正在运行的「班机」进程，避免 exe 被占用。
      - 测试项目退出码非 0 时立即中止。
      - v1.2.0 起主程序内置 LLamaSharp CPU 推理库（llama.dll / ggml*.dll 等原生库），
        单文件发布体积约 209MB；原生库随 IncludeAllContentForSelfExtract 自解压，安装包相应增大约 22MB。
      - 机器上若有无法结束的旧实例（其它权限上下文），脚本会跳过它继续构建（不阻塞打包）。
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipPortable,
    [switch]$KeepTemp,
    [switch]$SmokeTest
)

$ErrorActionPreference = "Stop"
$script:StartTime = Get-Date

# =============================================================================
# 路径
# =============================================================================
$script:Root = Split-Path -Parent $PSScriptRoot
Set-Location $script:Root

$Dotnet        = Join-Path $script:Root ".dotnet\dotnet.exe"
$MainProject   = Join-Path $script:Root "src\Banji\Banji.csproj"
$TestsProject  = Join-Path $script:Root "src\Banji.Tests\Banji.Tests.csproj"
$SlnFile       = Join-Path $script:Root "Banji.sln"
$InstallerProj = Join-Path $script:Root "src\Banji.Installer\Banji.Installer.csproj"

$BuildDir          = Join-Path $script:Root "build"
$PublishDir        = Join-Path $BuildDir "publish"
$PayloadRoot       = Join-Path $BuildDir "payload"
$PayloadZip        = Join-Path $BuildDir "payload.zip"
$PortableDir       = Join-Path $BuildDir "portable"
$InstallerTempDir  = Join-Path $BuildDir "installer"
$InstallerPayload  = Join-Path $script:Root "src\Banji.Installer\Payload\payload.zip"

$DistDir           = Join-Path $script:Root "dist"
$DistInstaller     = Join-Path $DistDir "班机安装程序.exe"
$DistPortable      = Join-Path $DistDir "班机-便携版.zip"
$BuildReport       = Join-Path $BuildDir "构建报告.txt"

$FontsSourceDir    = Join-Path $script:Root "src\Banji\Assets\Fonts"
$FontLicenseSource = Join-Path $script:Root "src\Banji\Assets\Docs\HarmonyOS_Sans_LICENSE.txt"
$ReadmeSource      = Join-Path $script:Root "README.md"

$script:StepNo = 0

# =============================================================================
# 输出辅助
# =============================================================================
function Write-Banner {
    Write-Host ""
    Write-Host "  ============================================================" -ForegroundColor DarkYellow
    Write-Host "     [班机] 一键构建  -  安装程序 + 便携版" -ForegroundColor Yellow
    Write-Host "     时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor DarkGray
    Write-Host "  ============================================================" -ForegroundColor DarkYellow
}

function Write-Section([string]$Text) {
    $script:StepNo++
    Write-Host ""
    Write-Host ("  [{0}] {1}" -f $script:StepNo, $Text) -ForegroundColor Cyan
    Write-Host ("  " + ("-" * 60)) -ForegroundColor DarkGray
}

function Write-Ok([string]$Text)    { Write-Host ("        OK  " + $Text) -ForegroundColor Green }
function Write-Info([string]$Text)  { Write-Host ("        " + $Text) -ForegroundColor Gray }
function Write-Warn([string]$Text)  { Write-Host ("        注意 " + $Text) -ForegroundColor Yellow }

function Fail([string]$Text, [string]$Hint = "") {
    Write-Host ""
    Write-Host "  [构建失败] $Text" -ForegroundColor Red
    if ($Hint) { Write-Host "  $Hint" -ForegroundColor Red }
    Write-Host ""
    Write-Host "  已用时间：$((Get-Date) - $script:StartTime)" -ForegroundColor DarkGray
    exit 1
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ("{0:N2} GB" -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ("{0:N1} MB" -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ("{0:N0} KB" -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

function Get-FileSizeOrZero([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return (Get-Item -LiteralPath $Path).Length }
    return 0
}

function Invoke-Dotnet([string[]]$Arguments, [string]$What) {
    Write-Info ("> dotnet " + ($Arguments -join " "))
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        Fail "$What 失败（dotnet 退出码 $LASTEXITCODE）" "请查看上面的构建输出；如果文件被占用，请先关闭正在运行的班机。"
    }
}

# =============================================================================
# 1. 环境检查
# =============================================================================
Write-Banner

Write-Section "检查构建环境"
if (-not (Test-Path -LiteralPath $Dotnet)) {
    Fail "找不到仓库内本地 .NET SDK：$Dotnet" "请确认 .dotnet 目录完整（本项目统一使用 .dotnet\dotnet.exe）。"
}
$sdkVersion = (& $Dotnet --version 2>$null)
Write-Ok ".NET SDK：$sdkVersion（$Dotnet）"

foreach ($required in @($SlnFile, $MainProject, $TestsProject, $InstallerProj, $ReadmeSource, $FontLicenseSource)) {
    if (-not (Test-Path -LiteralPath $required)) { Fail "缺少必要文件：$required" }
}
Write-Ok "主工程 / 测试工程 / 安装器工程 / README / 字体许可证 均在位"

$fonts = @(Get-ChildItem -LiteralPath $FontsSourceDir -Filter "*.ttf" -ErrorAction SilentlyContinue)
if ($fonts.Count -lt 6) {
    Fail "字体文件不完整：$FontsSourceDir 下应有 6 个 HarmonyOS Sans SC ttf，实际 $($fonts.Count) 个"
}
Write-Ok "鸿蒙字体：$($fonts.Count) 个 ttf"

foreach ($template in @("花名册示例.xlsx", "课程表示例.xlsx")) {
    $path = Join-Path $script:Root ("src\Banji\Assets\Templates\" + $template)
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Warn "缺少样例模板 $template（可用 build\gen_templates.py 重新生成，不影响本次构建）"
    } else {
        Write-Info "样例模板：$template"
    }
}

# 版本号以主工程 csproj 为准，同步给安装器（卸载信息里显示同一个版本）
$appVersion = "1.0.0"
try {
    [xml]$mainProjectXml = Get-Content -LiteralPath $MainProject -Raw
    $versionNode = $mainProjectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    if ($versionNode -and $versionNode.Version) { $appVersion = [string]$versionNode.Version }
} catch {
    Write-Warn "读取主程序版本号失败，使用默认 1.0.0"
}
Write-Ok "版本号：$appVersion"

# =============================================================================
# 2. 结束占用进程
# =============================================================================
Write-Section "结束正在运行的班机 / 安装程序（避免 exe 被占用）"
foreach ($procName in @("Banji", "BanjiInstaller", "班机安装程序", "卸载班机")) {
    $procs = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        Write-Ok "已结束 $procName 进程 $($procs.Count) 个"
    }
}
Write-Ok "进程检查完成"

# =============================================================================
# 3. Debug 构建（含安装器工程，验证整体可编译）
# =============================================================================
Write-Section "Debug 构建主程序与测试"
Invoke-Dotnet @("build", $SlnFile, "-c", "Debug", "-nologo", "-v", "minimal") "Debug 构建 Banji.sln"
Write-Ok "Banji.sln Debug 构建通过"

# =============================================================================
# 4. 运行测试
# =============================================================================
if ($SkipTests) {
    Write-Section "运行测试（已跳过 -SkipTests）"
    Write-Warn "按参数要求跳过测试；正式交付前请不要跳过。"
} else {
    Write-Section "运行单元测试（退出码非 0 立即中止）"
    # 注意：不能给 `dotnet run` 加 -nologo 之类的未知参数，它会被转发给测试程序当成过滤词
    & $Dotnet run --project $TestsProject -c Debug
    $testExit = $LASTEXITCODE
    if ($testExit -ne 0) {
        Fail "测试未通过（退出码 $testExit）" "请先修复测试失败，或先用 -SkipTests 出包做开发期验证。"
    }
    Write-Ok "测试全部通过"
}

# =============================================================================
# 5. 清理中间目录
# =============================================================================
Write-Section "清理旧的构建中间产物"
foreach ($dir in @($PublishDir, $PayloadRoot, $PortableDir, $InstallerTempDir)) {
    if (Test-Path -LiteralPath $dir) {
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if (Test-Path -LiteralPath $PayloadZip) { Remove-Item -LiteralPath $PayloadZip -Force }
if (Test-Path -LiteralPath $InstallerPayload) { Remove-Item -LiteralPath $InstallerPayload -Force }
if (-not (Test-Path -LiteralPath $DistDir)) { New-Item -ItemType Directory -Path $DistDir -Force | Out-Null }
foreach ($old in @($DistInstaller, $DistPortable)) {
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force }
}
Write-Ok "清理完成"

# =============================================================================
# 6. Release publish 主程序（自包含单文件）
# =============================================================================
Write-Section "发布主程序（Release / win-x64 / 自包含单文件，文件较大请稍候）"
Invoke-Dotnet @(
    "publish", $MainProject,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeAllContentForSelfExtract=true",
    "-p:DebugType=None",
    "-o", $PublishDir,
    "-nologo"
) "主程序 publish"

$mainExe = Join-Path $PublishDir "Banji.exe"
if (-not (Test-Path -LiteralPath $mainExe)) {
    Fail "publish 后没有找到 $mainExe" "请检查上面的 publish 输出。"
}
$mainExeSize = Get-FileSizeOrZero $mainExe
Write-Ok ("Banji.exe 生成：{0}" -f (Format-Size $mainExeSize))
$publishFiles = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File)
Write-Info ("发布目录共 {0} 个文件，{1}" -f $publishFiles.Count, (Format-Size (($publishFiles | Measure-Object Length -Sum).Sum)))

# =============================================================================
# 7. 组装安装负载
# =============================================================================
Write-Section "组装安装负载（程序 + 字体 + README + 字体许可证）"
New-Item -ItemType Directory -Path $PayloadRoot -Force | Out-Null
$PayloadApp = Join-Path $PayloadRoot "app"
$PayloadFonts = Join-Path $PayloadRoot "fonts"
New-Item -ItemType Directory -Path $PayloadApp -Force | Out-Null
New-Item -ItemType Directory -Path $PayloadFonts -Force | Out-Null

Copy-Item -Path (Join-Path $PublishDir "*") -Destination $PayloadApp -Recurse -Force
Copy-Item -Path (Join-Path $FontsSourceDir "*.ttf") -Destination $PayloadFonts -Force
Copy-Item -LiteralPath $ReadmeSource -Destination (Join-Path $PayloadRoot "README.md") -Force
Copy-Item -LiteralPath $FontLicenseSource -Destination (Join-Path $PayloadRoot "HarmonyOS_Sans_LICENSE.txt") -Force

# 单文件发布会把 Assets 打进 exe；这里额外在安装目录放一份样例模板，方便用户直接打开查看
$templatesSource = Join-Path $script:Root "src\Banji\Assets\Templates"
if (Test-Path -LiteralPath $templatesSource) {
    $templatesDest = Join-Path $PayloadApp "Assets\Templates"
    New-Item -ItemType Directory -Path $templatesDest -Force | Out-Null
    Copy-Item -Path (Join-Path $templatesSource "*") -Destination $templatesDest -Recurse -Force
    Write-Info "样例模板已同时复制到安装目录 Assets\Templates"
}
# README 里引用的界面截图也放一份，安装目录里的 README 图片才能显示
$screenshotsSource = Join-Path $script:Root "assets\screenshots"
if (Test-Path -LiteralPath $screenshotsSource) {
    $screenshotsDest = Join-Path $PayloadApp "assets\screenshots"
    New-Item -ItemType Directory -Path $screenshotsDest -Force | Out-Null
    Copy-Item -Path (Join-Path $screenshotsSource "*.png") -Destination $screenshotsDest -Force
    Write-Info "README 截图已同时复制到安装目录 assets\screenshots"
}
Write-Ok "负载目录：app\（程序）、fonts\（6 个字体）、README.md、HarmonyOS_Sans_LICENSE.txt"

# =============================================================================
# 8. 压缩 payload.zip
# =============================================================================
Write-Section "压缩安装负载 payload.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$compressStart = Get-Date
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $PayloadRoot,
    $PayloadZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)
$payloadSize = Get-FileSizeOrZero $PayloadZip
Write-Ok ("payload.zip：{0}（耗时 {1:N1} 秒）" -f (Format-Size $payloadSize), ((Get-Date) - $compressStart).TotalSeconds)

# =============================================================================
# 9. 构建安装程序（嵌入负载）
# =============================================================================
Write-Section "构建安装程序（把负载嵌入单文件 exe，约需 1~2 分钟）"
$payloadDir = Split-Path -Parent $InstallerPayload
if (-not (Test-Path -LiteralPath $payloadDir)) { New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null }
Copy-Item -LiteralPath $PayloadZip -Destination $InstallerPayload -Force
if (-not (Test-Path -LiteralPath $InstallerPayload)) {
    Fail "复制安装负载到安装器工程失败：$InstallerPayload"
}
Write-Info "已把 payload.zip 放入安装器工程（EmbeddedResource）"

Invoke-Dotnet @(
    "publish", $InstallerProj,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    # 安装器只需要把原生库解压一次；托管程序集（含内嵌负载）直接从单文件加载，
    # 冷启动比 IncludeAllContentForSelfExtract 快很多；再开单文件压缩减小体积。
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=None",
    "-p:Version=$appVersion",
    "-o", $InstallerTempDir,
    "-nologo"
) "安装程序 publish"

$installerBuilt = Join-Path $InstallerTempDir "BanjiInstaller.exe"
if (-not (Test-Path -LiteralPath $installerBuilt)) {
    Fail "安装程序 publish 后没有找到 $installerBuilt"
}
Copy-Item -LiteralPath $installerBuilt -Destination $DistInstaller -Force
$installerSize = Get-FileSizeOrZero $DistInstaller
Write-Ok ("dist\班机安装程序.exe 生成：{0}" -f (Format-Size $installerSize))

# =============================================================================
# 10. 便携版 zip
# =============================================================================
if ($SkipPortable) {
    Write-Section "生成便携版（已跳过 -SkipPortable）"
    Write-Warn "按参数要求跳过便携版。"
} else {
    Write-Section "生成便携版 dist\班机-便携版.zip"
    New-Item -ItemType Directory -Path $PortableDir -Force | Out-Null
    Copy-Item -Path (Join-Path $PublishDir "*") -Destination $PortableDir -Recurse -Force
    Copy-Item -LiteralPath $ReadmeSource -Destination (Join-Path $PortableDir "README.md") -Force
    Copy-Item -LiteralPath $FontLicenseSource -Destination (Join-Path $PortableDir "HarmonyOS_Sans_LICENSE.txt") -Force
    if (Test-Path -LiteralPath $templatesSource) {
        $templatesDest = Join-Path $PortableDir "Assets\Templates"
        New-Item -ItemType Directory -Path $templatesDest -Force | Out-Null
        Copy-Item -Path (Join-Path $templatesSource "*") -Destination $templatesDest -Recurse -Force
    }
    if (Test-Path -LiteralPath $screenshotsSource) {
        $screenshotsDest = Join-Path $PortableDir "assets\screenshots"
        New-Item -ItemType Directory -Path $screenshotsDest -Force | Out-Null
        Copy-Item -Path (Join-Path $screenshotsSource "*.png") -Destination $screenshotsDest -Force
    }
    if (Test-Path -LiteralPath $DistPortable) { Remove-Item -LiteralPath $DistPortable -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $PortableDir,
        $DistPortable,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )
    Write-Ok ("dist\班机-便携版.zip 生成：{0}" -f (Format-Size (Get-FileSizeOrZero $DistPortable)))
}

# =============================================================================
# 11. 校验产物
# =============================================================================
Write-Section "校验构建产物"
if ($installerSize -lt 50MB) {
    Fail "安装程序体积异常（$((Format-Size $installerSize))），可能没有把负载嵌入进去。"
}
if ($payloadSize -lt 20MB) {
    Fail "payload.zip 体积异常（$((Format-Size $payloadSize))），请检查主程序 publish 结果。"
}
if (-not $SkipPortable -and -not (Test-Path -LiteralPath $DistPortable)) {
    Fail "便携版 zip 没有生成成功。"
}
Write-Ok "产物校验通过"

if ($SmokeTest) {
    Write-Section "安装器端到端自测（-SmokeTest）"
    $selfTestScript = Join-Path $BuildDir "selftest-installer.ps1"
    if (-not (Test-Path -LiteralPath $selfTestScript)) {
        Fail "找不到自测脚本：$selfTestScript"
    }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $selfTestScript
    if ($LASTEXITCODE -ne 0) {
        Fail "安装器端到端自测未通过（退出码 $LASTEXITCODE）" "请查看上面的失败项，或运行 build\selftest-installer.ps1 单独排查。"
    }
    Write-Ok "安装器端到端自测通过"
}

# =============================================================================
# 12. 构建报告
# =============================================================================
$elapsed = (Get-Date) - $script:StartTime
$reportLines = @()
$reportLines += "《班机》构建报告"
$reportLines += "构建时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$reportLines += "总耗时：$([math]::Round($elapsed.TotalSeconds,1)) 秒"
$reportLines += ""
$reportLines += "产物："
$reportLines += "  dist\班机安装程序.exe       $(Format-Size (Get-FileSizeOrZero $DistInstaller))"
if (Test-Path -LiteralPath $DistPortable) {
    $reportLines += "  dist\班机-便携版.zip        $(Format-Size (Get-FileSizeOrZero $DistPortable))"
}
$reportLines += ""
$reportLines += "安装负载 payload.zip：$(Format-Size $payloadSize)"
$reportLines += "主程序 Banji.exe（自包含单文件）：$(Format-Size $mainExeSize)"
$reportLines += "版本：$appVersion"
$reportLines += "字体：HarmonyOS Sans SC Thin/Light/Regular/Medium/Bold/Black（6 个）"
$reportLines += "测试：$(if ($SkipTests) { '已跳过' } else { '通过' })"
$reportLines += "安装器端到端自测：$(if ($SmokeTest) { '通过' } else { '未运行（可用 -SmokeTest）' })"
Set-Content -LiteralPath $BuildReport -Value $reportLines -Encoding UTF8
Write-Ok "构建报告：$BuildReport"

# =============================================================================
# 13. 清理中间产物
# =============================================================================
if (-not $KeepTemp) {
    Write-Section "清理中间产物（保留 dist；如需保留请加 -KeepTemp）"
    foreach ($dir in @($PublishDir, $PayloadRoot, $PortableDir, $InstallerTempDir)) {
        if (Test-Path -LiteralPath $dir) {
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path -LiteralPath $PayloadZip) { Remove-Item -LiteralPath $PayloadZip -Force }
    if (Test-Path -LiteralPath $InstallerPayload) { Remove-Item -LiteralPath $InstallerPayload -Force }
    Write-Ok "清理由 build\publish、payload、installer 产生的中间文件"
}

# =============================================================================
# 完成
# =============================================================================
Write-Host ""
Write-Host "  ============================================================" -ForegroundColor Green
Write-Host "     构建成功！" -ForegroundColor Green
Write-Host "  ============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  交付产物：" -ForegroundColor White
Write-Host "    dist\班机安装程序.exe      $(Format-Size (Get-FileSizeOrZero $DistInstaller))" -ForegroundColor White
if (Test-Path -LiteralPath $DistPortable) {
    Write-Host "    dist\班机-便携版.zip       $(Format-Size (Get-FileSizeOrZero $DistPortable))" -ForegroundColor White
}
Write-Host ""
Write-Host "  自测建议：" -ForegroundColor DarkGray
Write-Host "    1) 双击 dist\班机安装程序.exe 走一遍界面安装/卸载；" -ForegroundColor DarkGray
Write-Host "    2) 静默安装：dist\班机安装程序.exe /S /D=D:\temp\banji-test" -ForegroundColor DarkGray
Write-Host "    3) 静默卸载：安装目录\卸载班机.exe /uninstall /S" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  总耗时：$([math]::Round($elapsed.TotalSeconds,1)) 秒" -ForegroundColor DarkGray
Write-Host ""

exit 0
