using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 4A.1: Runtime 干预结果 — 允许 adapter 影响 runtime 行为
/// </summary>
public sealed class QueryRuntimeIntervention
{
    /// <summary>是否应该阻止当前操作</summary>
    public bool ShouldBlock { get; init; }

    /// <summary>要注入的消息（将添加到 messages 末尾）</summary>
    public ChatMessage? InjectedMessage { get; init; }

    /// <summary>干预原因（用于日志）</summary>
    public string? Reason { get; init; }

    /// <summary>是否应该跳过当前工具执行（仅用于 ToolExecutionCompleted）</summary>
    public bool ShouldSkipToolResult { get; init; }

    /// <summary>无干预</summary>
    public static QueryRuntimeIntervention None { get; } = new();

    /// <summary>阻止并注入消息</summary>
    public static QueryRuntimeIntervention BlockWithMessage(ChatMessage message, string? reason = null) => new()
    {
        ShouldBlock = true,
        InjectedMessage = message,
        Reason = reason
    };

    /// <summary>跳过工具结果并注入反馈</summary>
    public static QueryRuntimeIntervention SkipToolResultWithFeedback(ChatMessage feedback, string? reason = null) => new()
    {
        ShouldSkipToolResult = true,
        InjectedMessage = feedback,
        Reason = reason
    };
}

/// <summary>
/// Phase 4A.1: Runtime 干预 hook 接口 — 允许入口层干预 runtime 行为
/// </summary>
/// <remarks>
/// 用于支持 Kernel 的特有职责：
/// - Guardrail: 在工具执行前检查是否允许
/// - Critique: 在工具执行后审查并决定是否接受结果
///
/// 这是"特性保留"的关键机制，让 kernel 在使用 runtime 共性 loop 的同时保留自己的策略。
/// </remarks>
public interface IQueryRuntimeInterventionHook
{
    /// <summary>
    /// 在工具执行前检查是否允许（Guardrail）
    /// </summary>
    /// <param name="toolName">工具名称</param>
    /// <param name="arguments">工具参数</param>
    /// <param name="session">会话上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>干预结果（None 表示允许执行）</returns>
    ValueTask<QueryRuntimeIntervention> OnToolCallRequestedAsync(
        string toolName,
        IDictionary<string, object?> arguments,
        object? session,
        CancellationToken ct = default);

    /// <summary>
    /// 在工具执行后审查结果（Critique）
    /// </summary>
    /// <param name="toolName">工具名称</param>
    /// <param name="result">执行结果</param>
    /// <param name="success">是否成功</param>
    /// <param name="session">会话上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>干预结果（None 表示接受结果）</returns>
    ValueTask<QueryRuntimeIntervention> OnToolExecutionCompletedAsync(
        string toolName,
        string result,
        bool success,
        object? session,
        CancellationToken ct = default);
}