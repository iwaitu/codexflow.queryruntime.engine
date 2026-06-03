using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 0B: 工具执行协调器接口 — 负责工具调用去重、执行、结果归一
/// </summary>
public interface IToolExecutionCoordinator
{
    /// <summary>
    /// 检查是否应跳过重复工具调用
    /// </summary>
    /// <param name="toolCall">待执行的工具调用</param>
    /// <param name="state">当前 runtime 状态</param>
    /// <returns>若应跳过，返回缓存结果；否则返回 null</returns>
    ToolDedupResult? CheckDuplicate(
        FunctionCallContent toolCall,
        QueryRuntimeState state);

    /// <summary>
    /// 执行单个工具调用
    /// </summary>
    /// <param name="toolCall">工具调用内容</param>
    /// <param name="availableTools">可用工具列表</param>
    /// <param name="request">当前 runtime 请求上下文</param>
    /// <param name="state">当前 runtime 状态</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>执行结果</returns>
    Task<ToolExecutionResult> ExecuteAsync(
        FunctionCallContent toolCall,
        IReadOnlyList<AIFunction>? availableTools,
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        CancellationToken ct = default);

    /// <summary>
    /// 执行一组工具调用。
    /// 协调器可根据工具语义将并发安全的只读调用分批并行执行，
    /// 其余调用保持串行。
    /// </summary>
    /// <param name="toolCalls">工具调用列表</param>
    /// <param name="availableTools">可用工具列表</param>
    /// <param name="request">当前 runtime 请求上下文</param>
    /// <param name="state">当前 runtime 状态</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>执行结果流</returns>
    IAsyncEnumerable<ToolExecutionResult> ExecuteBatchAsync(
        IReadOnlyList<FunctionCallContent> toolCalls,
        IReadOnlyList<AIFunction>? availableTools,
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        CancellationToken ct = default);

    /// <summary>
    /// 计算工具调用签名（用于去重）
    /// </summary>
    /// <param name="toolCall">工具调用内容</param>
    /// <returns>唯一签名字符串</returns>
    string ComputeSignature(FunctionCallContent toolCall);
}

/// <summary>
/// 工具去重检查结果
/// </summary>
public sealed record ToolDedupResult(
    bool ShouldSkip,
    string? CachedResult,
    bool WasFailed);

/// <summary>
/// 工具执行结果
/// </summary>
public sealed record ToolExecutionResult(
    string ToolName,
    string CallId,
    string Result,
    bool Success,
    int? ResultLength = null,
    Exception? Exception = null,
    string? Summary = null,
    bool IsOutputTruncated = false,
    object? Metadata = null,
    string? SystemHint = null,
    ToolSystemHint? SystemHintDetail = null);
