# 班机安装程序（Banji.Installer）

自研的 WPF 安装程序，负责把《班机》主程序、鸿蒙字体、README 和字体许可证安装到当前用户环境。
**不需要管理员权限**：只写当前用户目录、HKCU 注册表和开始菜单/桌面。

## 构建

```powershell
# 开发编译（没有内嵌负载时也能编译，运行时安装会提示“安装包不完整”）
.\.dotnet\dotnet.exe build src\Banji.Installer\Banji.Installer.csproj -c Debug

# 正式构建由 build\build.ps1 完成：先把 payload.zip 放进 Payload\ 目录，再单文件 publish
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

正式发布参数（见 `build/build.ps1`）：

```
dotnet publish src\Banji.Installer\Banji.Installer.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -o build\installer
```

> 注意：安装器使用 `IncludeNativeLibrariesForSelfExtract`（而不是主程序的 `IncludeAllContentForSelfExtract`）。
> 内嵌的 ~135MB 负载（v1.2.0 起主程序内置 LLamaSharp CPU 推理库）如果全部解包到临时目录，首次打开会等待 20 秒以上；只解包原生库、托管程序集直接从单文件加载，首次打开约 10 秒、之后每次 2~3 秒。

## 命令行参数

| 参数 | 说明 |
| --- | --- |
| （无参数） | 显示图形安装界面 |
| `/S` | 静默安装到默认目录 `%LOCALAPPDATA%\Programs\Banji` |
| `/S /D=D:\temp\banji-test` | 静默安装到指定目录；`/D` 必须放最后、路径不加引号 |
| `/uninstall` | 图形界面卸载（中文确认） |
| `/uninstall /S` | 静默卸载 |

- 退出码：`0` 成功，`1` 失败。
- 日志：`%TEMP%\BanjiInstaller.log`（静默安装排错看这里）。
- 静默安装/卸载不显示任何窗口。

## 负载（payload.zip）约定

`build\build.ps1` 生成并拷贝到 `Payload\payload.zip`，作为 `EmbeddedResource` 编进 exe（`Banji.Installer.payload.zip`）。zip 内结构：

```
payload.zip
├─ app\                      → 解压到安装目录根（Banji.exe（含 LLamaSharp 原生库）、Assets\Templates 等）
├─ fonts\                    → 解压到 安装目录\Fonts（6 个 HarmonyOS Sans SC ttf）
├─ README.md                 → 安装目录\README.md
└─ HarmonyOS_Sans_LICENSE.txt→ 安装目录\HarmonyOS_Sans_LICENSE.txt
```

## 安装时做了什么

1. 结束正在运行的 `Banji.exe`（覆盖升级需要）；
2. 解压负载到安装目录（默认 `%LOCALAPPDATA%\Programs\Banji`，v1.2.0 安装后约 260MB）；
3. 把 6 个字体复制到 `%LOCALAPPDATA%\Microsoft\Windows\Fonts`，并写
   `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts`（显示名与字体 Full Name 一致）；
4. 创建桌面快捷方式 `班机.lnk`、开始菜单 `班机\班机.lnk` 和 `班机\卸载班机.lnk`；
5. 把安装器自身复制为安装目录里的 `卸载班机.exe`（同盘时用硬链接，不额外占空间）；
6. 写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Banji` 卸载项
   （DisplayName/DisplayVersion/Publisher/DisplayIcon/InstallLocation/UninstallString/QuietUninstallString 等）；
7. 任何一步失败都会尽力回滚本次新创建的文件、字体注册、快捷方式和注册表项，并给出中文错误提示。

> AI 模型（GGUF）**不随安装包分发**：安装在 `%APPDATA%\Banji\models`，由用户在“设置 → 端侧 AI → 打开模型管理”里下载或选择本地文件；卸载安装版不会删除该目录。

## 卸载时做了什么

删除快捷方式 → 移除用户字体文件和字体注册 → 删除卸载信息 → 删除安装目录（卸载程序自己在目录里时，退出后由一条延迟 cmd 命令删除整个目录）。
**不会删除** `%APPDATA%\Banji` 里的花名册、课表、设置和抽签历史。

## 文件说明

| 文件 | 作用 |
| --- | --- |
| `App.xaml(.cs)` | 应用入口；区分界面安装 / 静默安装 / 卸载；单实例互斥 |
| `MainWindow.xaml(.cs)` | 四页安装界面：欢迎 → 选择目录 → 进度 → 完成 |
| `InstallerEngine.cs` | 安装/卸载核心逻辑、字体/快捷方式/注册表、回滚 |
| `SilentRunner.cs` | `/S`、`/uninstall /S` 静默执行体 |
| `InstallerArgs.cs` | 命令行参数解析 |
| `CleanupScheduler.cs` | 卸载后延迟删除自身所在安装目录 |
| `InstallerLog.cs` | 写 `%TEMP%\BanjiInstaller.log` |
| `Assets\` | 图标、界面用 png、界面内嵌的 Regular/Bold 两款鸿蒙字体 |

## 自测

```powershell
powershell -ExecutionPolicy Bypass -File build\selftest-installer.ps1
```
