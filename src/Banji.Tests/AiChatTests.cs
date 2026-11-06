using Banji.Core.Abstractions;
using Banji.Core.Ai;
using Banji.Core.Settings;

namespace Banji.Tests;

/// <summary>
/// v1.2.0 端侧 AI 纯函数测试：ChatML prompt、历史截断、台词清洗、模型目录/校验、下载进度。
/// 不依赖网络与真实模型。
/// </summary>
public static class AiChatTests
{
    // =========================================================================
    // ChatML prompt
    // =========================================================================

    [BanjiTest]
    public static void ChatML_包含系统用户助手标记且以助手开头结尾()
    {
        var history = new List<AiChatMessage>
        {
            new() { Role = AiChatRole.User, Content = "你好" }
        };

        var prompt = AiPromptBuilder.BuildChatPrompt(history, 512, AiPromptBuilder.EstimateTokens,
            out var dropped);

        Assert.Equal(0, dropped);
        Assert.True(prompt.Contains("<|im_start|>system\n"), "缺少 system 段");
        Assert.True(prompt.Contains("<|im_start|>user\n你好<|im_end|>"), "user 段格式不对");
        Assert.True(prompt.EndsWith("<|im_start|>assistant\n"), "prompt 结尾必须是 assistant 开头");
        Assert.True(prompt.Contains("班机"), "系统提示词应包含宠物身份「班机」");
        Assert.True(prompt.Contains("中文"), "系统提示词应要求中文回答");
    }

    [BanjiTest]
    public static void ChatML_多轮历史按时间顺序拼接()
    {
        var history = new List<AiChatMessage>
        {
            new() { Role = AiChatRole.User, Content = "第一问" },
            new() { Role = AiChatRole.Assistant, Content = "第一答" },
            new() { Role = AiChatRole.User, Content = "第二问" }
        };

        var prompt = AiPromptBuilder.BuildChatPrompt(history, 1024, AiPromptBuilder.EstimateTokens, out _);
        var first = prompt.IndexOf("第一问", StringComparison.Ordinal);
        var second = prompt.IndexOf("第一答", StringComparison.Ordinal);
        var third = prompt.IndexOf("第二问", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first && third > second, "历史消息顺序不对");
        Assert.True(prompt.Contains("<|im_start|>assistant\n第一答<|im_end|>"));
    }

    [BanjiTest]
    public static void ChatML_历史超长时丢弃最旧消息并保留最新()
    {
        var history = new List<AiChatMessage>();
        for (var i = 0; i < 30; i++)
        {
            history.Add(new AiChatMessage
            {
                Role = i % 2 == 0 ? AiChatRole.User : AiChatRole.Assistant,
                Content = $"第{i}条：这是一段用于测试截断的较长对话内容，包含一些中文文字。"
            });
        }

        const int budget = 260;
        var prompt = AiPromptBuilder.BuildChatPrompt(history, budget, AiPromptBuilder.EstimateTokens,
            out var dropped);

        Assert.True(dropped > 0, "应该丢弃了旧消息");
        Assert.True(prompt.Contains("第29条"), "最新消息必须保留");
        Assert.False(prompt.Contains("第0条"), "最旧消息应被丢弃");
        Assert.True(AiPromptBuilder.EstimateTokens(prompt) <= budget + 8,
            $"prompt 超预算：{AiPromptBuilder.EstimateTokens(prompt)} > {budget}");
    }

    [BanjiTest]
    public static void ChatML_单条超长消息会被截断并保留开头()
    {
        var big = new string('学', 600);
        var history = new List<AiChatMessage> { new() { Role = AiChatRole.User, Content = big } };

        const int budget = 300;
        var prompt = AiPromptBuilder.BuildChatPrompt(history, budget, AiPromptBuilder.EstimateTokens,
            out var dropped);

        Assert.Equal(0, dropped);
        Assert.True(prompt.Contains('…'), "超长内容截断后应有省略号");
        Assert.True(prompt.Contains('学'), "截断后应保留开头内容");
        Assert.True(AiPromptBuilder.EstimateTokens(prompt) <= budget + 8);
    }

