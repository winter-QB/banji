using Banji.Core.Abstractions;
using Banji.Core.Ai;
using Banji.Core.Boot;
using Banji.Core.Models;

namespace Banji.Tests;

/// <summary>
/// v1.4.0 AI 联动测试：动态上下文（日期/星期、单双周、值日）的纯函数拼装与 system prompt 注入。
/// 不依赖网络、真实模型或真实服务。
/// </summary>
public static class AiContextTests
{
    private static readonly DateOnly SampleDate = new(2026, 11, 9); // 周一

    private static AiPromptContext FullContext() => new()
    {
        Today = SampleDate,
        DateText = AiPromptContextBuilder.FormatDateText(SampleDate),
        WeekParityText = AiPromptContextBuilder.BuildWeekParityText(
            10, WeekParity.Odd, false, 11, WeekParity.Even),
        DutyText = AiPromptContextBuilder.BuildDutyText(new DutyScheduleView
        {
            Date = SampleDate,
            Mode = DutyMode.Daily,
            GroupName = "第 2 组",
            Tasks = new List<DutyTaskView>
            {
                new() { TaskName = "擦黑板", StudentNames = new[] { "张三", "李四" } },
                new() { TaskName = "扫地", StudentNames = new[] { "王五" } }
            }
        })
    };

    // =========================================================================
    // 动态上下文（纯拼装）
    // =========================================================================

    [BanjiTest]
    public static void 动态上下文_完整信息包含日期单双周与值日()
    {
        var block = AiPromptContextBuilder.BuildSystemContext(FullContext());

        Assert.True(block.Contains("【当前动态信息】"), "缺少上下文标题");
        Assert.True(block.Contains("2026年11月9日"), "缺少日期");
        Assert.True(block.Contains("星期"), "缺少星期");
        Assert.True(block.Contains("第 10 周"), "缺少周次");
        Assert.True(block.Contains("单周"), "缺少单双周");
        Assert.True(block.Contains("下周"), "缺少下周单双周（用于回答“下周是单周还是双周”）");
        Assert.True(block.Contains("第 11 周"), "缺少下周周次");
        Assert.True(block.Contains("双周"), "缺少下周双周");
        Assert.True(block.Contains("今日值日"), "缺少值日口径");
        Assert.True(block.Contains("第 2 组"), "缺少值日组");
        Assert.True(block.Contains("擦黑板"), "缺少值日任务");
        Assert.True(block.Contains("张三"), "缺少值日成员");
        Assert.True(block.Length <= AiPromptContextBuilder.MaxContextChars);
    }

    [BanjiTest]
    public static void 动态上下文_只有日期时也能用且不含值日()
    {
        var block = AiPromptContextBuilder.BuildSystemContext(new AiPromptContext { Today = SampleDate });
        Assert.True(block.Contains("2026年11月9日"));
        Assert.True(block.Contains("星期一"));
        Assert.False(block.Contains("值日"));
        Assert.False(block.Contains("单周"));
        Assert.False(block.Contains("双周"));
    }

    [BanjiTest]
    public static void 动态上下文_单双周关闭时省略周次信息()
    {
        var context = new AiPromptContext
        {
            Today = SampleDate,
            DateText = AiPromptContextBuilder.FormatDateText(SampleDate),
            DutyText = AiPromptContextBuilder.BuildDutyText(new DutyScheduleView
            {
                Date = SampleDate,
                Mode = DutyMode.Weekly,
                GroupName = "第 1 组"
            })
        };

        var block = AiPromptContextBuilder.BuildSystemContext(context);
        Assert.True(block.Contains("本周值周"));
        Assert.False(block.Contains("单周"));
        Assert.False(block.Contains("双周"));
        Assert.False(block.Contains("本周是第"), "没有周次配置时不应出现单双周周次");
    }

    [BanjiTest]
    public static void 动态上下文_值日未配置时省略值日信息()
    {
        var context = new AiPromptContext
        {
            Today = SampleDate,
            DateText = AiPromptContextBuilder.FormatDateText(SampleDate),
            WeekParityText = AiPromptContextBuilder.BuildWeekParityText(3, WeekParity.Even, false)
        };

        var block = AiPromptContextBuilder.BuildSystemContext(context);
        Assert.True(block.Contains("第 3 周"));
        Assert.False(block.Contains("值日"));
        Assert.False(block.Contains("值周"));
    }

    [BanjiTest]
    public static void 动态上下文_空快照返回空串()
    {
        Assert.Equal("", AiPromptContextBuilder.BuildSystemContext(null));
        Assert.Equal("", AiPromptContextBuilder.BuildSystemContext(new AiPromptContext()));
        Assert.Equal("", new AiPromptContext().ToSystemBlock());
    }

    // =========================================================================
    // 值日摘要
    // =========================================================================

