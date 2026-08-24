using System.Text.Json;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models;

public sealed class MeaiRuntimeProtocolAdapterTests
{
    [Fact]
    public void Messages_RoundTripTextReasoningToolCallAndResult()
    {
        var call = new FunctionCallContent(
            "call-1",
            "read_file",
            new Dictionary<string, object?>
            {
                ["path"] = "README.md",
                ["line"] = 3,
                ["flags"] = new object?[] { true, "ordered" }
            });
        var reasoning = new TextReasoningContent("inspect first") { ProtectedData = "opaque" };
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, [new TextContent("inspect")]),
            new ChatMessage(ChatRole.Assistant, [reasoning, call]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "ok")])
        };

        var protocol = MeaiRuntimeProtocolAdapter.ToProtocolMessages(messages);
        var roundTrip = MeaiRuntimeProtocolAdapter.ToMeaiMessages(protocol);

        Assert.Equal(3, protocol.Count);
        var protocolCall = Assert.IsType<RuntimeToolCallItem>(protocol[1].Items[1]).Call;
        Assert.Equal("README.md", protocolCall.Arguments.GetProperty("path").GetString());
        Assert.Equal(3, protocolCall.Arguments.GetProperty("line").GetInt64());
        var roundTripCall = Assert.IsType<FunctionCallContent>(roundTrip[1].Contents[1]);
        Assert.Equal(new[] { "path", "line", "flags" }, roundTripCall.Arguments!.Keys);
        Assert.Equal("opaque", Assert.IsType<TextReasoningContent>(roundTrip[1].Contents[0]).ProtectedData);
        Assert.Equal("ok", Assert.IsType<FunctionResultContent>(roundTrip[2].Contents[0]).Result);
    }

    [Fact]
    public void StreamUpdate_PreservesReasoningUsageWarningToolOrderAndStopReason()
    {
        var update = new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            FinishReason = ChatFinishReason.ToolCalls,
            Contents =
            [
                new TextReasoningContent("reason"),
                new TextContent("text"),
                new FunctionCallContent("call-1", "first", new Dictionary<string, object?>()),
                new FunctionCallContent("call-2", "second", new Dictionary<string, object?>()),
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 4,
                    TotalTokenCount = 14
                }),
                new ErrorContent("provider warning")
            ]
        };

        var events = MeaiRuntimeProtocolAdapter.ToProtocolEvents(update);

        Assert.Collection(
            events,
            item => Assert.IsType<RuntimeReasoningDeltaEvent>(item),
            item => Assert.IsType<RuntimeTextDeltaEvent>(item),
            item => Assert.Equal("first", Assert.IsType<RuntimeToolCallEvent>(item).Call.Name),
            item => Assert.Equal("second", Assert.IsType<RuntimeToolCallEvent>(item).Call.Name),
            item => Assert.Equal(14, Assert.IsType<RuntimeUsageEvent>(item).Usage.TotalTokens),
            item => Assert.Equal("meai_error_content", Assert.IsType<RuntimeWarningEvent>(item).Warning.Code),
            item => Assert.Equal(RuntimeModelStopReason.ToolCall, Assert.IsType<RuntimeModelCompletedEvent>(item).StopReason));
    }

    [Fact]
    public void VllmReasoningAndUsageUpdateSubtypes_MapToSeparateProtocolEvents()
    {
        var reasoning = new ReasoningChatResponseUpdate
        {
            Thinking = true,
            Contents = [new TextContent("private reasoning")]
        };
        var answer = new ReasoningChatResponseUpdate
        {
            Thinking = false,
            Contents = [new TextContent("public answer")]
        };
        var usage = new UsageChatResponseUpdate
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 7,
                OutputTokenCount = 2,
                TotalTokenCount = 9
            }
        };

        Assert.Equal(
            "private reasoning",
            Assert.IsType<RuntimeReasoningDeltaEvent>(
                Assert.Single(MeaiRuntimeProtocolAdapter.ToProtocolEvents(reasoning))).Text);
        Assert.Equal(
            "public answer",
            Assert.IsType<RuntimeTextDeltaEvent>(
                Assert.Single(MeaiRuntimeProtocolAdapter.ToProtocolEvents(answer))).Text);
        Assert.Equal(
            9,
            Assert.IsType<RuntimeUsageEvent>(
                Assert.Single(MeaiRuntimeProtocolAdapter.ToProtocolEvents(usage))).Usage.TotalTokens);
    }

    [Fact]
    public void ToolArguments_RejectUnknownRuntimeTypesInsteadOfReflectionSerialization()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-1",
                    "unsafe",
                    new Dictionary<string, object?> { ["value"] = new UnsupportedArgument("x") })
            ])
        };

        var error = Assert.Throws<RuntimeProtocolAdapterException>(() =>
            MeaiRuntimeProtocolAdapter.ToProtocolMessages(messages));

        Assert.Contains(nameof(UnsupportedArgument), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeModelClient_StreamsProtocolEventsAndPropagatesCancellation()
    {
        var fake = new FakeChatClient();
        using var adapter = new MeaiRuntimeModelClient(fake, _ => new ChatOptions());
        var request = new RuntimeModelRequest(
            new RuntimeSessionId("session-1"),
            new RuntimeTurnId("turn-1"),
            new RuntimeStepId("step-1"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hello")])],
            [],
            new RuntimeModelParameters(),
            HistoryVersion: 0);
        var events = new List<RuntimeModelStreamEvent>();

        await foreach (var item in adapter.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        Assert.Collection(
            events,
            item => Assert.Equal("hello", Assert.IsType<RuntimeTextDeltaEvent>(item).Text),
            item => Assert.Equal(RuntimeModelStopReason.EndTurn, Assert.IsType<RuntimeModelCompletedEvent>(item).StopReason));
        Assert.Equal("hello", fake.LastMessages!.Single().Text);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in adapter.StreamAsync(request, cancelled.Token))
            {
            }
        });
    }

    [Fact]
    public async Task RuntimeModelClient_NormalizesMissingProviderFinishReason()
    {
        using var adapter = new MeaiRuntimeModelClient(
            new FakeChatClient(includeFinishReason: false),
            _ => new ChatOptions());
        var request = new RuntimeModelRequest(
            new RuntimeSessionId("session-1"),
            new RuntimeTurnId("turn-1"),
            new RuntimeStepId("step-1"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hello")])],
            [],
            new RuntimeModelParameters(),
            HistoryVersion: 0);
        var events = new List<RuntimeModelStreamEvent>();

        await foreach (var item in adapter.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        Assert.Equal(
            "missing_provider_finish_reason",
            Assert.IsType<RuntimeWarningEvent>(events[^2]).Warning.Code);
        Assert.Equal(
            RuntimeModelStopReason.Unknown,
            Assert.IsType<RuntimeModelCompletedEvent>(events[^1]).StopReason);
        var validator = new RuntimeModelStreamValidator();
        foreach (var item in events)
        {
            validator.Apply(item);
        }
        validator.Complete();
    }

    [Fact]
    public async Task RuntimeModelClient_ProjectsFrozenToolDescriptorsAndRequiredMode()
    {
        var fake = new FakeChatClient();
        using var adapter = new MeaiRuntimeModelClient(fake, _ => new ChatOptions());
        using var schemaDocument = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}");
        var request = new RuntimeModelRequest(
            new RuntimeSessionId("session-1"),
            new RuntimeTurnId("turn-1"),
            new RuntimeStepId("step-1"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hello")])],
            [new RuntimeToolDescriptor(
                "read_file",
                "1.0.0",
                "Read a file.",
                schemaDocument.RootElement.Clone(),
                RuntimeToolSideEffect.ReadOnly,
                RuntimeToolIdempotency.Idempotent)],
            new RuntimeModelParameters(RequiredToolName: "read_file"),
            HistoryVersion: 0);

        await foreach (var _ in adapter.StreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        var declaration = Assert.IsAssignableFrom<AIFunction>(Assert.Single(fake.LastOptions!.Tools!));
        Assert.Equal("read_file", declaration.Name);
        Assert.Equal("string", declaration.JsonSchema.GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
        Assert.Equal(ChatToolMode.RequireSpecific("read_file"), fake.LastOptions.ToolMode);
    }

    [Fact]
    public async Task RuntimeModelClient_BuffersFinishReasonUntilTrailingUsage()
    {
        using var adapter = new MeaiRuntimeModelClient(
            new FakeChatClient(emitUsageAfterFinish: true),
            _ => new ChatOptions());
        var request = new RuntimeModelRequest(
            new RuntimeSessionId("session-1"),
            new RuntimeTurnId("turn-1"),
            new RuntimeStepId("step-1"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hello")])],
            [],
            new RuntimeModelParameters(),
            HistoryVersion: 0);
        var events = new List<RuntimeModelStreamEvent>();

        await foreach (var item in adapter.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        Assert.Collection(
            events,
            static item => Assert.IsType<RuntimeTextDeltaEvent>(item),
            static item => Assert.IsType<RuntimeUsageEvent>(item),
            static item => Assert.IsType<RuntimeModelCompletedEvent>(item));
    }

    private sealed record UnsupportedArgument(string Value);

    private sealed class FakeChatClient(
        bool includeFinishReason = true,
        bool emitUsageAfterFinish = false) : IChatClient
    {
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastMessages = messages.ToArray();
            LastOptions = options;
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("hello")]);
            if (includeFinishReason)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    FinishReason = ChatFinishReason.Stop
                };
            }
            if (emitUsageAfterFinish)
            {
                yield return new UsageChatResponseUpdate
                {
                    Usage = new UsageDetails
                    {
                        InputTokenCount = 4,
                        OutputTokenCount = 1,
                        TotalTokenCount = 5
                    }
                };
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