    [BanjiTest]
    public static void ChatML_系统消息会合并进系统段()
    {
        var history = new List<AiChatMessage>
        {
            new() { Role = AiChatRole.System, Content = "额外人设：喜欢数学" },
            new() { Role = AiChatRole.User, Content = "在吗" }
        };
        var prompt = AiPromptBuilder.BuildChatPrompt(history, 1024, AiPromptBuilder.EstimateTokens, out _);
        Assert.True(prompt.Contains("额外人设：喜欢数学"));
        // 历史里的 system 消息不应作为单独一条 user/assistant 出现
        Assert.Equal(1, CountOccurrences(prompt, "<|im_start|>system"));
    }

    [BanjiTest]
    public static void Token预算_按上下文与回复长度计算且有下限()
    {
        Assert.Equal(2048 - 256 - AiPromptBuilder.SafetyMarginTokens,
            AiPromptBuilder.ComputePromptBudget(2048, 256));
        Assert.Equal(AiPromptBuilder.MinPromptTokenBudget, AiPromptBuilder.ComputePromptBudget(100, 200));
    }

    [BanjiTest]
    public static void 截断函数_按token预算保留开头()
    {
        var text = new string('数', 500);
        var truncated = AiPromptBuilder.TruncateToTokenBudget(text, 20);
        Assert.True(truncated.Length < text.Length);
        Assert.True(truncated.EndsWith('…'));
        Assert.True(AiPromptBuilder.EstimateTokens(truncated) <= 20);
        Assert.Equal("", AiPromptBuilder.TruncateToTokenBudget(null, 10));
    }

    [BanjiTest]
    public static void Token估算_中文约一字一token且非空至少1()
    {
        Assert.Equal(0, AiPromptBuilder.EstimateTokens(""));
        Assert.Equal(0, AiPromptBuilder.EstimateTokens(null));
        Assert.InRange(AiPromptBuilder.EstimateTokens("你好"), 1, 4);
        var longText = new string('好', 100);
        Assert.InRange(AiPromptBuilder.EstimateTokens(longText), 90, 130);
    }

    // =========================================================================
    // 台词 / 回复清洗
    // =========================================================================

    [BanjiTest]
    public static void 台词解析_去编号引号且过滤超长和重复()
    {
        var raw = string.Join('\n', new[]
        {
            "1. 数学课别走神，我在看着你",
            "2、“下课啦，去接杯水吧！”",
            "3. 这道题不会就先跳过，别硬磕",
            "4. 今天也要好好吃饭",
            "5. 这行太长了这行太长了这行太长了这行太长了这行太长了这行太长了这行太长了这行太长了这行太长了",
            "6. meaningless english line",
            "7. 数学课别走神，我在看着你"
        });

        var lines = AiTextSanitizer.ParseQuipLines(raw, 5, 30);
        Assert.True(lines.Count >= 3, "至少应解析出 3 条");
        Assert.True(lines.Count <= 5, "最多 5 条");
        foreach (var line in lines)
        {
            Assert.True(line.Length <= 30, "台词超长：" + line);
            Assert.False(line.StartsWith('1') || line.StartsWith('2') || line.StartsWith('“'), "编号/引号未清理：" + line);
            Assert.True(line.Any(c => c >= 0x4E00 && c <= 0x9FFF), "台词应包含中文：" + line);
        }
        Assert.Equal(lines.Count, lines.Distinct().Count());
        Assert.False(lines.Any(l => l.Contains("meaningless")), "不应包含纯英文行");
    }

    [BanjiTest]
    public static void 台词解析_单行内编号会被拆开()
    {
        var raw = "1. 上课要坐直 2. 记得喝水 3. 别熬夜";
        var lines = AiTextSanitizer.ParseQuipLines(raw, 5, 30);
        Assert.True(lines.Count >= 2, "单行内编号应能拆出多条");
    }

    [BanjiTest]
    public static void 回复清洗_去掉ChatML标记与角色前缀()
    {
        Assert.Equal("你好呀", AiTextSanitizer.CleanAssistantText("assistant:\n你好呀<|im_end|>"));
        Assert.Equal("你好世界", AiTextSanitizer.CleanReply("你好<|im_start|>世界"));
        Assert.Equal("", AiTextSanitizer.CleanReply(null));
    }

    [BanjiTest]
    public static void 长度限制_超长截断加省略号()
    {
        var limited = AiTextSanitizer.LimitLength("一二三四五六七八九十", 5);
        Assert.True(limited.Length <= 6);
        Assert.True(limited.EndsWith('…'));
        Assert.Equal("短句", AiTextSanitizer.LimitLength("短句", 10));
    }

    // =========================================================================
    // 模型目录 / 选项 / 校验
    // =========================================================================

