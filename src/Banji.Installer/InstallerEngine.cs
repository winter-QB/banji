using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Banji.Installer;

/// <summary>安装/卸载中出现的、可以直接展示给用户的中文异常。</summary>
public sealed class InstallerException : Exception
{
    public InstallerException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record InstallProgress(int Percent, string Message);

public sealed record InstallResult(string InstallDir, IReadOnlyList<string> FontsInstalled);

public sealed record UninstallResult(string InstallDir, bool NeedSelfCleanup, string? SelfPath);

/// <summary>
/// 安装/卸载核心逻辑：解压负载、安装用户字体、创建快捷方式、写 HKCU 卸载信息、回滚。
/// 所有操作都限定在当前用户范围（安装目录 + HKCU），不需要管理员权限。
/// </summary>
public sealed class InstallerEngine
{
    public const string AppName = "班机";
    public const string AppId = "Banji";
    public const string AppExeName = "Banji.exe";
    public const string UninstallerExeName = "卸载班机.exe";
    public const string PayloadResourceName = "Banji.Installer.payload.zip";
    public const string FontRegistryPath = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
    public const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Banji";
    public const string AppRegistryPath = @"Software\Banji";

    /// <summary>字体文件 → Windows 字体注册表显示名（与字体内部 Full Name 一致）。</summary>
    private static readonly (string FileName, string DisplayName)[] FontSpecs =
    {
        ("HarmonyOS_Sans_SC_Thin.ttf", "HarmonyOS Sans SC Thin (TrueType)"),
        ("HarmonyOS_Sans_SC_Light.ttf", "HarmonyOS Sans SC Light (TrueType)"),
        ("HarmonyOS_Sans_SC_Regular.ttf", "HarmonyOS Sans SC (TrueType)"),
        ("HarmonyOS_Sans_SC_Medium.ttf", "HarmonyOS Sans SC Medium (TrueType)"),
        ("HarmonyOS_Sans_SC_Bold.ttf", "HarmonyOS Sans SC Bold (TrueType)"),
        ("HarmonyOS_Sans_SC_Black.ttf", "HarmonyOS Sans SC Black (TrueType)"),
    };

    public static string VersionText
    {
        get
        {
            var v = typeof(InstallerEngine).Assembly.GetName().Version;
            if (v is null) return "1.0.0";
            // 注意：1.0.0.0 的 Build 是 0（而不是“没有设置”），只有 Build < 0 才说明没填到第三段
            return v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
        }
    }

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Banji");

    public static string UserFontsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

    /// <summary>用户数据目录（卸载时保留）。</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId);

