using Banji.Core.Settings;

namespace Banji.Core.Abstractions;

// ============================================================================
// v1.2.0 端侧 AI 契约（Lead 冻结）
// ============================================================================

public enum AiModelStatus
{
    /// <summary>还没下载/选择模型。</summary>
    NotInstalled = 0,
    /// <summary>正在加载到内存。</summary>
    Loading = 1,
    /// <summary>已就绪，可对话/生成台词。</summary>
    Ready = 2,
    /// <summary>加载或推理失败。</summary>
    Failed = 3
}

public enum AiChatRole
{
    System = 0,
    User = 1,
    Assistant = 2
}

public sealed class AiChatMessage
{
    public AiChatRole Role { get; init; }
    public string Content { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
}

/// <summary>可下载的端侧模型（GGUF）。</summary>
public sealed class AiModelOption
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public long SizeBytes { get; init; }
    /// <summary>下载地址（默认 hf-mirror 国内镜像）。</summary>
    public string Url { get; init; } = "";
    public string FileName { get; init; } = "";
    public bool Recommended { get; init; }

    public string SizeText => SizeBytes <= 0 ? "未知" : $"{SizeBytes / 1024.0 / 1024.0:0} MB";
}

public sealed class AiDownloadProgress
{
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }
    public string Phase { get; init; } = "";
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived * 100.0 / TotalBytes, 0, 100);
}

/// <summary>
/// 端侧生成式 AI：模型管理（下载/加载/卸载）、聊天生成、宠物台词生成。
/// 不需要联网才能聊天；只有下载模型时需要网络。模型加载在后台线程，不阻塞 UI。
/// </summary>
public interface IAiService
{
    AiModelStatus Status { get; }
    bool IsModelLoaded { get; }
    string? LoadedModelName { get; }
    string? LoadedModelPath { get; }
    string ModelDirectory { get; }
    IReadOnlyList<AiModelOption> ModelOptions { get; }
    event EventHandler? StatusChanged;
    /// <summary>下载进度（0~100），仅下载期间触发。</summary>
    event EventHandler<AiDownloadProgress>? DownloadProgress;

    /// <summary>后台加载 GGUF 模型；返回是否成功。</summary>
    Task<bool> LoadModelAsync(string modelPath, CancellationToken cancellationToken = default);
    /// <summary>卸载并释放内存。</summary>
    void Unload();
    /// <summary>启动时若设置里已有模型路径，自动尝试加载（后台，失败不弹窗）。</summary>
    void TryAutoLoad();

    /// <summary>下载模型到 %APPDATA%\Banji\models，并写入设置、自动加载。</summary>
    Task<bool> DownloadModelAsync(AiModelOption option, CancellationToken cancellationToken = default);

    /// <summary>
    /// 生成聊天回复。onDelta 会在生成过程中收到增量文本（可在 UI 线程外调用）。
    /// 未加载模型时返回 null。
    /// </summary>
    Task<string?> GenerateChatReplyAsync(IReadOnlyList<AiChatMessage> history,
        Action<string>? onDelta, CancellationToken cancellationToken = default);

    /// <summary>生成一句宠物台词（短句）。未就绪返回 null，调用方回退本地台词生成器。</summary>
    Task<string?> TryGenerateQuipAsync(string? contextHint = null, CancellationToken cancellationToken = default);

    /// <summary>生成一条“与课堂/科目相关”的短闲聊，供聊天窗口快捷开场。</summary>
    Task<string?> TryGenerateGreetingAsync(CancellationToken cancellationToken = default);
}
