#requires -version 5.1
<#
.SYNOPSIS
    生成桌面统一发布目录 `Desktop\班机Releases\班机 vX.Y.Z Release`（英文资产名：安装程序 / 便携版 / README / 发布说明 / LICENSE / SHA256SUMS / 截图）。

.DESCRIPTION
    先运行 build\build.ps1 生成 dist 产物，再运行本脚本。版本号自动从 src\Banji\Banji.csproj 读取。
    v1.4.0 起发布文件夹内的资产使用**英文文件名**（可直接传到 GitHub Release / 网盘），
    需要旧版中文文件名时加 -ChineseNames。

    用法：
      powershell -ExecutionPolicy Bypass -File build\make-desktop-release.ps1
      powershell -ExecutionPolicy Bypass -File build\make-desktop-release.ps1 -OutDir "D:\发布\班机"
      powershell -ExecutionPolicy Bypass -File build\make-desktop-release.ps1 -NoScreenshots
      powershell -ExecutionPolicy Bypass -File build\make-desktop-release.ps1 -ChineseNames

    输出目录（默认桌面）：
      班机Releases\班机 v1.5.0 Release\
      ├─ Banji-Setup-1.5.0.exe
      ├─ Banji-Portable-1.5.0.zip
      ├─ README.md
      ├─ RELEASE_NOTES-v1.5.0.md
      ├─ LICENSE.txt
      ├─ SHA256SUMS.txt
      ├─ VERSION.txt
      └─ assets\screenshots\*.png
