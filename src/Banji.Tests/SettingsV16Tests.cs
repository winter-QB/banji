using Banji.Core.IO;
using Banji.Core.Models;
using Banji.Core.Settings;

namespace Banji.Tests;

/// <summary>v1.6.0 设置单测：专注便签样式字段默认值 / 收敛 / JSON round-trip / Clone。</summary>
public static class SettingsV16Tests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "banji-v16-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }

    [BanjiTest(Name = "v1.6 专注便签默认值（左上水平/35%/12%/10）")]
    public static void v16专注便签默认值()
    {
        var s = new AppSettings();
        s.Normalize();

        Assert.Equal(FocusNotePlacement.TopLeftHorizontal, s.FocusNotePlacement);
        Assert.Equal(35, s.FocusNoteLengthPercent);
        Assert.Equal(12, s.FocusNoteDimPercent);
        Assert.Equal(10, s.FocusNoteBlurRadius);
        Assert.Null(s.PanelWindowLeft, "课表独立窗口位置默认自动（null）");
        Assert.Null(s.PanelWindowTop);
        Assert.Null(s.FocusNoteFreeLeft);
        Assert.Null(s.FocusNoteFreeTop);
    }

    [BanjiTest(Name = "v1.6 专注便签参数收敛（20~80 / 0~40 / 0~24）")]
    public static void v16专注便签收敛()
    {
        var s = new AppSettings
        {
            FocusNoteLengthPercent = -5,
            FocusNoteDimPercent = 99,
            FocusNoteBlurRadius = -1
        };
        s.Normalize();
        Assert.Equal(20, s.FocusNoteLengthPercent, "长度下限 20");
        Assert.Equal(40, s.FocusNoteDimPercent, "压暗上限 40");
        Assert.Equal(0, s.FocusNoteBlurRadius, "模糊下限 0");

        s.FocusNoteLengthPercent = 500;
        s.FocusNoteDimPercent = -20;
        s.FocusNoteBlurRadius = 100;
        s.Normalize();
        Assert.Equal(80, s.FocusNoteLengthPercent, "长度上限 80");
        Assert.Equal(0, s.FocusNoteDimPercent, "压暗下限 0");
        Assert.Equal(24, s.FocusNoteBlurRadius, "模糊上限 24");
    }

    [BanjiTest(Name = "v1.6 专注便签/独立窗口位置 JSON round-trip")]
    public static void v16专注便签roundtrip()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");

        var s = new AppSettings
        {
            FocusNotePlacement = FocusNotePlacement.Free,
            FocusNoteLengthPercent = 55,
            FocusNoteDimPercent = 30,
            FocusNoteBlurRadius = 20,
            FocusNoteFreeLeft = 321.5,
            FocusNoteFreeTop = -45.25,
            PanelWindowLeft = 111.5,
            PanelWindowTop = -222.25
        };
        s.Normalize();
        SafeJsonStore.Save(path, s);

        var loaded = SafeJsonStore.Load(path, () => new AppSettings());
        loaded.Normalize();

        Assert.Equal(FocusNotePlacement.Free, loaded.FocusNotePlacement);
        Assert.Equal(55, loaded.FocusNoteLengthPercent);
        Assert.Equal(30, loaded.FocusNoteDimPercent);
        Assert.Equal(20, loaded.FocusNoteBlurRadius);
        Assert.Equal(321.5, loaded.FocusNoteFreeLeft!.Value);
        Assert.Equal(-45.25, loaded.FocusNoteFreeTop!.Value);
        Assert.Equal(111.5, loaded.PanelWindowLeft!.Value, "课表独立窗口的拖动位置需要持久化");
        Assert.Equal(-222.25, loaded.PanelWindowTop!.Value);

        // Clone 后修改互不影响
        var clone = loaded.Clone();
        clone.FocusNotePlacement = FocusNotePlacement.BottomRightVertical;
        clone.FocusNoteLengthPercent = 70;
        clone.PanelWindowLeft = null;
        Assert.Equal(FocusNotePlacement.Free, loaded.FocusNotePlacement);
        Assert.Equal(55, loaded.FocusNoteLengthPercent);
        Assert.Equal(111.5, loaded.PanelWindowLeft!.Value);
    }
}
