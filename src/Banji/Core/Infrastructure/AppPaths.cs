namespace Banji.Core.Infrastructure;

/// <summary>应用运行期用到的所有本地路径。</summary>
public static class AppPaths
{
    public const string AppName = "班机";
    public const string AppId = "Banji";

    private static string _root = "";
    private static string _logs = "";

    /// <summary>
    /// %APPDATA%\Banji（正式数据目录）。
    /// 若设置了 BANJI_INSTANCE_SUFFIX 环境变量（仅开发/QA），数据目录隔离为 %APPDATA%\Banji-qa-&lt;suffix&gt;，
    /// 避免并行测试实例与用户正在运行的班机互相覆盖设置/花名册。
    /// </summary>
    public static string Root
    {
        get
        {
            if (string.IsNullOrEmpty(_root))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var suffix = Environment.GetEnvironmentVariable("BANJI_INSTANCE_SUFFIX");
                var folder = AppId;
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    var safe = new string(suffix.Where(char.IsLetterOrDigit).ToArray());
                    if (safe.Length == 0) safe = "qa";
                    folder = $"{AppId}-qa-{safe}";
                }
                _root = Path.Combine(appData, folder);
                Directory.CreateDirectory(_root);
            }
            return _root;
        }
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string RostersFile => Path.Combine(Root, "rosters.json");
    public static string TimetableFile => Path.Combine(Root, "timetable.json");
    public static string OverridesFile => Path.Combine(Root, "overrides.json");
    public static string PickHistoryFile => Path.Combine(Root, "pick-history.json");

    /// <summary>%APPDATA%\Banji\logs</summary>
    public static string LogsDir
    {
        get
        {
            if (string.IsNullOrEmpty(_logs))
            {
                _logs = Path.Combine(Root, "logs");
                Directory.CreateDirectory(_logs);
            }
            return _logs;
        }
    }

    /// <summary>可执行文件所在目录（便携版资源/默认数据根）。</summary>
    public static string AppBaseDir => AppContext.BaseDirectory;

    /// <summary>程序内置资源目录（音频/模板等随 exe 复制）。</summary>
    public static string AssetsDir => Path.Combine(AppBaseDir, "Assets");

    public static string AudioDir => Path.Combine(AssetsDir, "Audio");
    public static string TemplatesDir => Path.Combine(AssetsDir, "Templates");
}
