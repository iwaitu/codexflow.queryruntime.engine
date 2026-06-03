using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Hashline;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using CodexFlow.Core.Workers;
using CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System.Text;
using Xunit;

namespace CodexFlow.QueryRuntime.IntegrationTests;

public sealed class TaskBug001ForgeWorkerRealLlmTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TaskBug001_ForgeWorker_RealLlm_CompletesHashlineWriteFlow()
    {
        if (!IsTaskBug001LiveTestEnabled())
        {
            Assert.Skip("Set RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true and RUN_TASK_BUG_001_REAL_LLM_TESTS=true to run this live Forge worker regression test.");
        }

        var liveHost = CreateHostOrSkip();
        using (liveHost)
        {
            var workspace = CreateTaskBugWorkspace();
            var keepWorkspace = ShouldKeepWorkspace();
            output.WriteLine($"Workspace: {workspace}");

            try
            {
                using var serviceProvider = CreateHashlineServiceProvider(workspace);
                var hashlineOptions = serviceProvider.GetRequiredService<HashlineOptions>();
                var hashlineService = serviceProvider.GetRequiredService<IHashlineFileService>();
                var tools = CreateForgeTools(hashlineService, hashlineOptions);
                var functions = tools.Select(CodexToolFunctionAdapterFactory.CreateAIFunction).ToArray();
                var workerRegistry = new DefaultWorkerDefinitionRegistry(hashlineOptions);
                var workerDefinition = workerRegistry.GetRequired(WorkerType.Forge);
                var workerContext = workerRegistry.BuildRuntimeContext(WorkerType.Forge);
                var sessionId = $"task-bug-001-forge-{Guid.NewGuid():N}";
                var session = new CodexSession
                {
                    Id = sessionId,
                    WorkspacePath = workspace,
                    ProjectSummary = "TASK_BUG_001 live regression workspace: one existing C# domain file must be changed with Hashline."
                };
                var sink = new CapturingEventSink();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(4));

                var request = new QueryRuntimeRequest
                {
                    SessionId = sessionId,
                    EntryPoint = QueryLoopEntryPoint.ForgeWorker,
                    Session = session,
                    Scenario = MemoryInjectionScenario.Execution,
                    InitialMessages =
                    [
                        new ChatMessage(ChatRole.System, workerDefinition.BuildSystemPrompt(session)),
                        new ChatMessage(ChatRole.User,
                            "TASK_BUG_001 回归测试：请修改既有文件 `src/CleanApp.Domain/AppFile.cs`，" +
                            "把 `public string Status { get; set; } = \"Pending\";` 改成默认值 `\"Ready\"`。" +
                            "必须先调用 `hs_read` 读取该文件快照，然后调用 `hs_write` 提交修改。" +
                            "不要使用 `write_file`、`apply_patch` 或 `ivilson_smart_patch`，不要只描述计划。")
                    ],
                    EnableTools = true,
                    MaxRounds = 8,
                    AvailableTools = functions,
                    AvailableToolsProvider = () => functions,
                    AvailableCodexTools = tools,
                    AvailableCodexToolsProvider = () => tools,
                    WorkerContext = workerContext,
                    RequiredToolContract = workerDefinition.RequiredToolContract,
                    PromptMetadata = new PromptMetadata(
                        RolePrompt: workerDefinition.BuildSystemPrompt(session),
                        WorkspacePath: workspace,
                        PlanSize: null,
                        InitialStage: 3),
                    AdapterHints = new AdapterHints(
                        EnableEmptyResponseRecovery: true,
                        EnableMalformedProtocolRecovery: true,
                        EnableToolDeduplication: true,
                        MaxRecoveryAttempts: 3),
                    Options = new VllmChatOptions
                    {
                        Temperature = 0.1f,
                        TopP = liveHost.Settings.TopP,
                        MaxOutputTokens = 1024,
                        ThinkingEnabled = IsThinkingTraceEnabled(),
                        EnableLegacyToolCallTextFallback = true,
                        Tools = functions.Cast<AITool>().ToList()
                    }
                };

                WriteWorkerInput(liveHost, workspace, session, workerDefinition, workerContext, request, tools, functions);

                var result = await liveHost.Engine.ExecuteAsync(request, sink, cts.Token);
                var finalFile = await File.ReadAllTextAsync(
                    Path.Combine(workspace, "src", "CleanApp.Domain", "AppFile.cs"),
                    cts.Token);

                WriteDetailedWorkerOutput(request, result, sink.Events, finalFile);

                Assert.NotEqual(QueryTerminationReason.Exception, result.TerminationReason);
                Assert.NotEqual(QueryTerminationReason.RecoveryExhausted, result.TerminationReason);
                Assert.Contains("= \"Ready\";", finalFile, StringComparison.Ordinal);
                Assert.DoesNotContain("= \"Pending\";", finalFile, StringComparison.Ordinal);
                Assert.True(result.WriteToolCalls >= 1, "Expected Forge worker to execute at least one write tool.");
                Assert.Contains(
                    sink.Events.OfType<ToolExecutionCompletedEvent>(),
                    evt => string.Equals(evt.ToolName, "hs_write", StringComparison.OrdinalIgnoreCase) && evt.Success);
                Assert.Contains(
                    sink.Events.OfType<ToolExecutionCompletedEvent>(),
                    evt => string.Equals(evt.ToolName, "hs_read", StringComparison.OrdinalIgnoreCase) && evt.Success);
            }
            finally
            {
                if (!keepWorkspace)
                {
                    Directory.Delete(workspace, recursive: true);
                }
            }
        }
    }

    private RealQueryRuntimeTestHost CreateHostOrSkip()
    {
        if (!RealQueryRuntimeTestHost.TryCreate(out var host, out var reason))
        {
            output.WriteLine(reason);
            Assert.Skip(reason);
        }

        return host!;
    }

    private static bool IsTaskBug001LiveTestEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("RUN_TASK_BUG_001_REAL_LLM_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static bool ShouldKeepWorkspace()
        => string.Equals(
            Environment.GetEnvironmentVariable("KEEP_TASK_BUG_001_WORKSPACE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsThinkingTraceEnabled()
        => !string.Equals(
            Environment.GetEnvironmentVariable("TASK_BUG_001_THINKING_ENABLED"),
            "false",
            StringComparison.OrdinalIgnoreCase);

    private static string CreateTaskBugWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "codexflow-task-bug-001-" + Guid.NewGuid().ToString("N"));
        var domainDir = Path.Combine(workspace, "src", "CleanApp.Domain");
        Directory.CreateDirectory(domainDir);
        File.WriteAllText(
            Path.Combine(domainDir, "AppFile.cs"),
            """
            namespace CleanApp.Domain;

            public sealed class AppFile
            {
                public string Id { get; set; } = string.Empty;
                public string FileName { get; set; } = string.Empty;
                public string Status { get; set; } = "Pending";
            }
            """);
        return workspace;
    }

    private static ServiceProvider CreateHashlineServiceProvider(string workspace)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHashlineServices(new HashlineOptions
        {
            Enabled = true,
            ForceForHighRiskFiles = true,
            AllowRewriteWholeFile = true,
            EnableAuditLogging = false,
            AllowedRoots = [workspace]
        });
        return services.BuildServiceProvider();
    }

    private static IReadOnlyList<ICodexTool> CreateForgeTools(
        IHashlineFileService hashlineService,
        HashlineOptions hashlineOptions)
    {
        var readFileTool = new ReadFileTool(
            NullLogger<ReadFileTool>.Instance,
            hashlineService,
            hashlineOptions);
        var applyPatchTool = new ApplyPatchTool(
            new NoOpGitService(),
            hashlineService,
            hashlineOptions,
            NullLogger<ApplyPatchTool>.Instance);

        return
        [
            new HashlineReadTool(readFileTool),
            readFileTool,
            new HashlineWriteTool(applyPatchTool, hashlineService)
        ];
    }

    private void WriteWorkerInput(
        RealQueryRuntimeTestHost liveHost,
        string workspace,
        CodexSession session,
        WorkerDefinition workerDefinition,
        WorkerRuntimeContext workerContext,
        QueryRuntimeRequest request,
        IReadOnlyList<ICodexTool> tools,
        IReadOnlyList<AIFunction> functions)
    {
        output.WriteLine("========== TASK_BUG_001 WORKER INPUT ==========");
        output.WriteLine($"Model: {liveHost.Settings.Model}");
        output.WriteLine($"ApiMode: {liveHost.Settings.ApiMode}");
        output.WriteLine($"Workspace: {workspace}");
        output.WriteLine($"SessionId: {session.Id}");
        output.WriteLine($"EntryPoint: {request.EntryPoint}");
        output.WriteLine($"Scenario: {request.Scenario}");
        output.WriteLine($"MaxRounds: {request.MaxRounds}");
        output.WriteLine($"ThinkingEnabled: {(request.Options as VllmChatOptions)?.ThinkingEnabled}");
        output.WriteLine($"Temperature: {request.Options?.Temperature}");
        output.WriteLine($"TopP: {request.Options?.TopP}");
        output.WriteLine($"MaxOutputTokens: {request.Options?.MaxOutputTokens}");
        output.WriteLine($"Worker: {workerDefinition.DisplayName} ({workerDefinition.WorkerType})");
        output.WriteLine($"IsolationMode: {workerContext.IsolationMode}");
        output.WriteLine($"RequiredToolContract: {request.RequiredToolContract?.Name}");
        output.WriteLine($"RequiredToolPreferred: {request.RequiredToolContract?.PreferredRecoveryToolName}");
        output.WriteLine($"RequiredToolAnyOf: {request.RequiredToolContract?.FormatToolList()}");
        output.WriteLine($"Codex tools: {string.Join(", ", tools.Select(static tool => tool.Name))}");
        output.WriteLine($"LLM functions: {string.Join(", ", functions.Select(static tool => tool.Name))}");
        output.WriteLine("---------- Initial Messages ----------");
        WriteMessages(request.InitialMessages, includeFullToolResults: true);
        output.WriteLine("========== END WORKER INPUT ==========");
    }

    private void WriteDetailedWorkerOutput(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        IReadOnlyList<QueryRuntimeEvent> events,
        string finalFile)
    {
        output.WriteLine("========== TASK_BUG_001 WORKER OUTPUT ==========");
        WriteResult(result);
        WriteRuntimeCheckpoint(result.RuntimeCheckpoint);
        WriteAggregatedWorkerTranscript(events);
        WriteRuntimeTrace(events);
        output.WriteLine("---------- Final Runtime Messages ----------");
        if (result.FinalMessages is { Count: > 0 } finalMessages)
        {
            WriteMessages(finalMessages, includeFullToolResults: true);
        }
        else
        {
            output.WriteLine("(no final messages captured)");
        }

        output.WriteLine("---------- Final AppFile.cs ----------");
        output.WriteLine(finalFile);
        output.WriteLine("---------- Output Summary ----------");
        output.WriteLine($"Initial user request: {request.InitialMessages.LastOrDefault(message => message.Role == ChatRole.User)?.Text}");
        output.WriteLine($"Final returned text: {result.FinalText}");
        output.WriteLine("========== END WORKER OUTPUT ==========");
    }

    private void WriteResult(QueryRuntimeResult result)
    {
        output.WriteLine("---------- Result ----------");
        output.WriteLine($"Termination: {result.TerminationReason}");
        output.WriteLine($"Detail code: {result.TerminalDetailCode}");
        output.WriteLine($"Rounds: {result.TotalRounds}");
        output.WriteLine($"Tool calls: {result.TotalToolCalls}");
        output.WriteLine($"Write tool calls: {result.WriteToolCalls}");
        output.WriteLine($"Recoveries: {result.RecoveryCount}");
        output.WriteLine($"Flags: {result.Flags}");
        output.WriteLine($"Prompt tokens: {result.TotalPromptTokens}");
        output.WriteLine($"Completion tokens: {result.TotalCompletionTokens}");
        output.WriteLine($"Final text: {result.FinalText}");
        if (!string.IsNullOrWhiteSpace(result.FinalThinking))
        {
            output.WriteLine("Final thinking:");
            output.WriteLine(result.FinalThinking);
        }
    }

    private void WriteRuntimeCheckpoint(QueryRuntimeCheckpoint? checkpoint)
    {
        output.WriteLine("---------- Runtime Checkpoint ----------");
        if (checkpoint == null)
        {
            output.WriteLine("(none)");
            return;
        }

        output.WriteLine($"QueryId: {checkpoint.QueryId}");
        output.WriteLine($"SessionId: {checkpoint.SessionId}");
        output.WriteLine($"EntryPoint: {checkpoint.EntryPoint}");
        output.WriteLine($"Round: {checkpoint.Round}");
        output.WriteLine($"CurrentPhase: {checkpoint.CurrentPhase}");
        output.WriteLine($"ActiveTask: {checkpoint.ActiveTaskSummary}");
        output.WriteLine($"Worker: {checkpoint.WorkerDisplayName} ({checkpoint.WorkerType}) isolation={checkpoint.WorkerIsolationMode}");
        output.WriteLine($"RequiredContract: {checkpoint.RequiredToolContractName} satisfied={checkpoint.RequiredToolContractSatisfied}");
        output.WriteLine($"RequiredContractTools: {string.Join(", ", checkpoint.RequiredToolContractToolNames)}");
        output.WriteLine($"RequiredToolNextRound: {checkpoint.RequiredToolNameForNextRound ?? "(none)"}");
        output.WriteLine($"ToolCalls: {checkpoint.TotalToolCalls} WriteToolCalls: {checkpoint.WriteToolCalls} Recoveries: {checkpoint.RecoveryCount}");
        output.WriteLine($"LastContinueReason: {checkpoint.LastContinueReason ?? "(none)"}");
        output.WriteLine($"Ledger files={checkpoint.EvidenceLedger.Files.Count} tools={checkpoint.EvidenceLedger.ToolResults.Count} pending={checkpoint.EvidenceLedger.PendingModifications.Count} failures={checkpoint.EvidenceLedger.Failures.Count}");

        foreach (var file in checkpoint.EvidenceLedger.Files)
        {
            output.WriteLine($"  file {file.FilePath} tool={file.ToolName} snapshot={file.SnapshotId ?? "(none)"} fp={file.FileFingerprint ?? "(none)"} lines={file.WindowStartLine?.ToString() ?? "?"}-{file.WindowEndLine?.ToString() ?? "?"}");
            if (!string.IsNullOrWhiteSpace(file.Summary))
            {
                output.WriteLine($"    summary={file.Summary}");
            }
        }

        foreach (var tool in checkpoint.EvidenceLedger.ToolResults)
        {
            output.WriteLine($"  tool {tool.ToolName} callId={tool.CallId} success={tool.Success} length={tool.ResultLength} truncated={tool.IsOutputTruncated}");
            if (!string.IsNullOrWhiteSpace(tool.Summary))
            {
                output.WriteLine($"    summary={tool.Summary}");
            }
        }

        foreach (var pending in checkpoint.EvidenceLedger.PendingModifications)
        {
            output.WriteLine($"  pending source={pending.Source} requiredTool={pending.RequiredToolName ?? "(none)"} files={string.Join(", ", pending.CandidateFiles)}");
            if (!string.IsNullOrWhiteSpace(pending.AssistantPlanSummary))
            {
                output.WriteLine($"    plan={pending.AssistantPlanSummary}");
            }
        }
    }

    private void WriteAggregatedWorkerTranscript(IReadOnlyList<QueryRuntimeEvent> events)
    {
        output.WriteLine("---------- Aggregated Worker Transcript ----------");
        var byRound = new SortedDictionary<int, RoundTrace>();
        foreach (var runtimeEvent in events)
        {
            switch (runtimeEvent)
            {
                case ThinkingDeltaEvent thinking:
                    GetRound(thinking.Round).Thinking.Append(thinking.Delta);
                    break;
                case AssistantDeltaEvent assistant:
                    GetRound(assistant.Round).Assistant.Append(assistant.Delta);
                    break;
                case ToolCallRequestedEvent requested:
                    GetRound(requested.Round).ToolRequests.Add(requested);
                    break;
                case ToolExecutionCompletedEvent completed:
                    GetRound(completed.Round).ToolResults.Add(completed);
                    break;
                case RecoveryTriggeredEvent recovery:
                    GetRound(recovery.Round).Recoveries.Add(recovery);
                    break;
                case SystemNoticeEvent notice:
                    GetRound(-1).Notices.Add(notice);
                    break;
            }
        }

        foreach (var (round, trace) in byRound)
        {
            var roundLabel = round < 0 ? "global" : round.ToString();
            output.WriteLine($"[round {roundLabel}]");
            output.WriteLine(trace.Thinking.Length > 0 ? "thinking:" : "thinking: (none emitted)");
            if (trace.Thinking.Length > 0)
            {
                output.WriteLine(trace.Thinking.ToString());
            }

            output.WriteLine(trace.Assistant.Length > 0 ? "assistant:" : "assistant: (no visible text)");
            if (trace.Assistant.Length > 0)
            {
                output.WriteLine(trace.Assistant.ToString());
            }

            foreach (var request in trace.ToolRequests)
            {
                output.WriteLine($"tool_call: {request.ToolName} callId={request.CallId}");
                output.WriteLine(JsonConvert.SerializeObject(request.Arguments, Formatting.Indented));
            }

            foreach (var result in trace.ToolResults)
            {
                output.WriteLine($"tool_result: {result.ToolName} callId={result.CallId} success={result.Success} length={result.ResultLength}");
                output.WriteLine(result.Result);
            }

            foreach (var recovery in trace.Recoveries)
            {
                output.WriteLine($"recovery: type={recovery.RecoveryType} attempt={recovery.Attempt} reason={recovery.Reason}");
            }

            foreach (var notice in trace.Notices)
            {
                output.WriteLine($"notice: type={notice.NoticeType}");
                output.WriteLine(notice.Content);
            }
        }

        RoundTrace GetRound(int round)
        {
            if (!byRound.TryGetValue(round, out var trace))
            {
                trace = new RoundTrace();
                byRound[round] = trace;
            }

            return trace;
        }
    }

    private void WriteRuntimeTrace(IReadOnlyList<QueryRuntimeEvent> events)
    {
        output.WriteLine("---------- Runtime Event Trace ----------");
        foreach (var runtimeEvent in events.OrderBy(evt => evt.Seq))
        {
            switch (runtimeEvent)
            {
                case ThinkingStartedEvent thinkingStarted:
                    output.WriteLine($"[{thinkingStarted.Seq}] thinking-started round={thinkingStarted.Round}");
                    break;
                case ThinkingDeltaEvent thinking:
                    output.WriteLine($"[{thinking.Seq}] thinking-delta round={thinking.Round}: {thinking.Delta}");
                    break;
                case ThinkingEndedEvent thinkingEnded:
                    output.WriteLine($"[{thinkingEnded.Seq}] thinking-ended round={thinkingEnded.Round} length={thinkingEnded.FullThinking.Length}");
                    output.WriteLine(thinkingEnded.FullThinking);
                    break;
                case AssistantDeltaEvent assistant:
                    output.WriteLine($"[{assistant.Seq}] assistant round={assistant.Round}: {assistant.Delta}");
                    break;
                case ModelResponseSampledEvent sampled:
                    output.WriteLine($"[{sampled.Seq}] model-sampled round={sampled.Round} textLength={sampled.AssistantTextLength} thinkingLength={sampled.ThinkingTextLength} structuredToolCalls={sampled.StructuredToolCallCount} prestartedTools={sampled.PrestartedToolExecutionCount}");
                    break;
                case ToolCallRequestedEvent toolCall:
                    output.WriteLine($"[{toolCall.Seq}] tool-request round={toolCall.Round} {toolCall.ToolName} callId={toolCall.CallId}: {JsonConvert.SerializeObject(toolCall.Arguments, Formatting.Indented)}");
                    break;
                case ToolExecutionStartedEvent started:
                    output.WriteLine($"[{started.Seq}] tool-start round={started.Round} {started.ToolName} callId={started.CallId}");
                    break;
                case ToolExecutionCompletedEvent toolResult:
                    output.WriteLine($"[{toolResult.Seq}] tool-result round={toolResult.Round} {toolResult.ToolName} callId={toolResult.CallId}: success={toolResult.Success} length={toolResult.ResultLength}");
                    output.WriteLine(toolResult.Result);
                    break;
                case PromptAssemblySnapshotEvent snapshot:
                    output.WriteLine($"[{snapshot.Seq}] prompt-assembly round={snapshot.Round}");
                    WritePromptAssemblySnapshot(snapshot.Snapshot);
                    break;
                case LoopPhaseChangedEvent phase:
                    output.WriteLine($"[{phase.Seq}] loop-phase round={phase.Round} phase={phase.Phase} detail={phase.Detail ?? "(none)"}");
                    break;
                case ToolPlanExtractedEvent plan:
                    output.WriteLine($"[{plan.Seq}] tool-plan round={plan.Round} calls={plan.ToolCallCount} legacyFallback={plan.FromLegacyTextFallback} assistantTextLength={plan.AssistantTextLength} thinkingLength={plan.ThinkingTextLength}");
                    break;
                case ToolPlanValidatedEvent validation:
                    output.WriteLine($"[{validation.Seq}] tool-plan-validation round={validation.Round} accepted={validation.AcceptedCount} rejected={validation.RejectedCount} recovery={validation.RequiresRecovery} reason={validation.RecoveryReason ?? "(none)"}");
                    foreach (var rejected in validation.RejectedCalls)
                    {
                        output.WriteLine($"  rejected {rejected.ToolName} callId={rejected.CallId} reason={rejected.ReasonCode} detail={rejected.Detail}");
                    }

                    break;
                case ToolArgumentsNormalizedEvent normalized:
                    output.WriteLine($"[{normalized.Seq}] tool-arguments-normalized round={normalized.Round} accepted={normalized.AcceptedCount} normalized={normalized.NormalizedCount} prestarted={normalized.PrestartedToolCount}");
                    break;
                case ToolObservationCompletedEvent observation:
                    output.WriteLine($"[{observation.Seq}] tool-observation round={observation.Round} results={observation.ToolResultCount} writeEvidence={observation.HasWriteEvidence} repeatedRead={observation.HasRepeatedReadEvidence} requiredContractSatisfied={observation.RequiredToolContractSatisfied}");
                    output.WriteLine($"  ledger files={observation.FileEvidenceCount} tools={observation.ToolEvidenceCount} pendingModifications={observation.PendingModificationCount} failures={observation.FailureCount}");
                    if (observation.RepeatedReadTargets.Count > 0)
                    {
                        output.WriteLine($"  repeatedTargets={string.Join(", ", observation.RepeatedReadTargets)}");
                    }

                    break;
                case ContextCompactionCompletedEvent compaction:
                    output.WriteLine($"[{compaction.Seq}] context-compaction round={compaction.Round} success={compaction.Success} messages={compaction.FinalMessageCount} files={compaction.FileEvidenceCount} pending={compaction.PendingModificationCount} error={compaction.ErrorType ?? "(none)"}:{compaction.ErrorMessage ?? "(none)"}");
                    break;
                case RecoveryTriggeredEvent recovery:
                    output.WriteLine($"[{recovery.Seq}] recovery round={recovery.Round} type={recovery.RecoveryType} attempt={recovery.Attempt} reason={recovery.Reason}");
                    break;
                case SystemNoticeEvent notice:
                    output.WriteLine($"[{notice.Seq}] notice type={notice.NoticeType}: {notice.Content}");
                    break;
                case RoundCompletedEvent completed:
                    output.WriteLine($"[{completed.Seq}] round-completed round={completed.Round} tools={completed.ToolCallCount} textLength={completed.TextLength} continue={completed.ContinueReason}");
                    break;
                case TerminatedEvent terminated:
                    output.WriteLine($"[{terminated.Seq}] terminated reason={terminated.Reason} rounds={terminated.TotalRounds} toolCalls={terminated.TotalToolCalls}");
                    break;
            }
        }
    }

    private void WritePromptAssemblySnapshot(PromptAssemblySnapshot snapshot)
    {
        output.WriteLine($"  messages={snapshot.MessageCount} toolsEnabled={snapshot.ToolsEnabled} toolCallsAllowed={snapshot.ToolCallsAllowed}");
        output.WriteLine($"  tools=[{string.Join(", ", snapshot.ToolNames)}]");
        output.WriteLine($"  toolChoice={snapshot.ToolChoice ?? "(none)"} requiredTool={snapshot.RequiredToolName ?? "(none)"}");
        output.WriteLine($"  estimatedContextChars={snapshot.EstimatedContextChars} estimatedPromptTokens={snapshot.EstimatedPromptTokens}");
        if (snapshot.DroppedFrames.Count > 0)
        {
            output.WriteLine($"  droppedFrames=[{string.Join(", ", snapshot.DroppedFrames)}]");
        }

        if (snapshot.BudgetDecisions.Count > 0)
        {
            output.WriteLine("  budgetDecisions:");
            foreach (var decision in snapshot.BudgetDecisions)
            {
                output.WriteLine($"    - {decision}");
            }
        }

        output.WriteLine("  frames:");
        foreach (var frame in snapshot.Frames)
        {
            output.WriteLine($"    - {frame.Name} kind={frame.Kind} priority={frame.Priority} chars={frame.EstimatedChars} tokens={frame.EstimatedTokens} stable={frame.StableAcrossRounds} compressible={frame.Compressible}");
            if (!string.IsNullOrWhiteSpace(frame.Source))
            {
                output.WriteLine($"      source={frame.Source}");
            }

            if (!string.IsNullOrWhiteSpace(frame.Summary))
            {
                output.WriteLine($"      summary={frame.Summary}");
            }
        }
    }

    private void WriteMessages(IReadOnlyList<ChatMessage> messages, bool includeFullToolResults)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            output.WriteLine($"message[{i}] role={message.Role}");
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                output.WriteLine(message.Text);
            }

            var contents = message.Contents;
            if (contents == null || contents.Count == 0)
            {
                continue;
            }

            for (var j = 0; j < contents.Count; j++)
            {
                output.WriteLine($"  content[{j}] type={contents[j].GetType().Name}");
                WriteContent(contents[j], includeFullToolResults);
            }
        }
    }

    private void WriteContent(AIContent content, bool includeFullToolResults)
    {
        switch (content)
        {
            case TextContent text:
                output.WriteLine(text.Text);
                break;
            case FunctionCallContent call:
                output.WriteLine($"  tool_call name={call.Name} callId={call.CallId}");
                output.WriteLine(JsonConvert.SerializeObject(call.Arguments, Formatting.Indented));
                break;
            case FunctionResultContent result:
                output.WriteLine($"  tool_result callId={result.CallId}");
                var resultText = result.Result?.ToString() ?? string.Empty;
                output.WriteLine(includeFullToolResults ? resultText : Truncate(resultText, 2000));
                break;
            default:
                output.WriteLine(JsonConvert.SerializeObject(content, Formatting.Indented));
                break;
        }
    }

    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + "...[truncated]";

    private sealed class RoundTrace
    {
        public StringBuilder Thinking { get; } = new();
        public StringBuilder Assistant { get; } = new();
        public List<ToolCallRequestedEvent> ToolRequests { get; } = [];
        public List<ToolExecutionCompletedEvent> ToolResults { get; } = [];
        public List<RecoveryTriggeredEvent> Recoveries { get; } = [];
        public List<SystemNoticeEvent> Notices { get; } = [];
    }

    private sealed class NoOpGitService : IGitService
    {
        public Task<bool> InitRepositoryAsync(string path) => Task.FromResult(false);
        public Task<bool> CloneRepositoryAsync(Uri url, string path, string? branch = null, string? githubToken = null) => Task.FromResult(false);
        public Task<bool> CloneRepositoryAsync(string url, string path, string? branch = null, string? githubToken = null) => Task.FromResult(false);
        public Task<bool> CreateBranchAsync(string path, string branchName, string? startPoint = null) => Task.FromResult(false);
        public Task<bool> CommitAsync(string path, string message) => Task.FromResult(false);
        public Task<bool> MergeAsync(string path, string sourceBranch, string targetBranch = "main") => Task.FromResult(false);
        public Task<bool> RevertToLastCommitAsync(string path) => Task.FromResult(false);
        public Task<string> GetDiffSummaryAsync(string path, string fromBranch, string toBranch) => Task.FromResult(string.Empty);
        public Task<bool> ApplyPatchAsync(string path, string patchContent) => Task.FromResult(false);
        public Task<bool> PushAsync(string path, string branchName) => Task.FromResult(false);
        public Task<string?> GetRemoteUrlAsync(string path) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<GitFileChange>> GetChangedFilesAsync(string path, string fromBranch, string toBranch) => Task.FromResult<IReadOnlyList<GitFileChange>>([]);
        public Task<IReadOnlyList<GitFileChange>> GetWorkingTreeChangedFilesAsync(string path, string baseRef) => Task.FromResult<IReadOnlyList<GitFileChange>>([]);
        public Task<string?> GetFileAtRefAsync(string path, string relativePath, string gitRef = "HEAD~1") => Task.FromResult<string?>(null);
        public Task<string?> GetHeadHashAsync(string path) => Task.FromResult<string?>(null);
        public Task<string?> GetCurrentBranchAsync(string path) => Task.FromResult<string?>(null);
        public Task<bool?> HasUncommittedChangesAsync(string path) => Task.FromResult<bool?>(null);
        public Task<bool> ResetToCommitAsync(string path, string commitHash) => Task.FromResult(false);
        public Task<string?> CreateShadowWorktreeAsync(string mainPath, string taskId, string baseBranch) => Task.FromResult<string?>(null);
        public Task<bool> RemoveShadowWorktreeAsync(string mainPath, string taskId) => Task.FromResult(false);
    }
}
