using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CodexFlow.Core.Agents;

public class UserMemoryTool : ICodexTool
{
    private readonly CodexSessionManager _sessionManager;
    private readonly ILogger<UserMemoryTool> _logger;

    public string Name => "user_learn_preference";
    public string Description => "记录用户的全局偏好、习惯或跨项目的关键技术决策。这些记忆将跟随用户，在所有项目空间中生效。Few-shot: user_learn_preference({\"key\":\"code_style\",\"value\":\"prefer async/await\"})。";
    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 4_096);
    public IReadOnlyList<int> AllowedStages => new[] { 1, 2, 3, 4, 5 }; // 全阶段可用

    public UserMemoryTool(CodexSessionManager sessionManager, ILogger<UserMemoryTool> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var key = arguments.TryGetValue("key", out var keyValue) ? keyValue?.ToString() : null;
        var value = arguments.TryGetValue("value", out var valueObject) ? valueObject?.ToString() : null;

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
        {
            return CodexToolResult.Error("Both 'key' and 'value' are required to learn a preference.");
        }

        var sessionId = arguments.TryGetValue("session_id", out var sessionValue) ? sessionValue?.ToString() : null;
        if (string.IsNullOrEmpty(sessionId)) return CodexToolResult.Error("session_id not found in tool arguments.");

        try
        {
            var session = await _sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
            if (string.IsNullOrEmpty(session.UserId)) return CodexToolResult.Error("Cannot learn user preference without a valid UserId in session.");

            await _sessionManager.LearnUserFactAsync(session.UserId, key, value).ConfigureAwait(false);
            return CodexToolResult.Succeeded($"✅ 已记录用户全局偏好：[{key}] = {value}。该偏好将在您的所有项目中生效。", new { Key = key, Value = value });
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to learn user preference");
            return CodexToolResult.Error($"❌ 记录偏好失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to learn user preference");
            return CodexToolResult.Error($"❌ 记录偏好失败：{ex.Message}");
        }
        catch (TimeoutException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to learn user preference");
            return CodexToolResult.Error($"❌ 记录偏好失败：{ex.Message}");
        }
    }
}

