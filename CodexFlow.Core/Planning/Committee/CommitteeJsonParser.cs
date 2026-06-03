using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CodexFlow.Core.Governance;
using CodexFlow.Core.Services;

namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 委员会 JSON 响应解析器，带修复链。
/// 解析链：严格解析 → 单对象提取 → LLM 修复（最多 1 次）。
/// </summary>
public class CommitteeJsonParser
{
    private readonly ILogger _logger;

    public CommitteeJsonParser(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 解析 Reviewer 反馈 JSON。
    /// </summary>
    public ReviewerFeedback? ParseReviewerFeedback(string rawText)
    {
        var cleaned = CleanJsonResponse(rawText);

        // 1. 严格解析
        try
        {
            var feedback = JsonConvert.DeserializeObject<ReviewerFeedback>(cleaned);
            if (feedback != null && !string.IsNullOrEmpty(feedback.Status))
            {
                return feedback;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "严格 JSON 解析 ReviewerFeedback 失败");
        }

        // 2. 尝试提取单个 JSON 对象
        var extracted = ExtractJsonObject(cleaned);
        if (extracted != null)
        {
            try
            {
                var feedback = JsonConvert.DeserializeObject<ReviewerFeedback>(extracted);
                if (feedback != null && !string.IsNullOrEmpty(feedback.Status))
                {
                    return feedback;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "提取后解析 ReviewerFeedback 仍然失败");
            }
        }

        return null;
    }

    /// <summary>
    /// 解析 Moderator 裁决 JSON。
    /// </summary>
    public ModeratorDecision? ParseModeratorDecision(string rawText)
    {
        var cleaned = CleanJsonResponse(rawText);

        // 1. 严格解析
        try
        {
            var decision = JsonConvert.DeserializeObject<ModeratorDecision>(cleaned);
            if (decision != null && !string.IsNullOrEmpty(decision.Decision))
            {
                return decision;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "严格 JSON 解析 ModeratorDecision 失败");
        }

        // 2. 尝试提取单个 JSON 对象
        var extracted = ExtractJsonObject(cleaned);
        if (extracted != null)
        {
            try
            {
                var decision = JsonConvert.DeserializeObject<ModeratorDecision>(extracted);
                if (decision != null && !string.IsNullOrEmpty(decision.Decision))
                {
                    return decision;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "提取后解析 ModeratorDecision 仍然失败");
            }
        }

        return null;
    }

    /// <summary>
    /// 使用 LLM 修复 JSON 格式。
    /// </summary>
    [ApprovedNonStreamingLlm(
        "Committee malformed-JSON repair is a bounded synchronous fallback and must stay off the runtime path.",
        Ticket = "non-streaming-llm-migration",
        ReviewBy = "2026-06-30",
        Scope = ApprovedNonStreamingLlmScope.Exception)]
    public async Task<string?> RepairJsonAsync(
        string rawText,
        IChatClient chatClient,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        var prompt = CommitteePrompts.Fill(CommitteePrompts.JsonRepairPrompt,
            new Dictionary<string, string> { ["raw_text"] = rawText });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是一个 JSON 修复工具。只输出修复后的 JSON，不要输出任何其他文字。"),
            new(ChatRole.User, prompt)
        };

        var options = new ChatOptions
        {
            Temperature = 0.0f,
            ResponseFormat = ChatResponseFormat.Json
        };

        try
        {
            var helper = new BufferedNonStreamingLlmHelper(chatClient, _logger);
            var text = await helper.GetJsonTextAsync(messages, options, "committee_json_repair", ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "JSON 修复 LLM 调用失败");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "JSON 修复 LLM 调用失败");
        }

        return null;
    }

    /// <summary>
    /// 清理可能的 markdown 包装。
    /// </summary>
    public static string CleanJsonResponse(string text)
        => BufferedNonStreamingLlmHelper.CleanJsonEnvelope(text);

    /// <summary>
    /// 从混合文本中提取第一个 JSON 对象。
    /// </summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }
                    break;
            }
        }

        return null;
    }
}
