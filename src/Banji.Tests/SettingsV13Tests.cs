using Banji.Core.IO;
using Banji.Core.Settings;

namespace Banji.Tests;

/// <summary>v1.3.0 设置单测：气泡提醒/隐藏开关/自动收起折叠的默认值、收敛与 round-trip。</summary>
public static class SettingsV13Tests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "banji-v13-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }

    [BanjiTest(Name = "v1.3 新字段默认值（气泡提醒/隐藏/自动收起折叠）")]
    public static void v13新字段默认值()
    {
        var s = new AppSettings();
        s.Normalize();

        Assert.False(s.HidePet, "HidePet 默认应为 false");
        Assert.True(s.ShowTimetablePanel, "ShowTimetablePanel 默认应为 true");
        Assert.Equal(10, s.PetIdleCollapseMinutes);
        Assert.Equal(10, s.TimetableIdleFoldMinutes);
        Assert.Equal(7, s.NoticeBubbleHoldSeconds, "气泡提醒默认应停留 7 秒");

        // Clone 后新字段值一致（值类型 MemberwiseClone 即深拷贝）
        var clone = s.Clone();
        clone.HidePet = true;
        clone.PetIdleCollapseMinutes = 0;
        clone.NoticeBubbleHoldSeconds = 2;
        Assert.False(s.HidePet);
        Assert.Equal(10, s.PetIdleCollapseMinutes);
        Assert.Equal(7, s.NoticeBubbleHoldSeconds);
    }

    [BanjiTest(Name = "v1.3 自动收起/折叠/气泡秒数越界收敛")]
    public static void v13设置收敛()
    {
        var s = new AppSettings
        {
            PetIdleCollapseMinutes = -5,
            TimetableIdleFoldMinutes = 999,
            NoticeBubbleHoldSeconds = 0
        };
        s.Normalize();
        Assert.Equal(0, s.PetIdleCollapseMinutes, "负数应收敛为 0（关闭）");
        Assert.Equal(240, s.TimetableIdleFoldMinutes, "超大值应收敛到上限 240");
        Assert.Equal(2, s.NoticeBubbleHoldSeconds, "气泡秒数下限为 2");

        s.PetIdleCollapseMinutes = 999;
        s.TimetableIdleFoldMinutes = -1;
        s.NoticeBubbleHoldSeconds = 99;
        s.Normalize();
        Assert.Equal(240, s.PetIdleCollapseMinutes);
        Assert.Equal(0, s.TimetableIdleFoldMinutes);
        Assert.Equal(30, s.NoticeBubbleHoldSeconds, "气泡秒数上限为 30");
    }

    [BanjiTest(Name = "v1.3 设置 JSON round-trip（隐藏/自动收起/气泡秒数）")]
    public static void v13设置roundtrip()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");

        var s = new AppSettings
        {
            HidePet = true,
            ShowTimetablePanel = false,
            PetIdleCollapseMinutes = 0,
            TimetableIdleFoldMinutes = 25,
            NoticeBubbleHoldSeconds = 12
        };
        s.Normalize();

        SafeJsonStore.Save(path, s);
        var loaded = SafeJsonStore.Load(path, () => new AppSettings());
        loaded.Normalize();

        Assert.True(loaded.HidePet);
        Assert.False(loaded.ShowTimetablePanel);
        Assert.Equal(0, loaded.PetIdleCollapseMinutes);
        Assert.Equal(25, loaded.TimetableIdleFoldMinutes);
        Assert.Equal(12, loaded.NoticeBubbleHoldSeconds);
    }
}
