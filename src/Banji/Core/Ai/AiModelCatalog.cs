using Banji.Core.Abstractions;

namespace Banji.Core.Ai;

/// <summary>
/// 端侧模型目录与纯函数工具（v1.2.0）。
/// 只做“选项定义 / 文件校验 / 文案格式化”等无副作用逻辑，方便单测覆盖；
/// 真正的下载/加载在 <see cref="AiService"/> 里。
/// </summary>
public static class AiModelCatalog
{
    /// <summary>公认的最小 GGUF 体积：小于 10 MB 几乎肯定不是完整模型（防止选错文件）。</summary>
    public const long MinGgufSizeBytes = 10L * 1024 * 1024;

    /// <summary>默认推荐模型 Id（首次打开的引导项）。</summary>
    public const string DefaultModelId = "qwen2.5-0.5b";

    public const string GgufExtension = ".gguf";

    /// <summary>
    /// 三档 Qwen2.5 GGUF 选项（下载源默认 hf-mirror 国内镜像；huggingface.co 国内不可直连）。
    /// 体积为官方仓库近似值（十进制展示由 <see cref="AiModelOption.SizeText"/> 换算）。
    /// </summary>
    public static List<AiModelOption> CreateOptions() => new()
    {
        new()
        {
            Id = "qwen2.5-0.5b",
            Name = "Qwen2.5 0.5B（轻量）",
            Description = "约 469 MB，下载快、内存占用小，适合先试试；8GB 内存也够用",
            SizeBytes = 491_389_000,
            FileName = "qwen2.5-0.5b-instruct-q4_k_m.gguf",
            Url = "https://hf-mirror.com/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf"
        },
        new()
        {
            Id = "qwen2.5-1.5b",
            Name = "Qwen2.5 1.5B（推荐）",
            Description = "约 1.07 GB，中文对话质量明显更好，16GB 内存轻松",
            SizeBytes = 1_117_327_000,
            FileName = "qwen2.5-1.5b-instruct-q4_k_m.gguf",
            Url = "https://hf-mirror.com/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf",
            Recommended = true
        },
        new()
        {
            Id = "qwen2.5-3b",
            Name = "Qwen2.5 3B（高质量）",
            Description = "约 2.01 GB，更聪明但更吃内存/更慢（建议 16GB 内存）",
            SizeBytes = 2_105_000_000,
            FileName = "qwen2.5-3b-instruct-q4_k_m.gguf",
            Url = "https://hf-mirror.com/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf"
        }
    };

    /// <summary>按 Id 查找模型选项（忽略大小写）；找不到返回 null。</summary>
    public static AiModelOption? FindById(IEnumerable<AiModelOption>? options, string? id)
    {
        if (options is null || string.IsNullOrWhiteSpace(id)) return null;
        var trimmed = id.Trim();
        return options.FirstOrDefault(o => string.Equals(o.Id, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按文件名（不含目录、忽略大小写）查找模型选项；找不到返回 null。</summary>
    public static AiModelOption? FindByFileName(IEnumerable<AiModelOption>? options, string? fileName)
    {
        if (options is null || string.IsNullOrWhiteSpace(fileName)) return null;
        var name = Path.GetFileName(fileName.Trim());
        return options.FirstOrDefault(o => string.Equals(o.FileName, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>模型在模型目录下的完整路径。</summary>
    public static string GetModelPath(string modelDirectory, AiModelOption option)
        => Path.Combine(modelDirectory, option.FileName);

    /// <summary>
    /// 校验用户选择的本地 GGUF 文件：
    /// 存在、扩展名 .gguf、体积大于 10 MB。失败时给出中文原因。
    /// </summary>
    public static bool ValidateGgufFile(string? path, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            message = "没有选择模型文件";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            message = "模型文件路径不合法";
            return false;
        }

        if (!File.Exists(full))
        {
            message = "文件不存在，请重新选择";
            return false;
        }

        if (!string.Equals(Path.GetExtension(full), GgufExtension, StringComparison.OrdinalIgnoreCase))
        {
            message = "请选择 .gguf 格式的模型文件";
            return false;
        }

        long size;
        try
        {
            size = new FileInfo(full).Length;
        }
        catch (Exception)
        {
            message = "无法读取模型文件，请检查文件权限";
            return false;
        }

        if (size < MinGgufSizeBytes)
        {
            message = $"文件太小（{FormatSize(size)}），可能不是完整模型或仍在下载中";
            return false;
        }

        return true;
    }

    /// <summary>把字节数格式化成中文友好的大小文案（1024 进制，如 “1.07 GB”）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "未知";
        const double kb = 1024.0;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / gb:0.00} GB";
        if (bytes >= mb) return $"{bytes / mb:0} MB";
        if (bytes >= kb) return $"{bytes / kb:0} KB";
        return $"{bytes} B";
    }

    /// <summary>模型状态的中文文案（UI 用）。</summary>
    public static string StatusText(AiModelStatus status) => status switch
    {
        AiModelStatus.Loading => "加载中…",
        AiModelStatus.Ready => "已就绪",
        AiModelStatus.Failed => "加载失败",
        _ => "未安装"
    };

    /// <summary>下载阶段的中文文案（下载失败/完成等细节由服务端写入 Phase）。</summary>
    public static string DownloadPhaseText(long received, long total)
    {
        if (total <= 0) return "下载中";
        if (received >= total) return "已完成";
        return "下载中";
    }
}
