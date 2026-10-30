namespace Banji.Installer;

/// <summary>静默模式（/S、/uninstall /S）执行体：不显示任何窗口，用退出码反馈结果。</summary>
internal static class SilentRunner
{
    public static int RunInstall(string? targetDir)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(targetDir) ? InstallerEngine.DefaultInstallDir : targetDir!;
            InstallerLog.Info($"静默安装开始 → {dir}");
            var engine = new InstallerEngine();
            var result = engine.Install(dir, p => InstallerLog.Info($"  {p.Percent,3}% {p.Message}"));
            InstallerLog.Info($"静默安装成功 → {result.InstallDir}（字体 {result.FontsInstalled.Count} 个）");
            return 0;
        }
        catch (Exception ex)
        {
            InstallerLog.Error("静默安装失败", ex);
            return 1;
        }
    }

    public static int RunUninstall()
    {
        try
        {
            var dir = InstallerEngine.FindInstalledDir();
            if (dir is null)
            {
                InstallerLog.Error("静默卸载失败：没有找到已安装的班机。");
                return 1;
            }

            InstallerLog.Info($"静默卸载开始 → {dir}");
            var engine = new InstallerEngine();
            var result = engine.Uninstall(dir, p => InstallerLog.Info($"  {p.Percent,3}% {p.Message}"));
            if (result.NeedSelfCleanup)
            {
                CleanupScheduler.ScheduleDirectoryDelete(result.InstallDir, result.SelfPath);
            }
            InstallerLog.Info("静默卸载成功");
            return 0;
        }
        catch (Exception ex)
        {
            InstallerLog.Error("静默卸载失败", ex);
            return 1;
        }
    }
}
