using System.Diagnostics;
using System.IO;

namespace Banji.Installer;

/// <summary>
/// 卸载程序自己就在要被删除的安装目录里，Windows 不允许删除正在运行的文件。
/// 这里让 cmd 等本进程退出后再删除安装目录（用 Unicode 命令行传中文路径，避免批处理编码问题）。
/// </summary>
internal static class CleanupScheduler
{
    public static void ScheduleDirectoryDelete(string installDir, string? selfPath)
    {
        try
        {
            var dir = installDir.TrimEnd('\\');
            var cmd = "/c timeout /t 3 /nobreak >nul 2>nul & ";
            if (!string.IsNullOrEmpty(selfPath) && File.Exists(selfPath))
            {
                cmd += $"del /f /q \"{selfPath}\" >nul 2>nul & ";
            }
            cmd += $"rmdir /s /q \"{dir}\" >nul 2>nul";

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmd,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            InstallerLog.Info($"已安排延迟清理安装目录：{installDir}");
        }
        catch (Exception ex)
        {
            InstallerLog.Warn("安排延迟清理失败（可手动删除安装目录）：" + ex.Message);
        }
    }
}
