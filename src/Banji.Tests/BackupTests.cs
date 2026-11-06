using System.IO.Compression;
using Banji.Core.Backup;
using Banji.Core.Models;
using Banji.Core.IO;

namespace Banji.Tests;

/// <summary>第二轮：一键备份/恢复服务单测（全部在临时目录，不碰真实 %APPDATA%）。</summary>
public static class BackupTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "banji-backup-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string DataDir
        {
            get
            {
                var dir = System.IO.Path.Combine(Path, "data");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }

    private static readonly DateTime TestNow = new(2026, 11, 6, 12, 34, 56);

    [BanjiTest(Name = "备份导出→改文件→恢复 round-trip")]
    public static void 备份导出与恢复往返()
    {
        using var temp = new TempDirectory();
        var dataDir = temp.DataDir;
        var settingsJson = "{\"version\":1,\"备注\":\"原始设置\"}";
        File.WriteAllText(System.IO.Path.Combine(dataDir, "settings.json"), settingsJson);

        var rosters = new List<Roster>
        {
            new()
            {
                Id = "roster-1",
                Name = "测试班",
                Students = new List<Student>
                {
                    new() { Id = "1", Name = "张三" },
                    new() { Id = "2", Name = "李四" }
                }
            }
        };
        File.WriteAllText(System.IO.Path.Combine(dataDir, "rosters.json"),
            System.Text.Json.JsonSerializer.Serialize(rosters, SafeJsonStore.Options));
        File.WriteAllText(System.IO.Path.Combine(dataDir, "timetable.json"), "{\"periods\":[]}");

        var backup = new BackupService(dataDir, () => TestNow);
        var backupFile = System.IO.Path.Combine(temp.Path, "out", "我的备份.banji");

        var size = backup.ExportTo(backupFile);
        Assert.True(size > 0, "备份文件大小应大于 0");
        Assert.True(File.Exists(backupFile), "备份文件应已生成");
        Assert.Null(backup.LastError);

        var info = backup.Inspect(backupFile);
        Assert.Equal(TestNow, info.CreatedAt);
        Assert.Equal(1, info.RosterCount);
        Assert.Equal(2, info.StudentCount);
        Assert.True(info.HasTimetable, "备份应包含课表");
        Assert.True(info.HasSettings, "备份应包含设置");
        Assert.True(info.Files.Contains("settings.json"));
        Assert.True(info.Files.Contains("rosters.json"));
        Assert.True(info.AppVersion.Length > 0);

        // 模拟“改坏/删除本地数据”后从备份恢复
        File.WriteAllText(System.IO.Path.Combine(dataDir, "settings.json"), "{\"version\":999}");
        File.Delete(System.IO.Path.Combine(dataDir, "timetable.json"));

        backup.RestoreFrom(backupFile);
        Assert.Null(backup.LastError);
        Assert.Equal(settingsJson, File.ReadAllText(System.IO.Path.Combine(dataDir, "settings.json")), "设置应恢复成备份里的内容");
        Assert.True(File.Exists(System.IO.Path.Combine(dataDir, "timetable.json")), "课表文件应被恢复");

        // 恢复后的花名册仍可解析
        var restored = SafeJsonStore.Load(System.IO.Path.Combine(dataDir, "rosters.json"), () => new List<Roster>());
        Assert.Equal(1, restored.Count);
        Assert.Equal(2, restored[0].Students.Count);
    }

    [BanjiTest(Name = "备份建议文件名与扩展名")]
    public static void 备份建议文件名()
    {
        var backup = new BackupService(System.IO.Path.GetTempPath(), () => TestNow);
        Assert.Equal(".banji", BackupService.Extension);
        var name = backup.SuggestFileName();
        Assert.True(name.EndsWith(".banji", StringComparison.OrdinalIgnoreCase), "建议文件名应以 .banji 结尾");
        Assert.True(name.Contains("20261106-1234"), "建议文件名应包含时间：" + name);
        Assert.True(BackupService.DataFileNames.Contains("settings.json"));
        Assert.True(BackupService.DataFileNames.Contains("rosters.json"));
        Assert.True(BackupService.DataFileNames.Contains("pet-growth.json"));
    }

    [BanjiTest(Name = "恢复不存在的备份给出中文错误")]
    public static void 恢复不存在的备份报错()
    {
        using var temp = new TempDirectory();
        var backup = new BackupService(temp.DataDir, () => TestNow);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            backup.RestoreFrom(System.IO.Path.Combine(temp.Path, "不存在.banji")));
        Assert.NotNull(backup.LastError);
        Assert.True(backup.LastError!.StartsWith("恢复失败", StringComparison.Ordinal), "LastError 应为中文恢复失败：" + backup.LastError);
        Assert.True(backup.LastError.Contains("找不到"), "应提示找不到文件：" + backup.LastError);
        Assert.True(ex.Message.StartsWith("恢复失败", StringComparison.Ordinal));
    }

    [BanjiTest(Name = "读取损坏的备份文件给出中文错误")]
    public static void 读取损坏备份报错()
    {
        using var temp = new TempDirectory();
        var badFile = System.IO.Path.Combine(temp.Path, "坏文件.banji");
        File.WriteAllText(badFile, "这不是一个 zip 包");
        var backup = new BackupService(temp.DataDir, () => TestNow);

        Assert.Throws<InvalidOperationException>(() => backup.Inspect(badFile));
        Assert.NotNull(backup.LastError);
        Assert.True(backup.LastError!.StartsWith("读取备份失败", StringComparison.Ordinal),
            "LastError 应为中文读取失败：" + backup.LastError);
    }

    [BanjiTest(Name = "旧版无 manifest 备份也能推断名单数量")]
    public static void 无manifest备份推断信息()
    {
        using var temp = new TempDirectory();
        var zipPath = System.IO.Path.Combine(temp.Path, "旧备份.banji");
        var rosters = new List<Roster>
        {
            new()
            {
                Id = "r1",
                Name = "旧版名单",
                Students = new List<Student> { new() { Id = "1", Name = "甲" }, new() { Id = "2", Name = "乙" }, new() { Id = "3", Name = "丙" } }
            },
            new() { Id = "r2", Name = "空名单" }
        };
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("rosters.json");
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(System.Text.Json.JsonSerializer.Serialize(rosters, SafeJsonStore.Options));
            }
            zip.CreateEntry("timetable.json");
        }

        var backup = new BackupService(temp.DataDir, () => TestNow);
        var info = backup.Inspect(zipPath);
        Assert.Equal(2, info.RosterCount);
        Assert.Equal(3, info.StudentCount);
        Assert.True(info.HasTimetable);
        Assert.False(info.HasSettings);
        Assert.Equal("未知", info.AppVersion);
    }

    [BanjiTest(Name = "导出空数据目录也可用，恢复时提示没有数据")]
    public static void 空备份导出与恢复()
    {
        using var temp = new TempDirectory();
        var backup = new BackupService(temp.DataDir, () => TestNow);
        var file = System.IO.Path.Combine(temp.Path, "空备份.banji");
        var size = backup.ExportTo(file);
        Assert.True(size > 0);
        var info = backup.Inspect(file);
        Assert.Equal(0, info.RosterCount);
        Assert.Equal(0, info.StudentCount);
        Assert.False(info.HasSettings);
        Assert.False(info.HasTimetable);

        var ex = Assert.Throws<InvalidOperationException>(() => backup.RestoreFrom(file));
        Assert.True(backup.LastError!.Contains("没有可恢复"), "应提示备份包里没有可恢复数据：" + backup.LastError);
        Assert.True(ex.Message.Length > 0);
    }
}
