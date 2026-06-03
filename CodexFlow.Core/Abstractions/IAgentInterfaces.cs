using Microsoft.Extensions.AI;
using CodexFlow.Core.Models;
using CodexFlow.Core.Agents;

namespace CodexFlow.Core.Abstractions;

public interface ICodexAgentKernel
{
    /// <summary>
    /// 内核级事件订阅 (熔断、挑刺等)
    /// </summary>
#pragma warning disable CA1003 // Preserve legacy public event shape for compatibility.
    event Action<CodexEvent>? OnEvent;
#pragma warning restore CA1003

    /// <summary>
    /// 核心推理循环：接收当前状态与角色，决定下一步行动
    /// </summary>
    /// <param name="enableTools">是否向 LLM 提供工具列表。新建项目的 Stage 1 应设为 false，避免 LLM 对空工作区执行无意义的工具调用。</param>
    /// <param name="taskFileScope">Optional task file scope descriptor for pre-execution scope guard.</param>
#pragma warning disable CA1068 // Preserve legacy public parameter order for compatibility.
    Task<CodexResponse> RunLoopAsync(CodexSession session, string userPrompt, CodexAgentRole role = CodexAgentRole.Forge, CancellationToken ct = default, bool enableTools = true, TaskFileScopeDescriptor? taskFileScope = null);
#pragma warning restore CA1068

    /// <summary>
    /// 流式核心推理循环：与 RunLoopAsync 相同，但使用 streaming 调用 LLM，并记录思维链内容。
    /// </summary>
    /// <param name="enableTools">是否向 LLM 提供工具列表。</param>
    /// <param name="taskFileScope">Optional task file scope descriptor for pre-execution scope guard.</param>
#pragma warning disable CA1068
    Task<CodexResponse> RunLoopStreamingAsync(CodexSession session, string userPrompt, CodexAgentRole role = CodexAgentRole.Forge, CancellationToken ct = default, bool enableTools = true, TaskFileScopeDescriptor? taskFileScope = null);
#pragma warning restore CA1068
}

public interface ICodexPlanner
{
    /// <summary>
    /// 生成或更新任务规划
    /// </summary>
#pragma warning disable CA1002 // Preserve legacy list-based API for compatibility.
    Task<List<CodexTask>> GeneratePlanAsync(CodexSession session, string goal, CancellationToken ct = default);
#pragma warning restore CA1002
}

public interface ICodexValidator
{
    /// <summary>
    /// 验证任务或阶段是否完成
    /// </summary>
    Task<ValidationResult> ValidateAsync(CodexSession session, CodexTask task, CancellationToken ct = default);
}

public record CodexResponse(string Text, bool IsComplete, int TotalToolCalls = 0, int WriteToolCalls = 0, string? ThinkingContent = null);
public record ValidationResult(
    bool Success,
    string Summary,
    IReadOnlyList<string> Issues,
    bool IsInfrastructureError = false,
    bool HasQualityWarnings = false,
    IReadOnlyList<string>? WarningDetails = null,
    // [Bug-03 Fix] True when validation passed via deterministic fallback (not actual LLM verdict).
    // Orchestrator must log this distinctly. UI must NOT show this as a normal "✅ Passed".
    bool IsFallback = false,
    ChecklistEvaluation? ChecklistEvaluation = null);