#>
param(
    [string]$OutDir = "",
    [switch]$NoScreenshots,
    [switch]$ChineseNames
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- 版本号：以主工程 csproj 为准 -------------------------------------------------
$version = "1.5.0"
[xml]$mainProjectXml = Get-Content -LiteralPath (Join-Path $root "src\Banji\Banji.csproj") -Raw
$versionNode = $mainProjectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
if ($versionNode -and $versionNode.Version) { $version = [string]$versionNode.Version }

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $desktop = [Environment]::GetFolderPath("Desktop")
    if ([string]::IsNullOrWhiteSpace($desktop)) { throw "无法定位桌面目录，请用 -OutDir 指定输出目录。" }
    $releaseRoot = Join-Path $desktop "班机Releases"
if (-not (Test-Path -LiteralPath $releaseRoot)) { New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null }
$OutDir = Join-Path $releaseRoot ("班机 v{0} Release" -f $version)
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

# ---- 资产文件名（英文；-ChineseNames 保留旧命名）----------------------------------
if ($ChineseNames) {
    $installerName = "班机安装程序.exe"
    $portableName  = "班机-便携版.zip"
    $licenseName   = "HarmonyOS_Sans_LICENSE.txt"
    $infoName      = "版本信息.txt"
} else {
    $installerName = "Banji-Setup-{0}.exe" -f $version
    $portableName  = "Banji-Portable-{0}.zip" -f $version
    $licenseName   = "LICENSE.txt"
    $infoName      = "VERSION.txt"
}
$releaseName = "RELEASE_NOTES-v{0}.md" -f $version

function Write-Step([string]$Text) { Write-Host ""; Write-Host ("== " + $Text) -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host ("   OK  " + $Text) -ForegroundColor Green }
function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ("{0:N2} GB" -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ("{0:N1} MB" -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ("{0:N0} KB" -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

# ---- 输入文件检查 ----------------------------------------------------------------
$installer = Join-Path $root "dist\班机安装程序.exe"
$portable  = Join-Path $root "dist\班机-便携版.zip"
$readme    = Join-Path $root "README.md"
$release   = Join-Path $root $releaseName
$license   = Join-Path $root "src\Banji\Assets\Docs\HarmonyOS_Sans_LICENSE.txt"
$shotsDir  = Join-Path $root "assets\screenshots"

Write-Step "检查发布素材（v$version）"
$required = @(
    @{ Path = $installer; Name = "安装程序 dist\班机安装程序.exe" },
    @{ Path = $portable;  Name = "便携版 dist\班机-便携版.zip" },
    @{ Path = $readme;    Name = "README.md" },
    @{ Path = $release;   Name = $releaseName },
    @{ Path = $license;   Name = "字体许可证 HarmonyOS_Sans_LICENSE.txt" }
)
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath $item.Path)) {
        throw "缺少发布素材：$($item.Name)（$($item.Path)）。请先运行 build\build.ps1。"
    }
    Write-Ok ("{0}（{1}）" -f $item.Name, (Format-Size (Get-Item -LiteralPath $item.Path).Length))
}
if ($NoScreenshots -eq $false -and -not (Test-Path -LiteralPath $shotsDir)) {
    throw "缺少截图目录 assets\screenshots（README 需要）。如确实不需要可用 -NoScreenshots。"
}

# ---- 准备输出目录 ----------------------------------------------------------------
Write-Step "准备输出目录"
if (Test-Path -LiteralPath $OutDir) {
    $parent = Split-Path -Parent $OutDir
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Write-Host ("   已存在，先清空：" + $OutDir) -ForegroundColor Yellow
    Remove-Item -LiteralPath $OutDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$shotsOut = Join-Path $OutDir "assets\screenshots"
if (-not $NoScreenshots) { New-Item -ItemType Directory -Path $shotsOut -Force | Out-Null }
Write-Ok $OutDir

# ---- 复制文件 --------------------------------------------------------------------
Write-Step "复制发布文件"
Copy-Item -LiteralPath $installer -Destination (Join-Path $OutDir $installerName) -Force
Copy-Item -LiteralPath $portable  -Destination (Join-Path $OutDir $portableName) -Force
Copy-Item -LiteralPath $readme    -Destination (Join-Path $OutDir "README.md") -Force
Copy-Item -LiteralPath $release   -Destination (Join-Path $OutDir $releaseName) -Force
Copy-Item -LiteralPath $license   -Destination (Join-Path $OutDir $licenseName) -Force
if (-not $NoScreenshots) {
    Copy-Item -Path (Join-Path $shotsDir "*.png") -Destination $shotsOut -Force
    Write-Ok ("截图 {0} 张 → assets\screenshots" -f @(Get-ChildItem $shotsOut -File).Count)
}
Write-Ok ("{0} / {1} / README.md / {2} / {3}" -f $installerName, $portableName, $releaseName, $licenseName)

# ---- SHA256SUMS ------------------------------------------------------------------
Write-Step "生成 SHA256SUMS.txt"
$sumLines = @()
$hashTargets = @($installerName, $portableName, "README.md", $releaseName, $licenseName)
foreach ($name in $hashTargets) {
    $path = Join-Path $OutDir $name
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumLines += ("{0}  {1}" -f $hash, $name)
}
if (-not $NoScreenshots) {
    Get-ChildItem $shotsOut -File | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $sumLines += ("{0}  assets/screenshots/{1}" -f $hash, $_.Name)
    }
}
$sumFile = Join-Path $OutDir "SHA256SUMS.txt"
Set-Content -LiteralPath $sumFile -Value $sumLines -Encoding UTF8
Write-Ok ("SHA256SUMS.txt（{0} 个文件）" -f $sumLines.Count)

# ---- 版本信息 --------------------------------------------------------------------
$installerSize = (Get-Item -LiteralPath $installer).Length
$portableSize = (Get-Item -LiteralPath $portable).Length
$info = @(
    "班机 v$version 发布文件夹",
    "",
    ("生成时间：{0}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss")),
    ("版本号：v{0}" -f $version),
    ("安装程序：{0}（{1}）" -f $installerName, (Format-Size $installerSize)),
    ("便携版：{0}（{1}）" -f $portableName, (Format-Size $portableSize)),
    "数据目录：%APPDATA%\Banji（升级/卸载不会删除）",
    ("字体许可证：{0}" -f $licenseName),
    "校验：SHA256SUMS.txt",
    "",
    ("安装：双击 {0}，按提示下一步即可（无需管理员权限）。" -f $installerName),
    "SmartScreen 提示：点「更多信息」→「仍要运行」。",
    "升级：直接运行新安装程序覆盖安装，数据保留。"
)
Set-Content -LiteralPath (Join-Path $OutDir $infoName) -Value $info -Encoding UTF8
Write-Ok $infoName

# ---- 完成 ------------------------------------------------------------------------
Write-Step "发布文件夹已生成"
$files = @(Get-ChildItem -LiteralPath $OutDir -Recurse -File)
$total = ($files | Measure-Object Length -Sum).Sum
Write-Host ("   路径：" + $OutDir) -ForegroundColor White
Write-Host ("   文件：{0} 个，合计 {1}" -f $files.Count, (Format-Size $total)) -ForegroundColor White
Write-Host ""
Write-Host "   文件夹结构：" -ForegroundColor DarkGray
Get-ChildItem -LiteralPath $OutDir | ForEach-Object {
    if ($_.PSIsContainer) { Write-Host ("     " + $_.Name + "\") -ForegroundColor DarkGray }
    else { Write-Host ("     " + $_.Name + "  (" + (Format-Size $_.Length) + ")") -ForegroundColor DarkGray }
}
Write-Host ""

exit 0
