using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Abstractions;

public interface ICodexCritiqueService
{
    /// <summary>
    /// 对 Agent 提议的操作或代码进行“挑刺”评审
    /// </summary>
    /// <param name="session">当前会话上下文</param>
    /// <param name="proposedActions">Agent 提议调用的工具及参数描述</param>
    /// <param name="ct"></param>
    /// <returns>评审结论：Success 表示通过，失败则包含具体的改进建议</returns>
    Task<CritiqueResult> ReviewAsync(CodexSession session, string proposedActions, CancellationToken ct = default);
}

public record CritiqueResult(bool IsPassed, string Feedback);
