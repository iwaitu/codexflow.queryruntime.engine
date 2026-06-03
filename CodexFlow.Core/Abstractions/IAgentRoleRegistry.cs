using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

public interface IAgentRoleRegistry
{
    string GetSystemPrompt(CodexAgentRole role, CodexSession? session = null);
    string GetRoleName(CodexAgentRole role);
}
