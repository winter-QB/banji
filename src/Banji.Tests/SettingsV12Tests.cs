using Banji.Core.IO;
using Banji.Core.Settings;

namespace Banji.Tests;

/// <summary>v1.2.0 设置单测：分离缩放/课表位置/AI 设置默认值、收敛与 round-trip。</summary>
public static class SettingsV12Tests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "banji-v12-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }

    [BanjiTest(Name = "v1.2 新字段默认值（分离缩放/位置/AI）")]
    public static void v12新字段默认值()
    {
        var s = new AppSettings();
        s.Normalize();

        // 分离缩放与课表位置
        Assert.Equal(1.0, s.UiScale);
        Assert.Equal(1.0, s.PetScale);
        Assert.Equal(1.0, s.PanelScale);
        Assert.Equal(0.0, s.PanelOffsetX);
        Assert.Equal(0.0, s.PanelOffsetY);

        // 端侧 AI 默认值
        Assert.NotNull(s.Ai);
        Assert.Equal("", s.Ai.ModelPath);
        Assert.Equal("", s.Ai.ModelName);
        Assert.Equal("", s.Ai.LastModelId);
        Assert.True(s.Ai.EnableAiQuips, "EnableAiQuips 默认应为 true");
        Assert.True(s.Ai.AutoLoadModel, "AutoLoadModel 默认应为 true");
        Assert.Equal(0, s.Ai.Threads);
        Assert.Equal(2048, s.Ai.ContextSize);
        Assert.Equal(0.8, s.Ai.Temperature);
        Assert.Equal(256, s.Ai.MaxTokens);
    }

    [BanjiTest(Name = "v1.2 缩放/位置/AI 越界收敛")]
    public static void v12设置收敛()
    {
        // 缩放与偏移越界
        var s = new AppSettings { PetScale = 9, PanelScale = 0.1, PanelOffsetX = 999, PanelOffsetY = -999 };
        s.Normalize();
        Assert.Equal(5.0, s.PetScale);
        Assert.Equal(0.4, s.PanelScale);
        Assert.Equal(800.0, s.PanelOffsetX);
        Assert.Equal(-800.0, s.PanelOffsetY);

        // 0/负数缩放回退到 1
        var s2 = new AppSettings { PetScale = 0, PanelScale = -2 };
        s2.Normalize();
        Assert.Equal(1.0, s2.PetScale);
        Assert.Equal(1.0, s2.PanelScale);

        // AI 参数越界
        var s3 = new AppSettings();
        s3.Ai.Temperature = 2.0;
        s3.Ai.ContextSize = 100;
        s3.Ai.MaxTokens = 1;
        s3.Ai.Threads = 100;
        s3.Normalize();
        Assert.Equal(1.5, s3.Ai.Temperature);
        Assert.Equal(512, s3.Ai.ContextSize);
        Assert.Equal(32, s3.Ai.MaxTokens);
        Assert.Equal(64, s3.Ai.Threads);

        s3.Ai.Temperature = -1;
        s3.Ai.ContextSize = 99999;
        s3.Ai.MaxTokens = 99999;
        s3.Ai.Threads = -5;
        s3.Normalize();
        Assert.Equal(0.0, s3.Ai.Temperature);
        Assert.Equal(8192, s3.Ai.ContextSize);
        Assert.Equal(1024, s3.Ai.MaxTokens);
        Assert.Equal(0, s3.Ai.Threads);
    }

    [BanjiTest(Name = "v1.2 设置 JSON round-trip 与 Clone 深拷贝")]
    public static void v12设置roundtrip()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");

        var s = new AppSettings
        {
            PetScale = 2.5,
            PanelScale = 1.75,
            PanelOffsetX = -120,
            PanelOffsetY = 88
        };
        s.Ai.Temperature = 0.35;
        s.Ai.ContextSize = 4096;
        s.Ai.MaxTokens = 512;
        s.Ai.ModelPath = "C:\\models\\qwen2.5-1.5b.gguf";
        s.Ai.ModelName = "Qwen2.5 1.5B";
        s.Ai.LastModelId = "qwen2.5-1.5b";
        s.Ai.EnableAiQuips = false;

        SafeJsonStore.Save(path, s);
        var loaded = SafeJsonStore.Load(path, () => new AppSettings());
        loaded.Normalize();

        Assert.Equal(2.5, loaded.PetScale);
        Assert.Equal(1.75, loaded.PanelScale);
        Assert.Equal(-120.0, loaded.PanelOffsetX);
        Assert.Equal(88.0, loaded.PanelOffsetY);
        Assert.Equal(0.35, loaded.Ai.Temperature);
        Assert.Equal(4096, loaded.Ai.ContextSize);
        Assert.Equal(512, loaded.Ai.MaxTokens);
        Assert.Equal("C:\\models\\qwen2.5-1.5b.gguf", loaded.Ai.ModelPath);
        Assert.Equal("Qwen2.5 1.5B", loaded.Ai.ModelName);
        Assert.Equal("qwen2.5-1.5b", loaded.Ai.LastModelId);
        Assert.False(loaded.Ai.EnableAiQuips);

        // Clone 深拷贝：改副本不影响原对象
        var clone = s.Clone();
        clone.PetScale = 4.0;
        clone.Ai.Temperature = 1.2;
        clone.Ai.ModelPath = "D:\\other.gguf";
        Assert.Equal(2.5, s.PetScale);
        Assert.Equal(0.35, s.Ai.Temperature);
        Assert.Equal("C:\\models\\qwen2.5-1.5b.gguf", s.Ai.ModelPath);
    }
}