    [BanjiTest]
    public static void 值日摘要_值日模式含组名任务与多成员()
    {
        var text = AiPromptContextBuilder.BuildDutyText(new DutyScheduleView
        {
            Date = SampleDate,
            Mode = DutyMode.Daily,
            GroupName = "第 2 组",
            Tasks = new List<DutyTaskView>
            {
                new() { TaskName = "擦黑板", StudentNames = new[] { "张三", "李四" } },
                new() { TaskName = "扫地", StudentNames = new[] { "王五" } }
            }
        });

        Assert.NotNull(text);
        Assert.True(text!.Contains("今日值日"));
        Assert.True(text.Contains("第 2 组"));
        Assert.True(text.Contains("擦黑板（张三、李四）"), "一个岗位两人应都列出：" + text);
        Assert.True(text.Contains("扫地（王五）"));
    }

    [BanjiTest]
    public static void 值日摘要_值周模式与暂停周()
    {
        Assert.True(AiPromptContextBuilder.BuildDutyText(new DutyScheduleView
        {
            Mode = DutyMode.Weekly, GroupName = "第 3 组"
        })!.Contains("本周值周"));

        var paused = AiPromptContextBuilder.BuildDutyText(new DutyScheduleView
        {
            Mode = DutyMode.Weekly, GroupName = "第 3 组", IsPaused = true
        });
        Assert.NotNull(paused);
        Assert.True(paused!.Contains("暂停"));
    }

    [BanjiTest]
    public static void 值日摘要_空任务只显示组名且未配置返回null()
    {
        var noTasks = AiPromptContextBuilder.BuildDutyText(new DutyScheduleView
        {
            Mode = DutyMode.Daily, GroupName = "第 4 组"
        });
        Assert.NotNull(noTasks);
        Assert.True(noTasks!.Contains("第 4 组"));
        Assert.False(noTasks.Contains("任务："));

        Assert.Null(AiPromptContextBuilder.BuildDutyText(null));
        Assert.Null(AiPromptContextBuilder.BuildDutyText(new DutyScheduleView
        {
            Mode = DutyMode.Daily, Tasks = Array.Empty<DutyTaskView>()
        }));
    }

    [BanjiTest]
    public static void 值日摘要_任务过多或名字超长会被截断()
    {
        var tasks = new List<DutyTaskView>();
        for (var i = 0; i < 20; i++)
        {
            tasks.Add(new DutyTaskView
            {
                TaskName = "岗位" + i + new string('黑', 40),
                StudentNames = new[] { "同学" + i + new string('板', 30) }
            });
        }

        var text = AiPromptContextBuilder.BuildDutyText(new DutyScheduleView
        {
            Mode = DutyMode.Daily, GroupName = "第 2 组", Tasks = tasks
        });

        Assert.NotNull(text);
        Assert.True(text!.Length <= AiPromptContextBuilder.MaxDutyChars + 8,
            $"值日摘要应截断，实际 {text.Length} 字");
        Assert.True(text.Contains("岗位0"), "应保留前面的任务");
        Assert.False(text.Contains("岗位19"), "超长列表后段应被截断");

        var block = AiPromptContextBuilder.BuildSystemContext(new AiPromptContext
        {
            Today = SampleDate,
            DutyText = text
        });
        Assert.True(block.Length <= AiPromptContextBuilder.MaxContextChars);
    }

    // =========================================================================
    // ChatML prompt 注入
    // =========================================================================

    [BanjiTest]
    public static void 聊天prompt_动态上下文注入system段且不改用户消息()
    {
        var history = new List<AiChatMessage>
        {
            new() { Role = AiChatRole.User, Content = "今天谁值日？" }
        };
        var contextBlock = AiPromptContextBuilder.BuildSystemContext(FullContext());

        var prompt = AiPromptBuilder.BuildChatPrompt(history, 1024, AiPromptBuilder.EstimateTokens,
            out var dropped, contextBlock);

        Assert.Equal(0, dropped);
        Assert.True(prompt.Contains("今日值日"));
        Assert.True(prompt.Contains("第 2 组"));
        Assert.True(prompt.Contains("<|im_start|>user\n今天谁值日？<|im_end|>"), "用户消息不应被改写");
        Assert.True(prompt.EndsWith("<|im_start|>assistant\n"));

        var contextIndex = prompt.IndexOf("【当前动态信息】", StringComparison.Ordinal);
        var firstUserIndex = prompt.IndexOf("<|im_start|>user", StringComparison.Ordinal);
        var systemIndex = prompt.IndexOf("<|im_start|>system", StringComparison.Ordinal);
        Assert.True(systemIndex >= 0 && contextIndex > systemIndex, "上下文应位于 system 段内");
        Assert.True(firstUserIndex > contextIndex, "上下文不能塞进用户消息");
    }

    [BanjiTest]
    public static void 聊天prompt_无动态上下文时行为与旧版一致()
    {
        var history = new List<AiChatMessage>
        {
            new() { Role = AiChatRole.User, Content = "你好" }
        };

        var withNull = AiPromptBuilder.BuildChatPrompt(history, 1024, AiPromptBuilder.EstimateTokens,
            out var dropped1, null);
        var oldSignature = AiPromptBuilder.BuildChatPrompt(history, 1024, AiPromptBuilder.EstimateTokens,
            out var dropped2);

        Assert.Equal(dropped1, dropped2);
        Assert.Equal(oldSignature, withNull);
        Assert.False(withNull.Contains("【当前动态信息】"));
    }

