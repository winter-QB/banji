using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Banji.Core.Abstractions;
using Banji.Core.Infrastructure;
using Banji.Core.IO;
using Banji.Core.Models;

namespace Banji.Core.Backup;

/// <summary>
/// 一键备份/恢复全部本地数据（第二轮新增）。
/// 备份文件本质是 zip：manifest.json + settings/rosters/timetable/overrides/pick-history/pet-growth。
/// 恢复后数据文件被覆盖，需要重启应用生效（UI 会提示）。
/// <para>根目录与时间可注入，便于单测在不碰真实 %APPDATA% 的情况下做导出→修改→恢复 round-trip。</para>
/// </summary>
public sealed class BackupService : IBackupService
{
    /// <summary>备份文件扩展名。</summary>
    public const string Extension = ".banji";

    private static readonly string[] DataFiles =
    {
        "settings.json",
        "rosters.json",
        "timetable.json",
        "overrides.json",
        "pick-history.json",
        "pet-growth.json"
    };

    private readonly string _rootDir;
    private readonly Func<DateTime> _now;

    public string? LastError { get; private set; }

    /// <summary>生产用：真实数据目录 + 真实时间。</summary>
    public BackupService() : this(null, null)
    {
    }

    /// <summary>测试/便携用：可注入数据根目录与时间源。</summary>
    public BackupService(string? rootDir, Func<DateTime>? now = null)
    {
        _rootDir = string.IsNullOrWhiteSpace(rootDir) ? AppPaths.Root : rootDir;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>当前备份涉及的数据文件名（只读副本，供 UI/测试展示）。</summary>
    public static IReadOnlyList<string> DataFileNames => DataFiles;

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>建议的默认备份文件名，如“班机备份-20261106-1200.banji”。</summary>
    public string SuggestFileName() => $"班机备份-{_now():yyyyMMdd-HHmm}{Extension}";

    public long ExportTo(string filePath)
    {
        LastError = null;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("备份路径为空。");
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (Directory.Exists(filePath)) throw new IOException("备份路径指向一个文件夹，请换一个文件名。");
            if (File.Exists(filePath)) File.Delete(filePath);

            var included = new List<string>();
            using (var zip = ZipFile.Open(filePath, ZipArchiveMode.Create))
            {
                foreach (var name in DataFiles)
                {
                    var source = Path.Combine(_rootDir, name);
                    if (!File.Exists(source)) continue;
                    zip.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
                    included.Add(name);
                }

                var (rosterCount, studentCount) = ReadRosterCounts(Path.Combine(_rootDir, "rosters.json"));
                var manifest = new BackupInfo
                {
                    CreatedAt = _now(),
                    AppVersion = AppVersion,
                    RosterCount = rosterCount,
                    StudentCount = studentCount,
                    HasTimetable = included.Contains("timetable.json"),
                    HasSettings = included.Contains("settings.json"),
                    Files = included
                };
                var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream);
                writer.Write(JsonSerializer.Serialize(manifest, SafeJsonStore.Options));
            }
            return new FileInfo(filePath).Length;
        }
        catch (Exception ex)
        {
            LastError = "备份失败：" + Friendly(ex);
            Log.Error("导出备份失败", ex);
            throw new InvalidOperationException(LastError, ex);
        }
    }

    public BackupInfo Inspect(string filePath)
    {
        LastError = null;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("备份文件路径为空。");
            if (!File.Exists(filePath)) throw new FileNotFoundException("备份文件不存在。", filePath);
            using var zip = ZipFile.OpenRead(filePath);

            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is not null)
            {
                using var stream = manifestEntry.Open();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var info = JsonSerializer.Deserialize<BackupInfo>(json, SafeJsonStore.Options);
                if (info is not null) return info;
            }

            // 兼容：没有 manifest 的旧包，按包内文件清单/花名册内容推断
            var rostersEntry = zip.GetEntry("rosters.json");
            int rosterCount = 0;
            int studentCount = 0;
            if (rostersEntry is not null)
            {
                try
                {
                    using var stream = rostersEntry.Open();
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    var rosters = JsonSerializer.Deserialize<List<Roster>>(json, SafeJsonStore.Options);
                    if (rosters is not null)
                    {
                        rosterCount = rosters.Count;
                        studentCount = rosters.Sum(r => r.Students.Count);
                    }
                }
                catch
                {
                    // 清单可读性优先：单个文件损坏不影响整体预览
                }
            }

            var files = zip.Entries.Select(e => e.FullName).ToList();
            return new BackupInfo
            {
                CreatedAt = File.GetLastWriteTime(filePath),
                AppVersion = "未知",
                RosterCount = rosterCount,
                StudentCount = studentCount,
                HasTimetable = files.Contains("timetable.json"),
                HasSettings = files.Contains("settings.json"),
                Files = files
            };
        }
        catch (Exception ex)
        {
            LastError = "读取备份失败：" + Friendly(ex);
            Log.Error("读取备份信息失败", ex);
            throw new InvalidOperationException(LastError, ex);
        }
    }

    public void RestoreFrom(string filePath)
    {
        LastError = null;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("备份文件路径为空。");
            if (!File.Exists(filePath)) throw new FileNotFoundException("备份文件不存在。", filePath);
            using var zip = ZipFile.OpenRead(filePath);
            Directory.CreateDirectory(_rootDir);
            var restored = new List<string>();
            foreach (var entry in zip.Entries)
            {
                // 只接受根级数据文件，避免路径穿越
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName != entry.Name) continue;
                if (Array.IndexOf(DataFiles, entry.FullName) < 0) continue;
                var target = Path.Combine(_rootDir, entry.FullName);
                entry.ExtractToFile(target, overwrite: true);
                restored.Add(entry.FullName);
            }
            if (restored.Count == 0)
                throw new InvalidDataException("备份包里没有可恢复的数据文件。");
            Log.Info("已恢复备份文件: " + string.Join(", ", restored));
        }
        catch (Exception ex)
        {
            LastError = "恢复失败：" + Friendly(ex);
            Log.Error("恢复备份失败", ex);
            throw new InvalidOperationException(LastError, ex);
        }
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static (int RosterCount, int StudentCount) ReadRosterCounts(string path)
    {
        try
        {
            if (!File.Exists(path)) return (0, 0);
            var rosters = SafeJsonStore.Load(path, () => new List<Roster>());
            return (rosters.Count, rosters.Sum(r => r.Students.Count));
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>把常见英文异常翻译成用户可读的中文提示。</summary>
    private static string Friendly(Exception ex) => ex switch
    {
        FileNotFoundException => "找不到文件。",
        DirectoryNotFoundException => "找不到目标文件夹。",
        UnauthorizedAccessException => "没有权限访问该文件，请检查路径或改用其它位置。",
        IOException io => string.IsNullOrWhiteSpace(io.Message) ? "文件读写失败，可能被占用。" : io.Message,
        JsonException => "文件内容不是有效的 JSON 数据。",
        InvalidDataException => ex.Message,
        _ => ex.Message
    };
}
