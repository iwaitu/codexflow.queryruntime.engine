using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Adapters;

/// <summary>
/// Phase 4A.1: Kernel Runtime 干预适配器 — 实现 guardrail + critique 闭环
/// </summary>
/// <remarks>
/// Kernel 的独特职责需要特殊处理：
/// 1. Guardrail: 在工具执行前检查是否允许（仅 Forge 角色）
/// 2. Critique: 在工具执行后审查结果，反馈能注入到下一轮
/// 3. 将 runtime 事件转发给 Kernel 的 OnEvent
///
/// 关键改进：
/// - 实现 IQueryRuntimeInterventionHook，让干预真正影响 runtime 行为
/// - Critique reject 时注入反馈消息到 runtime 下一轮
/// - Guardrail 触发时阻止工具执行并通知
/// </remarks>
public sealed class KernelRuntimeEventAdapter :
    IQueryRuntimeEventSink,
    IQueryRuntimeInterventionHook
{
    private readonly CodexSession _session;
    private readonly CodexAgentRole _role;
    private readonly ICodexCritiqueService? _critiqueService;
    private readonly ICodexGuardrail? _guardrail;
    private readonly ILogger _logger;
    private readonly Action<CodexEvent>? _onEvent;
    private readonly HashSet<QueryRuntimeEventType> _enabledEvents;
    private int _critiqueRetryCount;
    private int _guardrailTriggerCount;

    public KernelRuntimeEventAdapter(
        CodexSession session,
        CodexAgentRole role,
        ICodexCritiqueService? critiqueService,
        ICodexGuardrail? guardrail,
        ILogger logger,
        Action<CodexEvent>? onEvent)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _role = role;
        _critiqueService = critiqueService;
        _guardrail = guardrail;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onEvent = onEvent;
        _critiqueRetryCount = 0;
        _guardrailTriggerCount = 0;

        // Kernel 启用的事件类型
        _enabledEvents = new HashSet<QueryRuntimeEventType>
        {
            QueryRuntimeEventType.AssistantDelta,
            QueryRuntimeEventType.ThinkingStarted,
            QueryRuntimeEventType.ThinkingDelta,
            QueryRuntimeEventType.ThinkingEnded,
            QueryRuntimeEventType.ToolCallRequested,
            QueryRuntimeEventType.ToolExecutionStarted,
            QueryRuntimeEventType.ToolExecutionCompleted,
            QueryRuntimeEventType.RecoveryTriggered,
            QueryRuntimeEventType.Error
        };
    }

    #region IQueryRuntimeEventSink

    /// <inheritdoc/>
    public bool IsEnabled(QueryRuntimeEventType eventType)
    {
        return _enabledEvents.Contains(eventType);
    }

    /// <inheritdoc/>
    public async ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
    {
        // 转发事件到 Kernel 的 OnEvent
        ForwardToOnEvent(runtimeEvent);
        await ValueTask.CompletedTask;
    }

    #endregion

    #region IQueryRuntimeInterventionHook

    /// <summary>
    /// Guardrail 检查（工具执行前）
    /// </summary>
    public async ValueTask<QueryRuntimeIntervention> OnToolCallRequestedAsync(
        string toolName,
        IDictionary<string, object?> arguments,
        object? session,
        CancellationToken ct = default)
    {
        // Guardrail 仅对 Forge 角色启用
        if (_role != CodexAgentRole.Forge || _guardrail == null)
        {
            return QueryRuntimeIntervention.None;
        }

        try
        {
            var guardrailResult = await _guardrail.CheckAsync(
                _session,
                toolName,
                arguments,
                ct);

            if (!guardrailResult.IsAllowed)
            {
                _guardrailTriggerCount++;
                _logger.LogWarning(
                    "Guardrail blocked tool {ToolName}. Reason: {Reason}. Count: {Count}",
                    toolName, guardrailResult.Reason, _guardrailTriggerCount);

                // 发送 guardrail 事件
                _onEvent?.Invoke(new CodexEvent
                {
                    Type = CodexEventType.GuardrailBlocked,
                    Message = $"Guardrail blocked tool: {toolName}",
                    Payload = new { tool = toolName, reason = guardrailResult.Reason },
                    Timestamp = DateTime.UtcNow
                });

                // 注入 guardrail 反馈消息
                var feedbackMessage = new ChatMessage(
                    ChatRole.User,
                    $"⚠️ [GUARDRAIL] 工具 `{toolName}` 被安全策略阻止。\n" +
                    $"原因：{guardrailResult.Reason}\n\n" +
                    $"请选择其他方式完成任务，或向用户说明限制。");

                return QueryRuntimeIntervention.BlockWithMessage(
                    feedbackMessage,
                    $"Guardrail: {guardrailResult.Reason}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guardrail check failed for tool {ToolName}", toolName);
        }

        return QueryRuntimeIntervention.None;
    }

    /// <summary>
    /// Critique 检查（工具执行后）— 实现真正的闭环
    /// </summary>
    public async ValueTask<QueryRuntimeIntervention> OnToolExecutionCompletedAsync(
        string toolName,
        string result,
        bool success,
        object? session,
        CancellationToken ct = default)
    {
        // Critique 仅对非 Security 角色启用
        if (_role == CodexAgentRole.Security || _critiqueService == null)
        {
            return QueryRuntimeIntervention.None;
        }

        try
        {
            // 构建 proposed actions
            var proposedActions = $"Tool: {toolName}\nResult: {TruncateForLog(result, 500)}";

            var reviewResult = await _critiqueService.ReviewAsync(_session, proposedActions, ct);

            if (!reviewResult.IsPassed)
            {
                if (_critiqueRetryCount >= 3)
                {
                    _logger.LogWarning(
                        "Critique loop exceeded max retries. Tool: {Tool}, Feedback: {Feedback}",
                        toolName, reviewResult.Feedback);

                    // 超过重试上限，接受结果但发送警告
                    _onEvent?.Invoke(new CodexEvent
                    {
                        Type = CodexEventType.CritiqueFeedback,
                        Message = $"Critique retries exhausted. Tool result accepted with warnings.",
                        Payload = reviewResult.Feedback,
                        Timestamp = DateTime.UtcNow
                    });

                    return QueryRuntimeIntervention.None;
                }

                _critiqueRetryCount++;
                _logger.LogWarning(
                    "Critique rejected tool {Tool} result ({RetryCount}/3): {Feedback}",
                    toolName, _critiqueRetryCount, reviewResult.Feedback);

                // 发送 critique feedback 事件
                _onEvent?.Invoke(new CodexEvent
                {
                    Type = CodexEventType.CritiqueFeedback,
                    Message = $"Critique rejected tool result. ({_critiqueRetryCount}/3)",
                    Payload = reviewResult.Feedback,
                    Timestamp = DateTime.UtcNow
                });

                // 关键：注入 critique 反馈到下一轮，形成闭环
                // [Bug fix] Add structured repair protocol similar to Security/Validation repairs
                var critiqueFeedbackMessage = new ChatMessage(
                    ChatRole.User,
                    $"⚠️ [CRITIQUE REPAIR REQUIRED] 工具 `{toolName}` 的执行结果未通过同行审查。\n\n" +
                    $"审查反馈：{reviewResult.Feedback}\n\n" +
                    "👉 修复协议（按顺序执行）：\n" +
                    "1. **理解问题**：仔细阅读审查反馈，识别具体的瑕疵或逻辑漏洞。\n" +
                    "2. **分析根因**：判断是参数格式错误、工具选择不当、还是执行逻辑有问题。\n" +
                    "3. **调整方案**：根据反馈修改工具参数、更换工具、或重新规划执行步骤。\n" +
                    "4. **重新执行**：使用调整后的方案重新调用工具。\n" +
                    "⚠️ 不要重复相同的错误调用，不要忽略审查反馈中的具体问题。");

                return QueryRuntimeIntervention.SkipToolResultWithFeedback(
                    critiqueFeedbackMessage,
                    $"Critique rejected: {reviewResult.Feedback}");
            }
            else
            {
                // 成功通过 critique，重置计数
                _critiqueRetryCount = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critique review failed for tool {ToolName}", toolName);
        }

        return QueryRuntimeIntervention.None;
    }

    #endregion

    private void ForwardToOnEvent(QueryRuntimeEvent runtimeEvent)
    {
        if (_onEvent == null) return;

        var codexEvent = MapToCodexEvent(runtimeEvent);
        if (codexEvent != null)
        {
            _onEvent(codexEvent);
        }
    }

    private CodexEvent? MapToCodexEvent(QueryRuntimeEvent runtimeEvent)
    {
        return runtimeEvent switch
        {
            AssistantDeltaEvent e => new CodexEvent
            {
                Type = CodexEventType.Content,
                Message = e.Delta,
                Timestamp = DateTime.UtcNow
            },

            ThinkingStartedEvent => new CodexEvent
            {
                Type = CodexEventType.ThinkingStart,
                Message = "Thinking started",
                Timestamp = DateTime.UtcNow
            },

            ThinkingDeltaEvent e => new CodexEvent
            {
                Type = CodexEventType.ThinkingContent,
                Message = e.Delta,
                Timestamp = DateTime.UtcNow
            },

            ThinkingEndedEvent e => new CodexEvent
            {
                Type = CodexEventType.ThinkingEnd,
                Message = "Thinking ended",
                Payload = e.FullThinking,
                Timestamp = DateTime.UtcNow
            },

            ToolCallRequestedEvent e => new CodexEvent
            {
                Type = CodexEventType.ToolCall,
                Message = $"Tool call requested: {e.ToolName}",
                Payload = new { name = e.ToolName, callId = e.CallId, arguments = e.Arguments },
                Timestamp = DateTime.UtcNow
            },

            ToolExecutionCompletedEvent e => new CodexEvent
            {
                Type = CodexEventType.ToolResult,
                Message = $"Tool completed: {e.ToolName}",
                Payload = new { name = e.ToolName, success = e.Success, result = TruncateForLog(e.Result, 500) },
                Timestamp = DateTime.UtcNow
            },

            RecoveryTriggeredEvent e => new CodexEvent
            {
                Type = CodexEventType.RecoveryTriggered,
                Message = $"Recovery triggered: {e.RecoveryType}",
                Payload = new { type = e.RecoveryType, attempt = e.Attempt },
                Timestamp = DateTime.UtcNow
            },

            ErrorEvent e => new CodexEvent
            {
                Type = CodexEventType.Error,
                Message = e.Message,
                Payload = new { errorType = e.ErrorType },
                Timestamp = DateTime.UtcNow
            },

            _ => null
        };
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;

        return text[..maxLength] + "...";
    }
}