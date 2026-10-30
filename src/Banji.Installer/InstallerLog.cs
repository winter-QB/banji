using System.IO;
using System.Text;

namespace Banji.Installer;

/// <summary>安装器日志（写到 %TEMP%\BanjiInstaller.log，静默安装排错用）。</summary>
internal static class InstallerLog
{
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(Path.GetTempPath(), "BanjiInstaller.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex is not null) line += Environment.NewLine + ex;
                File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志写不进去不能影响安装流程
        }
    }
}