    [BanjiTest]
    public static void 模型选项_三档配置合法()
    {
        var options = AiModelCatalog.CreateOptions();
        Assert.Equal(3, options.Count);
        Assert.Equal(3, options.Select(o => o.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(options.All(o => o.Url.StartsWith("https://hf-mirror.com/", StringComparison.Ordinal)),
            "下载源应使用 hf-mirror 国内镜像");
        Assert.True(options.All(o => o.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)));
        Assert.True(options.All(o => o.SizeBytes > 0));
        Assert.Equal(1, options.Count(o => o.Recommended));
        Assert.True(AiModelCatalog.FindById(options, "qwen2.5-1.5b") is not null);
        Assert.True(AiModelCatalog.FindById(options, "not-exist") is null);
        Assert.True(AiModelCatalog.FindByFileName(options, "QWEN2.5-3B-INSTRUCT-Q4_K_M.GGUF") is not null);
        Assert.Equal("469 MB", options[0].SizeText);
    }

    [BanjiTest]
    public static void 模型文件校验_不存在过小和扩展名都返回中文原因()
    {
        var dir = Path.Combine(Path.GetTempPath(), "banji-ai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(AiModelCatalog.ValidateGgufFile(null, out var nullMsg));
            Assert.True(nullMsg.Length > 0);

            var missing = Path.Combine(dir, "missing.gguf");
            Assert.False(AiModelCatalog.ValidateGgufFile(missing, out var missingMsg));
            Assert.True(missingMsg.Contains("不存在"));

            var small = Path.Combine(dir, "small.gguf");
            File.WriteAllBytes(small, new byte[1024]);
            Assert.False(AiModelCatalog.ValidateGgufFile(small, out var smallMsg));
            Assert.True(smallMsg.Contains("太小"), "过小文件应提示太小，实际：" + smallMsg);

            var wrongExt = Path.Combine(dir, "model.bin");
            using (var fs = File.Create(wrongExt)) fs.SetLength(AiModelCatalog.MinGgufSizeBytes + 1);
            Assert.False(AiModelCatalog.ValidateGgufFile(wrongExt, out var extMsg));
            Assert.True(extMsg.Contains(".gguf"));

            var ok = Path.Combine(dir, "ok.gguf");
            using (var fs = File.Create(ok)) fs.SetLength(AiModelCatalog.MinGgufSizeBytes + 1);
            Assert.True(AiModelCatalog.ValidateGgufFile(ok, out _), "合法 gguf 应通过校验");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [BanjiTest]
    public static void 模型路径与大小格式化()
    {
        var option = new AiModelOption { FileName = "a.gguf" };
        Assert.Equal(Path.Combine(@"C:\models", "a.gguf"), AiModelCatalog.GetModelPath(@"C:\models", option));
        Assert.Equal("500 MB", AiModelCatalog.FormatSize(500L * 1024 * 1024));
        Assert.Equal("1.00 GB", AiModelCatalog.FormatSize(1024L * 1024 * 1024));
        Assert.Equal("未知", AiModelCatalog.FormatSize(0));
        Assert.Equal("已就绪", AiModelCatalog.StatusText(AiModelStatus.Ready));
        Assert.Equal("未安装", AiModelCatalog.StatusText(AiModelStatus.NotInstalled));
    }

    [BanjiTest]
    public static void 下载进度_百分比计算与边界()
    {
        Assert.Equal(25.0, new AiDownloadProgress { BytesReceived = 50, TotalBytes = 200 }.Percent);
        Assert.Equal(0.0, new AiDownloadProgress { BytesReceived = 10, TotalBytes = 0 }.Percent);
        Assert.Equal(100.0, new AiDownloadProgress { BytesReceived = 300, TotalBytes = 100 }.Percent);
        Assert.Equal(0.0, new AiDownloadProgress { BytesReceived = 0, TotalBytes = 100 }.Percent);
    }

    [BanjiTest]
    public static void 台词提示词_要求多行短句且是ChatML()
    {
        var prompt = AiPromptBuilder.BuildQuipPrompt("数学课", 5);
        Assert.True(prompt.Contains("<|im_start|>system\n"));
        Assert.True(prompt.Contains("数学课"));
        Assert.True(prompt.Contains("5 条"));
        Assert.True(prompt.EndsWith("<|im_start|>assistant\n"));

        var greeting = AiPromptBuilder.BuildGreetingPrompt();
        Assert.True(greeting.Contains("<|im_start|>assistant\n"));
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}
