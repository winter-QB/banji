using Banji.Core.Growth;
using Banji.Core.IO;

namespace Banji.Tests;

/// <summary>宠物陪伴养成统计测试（第二轮新增，使用临时目录与假时间，不碰真实数据）。</summary>
public static class GrowthTests
{
    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "banji-growth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "pet-growth.json");
    }

    [BanjiTest]
    public static void 首次使用开始统计()
    {
        var file = TempFile();
        var now = new DateTime(2026, 11, 6, 9, 0, 0);
        var service = new PetGrowthService(file, () => now);
        service.RecordSessionStart();

        Assert.Equal(1, service.Stats.DaysUsed);
        Assert.Equal(1, service.Stats.SessionCount);
        Assert.Equal(0, service.Stats.PatCount);
    }

    [BanjiTest]
    public static void 同一天重复启动不重复累计天数()
    {
        var file = TempFile();
        var now = new DateTime(2026, 11, 6, 9, 0, 0);
        var service = new PetGrowthService(file, () => now);
        service.RecordSessionStart();
        service.RecordSessionStart();
        service.RecordSessionStart();

        Assert.Equal(1, service.Stats.DaysUsed);
        Assert.Equal(3, service.Stats.SessionCount);
    }

    [BanjiTest]
    public static void 跨天累计使用天数()
    {
        var file = TempFile();
        var now = new DateTime(2026, 11, 6, 9, 0, 0);
        var service = new PetGrowthService(file, () => now);
        service.RecordSessionStart();

        now = new DateTime(2026, 11, 7, 8, 0, 0);
        service.RecordSessionStart();
        Assert.Equal(2, service.Stats.DaysUsed);

        now = new DateTime(2026, 11, 10, 8, 0, 0);
        service.RecordSessionStart();
        Assert.Equal(5, service.Stats.DaysUsed);
    }

    [BanjiTest]
    public static void 摸头抽签分组提示分别计数()
    {
        var file = TempFile();
        var service = new PetGrowthService(file, () => new DateTime(2026, 11, 6, 9, 0, 0));
        service.RecordPat();
        service.RecordPat();
        service.RecordPick();
        service.RecordGroup();
        service.RecordNotice();

        Assert.Equal(2, service.Stats.PatCount);
        Assert.Equal(1, service.Stats.PickCount);
        Assert.Equal(1, service.Stats.GroupCount);
        Assert.Equal(1, service.Stats.NoticeCount);
    }

    [BanjiTest]
    public static void 成就按阈值解锁且不重复()
    {
        var file = TempFile();
        var service = new PetGrowthService(file, () => new DateTime(2026, 11, 6, 9, 0, 0));
        service.RecordSessionStart();

        for (var i = 0; i < 9; i++) service.RecordPat();
        Assert.True(service.Achievements.Any(a => a.Id == "first_day"), "第一次摸头应解锁“初次见面”");
        Assert.False(service.Achievements.Any(a => a.Id == "pat_10"), "9 次摸头不应解锁摸头 10 次");

        service.RecordPat();
        Assert.True(service.Achievements.Any(a => a.Id == "pat_10"), "第 10 次摸头应解锁“摸头 10 次”");
        Assert.Equal(service.Stats.Achievements.Count, service.Stats.Achievements.Distinct().Count());
    }

    [BanjiTest]
    public static void 成就与统计会持久化()
    {
        var file = TempFile();
        var now = new DateTime(2026, 11, 6, 9, 0, 0);
        var first = new PetGrowthService(file, () => now);
        first.RecordSessionStart();
        for (var i = 0; i < 12; i++) first.RecordPat();
        first.RecordPick();

        var second = new PetGrowthService(file, () => now);
        Assert.Equal(12, second.Stats.PatCount);
        Assert.Equal(1, second.Stats.PickCount);
        Assert.Equal(1, second.Stats.DaysUsed);
        Assert.True(second.Achievements.Any(a => a.Id == "pat_10"), "重新加载后成就应保留");
    }

    [BanjiTest]
    public static void 重置会清空统计()
    {
        var file = TempFile();
        var service = new PetGrowthService(file, () => new DateTime(2026, 11, 6, 9, 0, 0));
        service.RecordSessionStart();
        service.RecordPat();
        service.RecordPick();
        service.Reset();

        Assert.Equal(0, service.Stats.PatCount);
        Assert.Equal(0, service.Stats.PickCount);
        Assert.Equal(0, service.Stats.Achievements.Count);
        Assert.True(service.Stats.DaysUsed >= 1, "重置后至少算 1 天");

        var reloaded = new PetGrowthService(file, () => new DateTime(2026, 11, 6, 9, 0, 0));
        Assert.Equal(0, reloaded.Stats.PatCount);
    }

    [BanjiTest]
    public static void 成就定义合法且唯一()
    {
        var ids = PetGrowthService.Definitions.Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var def in PetGrowthService.Definitions)
        {
            Assert.False(string.IsNullOrWhiteSpace(def.Title), "成就标题不能为空");
            Assert.False(string.IsNullOrWhiteSpace(def.Description), "成就描述不能为空");
            Assert.NotNull(def.Condition);
        }
    }

    [BanjiTest]
    public static void 损坏的成长数据文件不会崩溃()
    {
        var file = TempFile();
        File.WriteAllText(file, "{ 这不是合法 json ");
        var service = new PetGrowthService(file, () => new DateTime(2026, 11, 6, 9, 0, 0));
        service.RecordPat();
        Assert.Equal(1, service.Stats.PatCount);
        Assert.True(File.Exists(file), "损坏文件隔离后应重新写入");
    }
}