    public static bool PayloadAvailable
    {
        get
        {
            try
            {
                using var stream = typeof(InstallerEngine).Assembly.GetManifestResourceStream(PayloadResourceName);
                return stream is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    // =========================================================================
    // 安装
    // =========================================================================

    public InstallResult Install(string targetDir, Action<InstallProgress>? progress = null)
    {
        void Report(int percent, string message) => progress?.Invoke(new InstallProgress(percent, message));

        targetDir = NormalizeTarget(targetDir);
        ValidateTarget(targetDir);
        var dirExisted = Directory.Exists(targetDir);
        var journal = new InstallJournal(targetDir, dirExisted);

        try
        {
            Report(2, "正在检查安装包…");
            using var payload = OpenPayload();

            Report(5, "正在退出正在运行的班机…");
            KillBanjiProcesses(File.Exists(Path.Combine(targetDir, AppExeName)) ? targetDir : null);

            Report(8, "正在准备安装目录…");
            try
            {
                Directory.CreateDirectory(targetDir);
            }
            catch (Exception ex)
            {
                throw new InstallerException($"无法创建安装目录：{targetDir}\n请换一个目录，或检查是否有写入权限。", ex);
            }

            Report(10, "正在解压程序文件…");
            ExtractPayload(payload, targetDir, journal, pct =>
                Report(10 + pct * 5 / 8, "正在解压程序文件…"));

            Report(68, "正在安装鸿蒙字体…");
            var fonts = InstallFonts(targetDir, journal);

            Report(76, "正在创建桌面和开始菜单快捷方式…");
            CreateShortcuts(targetDir, journal);

            Report(84, "正在创建卸载程序…");
            InstallUninstaller(targetDir, journal);

            Report(93, "正在写入卸载信息…");
            WriteUninstallRegistry(targetDir, journal);

            Report(97, "正在收尾…");
            WriteInstallLogToTarget(targetDir);
            BroadcastFontChange();

            Report(100, "安装完成");
            InstallerLog.Info($"安装成功：{targetDir}（字体 {fonts.Count} 个，版本 {VersionText}）");
            return new InstallResult(targetDir, fonts);
        }
        catch (Exception ex)
        {
            Report(0, "安装失败，正在回滚…");
            InstallerLog.Error($"安装失败（{targetDir}），开始回滚", ex);
            try
            {
                journal.Rollback();
                InstallerLog.Info("回滚完成");
            }
            catch (Exception rollbackEx)
            {
                InstallerLog.Error("回滚过程中出现错误", rollbackEx);
            }
            throw ex as InstallerException ?? new InstallerException($"安装失败：{ex.Message}", ex);
        }
    }

    private static Stream OpenPayload()
    {
        var stream = typeof(InstallerEngine).Assembly.GetManifestResourceStream(PayloadResourceName);
        return stream ?? throw new InstallerException(
            "安装包不完整：缺少内置的程序数据。\n请重新获取完整的「班机安装程序.exe」（不要直接使用开发构建的 exe）。");
    }

    /// <summary>按负载目录结构映射到安装目录：app/ → 安装根，fonts/ → 安装目录\Fonts，其它 → 安装根。</summary>
    private static string? MapEntry(string entryName, string targetDir)
    {
        var rel = entryName.Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0 || rel.EndsWith('/')) return null;
        var segments = rel.Split('/');
        if (segments.Any(s => s == ".." || s == ".")) return null;

        string sub;
        if (rel.StartsWith("app/", StringComparison.OrdinalIgnoreCase)) sub = rel[4..];
        else if (rel.StartsWith("fonts/", StringComparison.OrdinalIgnoreCase)) sub = "Fonts/" + rel[6..];
        else sub = rel;

        return Path.Combine(targetDir, sub.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void ExtractPayload(Stream payload, string targetDir, InstallJournal journal, Action<int> onPercent)
    {
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries.Where(e => e.Length > 0 && !string.IsNullOrEmpty(e.Name)).ToList();
        var totalBytes = entries.Sum(e => e.Length);
        long done = 0;
        var lastPercent = -1;
        var buffer = new byte[81920];

        foreach (var entry in entries)
        {
            var destination = MapEntry(entry.FullName, targetDir);
            if (destination is null) continue;

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            journal.RecordFile(destination);
            using (var input = entry.Open())
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                           buffer.Length, FileOptions.SequentialScan))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    done += read;
                    var percent = totalBytes <= 0 ? 100 : (int)(done * 100 / totalBytes);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        onPercent(percent);
                    }
                }
            }
        }
    }

    private static List<string> InstallFonts(string targetDir, InstallJournal journal)
    {
        var installed = new List<string>();
        var sourceDir = Path.Combine(targetDir, "Fonts");
        if (!Directory.Exists(sourceDir))
        {
            InstallerLog.Warn("安装包里没有 Fonts 目录，跳过字体安装。");
            return installed;
        }

        Directory.CreateDirectory(UserFontsDir);
        using var key = Registry.CurrentUser.CreateSubKey(FontRegistryPath, true)
                        ?? throw new InstallerException("无法写入字体注册表（HKCU\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Fonts）。");

        foreach (var (fileName, displayName) in FontSpecs)
        {
            var source = Path.Combine(sourceDir, fileName);
            if (!File.Exists(source))
            {
                InstallerLog.Warn($"缺少字体文件 {fileName}，已跳过。");
                continue;
            }

            var destination = Path.Combine(UserFontsDir, fileName);
            var existed = File.Exists(destination);
            try
            {
                File.Copy(source, destination, overwrite: true);
            }
            catch (IOException)
            {
                // 字体文件可能正被其它程序占用；如果目标已存在就沿用，保证覆盖安装不失败
                if (!File.Exists(destination)) throw;
                InstallerLog.Warn($"字体文件正被占用，沿用已安装的版本：{fileName}");
            }
            journal.RecordFontFile(destination, existed);

            var oldValue = key.GetValue(displayName) as string;
            journal.RecordFontRegistry(displayName, oldValue is not null, oldValue);
            key.SetValue(displayName, destination, RegistryValueKind.String);
            installed.Add(displayName);
        }

        InstallerLog.Info($"已安装 {installed.Count} 个鸿蒙字体到当前用户字体目录。");
        return installed;
    }

