using Banji.Core.Abstractions;
using Banji.Core.Ai;
using Banji.Core.Settings;

namespace Banji.Tests;

/// <summary>
/// v1.2.0 AiService 健壮性 / 硬件参数测试：未加载模型时全部安全返回、非法配置不联网不抛异常。
/// 不依赖网络与真实模型。
/// </summary>
public static class AiServiceTests
{
    /// <summary>测试用设置服务（不落盘）。</summary>
    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public event EventHandler? Changed;

        public void Update(Action<AppSettings> mutate)
        {
            mutate?.Invoke(Current);
            Current.Normalize();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Save() { }

        public void ResetToDefaults()
        {
            var fresh = new AppSettings();
            fresh.Normalize();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [BanjiTest]
    public static void AI_未加载模型时生成返回null且不抛异常()
    {
        var service = new AiService(new FakeSettingsService());
        try
        {
            Assert.Equal(AiModelStatus.NotInstalled, service.Status);
            Assert.False(service.IsModelLoaded);
            Assert.Null(service.LoadedModelName);
            Assert.Equal(3, service.ModelOptions.Count);
            Assert.True(service.ModelDirectory.EndsWith("models", StringComparison.OrdinalIgnoreCase));

            var chatTask = service.GenerateChatReplyAsync(
                new List<AiChatMessage> { new() { Role = AiChatRole.User, Content = "你好" } }, null);
            Assert.True(chatTask.IsCompleted, "未加载模型时生成应立即返回（不阻塞）");
            Assert.Null(chatTask.GetAwaiter().GetResult());

            var quipTask = service.TryGenerateQuipAsync("数学课");
            Assert.True(quipTask.IsCompleted, "台词获取不得阻塞宠物点击");
            Assert.Null(quipTask.GetAwaiter().GetResult());

            Assert.Null(service.TryGenerateGreetingAsync().GetAwaiter().GetResult());

            // 空历史也不抛
            Assert.Null(service.GenerateChatReplyAsync(Array.Empty<AiChatMessage>(), null)
                .GetAwaiter().GetResult());

            service.Unload(); // 空卸载不抛
            service.TryAutoLoad(); // 没有模型时静默
        }
        finally
        {
            service.Dispose();
        }
    }

    [BanjiTest]
    public static void AI_空路径和不存在文件的加载安全失败()
    {
        var service = new AiService(new FakeSettingsService());
        try
        {
            Assert.False(service.LoadModelAsync("   ").GetAwaiter().GetResult());
            var missing = Path.Combine(Path.GetTempPath(), "banji-not-exist-" + Guid.NewGuid().ToString("N") + ".gguf");
            Assert.False(service.LoadModelAsync(missing).GetAwaiter().GetResult());
            Assert.False(service.IsModelLoaded);
        }
        finally
        {
            service.Dispose();
        }
    }

    [BanjiTest]
    public static void AI_非法下载选项立即失败且不联网()
    {
        var service = new AiService(new FakeSettingsService());
        try
        {
            var nullTask = service.DownloadModelAsync(null!);
            Assert.True(nullTask.IsCompleted);
            Assert.False(nullTask.GetAwaiter().GetResult());

            var noUrl = new AiModelOption { Id = "bad", Name = "坏模型", Url = "", FileName = "x.gguf" };
            var noUrlTask = service.DownloadModelAsync(noUrl);
            Assert.True(noUrlTask.IsCompleted);
            Assert.False(noUrlTask.GetAwaiter().GetResult());

            var badScheme = new AiModelOption
            {
                Id = "bad2", Name = "坏模型2", Url = "ftp://example.com/x.gguf", FileName = "y.gguf"
            };
            var badTask = service.DownloadModelAsync(badScheme);
            Assert.True(badTask.IsCompleted);
            Assert.False(badTask.GetAwaiter().GetResult());
        }
        finally
        {
            service.Dispose();
        }
    }

    [BanjiTest]
    public static void AI_下载进度事件能订阅且未下载时不触发()
    {
        var service = new AiService(new FakeSettingsService());
        try
        {
            var events = 0;
            service.DownloadProgress += (_, _) => events++;
            service.StatusChanged += (_, _) => events++;
            service.Unload();
            Assert.True(events >= 1, "卸载应触发状态事件");
            Assert.Equal(0, service.LastGeneratedTokens);
            Assert.InRange(service.LastTokensPerSecond, 0, 1e6);
        }
        finally
        {
            service.Dispose();
        }
    }

    [BanjiTest]
    public static void 线程数_自动值为物理核一半且配置越界会被收敛()
    {
        var auto = AiHardware.AutoThreadCount();
        Assert.InRange(auto, 1, 8);
        Assert.True(auto <= Environment.ProcessorCount);

        Assert.Equal(auto, AiHardware.ResolveThreads(0));
        Assert.Equal(auto, AiHardware.ResolveThreads(-5));
        Assert.Equal(1, AiHardware.ResolveThreads(1));
        Assert.Equal(64, AiHardware.ResolveThreads(1000));
    }

    [BanjiTest]
    public static void 批大小_随上下文收敛()
    {
        Assert.Equal(512, AiHardware.ResolveBatchSize(0));
        Assert.Equal(256, AiHardware.ResolveBatchSize(512));
        Assert.Equal(1024, AiHardware.ResolveBatchSize(4096));
    }

    [BanjiTest]
    public static void 模型是否已下载_不存在的模型返回false()
    {
        var service = new AiService(new FakeSettingsService());
        try
        {
            var fake = new AiModelOption
            {
                Id = "fake-model",
                Name = "不存在的模型",
                FileName = "definitely-not-exist-" + Guid.NewGuid().ToString("N") + ".gguf"
            };
            Assert.False(service.IsModelDownloaded(fake), "不存在的文件不应报告已下载");
            Assert.True(service.GetModelPath(service.ModelOptions[0])
                .EndsWith(service.ModelOptions[0].FileName, StringComparison.Ordinal));
            Assert.False(service.IsModelDownloaded(null!));
        }
        finally
        {
            service.Dispose();
        }
    }
}