    [BanjiTest]
    public static void 聊天prompt_上下文过长时自动丢弃以保住正常对话()
    {
        var hugeContext = "【当前动态信息】\n" + new string('值', 6000);
        var history = new List<AiChatMessage>
        {
            new() { Role = AiChatRole.User, Content = "今天谁值日？" }
        };

        var prompt = AiPromptBuilder.BuildChatPrompt(history, 200, AiPromptBuilder.EstimateTokens,
            out _, hugeContext);

        Assert.False(prompt.Contains("【当前动态信息】"), "预算不够时应丢掉动态上下文");
        Assert.True(prompt.Contains("今天谁值日"), "应保留用户问题（可截断）");
        Assert.True(prompt.EndsWith("<|im_start|>assistant\n"));
    }

    // =========================================================================
    // 单双周 / 台词 prompt
    // =========================================================================

    [BanjiTest]
    public static void 单双周文案_周次未配置返回null()
    {
        Assert.Null(AiPromptContextBuilder.BuildWeekParityText(0, WeekParity.Odd, false));
        Assert.Null(AiPromptContextBuilder.BuildWeekParityText(-3, WeekParity.Even, false));

        var text = AiPromptContextBuilder.BuildWeekParityText(5, WeekParity.Even, true, 6, WeekParity.Odd);
        Assert.NotNull(text);
        Assert.True(text!.Contains("第 5 周") && text.Contains("双周"));
        Assert.True(text.Contains("手动覆盖"));
        Assert.True(text.Contains("第 6 周") && text.Contains("单周"));
    }

    [BanjiTest]
    public static void 日期文案_包含年月日与星期()
    {
        Assert.Equal("今天是 2024年1月1日 星期一。", AiPromptContextBuilder.FormatDateText(new DateOnly(2024, 1, 1)));
        Assert.Equal("今天是 2026年11月8日 星期日。", AiPromptContextBuilder.FormatDateText(new DateOnly(2026, 11, 8)));
    }

    [BanjiTest]
    public static void 台词prompt_值日场景注入值日上下文并要求提值日()
    {
        var contextBlock = AiPromptContextBuilder.BuildSystemContext(FullContext());
        var prompt = AiPromptBuilder.BuildQuipPrompt("放学后的值日提醒", 3, contextBlock, dutyFocus: true);

        Assert.True(prompt.Contains("今天的值日/值周安排"));
        Assert.True(prompt.Contains("第 2 组"), "台词 prompt 应包含值日组");
        Assert.True(prompt.Contains("擦黑板"));
        Assert.True(prompt.Contains("3 条"));
        Assert.True(prompt.EndsWith("<|im_start|>assistant\n"));
    }

    [BanjiTest]
    public static void 台词prompt_普通场景不注入值日要求()
    {
        var prompt = AiPromptBuilder.BuildQuipPrompt("课堂日常", 5);
        Assert.True(prompt.Contains("5 条"));
        Assert.False(prompt.Contains("值日"));
        Assert.False(prompt.Contains("【当前动态信息】"));
    }

    [BanjiTest]
    public static void 值日场景关键词识别()
    {
        Assert.True(AiPromptContextBuilder.IsDutyScenarioHint("放学后的值日提醒"));
        Assert.True(AiPromptContextBuilder.IsDutyScenarioHint("擦黑板"));
        Assert.True(AiPromptContextBuilder.IsDutyScenarioHint("值周换组了"));
        Assert.False(AiPromptContextBuilder.IsDutyScenarioHint("课堂桌面宠物的一句短台词"));
        Assert.False(AiPromptContextBuilder.IsDutyScenarioHint(null));
        Assert.False(AiPromptContextBuilder.IsDutyScenarioHint(""));
    }

    [BanjiTest]
    public static void 截断函数_按字符边界且加省略号()
    {
        Assert.Null(AiPromptContextBuilder.Truncate(null, 10));
        Assert.Null(AiPromptContextBuilder.Truncate("   ", 10));
        Assert.Equal("短文本", AiPromptContextBuilder.Truncate("短文本", 10));

        var longText = new string('测', 100);
        var truncated = AiPromptContextBuilder.Truncate(longText, 10);
        Assert.NotNull(truncated);
        Assert.Equal(10, truncated!.Length);
        Assert.True(truncated.EndsWith('…'));
    }

    [BanjiTest]
    public static void 运行期采集_未初始化或服务不可用时安全降级()
    {
        try
        {
            var context = AiPromptContextProvider.TryCapture();
            var block = AiPromptContextProvider.TryCaptureSystemBlock();

            if (!AppServices.IsInitialized)
            {
                Assert.Null(context);
                Assert.Null(block);
            }
            else if (context is not null)
            {
                // AppServices 已初始化时至少能拿到日期信息（服务异常时允许降级为 null）
                Assert.True(context.DateText!.Contains("今天是"));
            }
        }
        catch (Exception ex)
        {
            throw new Exception("动态上下文采集不应抛异常：" + ex.Message);
        }
    }
}

