namespace Banji.Core.Ai;

/// <summary>端侧推理的硬件参数推荐（纯函数，便于单测）。</summary>
public static class AiHardware
{
    /// <summary>
    /// 自动线程数：约定“物理核数的一半”。
    /// .NET 的 <see cref="Environment.ProcessorCount"/> 是逻辑核（含超线程），这里按常见的 2 路超线程估算物理核。
    /// 结果至少 1、至多 8，避免小机器线程过多反而变慢。
    /// </summary>
    public static int AutoThreadCount()
    {
        var logical = Environment.ProcessorCount;
        var physical = logical >= 4 ? logical / 2 : Math.Max(1, logical);
        var threads = Math.Max(1, physical / 2);
        return Math.Clamp(threads, 1, 8);
    }

    /// <summary>设置里 0/负数表示自动；否则按用户值收敛到 [1, 64]。</summary>
    public static int ResolveThreads(int configuredThreads)
    {
        if (configuredThreads <= 0) return AutoThreadCount();
        return Math.Clamp(configuredThreads, 1, 64);
    }

    /// <summary>生成阶段的批大小：Qwen2.5 这类小模型 512 足够，3B 也不会太吃内存。</summary>
    public static int ResolveBatchSize(int contextSize)
    {
        if (contextSize <= 0) return 512;
        // 批大小不超过上下文的一半，至少 32
        return Math.Clamp(contextSize / 2, 32, 1024);
    }
}
