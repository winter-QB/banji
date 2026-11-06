using Banji.Core.Models;
using Banji.Core.Rosters;
using Banji.Core.Settings;
using Banji.Views;

namespace Banji.Tests;

/// <summary>第二轮设置默认值、考试倒计时“最近事件”与积分排行数据单测。</summary>
public static class SettingsRound2Tests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "banji-settings2-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }

    [BanjiTest(Name = "第二轮新增设置项默认值")]
    public static void 第二轮新增设置默认值()
    {
        var s = new AppSettings();
        s.Normalize();

        // 考试倒计时 / 本地台词 / 养成开关默认开启
        Assert.True(s.ShowCountdownOnPet, "ShowCountdownOnPet 默认应为 true");
        Assert.True(s.UseLocalQuipGenerator, "UseLocalQuipGenerator 默认应为 true");
        Assert.True(s.EnableGrowth, "EnableGrowth 默认应为 true");
        Assert.NotNull(s.CountdownEvents, "CountdownEvents 不应为 null");
        Assert.Equal(0, s.CountdownEvents.Count);

        // 宠物第二轮默认：方形扁扁的雾蓝色
        Assert.Equal("#8FB6F2", s.Pet.BodyColor);
        Assert.Equal("#6E96DA", s.Pet.BodyShadeColor);
        Assert.Equal("#B7CFF7", s.Pet.BellyColor);
        Assert.False(s.Pet.ShowCheeks, "不要腮红");

        // 气泡第二轮默认：白底、关闭模糊、0% 透明度 = 完全不透明
        Assert.False(s.Bubble.UseBlur);
        Assert.Equal(0, s.Bubble.OpacityPercent);
        Assert.Equal("#FFFFFF", s.Bubble.BackgroundColor);

        // 版本号至少为 1（Lead 若升级到 2 做迁移，此断言仍成立）
        Assert.True(s.Version >= 1, "Version 应 >= 1");
    }

    [BanjiTest(Name = "考试倒计时：DaysLeft 与最近启用事件")]
    public static void 倒计时最近事件计算()
    {
        var today = new DateOnly(2026, 11, 6);
        var events = new List<CountdownEventItem>
        {
            new() { Name = "已过去", Date = today.AddDays(-10), Enabled = true },
            new() { Name = "禁用未来", Date = today.AddDays(3), Enabled = false },
            new() { Name = "期末", Date = today.AddDays(20), Enabled = true },
            new() { Name = "校运会", Date = today.AddDays(5), Enabled = true },
            new() { Name = "  ", Date = today.AddDays(1), Enabled = true } // 空名应忽略
        };

        var next = DashboardWindow.NextCountdownEvent(events, today);
        Assert.NotNull(next);
        Assert.Equal("校运会", next!.Name, "应取最近的启用未来事件");

        // 今天的事件优先
        events.Add(new CountdownEventItem { Name = "今天", Date = today, Enabled = true });
        Assert.Equal("今天", DashboardWindow.NextCountdownEvent(events, today)!.Name);

        // null / 空 / 全禁用
        Assert.Null(DashboardWindow.NextCountdownEvent(null, today));
        Assert.Null(DashboardWindow.NextCountdownEvent(new List<CountdownEventItem>(), today));
        Assert.Null(DashboardWindow.NextCountdownEvent(events.Where(e => !e.Enabled), today));

        // 只有过去事件时，取最近的过去事件
        var past = new List<CountdownEventItem>
        {
            new() { Name = "两周前", Date = today.AddDays(-14), Enabled = true },
            new() { Name = "三天前", Date = today.AddDays(-3), Enabled = true }
        };
        Assert.Equal("三天前", DashboardWindow.NextCountdownEvent(past, today)!.Name);
    }

    [BanjiTest(Name = "考试倒计时：天数计算与克隆独立")]
    public static void 倒计时天数与克隆()
    {
        var today = new DateOnly(2026, 11, 6);
        Assert.Equal(0, new CountdownEventItem { Name = "今天", Date = today }.DaysLeft(today));
        Assert.Equal(1, new CountdownEventItem { Name = "明天", Date = today.AddDays(1) }.DaysLeft(today));
        Assert.Equal(-2, new CountdownEventItem { Name = "前天", Date = today.AddDays(-2) }.DaysLeft(today));

        var item = new CountdownEventItem { Name = "期末", Date = today, Color = "#FF6B6B" };
        var copy = item.Clone();
        copy.Name = "改名";
        copy.Color = "#22C55E";
        Assert.Equal("期末", item.Name, "Clone 不应影响原对象");
        Assert.Equal("#FF6B6B", item.Color);
    }

    [BanjiTest(Name = "设置 Normalize 清理空名倒计时项")]
    public static void 倒计时列表规范化()
    {
        var s = new AppSettings
        {
            CountdownEvents = new List<CountdownEventItem>
            {
                new() { Name = "期末", Date = new DateOnly(2027, 1, 10), Color = "FF6B6B" },
                new() { Name = "", Date = new DateOnly(2027, 1, 11) },
                new() { Name = "   ", Date = new DateOnly(2027, 1, 12) }
            }
        };
        s.Normalize();
        Assert.Equal(1, s.CountdownEvents.Count);
        Assert.Equal("#FF6B6B", s.CountdownEvents[0].Color, "颜色应补齐 # 前缀");
        Assert.True(s.CountdownEvents[0].DaysLeft(new DateOnly(2026, 11, 6)) > 0);

        // Clone 深拷贝倒计时列表
        var clone = s.Clone();
        clone.CountdownEvents[0].Name = "改过的名字";
        Assert.Equal("期末", s.CountdownEvents[0].Name);
    }

    [BanjiTest(Name = "积分排行数据：加减分/排序/持久化/清零")]
    public static void 积分排行数据()
    {
        using var temp = new TempDirectory();
        var file = System.IO.Path.Combine(temp.Path, "rosters.json");
        var now = new DateTime(2026, 11, 6, 8, 0, 0);
        var svc = new RosterService(null, file, () => now);

        var roster = svc.Add(new Roster
        {
            Name = "测试班",
            Students = new List<Student>
            {
                new() { Id = "1", Name = "张三" },
                new() { Id = "2", Name = "李四" },
                new() { Id = "3", Name = "王五" }
            }
        });

        svc.AddPoints(roster.Id, "1", 5);
        svc.AddPoints(roster.Id, "1", 2);
        svc.AddPoints(roster.Id, "2", 10);

        var top = svc.TopPoints(roster.Id, 5);
        Assert.Equal(3, top.Count, "TopPoints 应返回全部学生（含 0 分）");
        Assert.Equal("2", top[0].StudentId);
        Assert.Equal(10, top[0].Points);
        Assert.Equal("1", top[1].StudentId);
        Assert.Equal(7, top[1].Points);
        Assert.Equal(0, top[2].Points);
        Assert.Equal("王五", top[2].StudentName);

        // 每人只取前 5：传 count=2 时只返回 2 条
        Assert.Equal(2, svc.TopPoints(roster.Id, 2).Count);

        // 持久化：重新加载后积分仍在
        var reloaded = new RosterService(null, file, () => now);
        var reloadedTop = reloaded.TopPoints(roster.Id, 1);
        Assert.Equal(1, reloadedTop.Count);
        Assert.Equal(10, reloadedTop[0].Points);

        // 清零
        svc.ResetPoints(roster.Id);
        Assert.True(svc.TopPoints(roster.Id, 5).All(p => p.Points == 0), "清零后所有积分应为 0");
    }

    [BanjiTest(Name = "积分操作错误提示为中文且不破坏数据")]
    public static void 积分错误提示中文()
    {
        using var temp = new TempDirectory();
        var svc = new RosterService(null, System.IO.Path.Combine(temp.Path, "rosters.json"), () => new DateTime(2026, 11, 6));
        var roster = svc.Add(new Roster { Name = "测试班", Students = new List<Student> { new() { Id = "1", Name = "张三" } } });

        var ex1 = Assert.Throws<InvalidOperationException>(() => svc.AddPoints(roster.Id, "不存在", 1));
        Assert.True(ex1.Message.Contains("找不到该学生"), "应为中文学生不存在提示：" + ex1.Message);

        var ex2 = Assert.Throws<InvalidOperationException>(() => svc.AddPoints("不存在名单", "1", 1));
        Assert.True(ex2.Message.Contains("花名册"), "应为中文花名册不存在提示：" + ex2.Message);

        // 分值仍为 0，数据未被破坏
        Assert.Equal(0, svc.TopPoints(roster.Id, 1)[0].Points);
    }
}