    private static void CreateShortcuts(string targetDir, InstallJournal journal)
    {
        var exePath = Path.Combine(targetDir, AppExeName);
        var uninstallerPath = Path.Combine(targetDir, UninstallerExeName);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop))
        {
            var lnk = Path.Combine(desktop, AppName + ".lnk");
            journal.RecordShortcut(lnk);
            CreateShortcut(lnk, exePath, "班机 — 永远置顶的课堂管理桌面宠物");
        }
        else
        {
            InstallerLog.Warn("找不到桌面目录，跳过桌面快捷方式。");
        }

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrEmpty(programs))
        {
            var folder = Path.Combine(programs, AppName);
            journal.RecordCreatedDirectory(folder);
            var startLnk = Path.Combine(folder, AppName + ".lnk");
            journal.RecordShortcut(startLnk);
            CreateShortcut(startLnk, exePath, "班机 — 永远置顶的课堂管理桌面宠物");

            var uninstallLnk = Path.Combine(folder, "卸载班机.lnk");
            journal.RecordShortcut(uninstallLnk);
            CreateShortcut(uninstallLnk, uninstallerPath, "卸载班机", arguments: "/uninstall");
        }
        else
        {
            InstallerLog.Warn("找不到开始菜单目录，跳过开始菜单快捷方式。");
        }
    }

    private static void CreateShortcut(string lnkPath, string targetPath, string description, string? arguments = null)
    {
        var folder = Path.GetDirectoryName(lnkPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var progId = Type.GetTypeFromProgID("WScript.Shell")
                     ?? throw new InstallerException("无法创建快捷方式：系统中缺少 WScript.Shell 组件。");
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(progId)!;
            dynamic wsh = shell;
            dynamic shortcut = wsh.CreateShortcut(lnkPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? "";
            shortcut.Description = description;
            shortcut.IconLocation = targetPath + ",0";
            if (!string.IsNullOrEmpty(arguments)) shortcut.Arguments = arguments;
            shortcut.Save();
        }
        catch (Exception ex)
        {
            throw new InstallerException($"创建快捷方式失败（{Path.GetFileName(lnkPath)}）：{ex.Message}", ex);
        }
        finally
        {
            if (shell is not null)
            {
                try { Marshal.FinalReleaseComObject(shell); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// 把安装器自身复制成安装目录里的「卸载班机.exe」。
    /// 同盘时用硬链接（不额外占空间），否则退回文件复制。
    /// </summary>
    private static void InstallUninstaller(string targetDir, InstallJournal journal)
    {
        var selfPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(selfPath) || !File.Exists(selfPath))
            throw new InstallerException("无法定位安装程序自身，无法创建卸载程序。");

        var destination = Path.Combine(targetDir, UninstallerExeName);
        if (string.Equals(Path.GetFullPath(selfPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return; // 已经是从安装目录运行（例如覆盖安装时）

        var existed = File.Exists(destination);
        journal.RecordFile(destination);
        if (existed)
        {
            try
            {
                File.Delete(destination);
            }
            catch (Exception ex)
            {
                throw new InstallerException("无法更新卸载程序（文件被占用）。请先关闭正在运行的班机，再重试安装。", ex);
            }
        }

        if (!TryCreateHardLink(destination, selfPath))
        {
            try
            {
                File.Copy(selfPath, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                throw new InstallerException($"创建卸载程序失败：{ex.Message}", ex);
            }
        }
    }

    private static void WriteUninstallRegistry(string targetDir, InstallJournal journal)
    {
        var exePath = Path.Combine(targetDir, AppExeName);
        var uninstallerPath = Path.Combine(targetDir, UninstallerExeName);
        var sizeKb = (int)Math.Min(int.MaxValue, GetDirectorySizeKb(targetDir));

        var uninstallKeyExisted = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath) is not null;
        journal.RecordUninstallKey(uninstallKeyExisted);
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, true)
                         ?? throw new InstallerException("无法写入卸载信息注册表。"))
        {
            key.SetValue("DisplayName", AppName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", VersionText, RegistryValueKind.String);
            key.SetValue("Publisher", AppName, RegistryValueKind.String);
            key.SetValue("DisplayIcon", exePath, RegistryValueKind.String);
            key.SetValue("InstallLocation", targetDir, RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{uninstallerPath}\" /uninstall", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" /uninstall /S", RegistryValueKind.String);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
            key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("Comments", "永远置顶的课堂管理桌面宠物：课表提醒 / 公平抽签 / 计时器 / 清醒一下", RegistryValueKind.String);
        }

        var appKeyExisted = Registry.CurrentUser.OpenSubKey(AppRegistryPath) is not null;
        journal.RecordAppKey(appKeyExisted);
        using (var key = Registry.CurrentUser.CreateSubKey(AppRegistryPath, true)
                         ?? throw new InstallerException("无法写入应用注册表信息。"))
        {
            key.SetValue("InstallPath", targetDir, RegistryValueKind.String);
            key.SetValue("Version", VersionText, RegistryValueKind.String);
            key.SetValue("LastInstallDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), RegistryValueKind.String);
        }
    }

    private static void WriteInstallLogToTarget(string targetDir)
    {
        try
        {
            var lines = new StringBuilder();
            lines.AppendLine("班机 安装信息");
            lines.AppendLine($"版本：{VersionText}");
            lines.AppendLine($"安装时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            lines.AppendLine($"安装目录：{targetDir}");
            lines.AppendLine($"数据目录：{DataDir}");
            lines.AppendLine($"安装器日志：{InstallerLog.LogPath}");
            File.WriteAllText(Path.Combine(targetDir, "安装信息.txt"), lines.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            InstallerLog.Warn("写入安装信息失败：" + ex.Message);
        }
    }

    // =========================================================================
    // 卸载
    // =========================================================================

    public static string? FindInstalledDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath);
            if (key?.GetValue("InstallLocation") is string dir && !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir.TrimEnd('\\');
        }
        catch (Exception ex)
        {
            InstallerLog.Warn("读取卸载信息失败：" + ex.Message);
        }

        // 兜底：安装目录里的卸载程序自己运行时，用所在目录
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (!string.IsNullOrEmpty(exeDir) &&
            File.Exists(Path.Combine(exeDir!, AppExeName)))
            return exeDir!.TrimEnd('\\');

        return null;
    }

    public UninstallResult Uninstall(string installDir, Action<InstallProgress>? progress = null)
    {
        void Report(int percent, string message) => progress?.Invoke(new InstallProgress(percent, message));

        installDir = NormalizePath(installDir);
        InstallerLog.Info($"开始卸载：{installDir}");

        Report(3, "正在退出正在运行的班机…");
        KillBanjiProcesses(installDir);

        Report(15, "正在删除快捷方式…");
        DeleteShortcuts();

        Report(30, "正在移除鸿蒙字体…");
        RemoveFonts();

        Report(50, "正在清理注册表信息…");
        DeleteRegistryKeys();

        Report(62, "正在删除程序文件…");
        var selfPath = Environment.ProcessPath;
        var selfInside = !string.IsNullOrEmpty(selfPath) && IsPathInside(selfPath!, installDir);
        bool deleted;
        if (selfInside)
        {
            DeleteDirectoryContentsExcept(installDir, selfPath!);
            deleted = false;
        }
        else
        {
            deleted = TryDeleteDirectory(installDir);
        }

        BroadcastFontChange();
        Report(100, "卸载完成");
        InstallerLog.Info($"卸载完成：{installDir}（需要延迟清理：{selfInside || !deleted}）");
        return new UninstallResult(installDir, selfInside || !deleted, selfInside ? selfPath : null);
    }

    private static void DeleteShortcuts()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop))
            TryDeleteFile(Path.Combine(desktop, AppName + ".lnk"));

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrEmpty(programs))
        {
            var folder = Path.Combine(programs, AppName);
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, true); }
                catch (Exception ex) { InstallerLog.Warn($"删除开始菜单文件夹失败：{ex.Message}"); }
            }
        }
    }

    private static void RemoveFonts()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(FontRegistryPath, true);
            foreach (var (fileName, displayName) in FontSpecs)
            {
                try { key?.DeleteValue(displayName, throwOnMissingValue: false); }
                catch (Exception ex) { InstallerLog.Warn($"删除字体注册项失败 {displayName}：{ex.Message}"); }

                var fontFile = Path.Combine(UserFontsDir, fileName);
                try
                {
                    if (File.Exists(fontFile)) File.Delete(fontFile);
                }
                catch (Exception ex)
                {
                    // 字体仍被其它程序使用时无法删除；注册项已移除，不影响卸载完成
                    InstallerLog.Warn($"删除字体文件失败（可能仍被占用）{fileName}：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            InstallerLog.Warn("打开字体注册表失败：" + ex.Message);
        }
    }

    private static void DeleteRegistryKeys()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            InstallerLog.Warn("删除卸载信息注册表失败：" + ex.Message);
        }
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(AppRegistryPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            InstallerLog.Warn("删除应用注册表失败：" + ex.Message);
        }
    }

    private static void DeleteDirectoryContentsExcept(string directory, string keepPath)
    {
        var keep = Path.GetFullPath(keepPath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var full = Path.GetFullPath(entry);
            if (string.Equals(full, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Directory.Exists(full)) Directory.Delete(full, true);
                else File.Delete(full);
            }
            catch (Exception ex)
            {
                InstallerLog.Warn($"删除 {full} 失败：{ex.Message}");
            }
        }
    }

    private static bool TryDeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(directory, true);
                return !Directory.Exists(directory);
            }
            catch (Exception ex)
            {
                InstallerLog.Warn($"删除目录失败（第 {attempt + 1} 次）：{ex.Message}");
                Thread.Sleep(400);
            }
        }
        return !Directory.Exists(directory);
    }

    // =========================================================================
    // 杂项
    // =========================================================================

    /// <summary>
    /// 结束正在运行的班机进程。传入目录时只结束「该安装目录里的」班机（避免误杀开发用实例或便携版），
    /// 覆盖安装/卸载自己的程序时使用。
    /// </summary>
    public static void KillBanjiProcesses(string? onlyFromDirectory = null)
    {
        foreach (var process in Process.GetProcessesByName("Banji"))
        {
            try
            {
                if (onlyFromDirectory is not null)
                {
                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception ex) { InstallerLog.Warn($"读取班机进程路径失败：{ex.Message}"); }

                    if (string.IsNullOrEmpty(path) || !IsPathInside(path!, onlyFromDirectory))
                    {
                        // 不是这个安装目录里的实例（例如开发调试实例），不动它
                        continue;
                    }
                }

                if (!process.HasExited)
                {
                    InstallerLog.Info($"退出正在运行的班机进程 PID={process.Id}");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                InstallerLog.Warn($"结束班机进程失败：{ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public static string NormalizeTarget(string? path)
    {
        var full = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(path))
            throw new InstallerException("安装目录不能为空，请选择一个安装位置。");
        return full;
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InstallerException("安装目录不能为空，请选择一个安装位置。");
        var value = path.Trim().Trim('"');
        try
        {
            value = Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            throw new InstallerException($"安装目录路径无效：{path}", ex);
        }
        if (value.Length > 3 && (value.EndsWith('\\') || value.EndsWith('/')))
            value = value.TrimEnd('\\', '/');
        return value;
    }

    private static void ValidateTarget(string targetDir)
    {
        if (targetDir.Length > 180)
            throw new InstallerException($"安装目录路径太长（{targetDir.Length} 个字符），请换一个短一点的文件夹。");

        var root = Path.GetPathRoot(targetDir) ?? "";
        if (string.Equals(targetDir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InstallerException("不能直接安装到磁盘根目录，请选择一个文件夹（例如 D:\\班机）。");

        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windowsDir) &&
            (targetDir + "\\").StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InstallerException("不能把班机安装到 Windows 系统目录里。");

        // 磁盘空间检查：程序 + 字体 + 卸载程序（跨盘时）大约需要 500MB
        try
        {
            var rootPath = Path.GetPathRoot(targetDir);
            if (!string.IsNullOrEmpty(rootPath))
            {
                var drive = new DriveInfo(rootPath);
                if (!drive.IsReady)
                    throw new InstallerException($"安装目录所在的磁盘不可用：{rootPath}");
                const long requiredBytes = 600L * 1024 * 1024;
                if (drive.AvailableFreeSpace < requiredBytes)
                {
                    throw new InstallerException(
                        $"磁盘空间不足：安装大约需要 500MB，当前 {rootPath.TrimEnd('\\')} 只剩 " +
                        $"{drive.AvailableFreeSpace / 1024 / 1024} MB。请清理磁盘或换一个安装位置。");
                }
            }
        }
        catch (InstallerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            InstallerLog.Warn("磁盘空间检查失败（忽略）：" + ex.Message);
        }

        if (Directory.Exists(targetDir))
        {
            try
            {
                var probe = Path.Combine(targetDir, ".banji-write-test");
                using (File.Create(probe)) { }
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                throw new InstallerException($"没有写入权限，无法安装到：{targetDir}。\n请换一个目录（建议使用默认位置）。", ex);
            }
            return;
        }

        // 目录还不存在：确认最近的已存在父目录可写
        var parent = Path.GetDirectoryName(targetDir);
        while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            parent = Path.GetDirectoryName(parent);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new InstallerException($"安装目录所在的磁盘或文件夹不存在：{targetDir}");
        try
        {
            var probe = Path.Combine(parent, ".banji-write-test");
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InstallerException($"没有写入权限，无法在下面创建安装目录：{parent}\n请换一个目录（建议使用默认位置）。", ex);
        }
    }

    private static bool IsPathInside(string filePath, string directory)
    {
        var file = Path.GetFullPath(filePath).TrimEnd('\\') + "\\";
        var dir = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        return file.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    private static long GetDirectorySizeKb(string directory)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* ignore */ }
            }
            return total / 1024;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            InstallerLog.Warn($"删除文件失败 {path}：{ex.Message}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            return CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    private static void BroadcastFontChange()
    {
        try
        {
            // 注意：必须用 SendMessageTimeout。同步的 SendMessage(HWND_BROADCAST) 会等待所有顶层窗口
            // 处理 WM_FONTCHANGE，只要有一个窗口不响应消息就会把安装/卸载卡死。
            const int HwndBroadcast = 0xffff;
            const int WmFontChange = 0x001D;
            const uint SmtoAbortIfHung = 0x0002;
            SendMessageTimeout(new IntPtr(HwndBroadcast), WmFontChange, IntPtr.Zero, IntPtr.Zero,
                SmtoAbortIfHung, 1000, out _);
        }
        catch
        {
            // 通知失败不影响结果
        }
    }

    // =========================================================================
    // 回滚记录
    // =========================================================================

    private sealed class InstallJournal
    {
        private readonly string _targetDir;
        private readonly bool _dirExisted;
        private readonly List<(string Path, bool Existed)> _files = new();
        private readonly List<(string Path, bool Existed)> _shortcuts = new();
        private readonly List<string> _createdDirectories = new();
        private readonly List<(string Path, bool Existed)> _fontFiles = new();
        private readonly List<(string ValueName, bool Existed, string? OldValue)> _fontRegistry = new();
        private bool _uninstallKeyExisted;
        private bool _appKeyExisted;

        public InstallJournal(string targetDir, bool dirExisted)
        {
            _targetDir = targetDir;
            _dirExisted = dirExisted;
        }

        public void RecordFile(string path)
        {
            _files.Add((path, File.Exists(path)));
        }

        public void RecordShortcut(string path)
        {
            _shortcuts.Add((path, File.Exists(path)));
        }

        public void RecordCreatedDirectory(string path)
        {
            _createdDirectories.Add(path);
        }

        public void RecordFontFile(string path, bool existed)
        {
            _fontFiles.Add((path, existed));
        }

        public void RecordFontRegistry(string valueName, bool existed, string? oldValue)
        {
            _fontRegistry.Add((valueName, existed, oldValue));
        }

        public void RecordUninstallKey(bool existed) => _uninstallKeyExisted = existed;

        public void RecordAppKey(bool existed) => _appKeyExisted = existed;

        public void Rollback()
        {
            if (!_dirExisted)
            {
                TryDeleteDirectory(_targetDir);
            }
            else
            {
                foreach (var (path, existed) in _files.AsEnumerable().Reverse())
                {
                    if (!existed) TryDeleteFile(path);
                }
            }

            foreach (var (path, existed) in _shortcuts.AsEnumerable().Reverse())
            {
                if (!existed) TryDeleteFile(path);
            }

            foreach (var (path, existed) in _fontFiles.AsEnumerable().Reverse())
            {
                if (!existed) TryDeleteFile(path);
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(FontRegistryPath, true);
                if (key is not null)
                {
                    foreach (var (valueName, existed, oldValue) in _fontRegistry.AsEnumerable().Reverse())
                    {
                        if (existed && oldValue is not null)
                            key.SetValue(valueName, oldValue, RegistryValueKind.String);
                        else
                            key.DeleteValue(valueName, throwOnMissingValue: false);
                    }
                }
            }
            catch (Exception ex)
            {
                InstallerLog.Warn("回滚字体注册表失败：" + ex.Message);
            }

            try
            {
                if (!_uninstallKeyExisted)
                    Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
                if (!_appKeyExisted)
                    Registry.CurrentUser.DeleteSubKeyTree(AppRegistryPath, throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                InstallerLog.Warn("回滚注册表失败：" + ex.Message);
            }

            foreach (var dir in _createdDirectories.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch
                {
                    // 忽略
                }
            }

            BroadcastFontChange();
        }
    }
}
