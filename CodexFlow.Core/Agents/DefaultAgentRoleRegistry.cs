using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Agents;

public class DefaultAgentRoleRegistry : IAgentRoleRegistry
{
    private readonly HashlineOptions? _hashlineOptions;

    public DefaultAgentRoleRegistry(HashlineOptions? hashlineOptions = null)
    {
        _hashlineOptions = hashlineOptions;
    }

    public string GetRoleName(CodexAgentRole role)
    {
        return role switch
        {
            CodexAgentRole.Architect => "Ivilson-Prime",
            CodexAgentRole.Coordinator => "Ivilson-Coordinator",
            CodexAgentRole.Forge => "Ivilson-Forge",
            CodexAgentRole.Sentry => "Ivilson-Sentry",
            CodexAgentRole.Security => "Ivilson-Guard",
            _ => "Ivilson-Agent"
        };
    }

    public string GetSystemPrompt(CodexAgentRole role, CodexSession? session = null)
    {
        return role switch
        {
            CodexAgentRole.Architect => CodexPrompts.GetArchitectPrompt(session, _hashlineOptions),
            CodexAgentRole.Coordinator => CodexPrompts.GetCoordinatorPrompt(session),
            CodexAgentRole.Forge => CodexPrompts.GetForgePrompt(session, _hashlineOptions),
            CodexAgentRole.Sentry => CodexPrompts.GetSentryPrompt(session),
            CodexAgentRole.Security => CodexPrompts.LegacySecurityPrompt,
            _ => CodexPrompts.DefaultAgentPrompt
        };
    }
}
