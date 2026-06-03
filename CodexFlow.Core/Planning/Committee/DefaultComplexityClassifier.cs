using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using CodexFlow.Core.Services;

namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 基于 LLM 的复杂度判定服务。
/// 判定给定目标是否为复杂需求，是否建议召开委员会评审。
/// </summary>
public class DefaultComplexityClassifier : IComplexityClassifier
{
    private readonly ILogger<DefaultComplexityClassifier> _logger;
    private readonly BufferedNonStreamingLlmHelper _bufferedLlmHelper;

    public DefaultComplexityClassifier(
        IChatClient chatClient,
        ILogger<DefaultComplexityClassifier> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _bufferedLlmHelper = new BufferedNonStreamingLlmHelper(chatClient, logger);
    }

    public async Task<ComplexityClassification> ClassifyAsync(
        string goal,
        string projectSummary,
        CancellationToken ct = default)
    {
        // 第一步：基于关键词的快速判定（简单需求短路）
        var quickResult = QuickClassify(goal);
        if (quickResult != null)
        {
            _logger.LogInformation("复杂度快速判定结果: IsComplex={IsComplex}, Reason={Reason}",
                quickResult.IsComplex, quickResult.Reason);
            return quickResult;
        }

        // 第二步：LLM 辅助判定
        try
        {
            return await LlmClassifyAsync(goal, projectSummary, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "LLM 复杂度判定失败，回退到简单模式");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "LLM 复杂度判定失败，回退到简单模式");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "LLM 复杂度判定失败，回退到简单模式");
        }

        return new ComplexityClassification
        {
            IsComplex = false,
            Reason = "LLM 判定失败，默认视为简单需求",
            TriggeredIndicators = []
        };
    }

    /// <summary>
    /// 基于关键词的快速短路判定。
    /// 明确简单需求直接返回 false，明确复杂意图返回 true，不确定返回 null 交给 LLM。
    /// </summary>
    public static ComplexityClassification? QuickClassify(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return new ComplexityClassification
            {
                IsComplex = false,
                Reason = "空目标",
                TriggeredIndicators = []
            };
        }

        // 明确简单的意图关键词
        var simpleIndicators = new[]
        {
            "修复拼写", "fix typo", "修改注释", "update comment", "调整文案",
            "rename", "重命名", "修改文案", "改名", "fix lint",
            "修复格式", "format", "调整样式"
        };

        if (simpleIndicators.Any(k => goal.Contains(k, StringComparison.OrdinalIgnoreCase))
            && goal.Length < 100)
        {
            return new ComplexityClassification
            {
                IsComplex = false,
                Reason = "匹配简单需求关键词",
                TriggeredIndicators = ["short_goal", "simple_keyword_match"]
            };
        }

        // 明确复杂的意图关键词
        var complexIndicators = new[]
        {
            "架构设计", "蓝图评审", "委员会", "方案比较", "系统设计",
            "architecture design", "blueprint review", "committee",
            "数据库迁移", "认证授权", "状态机", "后台任务",
            "database migration", "authentication", "state machine",
            "跨项目", "cross-project", "跨层", "cross-layer"
        };

        var triggered = complexIndicators
            .Where(k => goal.Contains(k, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (triggered.Count > 0)
        {
            return new ComplexityClassification
            {
                IsComplex = true,
                Reason = $"匹配复杂需求关键词: {string.Join(", ", triggered)}",
                TriggeredIndicators = triggered
            };
        }

        // 不确定，交给 LLM
        return null;
    }

    private async Task<ComplexityClassification> LlmClassifyAsync(
        string goal, string projectSummary, CancellationToken ct)
    {
        var systemPrompt = """
你是一个需求复杂度分析器。你的唯一任务是判断用户需求是否属于"复杂需求"。

## 复杂需求定义
满足任一条件即视为复杂需求：
1. 涉及跨项目或跨层改动
2. 涉及架构边界、协议、状态机、后台任务、数据库、认证授权、通知链路等系统级影响
3. 无法通过 1 到 3 个原子代码任务清晰落地
4. 包含"方案比较""架构设计""蓝图评审""委员会讨论"等意图

## 简单需求示例
- 单文件 bugfix
- 明确范围的小型 UI 修改
- 纯文案或注释调整
- 单个函数重构

## 输出格式
只输出 JSON，不输出任何其他文字：
{"is_complex": true/false, "reason": "一句话判定理由", "indicators": ["触发的指标1", "触发的指标2"]}
""";

        var userMessage = $"""
[用户目标]
{goal}

[项目背景]
{(string.IsNullOrWhiteSpace(projectSummary) ? "无项目摘要" : projectSummary.Length > 2000 ? projectSummary[..2000] + "..." : projectSummary)}
""";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage)
        };

        var options = new ChatOptions
        {
            Temperature = 0.1f,
            ResponseFormat = ChatResponseFormat.Json
        };

        var text = await _bufferedLlmHelper.GetJsonTextAsync(
            messages,
            options,
            "complexity_classifier",
            ct).ConfigureAwait(false);

        var parsed = JsonConvert.DeserializeAnonymousType(text, new
        {
            is_complex = false,
            reason = "",
            indicators = new List<string>()
        });

        return new ComplexityClassification
        {
            IsComplex = parsed?.is_complex ?? false,
            Reason = parsed?.reason ?? "LLM 未返回判定理由",
            TriggeredIndicators = parsed?.indicators ?? []
        };
    }
}
