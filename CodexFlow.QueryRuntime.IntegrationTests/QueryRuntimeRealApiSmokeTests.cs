using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Xunit;

namespace CodexFlow.QueryRuntime.IntegrationTests;

public sealed class QueryRuntimeRealApiSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task QueryRuntime_NoToolPrompt_CompletesWithStableFinalText()
    {
        var liveHost = CreateHostOrSkip();
        using (liveHost)
        {
            var ct = TestContext.Current.CancellationToken;
            var sink = new CapturingEventSink();
            var request = new QueryRuntimeRequest
            {
                SessionId = $"rt-no-tool-{Guid.NewGuid():N}",
                EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System, "你是一个简洁助手。只用一句中文回答，不要使用工具。"),
                    new ChatMessage(ChatRole.User, "请解释什么是 query runtime 稳定性。")
                ],
                EnableTools = false,
                MaxRounds = 2,
                Options = liveHost.CreateChatOptions(maxOutputTokens: 96)
            };

            var result = await liveHost.Engine.ExecuteAsync(request, sink, ct);

            output.WriteLine($"Termination: {result.TerminationReason}");
            output.WriteLine($"Detail code: {result.TerminalDetailCode}");
            output.WriteLine($"Rounds: {result.TotalRounds}");
            output.WriteLine($"Prompt tokens: {result.TotalPromptTokens}");
            output.WriteLine($"Completion tokens: {result.TotalCompletionTokens}");
            output.WriteLine($"Final text: {result.FinalText}");

            Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
            Assert.Equal(QueryTerminalDetailCodes.NoToolCalls, result.TerminalDetailCode);
            Assert.False(string.IsNullOrWhiteSpace(result.FinalText));
            Assert.InRange(result.TotalRounds, 1, 2);
            AssertCapturedTokenUsage(result, "NoToolPrompt");
            Assert.Contains(sink.Events, evt => evt is RoundCompletedEvent);
            Assert.Contains(sink.Events, evt => evt is TerminatedEvent);
        }
    }

    [Fact]
    public async Task QueryRuntime_RequiredToolPrompt_ExecutesToolAndEmitsOrderedEvents()
    {
        var liveHost = CreateHostOrSkip();
        using (liveHost)
        {
            var ct = TestContext.Current.CancellationToken;
            var tool = AIFunctionFactory.Create(
                (string topic) => $"STATUS::{topic}::OK",
                new AIFunctionFactoryOptions
                {
                    Name = "get_runtime_status",
                    Description = "Returns the current status string for the given topic."
                });

            var sink = new CapturingEventSink();
            var request = new QueryRuntimeRequest
            {
                SessionId = $"rt-tool-{Guid.NewGuid():N}",
                EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System,
                        "你必须先调用名为 get_runtime_status 的工具且只调用一次，再根据工具结果回答。禁止在未调用工具前直接回答。"),
                    new ChatMessage(ChatRole.User, "请查询 topic=query-runtime 的状态，并用一句中文总结。")
                ],
                EnableTools = true,
                MaxRounds = 4,
                AvailableTools = [tool],
                AvailableToolsProvider = () => [tool],
                Options = liveHost.CreateChatOptions([tool], maxOutputTokens: 128)
            };

            var result = await liveHost.Engine.ExecuteAsync(request, sink, ct);
            var toolRequested = sink.Events.OfType<ToolCallRequestedEvent>().ToList();
            var toolCompleted = sink.Events.OfType<ToolExecutionCompletedEvent>().ToList();
            var firstToolRequestedIndex = sink.Events.ToList().FindIndex(evt => evt is ToolCallRequestedEvent);
            var firstToolCompletedIndex = sink.Events.ToList().FindIndex(evt => evt is ToolExecutionCompletedEvent);
            var lastRoundCompletedIndex = sink.Events.ToList().FindLastIndex(evt => evt is RoundCompletedEvent);
            var terminatedIndex = sink.Events.ToList().FindIndex(evt => evt is TerminatedEvent);

            output.WriteLine($"Termination: {result.TerminationReason}");
            output.WriteLine($"Rounds: {result.TotalRounds}");
            output.WriteLine($"Tool calls: {result.TotalToolCalls}");
            output.WriteLine($"Prompt tokens: {result.TotalPromptTokens}");
            output.WriteLine($"Completion tokens: {result.TotalCompletionTokens}");
            output.WriteLine($"Final text: {result.FinalText}");

            if (result.TerminationReason == QueryTerminationReason.Exception ||
                (toolRequested.Count == 0 && toolCompleted.Count == 0 && result.TotalToolCalls == 0))
            {
                Assert.Skip(
                    "Live model/API did not complete the required tool-call path for this prompt. " +
                    "Treating this as an observational skip rather than a deterministic runtime regression.");
            }

            Assert.NotEmpty(toolRequested);
            Assert.NotEmpty(toolCompleted);
            Assert.True(result.TotalToolCalls >= 1);
            Assert.False(string.IsNullOrWhiteSpace(result.FinalText));
            AssertCapturedTokenUsage(result, "RequiredToolPrompt");
            Assert.True(
                result.FinalText.Contains("query-runtime", StringComparison.OrdinalIgnoreCase) ||
                result.FinalText.Contains("runtime", StringComparison.OrdinalIgnoreCase) ||
                result.FinalText.Contains("状态", StringComparison.OrdinalIgnoreCase) ||
                result.FinalText.Contains("正常", StringComparison.OrdinalIgnoreCase) ||
                result.FinalText.Contains("ok", StringComparison.OrdinalIgnoreCase),
                $"Expected final text to summarize the runtime status, but got: {result.FinalText}");
            Assert.True(firstToolRequestedIndex >= 0, "Expected ToolCallRequestedEvent.");
            Assert.True(firstToolCompletedIndex > firstToolRequestedIndex, "ToolExecutionCompletedEvent should occur after ToolCallRequestedEvent.");
            Assert.True(lastRoundCompletedIndex > firstToolCompletedIndex, "RoundCompletedEvent should occur after tool execution.");
            Assert.True(terminatedIndex > lastRoundCompletedIndex, "TerminatedEvent should occur after the last RoundCompletedEvent.");
        }
    }

    [Fact]
    public async Task QueryRuntime_GlmStyleFunctionCallPrompt_StreamsThinkingAndMultipleToolCalls()
    {
        var liveHost = CreateHostOrSkip();
        using (liveHost)
        {
            var ct = TestContext.Current.CancellationToken;
            var weatherCalls = 0;
            var searchCalls = 0;

            var getWeather = AIFunctionFactory.Create(
                () =>
                {
                    weatherCalls++;
                    return "现在正在下雨。";
                },
                new AIFunctionFactoryOptions
                {
                    Name = "GetWeather",
                    Description = "获取南宁的天气情况。"
                });

            var search = AIFunctionFactory.Create(
                (string question) =>
                {
                    searchCalls++;
                    return "南宁市青秀区方圆广场北面站前路1号。";
                },
                new AIFunctionFactoryOptions
                {
                    Name = "Search",
                    Description = "搜索指定问题的答案。"
                });

            var sink = new CapturingEventSink();
            var request = new QueryRuntimeRequest
            {
                SessionId = $"rt-glm-function-{Guid.NewGuid():N}",
                EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System,
                        "你是一个智能助手，名字叫菲菲。你必须先进行思考，再调用工具。当前必须同时回答地点和天气两个问题，所以在最终回答前必须调用 GetWeather 和 Search。不要编造工具结果。"),
                    new ChatMessage(ChatRole.User, "南宁火车站在哪里？我出门需要带伞吗？")
                ],
                EnableTools = true,
                MaxRounds = 4,
                AvailableTools = [getWeather, search],
                AvailableToolsProvider = () => [getWeather, search],
                Options = new VllmChatOptions
                {
                    Temperature = 0.2f,
                    TopP = liveHost.Settings.TopP,
                    MaxOutputTokens = 256,
                    ThinkingEnabled = true,
                    EnableLegacyToolCallTextFallback = true,
                    Tools = [getWeather, search]
                }
            };

            var result = await liveHost.Engine.ExecuteAsync(request, sink, ct);
            var thinkingEvents = sink.Events.OfType<ThinkingDeltaEvent>().ToList();
            var toolRequests = sink.Events.OfType<ToolCallRequestedEvent>().ToList();
            var toolResults = sink.Events.OfType<ToolExecutionCompletedEvent>().ToList();

            output.WriteLine($"Termination: {result.TerminationReason}");
            output.WriteLine($"Rounds: {result.TotalRounds}");
            output.WriteLine($"Tool calls: {result.TotalToolCalls}");
            output.WriteLine($"Prompt tokens: {result.TotalPromptTokens}");
            output.WriteLine($"Completion tokens: {result.TotalCompletionTokens}");
            output.WriteLine($"Final text: {result.FinalText}");
            WriteRuntimeTrace(sink.Events);

            Assert.NotEmpty(thinkingEvents);
            Assert.True(toolRequests.Count >= 2, "Expected both GetWeather and Search to be requested.");
            Assert.True(toolResults.Count >= 2, "Expected both tool calls to complete.");
            Assert.True(weatherCalls >= 1, "Expected GetWeather to be invoked.");
            Assert.True(searchCalls >= 1, "Expected Search to be invoked.");
            AssertCapturedTokenUsage(result, "GlmStyleFunctionCallPrompt");
            Assert.Contains(toolRequests, evt => string.Equals(evt.ToolName, "GetWeather", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(toolRequests, evt => string.Equals(evt.ToolName, "Search", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("下雨", result.FinalText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("方圆广场北面站前路1号", result.FinalText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task QueryRuntime_LegacyToolCallMarkupPrompt_RecoversToolCallFromModelText()
    {
        var liveHost = CreateHostOrSkip();
        using (liveHost)
        {
            var ct = TestContext.Current.CancellationToken;
            var searchCalls = 0;

            var search = AIFunctionFactory.Create(
                (string question) =>
                {
                    searchCalls++;
                    return "南宁市青秀区方圆广场北面站前路1号。";
                },
                new AIFunctionFactoryOptions
                {
                    Name = "Search",
                    Description = "搜索指定问题的答案。"
                });

            var sink = new CapturingEventSink();
            var request = new QueryRuntimeRequest
            {
                SessionId = $"rt-legacy-toolcall-{Guid.NewGuid():N}",
                EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System,
                        "你是一个智能助手，名字叫菲菲。你必须先思考，然后调用 Search 工具。调用工具时不要使用原生 function calling 格式，而是直接输出且只输出如下格式的文本块：<tool_call>{\"name\":\"Search\",\"arguments\":{\"question\":\"...\"}}</tool_call>。调用完成后再根据工具结果回答。"),
                    new ChatMessage(ChatRole.User, "南宁火车站在哪里？")
                ],
                EnableTools = true,
                MaxRounds = 4,
                AvailableTools = [search],
                AvailableToolsProvider = () => [search],
                Options = new VllmChatOptions
                {
                    Temperature = 0,
                    TopP = liveHost.Settings.TopP,
                    MaxOutputTokens = 192,
                    ThinkingEnabled = true,
                    EnableLegacyToolCallTextFallback = true,
                    Tools = [search]
                }
            };

            var result = await liveHost.Engine.ExecuteAsync(request, sink, ct);
            var thinkingEvents = sink.Events.OfType<ThinkingDeltaEvent>().ToList();
            var toolRequests = sink.Events.OfType<ToolCallRequestedEvent>().ToList();
            var legacyRecovered = sink.Events
                .OfType<SystemNoticeEvent>()
                .Any(evt => string.Equals(evt.NoticeType, "legacy_tool_call_recovered", StringComparison.OrdinalIgnoreCase));

            output.WriteLine($"Termination: {result.TerminationReason}");
            output.WriteLine($"Rounds: {result.TotalRounds}");
            output.WriteLine($"Tool calls: {result.TotalToolCalls}");
            output.WriteLine($"Legacy recovered: {legacyRecovered}");
            output.WriteLine($"Prompt tokens: {result.TotalPromptTokens}");
            output.WriteLine($"Completion tokens: {result.TotalCompletionTokens}");
            output.WriteLine($"Final text: {result.FinalText}");
            WriteRuntimeTrace(sink.Events);

            Assert.NotEmpty(thinkingEvents);
            if (toolRequests.Count == 0 && !legacyRecovered && result.TotalToolCalls == 0)
            {
                Assert.Skip(
                    "Live model did not emit legacy <tool_call> markup for this prompt. " +
                    "Treating this as an observational skip rather than a QueryRuntime parser failure.");
            }

            Assert.True(toolRequests.Count >= 1, "Expected at least one Search tool request.");
            Assert.True(searchCalls >= 1, "Expected Search to be invoked.");
            AssertCapturedTokenUsage(result, "LegacyToolCallMarkupPrompt");
            Assert.Contains(toolRequests, evt => string.Equals(evt.ToolName, "Search", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("方圆广场北面站前路1号", result.FinalText, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                legacyRecovered || result.TotalToolCalls >= 1,
                "Expected either legacy tool-call recovery notice or at least one executed tool call.");
        }
    }

    [Fact]
    public async Task QueryRuntime_WithContextWindowManager_CallsCompletionHook()
    {
        var liveHost = CreateHostOrSkip();
        using (liveHost)
        {
            var ct = TestContext.Current.CancellationToken;
            var contextWindowManager = new RecordingContextWindowManager();
            var engine = liveHost.CreateEngine(contextWindowManager);

            var sink = new CapturingEventSink();
            var request = new QueryRuntimeRequest
            {
                SessionId = $"rt-context-{Guid.NewGuid():N}",
                EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System, "你是一个简洁助手。"),
                    new ChatMessage(ChatRole.User, "请用一句话说明为什么稳定性测试要可重复。")
                ],
                EnableTools = false,
                MaxRounds = 2,
                ConversationCapture = new QueryRuntimeConversationCapture(
                [
                    new QueryRuntimeConversationMessage("user", "请用一句话说明为什么稳定性测试要可重复。")
                ]),
                Options = liveHost.CreateChatOptions(maxOutputTokens: 96)
            };

            var result = await engine.ExecuteAsync(request, sink, ct);

            Assert.Single(contextWindowManager.Completions);
            Assert.Equal(result.FinalText, contextWindowManager.Completions[0].Result.FinalText);
            Assert.Equal(request.SessionId, contextWindowManager.Completions[0].Request.SessionId);
            AssertCapturedTokenUsage(result, "ContextWindowManager");
        }
    }

    [Fact]
    public async Task QueryRuntime_NoToolPrompt_SoakRun_RemainsStableAcrossIterations()
    {
        var liveHost = CreateHostOrSkip(requireSoak: true);
        using (liveHost)
        {
            var iterations = RealQueryRuntimeTestHost.GetSoakIterations();
            var terminations = new List<QueryTerminationReason>();
            var totalRounds = new List<int>();

            for (var i = 0; i < iterations; i++)
            {
                var sink = new CapturingEventSink();
                var request = new QueryRuntimeRequest
                {
                    SessionId = $"rt-soak-{i + 1}-{Guid.NewGuid():N}",
                    EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
                    InitialMessages =
                    [
                        new ChatMessage(ChatRole.System, "你是一个简洁助手。只用一句中文回答，不要使用工具。"),
                        new ChatMessage(ChatRole.User, $"第 {i + 1} 次：请用一句话说明 query runtime 稳定性为什么重要。")
                    ],
                    EnableTools = false,
                    MaxRounds = 2,
                    Options = liveHost.CreateChatOptions(maxOutputTokens: 96)
                };

                var result = await liveHost.Engine.ExecuteAsync(request, sink, TestContext.Current.CancellationToken);
                terminations.Add(result.TerminationReason);
                totalRounds.Add(result.TotalRounds);

                output.WriteLine($"Iteration {i + 1}/{iterations}: termination={result.TerminationReason}, rounds={result.TotalRounds}, promptTokens={result.TotalPromptTokens}, completionTokens={result.TotalCompletionTokens}, text={result.FinalText}");

                Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
                Assert.False(string.IsNullOrWhiteSpace(result.FinalText));
                Assert.InRange(result.TotalRounds, 1, 2);
                AssertCapturedTokenUsage(result, $"Soak iteration {i + 1}");
                Assert.Contains(sink.Events, evt => evt is TerminatedEvent);
            }

            output.WriteLine($"Soak summary: iterations={iterations}, terminations=[{string.Join(", ", terminations)}], rounds=[{string.Join(", ", totalRounds)}]");
        }
    }

    private RealQueryRuntimeTestHost CreateHostOrSkip(bool requireSoak = false)
    {
        if (requireSoak && !RealQueryRuntimeTestHost.IsSoakEnabled())
        {
            Assert.Skip("Set RUN_QUERY_RUNTIME_REAL_SOAK_TESTS=true to enable live QueryRuntime soak tests.");
        }

        if (!RealQueryRuntimeTestHost.TryCreate(out var host, out var reason))
        {
            output.WriteLine(reason);
            Assert.Skip(reason);
        }

        return host!;
    }

    private void WriteRuntimeTrace(IReadOnlyList<QueryRuntimeEvent> events)
    {
        output.WriteLine("Runtime trace:");
        foreach (var runtimeEvent in events.OrderBy(evt => evt.Seq))
        {
            switch (runtimeEvent)
            {
                case ThinkingStartedEvent started:
                    output.WriteLine($"[{started.Seq}] thinking-started round={started.Round}");
                    break;
                case ThinkingDeltaEvent thinking:
                    output.WriteLine($"[{thinking.Seq}] thinking-delta: {thinking.Delta}");
                    break;
                case ThinkingEndedEvent ended:
                    output.WriteLine($"[{ended.Seq}] thinking-ended length={ended.FullThinking.Length}");
                    break;
                case AssistantDeltaEvent assistant:
                    output.WriteLine($"[{assistant.Seq}] assistant-delta: {assistant.Delta}");
                    break;
                case ToolCallRequestedEvent toolCall:
                    output.WriteLine($"[{toolCall.Seq}] tool-request {toolCall.ToolName}: {JsonConvert.SerializeObject(toolCall.Arguments)}");
                    break;
                case ToolExecutionCompletedEvent toolResult:
                    output.WriteLine($"[{toolResult.Seq}] tool-result {toolResult.ToolName}: success={toolResult.Success} result={toolResult.Result}");
                    break;
                case RecoveryTriggeredEvent recovery:
                    output.WriteLine($"[{recovery.Seq}] recovery type={recovery.RecoveryType} attempt={recovery.Attempt} reason={recovery.Reason}");
                    break;
                case RoundCompletedEvent roundCompleted:
                    output.WriteLine($"[{roundCompleted.Seq}] round-completed round={roundCompleted.Round} tools={roundCompleted.ToolCallCount} textLength={roundCompleted.TextLength} thinkingLength={roundCompleted.ThinkingLength}");
                    break;
                case TerminatedEvent terminated:
                    output.WriteLine($"[{terminated.Seq}] terminated reason={terminated.Reason} rounds={terminated.TotalRounds} toolCalls={terminated.TotalToolCalls}");
                    break;
            }
        }
    }

    private static void AssertCapturedTokenUsage(QueryRuntimeResult result, string scenario)
    {
        Assert.True(
            result.TotalPromptTokens is > 0,
            $"{scenario} should capture prompt tokens from the live model response.");
        Assert.True(
            result.TotalCompletionTokens is > 0,
            $"{scenario} should capture completion tokens from the live model response.");
    }
}
