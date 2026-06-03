using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// Phase 4A.1: Guardrail 服务接口 — 用于 Kernel 的工具执行前安全检查
/// </summary>
/// <remarks>
/// Kernel 的 Forge 角色在执行危险工具（write_file, ivilson_smart_patch, delete_file 等）前，
/// 需要检查目标文件是否被熔断机制锁定。
///
/// 这个接口抽象了检查逻辑，让 runtime 可以通过 intervention hook 调用。
/// </remarks>
public interface ICodexGuardrail
{
    /// <summary>
    /// 检查工具调用是否被安全策略阻止
    /// </summary>
    /// <param name="session">当前会话</param>
    /// <param name="toolName">工具名称</param>
    /// <param name="arguments">工具参数（包含目标路径等）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>检查结果（IsAllowed + Reason）</returns>
    ValueTask<GuardrailCheckResult> CheckAsync(
        CodexSession session,
        string toolName,
        IDictionary<string, object?> arguments,
        CancellationToken ct = default);
}

/// <summary>
/// Guardrail 检查结果
/// </summary>
public sealed record GuardrailCheckResult
{
    /// <summary>是否允许执行</summary>
    public bool IsAllowed { get; init; } = true;

    /// <summary>阻止原因（如果 IsAllowed = false）</summary>
    public string? Reason { get; init; }

    /// <summary>目标文件路径（用于日志）</summary>
    public string? TargetPath { get; init; }

    /// <summary>允许执行</summary>
    public static GuardrailCheckResult Allowed => new();

    /// <summary>阻止执行</summary>
    public static GuardrailCheckResult Blocked(string reason, string? targetPath = null) => new()
    {
        IsAllowed = false,
        Reason = reason,
        TargetPath = targetPath
    };
}