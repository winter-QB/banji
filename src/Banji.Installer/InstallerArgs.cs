namespace Banji.Installer;

/// <summary>
/// 安装器命令行参数。
/// 支持：
///   班机安装程序.exe /S                     静默安装到默认目录
///   班机安装程序.exe /S /D=D:\temp\banji     静默安装到指定目录（/D 必须最后且路径不加引号）
///   班机安装程序.exe /uninstall              界面卸载
///   班机安装程序.exe /uninstall /S           静默卸载
/// </summary>
public sealed class InstallerArgs
{
    public bool Silent { get; private set; }
    public bool Uninstall { get; private set; }
    public string? TargetDir { get; private set; }

    public static InstallerArgs Parse(string[] args)
    {
        var result = new InstallerArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = (args[i] ?? "").Trim();
            if (arg.Length == 0) continue;

            if (arg.Equals("/S", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("-S", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("/SILENT", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--silent", StringComparison.OrdinalIgnoreCase))
            {
                result.Silent = true;
                continue;
            }

            if (arg.Equals("/UNINSTALL", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("/U", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                result.Uninstall = true;
                continue;
            }

            // /D= 之后的内容整体视为安装目录（兼容未加引号、带空格的路径）
            if (arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
            {
                result.TargetDir = string.Join(" ", args.Skip(i))[3..].Trim().Trim('"');
                break;
            }
            if (arg.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
            {
                result.TargetDir = arg[6..].Trim().Trim('"');
                continue;
            }
        }
        return result;
    }
}
