using Banji.Core.IO;
using Banji.Core.Settings;

namespace Banji.Tests;

/// <summary>v1.5.0 设置单测：上课专注两开关默认值 / round-trip / Clone。</summary>
public static class SettingsV15Tests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "banji-v15-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }

    [BanjiTest(Name = "v1.5 上课专注开关默认值均为开")]
    public static void v15上课专注默认值()
    {
        var s = new AppSettings();
        s.Normalize();

        Assert.True(s.AutoHidePetDuringClass, "上课自动隐藏宠物默认开启");
        Assert.True(s.AutoSimplifyTimetableDuringClass, "上课自动简化课表默认开启");
    }

    [BanjiTest(Name = "v1.5 上课专注开关 JSON round-trip")]
    public static void v15上课专注roundtrip()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");

        var s = new AppSettings
        {
            AutoHidePetDuringClass = false,
            AutoSimplifyTimetableDuringClass = false,
            // 课表位置仍以拖动写入这两个字段（ui 不再提供滑块）
            PanelOffsetX = 88,
            PanelOffsetY = -42
        };
        s.Normalize();
        SafeJsonStore.Save(path, s);

        var loaded = SafeJsonStore.Load(path, () => new AppSettings());
        loaded.Normalize();

        Assert.False(loaded.AutoHidePetDuringClass);
        Assert.False(loaded.AutoSimplifyTimetableDuringClass);
        Assert.Equal(88.0, loaded.PanelOffsetX, "拖动的横向偏移仍需持久化");
        Assert.Equal(-42.0, loaded.PanelOffsetY, "拖动的纵向偏移仍需持久化");

        // 默认设置文件里也应是“开”
        var defaults = new AppSettings();
        defaults.Normalize();
        Assert.True(defaults.AutoHidePetDuringClass);
        Assert.True(defaults.AutoSimplifyTimetableDuringClass);
    }

    [BanjiTest(Name = "v1.5 上课专注开关 Clone 后修改互不影响")]
    public static void v15上课专注Clone()
    {
        var original = new AppSettings();
        original.Normalize();
        Assert.True(original.AutoHidePetDuringClass);
        Assert.True(original.AutoSimplifyTimetableDuringClass);

        var clone = original.Clone();
        clone.AutoHidePetDuringClass = false;
        clone.AutoSimplifyTimetableDuringClass = false;
        clone.PanelOffsetX = 120;

        Assert.True(original.AutoHidePetDuringClass, "Clone 修改不应影响原对象");
        Assert.True(original.AutoSimplifyTimetableDuringClass);
        Assert.Equal(0.0, original.PanelOffsetX);

        // 反向：修改原对象不影响已 Clone 的副本
        original.AutoHidePetDuringClass = false;
        Assert.True(clone.AutoHidePetDuringClass == false);
    }
}
