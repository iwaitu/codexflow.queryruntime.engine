using CodexFlow.Core.Agents;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// 默认上下文窗口治理实现。
/// 当前仅负责把 query turn 回写到 SessionManager，由它执行自动压缩阈值判断。
/// </summary>
public sealed class DefaultContextWindowManager : IContextWindowManager
{
    private readonly CodexSessionManager _sessionManager;
    private readonly ILogger<DefaultContextWindowManager> _logger;

    public DefaultContextWindowManager(
        CodexSessionManager sessionManager,
        ILogger<DefaultContextWindowManager> logger)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task OnTurnStartedAsync(
        QueryRuntimeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var capture = request.ConversationCapture;
        if (capture == null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return;
        }

        var inputTurns = capture.InputTurns
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Content))
            .Select(turn => (turn.Role, turn.Content))
            .ToArray();

        if (inputTurns.Length == 0)
        {
            return;
        }

        await _sessionManager.RecordMessagesAsync(request.SessionId, inputTurns).ConfigureAwait(false);
        await _sessionManager
            .ApplyContextGovernanceAsync(
                request.SessionId,
                request.AdapterHints?.ContextWarnLimit,
                request.AdapterHints?.ContextHardLimit,
                ct)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Context window preflight persisted by runtime for session {SessionId}. InputTurns={InputTurns}",
            request.SessionId,
            inputTurns.Length);
    }

    /// <inheritdoc />
    public async Task OnTurnCompletedAsync(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        var capture = request.ConversationCapture;
        if (capture == null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.FinalText))
        {
            await _sessionManager.RecordMessagesAsync(
                request.SessionId,
                (capture.AssistantRole, result.FinalText)).ConfigureAwait(false);
        }

        if ((result.TotalPromptTokens ?? 0) > 0 || (result.TotalCompletionTokens ?? 0) > 0)
        {
            await _sessionManager
                .RecordRuntimeUsageAsync(
                    request.SessionId,
                    result.TotalPromptTokens ?? 0,
                    result.TotalCompletionTokens ?? 0,
                    ct)
                .ConfigureAwait(false);
        }

        if (result.RuntimeCheckpoint != null)
        {
            await _sessionManager
                .RecordRuntimeCheckpointAsync(
                    request.SessionId,
                    result.RuntimeCheckpoint,
                    ct)
                .ConfigureAwait(false);
        }

        _logger.LogDebug(
            "Context window completion persisted by runtime for session {SessionId}. Termination={Termination}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, Checkpoint={Checkpoint}",
            request.SessionId,
            result.TerminationReason,
            result.TotalPromptTokens ?? 0,
            result.TotalCompletionTokens ?? 0,
            result.RuntimeCheckpoint != null);
    }
}
