using System.Collections.Concurrent;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.QueryRuntime.IntegrationTests;

public sealed class QueryRuntimeSessionContextTests
{
    [Fact]
    public async Task QueryRuntime_AccumulatesTokensFromUsageChatResponseUpdate()
    {
        var updates = new ChatResponseUpdate[]
        {
            new UsageChatResponseUpdate
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 11,
                    OutputTokenCount = 7,
                    TotalTokenCount = 18
                }
            },
            new(ChatRole.Assistant, "runtime token accounting works")
        };

        var engine = new QueryRuntimeEngine(
            new StubLlmExecutor(updates),
            contextWindowManager: null,
            toolCoordinator: null,
            recoveryPolicy: null,
            telemetry: null,
            logger: NullLogger<QueryRuntimeEngine>.Instance);

        var result = await engine.ExecuteAsync(
            new QueryRuntimeRequest
            {
                SessionId = "token-usage-session",
                EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
                InitialMessages =
                [
                    new ChatMessage(ChatRole.User, "hello")
                ],
                EnableTools = false,
                MaxRounds = 2
            },
            new CapturingEventSink(),
            TestContext.Current.CancellationToken);

        Assert.Equal(11, result.TotalPromptTokens);
        Assert.Equal(7, result.TotalCompletionTokens);
        Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal("runtime token accounting works", result.FinalText);
    }

    [Fact]
    public async Task QueryRuntime_TurnCompletion_RefreshesSessionContextAndCanTriggerCompression()
    {
        var sessionStore = new InMemorySessionStore();
        var memoryService = new InMemoryMemoryService();
        var sessionManager = new CodexSessionManager(
            sessionStore,
            memoryService,
            NullLogger<CodexSessionManager>.Instance,
            compressionStore: null,
            compressionTriggerOptions: new ContextCompressionTriggerOptions
            {
                ModelMaxContextTokens = 10,
                AutoCompressionTriggerRatio = 0.05d,
                MinRecentTurnsBeforeCompression = 2,
                RecentTurnsSoftLimit = 12,
                EstimatedCharsPerToken = 4.0d
            });

        await sessionManager.UpdateSessionAsync(new CodexSession
        {
            Id = "context-session",
            UserId = "user-1",
            WorkspacePath = "D:\\workspace"
        });

        var updates = new ChatResponseUpdate[]
        {
            new UsageChatResponseUpdate
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 9,
                    OutputTokenCount = 5,
                    TotalTokenCount = 14
                }
            },
            new(ChatRole.Assistant, new string('A', 160))
        };

        var engine = new QueryRuntimeEngine(
            new StubLlmExecutor(updates),
            new DefaultContextWindowManager(sessionManager, NullLogger<DefaultContextWindowManager>.Instance),
            toolCoordinator: null,
            recoveryPolicy: null,
            telemetry: null,
            logger: NullLogger<QueryRuntimeEngine>.Instance);

        await engine.ExecuteAsync(
            new QueryRuntimeRequest
            {
                SessionId = "context-session",
                EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System, "你是一个简洁助手。"),
                    new ChatMessage(ChatRole.User, "请给我一个足够长的回答，用于触发压缩。")
                ],
                EnableTools = false,
                MaxRounds = 2,
                ConversationCapture = new QueryRuntimeConversationCapture(
                [
                    new QueryRuntimeConversationMessage("user", "请给我一个足够长的回答，用于触发压缩。")
                ])
            },
            new CapturingEventSink(),
            TestContext.Current.CancellationToken);

        var savedSession = await sessionStore.GetSessionAsync("context-session", TestContext.Current.CancellationToken);
        Assert.NotNull(savedSession);
        Assert.NotNull(savedSession!.LastCompressedAt);
        Assert.NotEmpty(savedSession.HistorySummary);
        Assert.Empty(savedSession.RecentTurns);

        Assert.Equal("0", savedSession.Metadata["ContextWindowRecentTurns"]);
        Assert.Equal("9", savedSession.Metadata["SessionTotalPromptTokens"]);
        Assert.Equal("5", savedSession.Metadata["SessionTotalCompletionTokens"]);
        Assert.True(int.Parse(savedSession.Metadata["ContextWindowEstimatedChars"]) > 0);
        Assert.True(int.Parse(savedSession.Metadata["ContextWindowEstimatedTokens"]) > 0);
        Assert.True(savedSession.Metadata.ContainsKey("ContextWindowUpdatedAtUtc"));
    }

    private sealed class StubLlmExecutor(IReadOnlyList<ChatResponseUpdate> updates) : ILLMExecutor
    {
        public Task<ChatResponse> ExecuteAsync(LLMExecutionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Streaming path only in these tests.");

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            LLMExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var update in updates)
            {
                ct.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }
    }

    private sealed class InMemorySessionStore : ISessionStore
    {
        private readonly ConcurrentDictionary<string, CodexSession> _sessions = new(StringComparer.Ordinal);

        public Task<CodexSession?> GetSessionAsync(string sessionId, CancellationToken ct = default)
        {
            _sessions.TryGetValue(sessionId, out var session);
            return Task.FromResult(session);
        }

        public Task SaveSessionAsync(CodexSession session, CancellationToken ct = default)
        {
            _sessions[session.Id] = session;
            return Task.CompletedTask;
        }

        public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
        {
            _sessions.TryRemove(sessionId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryMemoryService : IMemoryService
    {
        private readonly ConcurrentDictionary<string, List<ChatTurn>> _turns = new(StringComparer.Ordinal);

        public Task LearnFactAsync(string sessionId, string key, string value, string category = "general", string? metadata = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<List<MemoryEntry>> RecallFactsAsync(string sessionId, string query = "", CancellationToken ct = default)
            => Task.FromResult(new List<MemoryEntry>());

        public Task ForgetFactAsync(string sessionId, string key, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LearnUserFactAsync(string userId, string key, string value, string category = "preference", string? metadata = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<List<MemoryEntry>> RecallUserFactsAsync(string userId, string query = "", CancellationToken ct = default)
            => Task.FromResult(new List<MemoryEntry>());

        public Task ForgetUserFactAsync(string userId, string key, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task AppendTurnAsync(string sessionId, ChatTurn turn, CancellationToken ct = default)
        {
            var turns = _turns.GetOrAdd(sessionId, _ => []);
            turns.Add(turn);
            return Task.CompletedTask;
        }

        public Task<List<ChatTurn>> GetRecentTurnsAsync(string sessionId, int count = 10, CancellationToken ct = default)
        {
            var turns = _turns.TryGetValue(sessionId, out var list)
                ? list.TakeLast(count).ToList()
                : new List<ChatTurn>();
            return Task.FromResult(turns);
        }

        public Task<string> SummarizeHistoryAsync(string sessionId, string existingSummary, IReadOnlyList<ChatTurn> newTurns, CancellationToken ct = default)
        {
            var combinedTurns = string.Join(" | ", newTurns.Select(turn => $"{turn.Role}:{turn.Content}"));
            var summary = string.IsNullOrWhiteSpace(existingSummary)
                ? $"summary::{combinedTurns}"
                : $"{existingSummary} || {combinedTurns}";
            return Task.FromResult(summary);
        }

        public Task<string> SummarizeExecutionAsync(string sessionId, IReadOnlyList<string> completedTasks, string? verificationSummary = null, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<(string Key, string Value, string Category)>> ExtractStructuredFactsAsync(string executionSummary, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string Key, string Value, string Category)>>(Array.Empty<(string Key, string Value, string Category)>());

        public Task LogEventAsync(string sessionId, string eventType, string data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ClearSessionMemoryAsync(string sessionId, CancellationToken ct = default)
        {
            _turns.TryRemove(sessionId, out _);
            return Task.CompletedTask;
        }
    }
}
