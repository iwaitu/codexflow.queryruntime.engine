using Microsoft.Extensions.AI;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Services;

public class DefaultCodexCritiqueService : ICodexCritiqueService
{
    private readonly IChatClient _chatClient;
    private readonly IAgentRoleRegistry _roleRegistry;
    private readonly IToolRegistry? _toolRegistry;

    // [Bug fix] Tools that Forge role cannot use (filtered by Kernel in RunLoopAsync)
    private static readonly HashSet<string> ForgeForbiddenTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "execute_code_task",
        PlanningToolNames.Primary,
        PlanningToolNames.LegacyAlias
    };

    public DefaultCodexCritiqueService(IChatClient chatClient, IAgentRoleRegistry roleRegistry, IToolRegistry? toolRegistry = null)
    {
        _chatClient = chatClient;
        _roleRegistry = roleRegistry;
        _toolRegistry = toolRegistry;
    }

    public async Task<CritiqueResult> ReviewAsync(CodexSession session, string proposedActions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proposedActions);

        try
        {
            var sentryPrompt = _roleRegistry.GetSystemPrompt(CodexAgentRole.Sentry);

            var projectMode = session.ProjectUrl == null ? "新建项目" : "已有项目";

            var projectSummary = session.ProjectSummary ?? "無項目摘要";

            // [Bug fix] Get actual available tools for Forge role to pass to Critique
            // This prevents Critique from suggesting tools that Forge doesn't actually have
            // Use GetAvailableTools(session) to respect stage/session constraints, then filter for Forge role
            // Forge tools = session-available tools minus execute_code_task and session-plan tools
            IEnumerable<string>? availableTools = null;
            if (_toolRegistry != null)
            {
                var sessionTools = _toolRegistry.GetAvailableTools(session);
                availableTools = sessionTools
                    .Where(t => !ForgeForbiddenTools.Contains(t.Name))
                    .Select(t => t.Name)
                    .ToList();
            }

            var prompt = CodexPrompts.GetCritiqueReviewPrompt(sentryPrompt, projectMode, projectSummary, proposedActions, availableTools);

            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            if (response == null) return new CritiqueResult(false, "Critique Error: ChatClient returned null response.");

            var feedback = response.Text?.Trim() ?? "FAIL: No feedback received";

            if (feedback.Equals("PASS", StringComparison.OrdinalIgnoreCase))
            {
                return new CritiqueResult(true, "Passed");
            }

            return new CritiqueResult(false, feedback);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return new CritiqueResult(true, $"⚠️ Critique system error (fail-open): {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return new CritiqueResult(true, $"⚠️ Critique system error (fail-open): {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            return new CritiqueResult(true, $"⚠️ Critique system error (fail-open): {ex.Message}");
        }
    }
}
