using System.Net;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Tools;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

// EmbeddedStructuredExtraction: a host-embedded, read-only "extract and score one rule"
// conversation, modelled on a planning-review bundle rule:
//   - host AIFunction tools registered with RuntimeAIFunctionTool
//   - JSON Schema output applied only to tool-free Steps
//   - budget-driven tool finalization (tool-call count + deadline)
//   - a termination policy that asks once for a tool-free JSON repair
//   - classified provider errors with backoff, and empty-stream fallback
//   - durable audit in a host-chosen directory with a selectable trace-data level
//
// Without --live the model is a scripted IChatClient behind the real
// MeaiRuntimeModelClient, so the whole chain runs offline and deterministically.
var (endpoint, model, apiKey) = LiveExample.Configuration(args);
var traceData = LiveExample.Argument(args, "--trace-data") ?? "sanitized";
var auditRoot = Path.GetFullPath(LiveExample.Argument(args, "--audit-dir")
    ?? Path.Combine(Path.GetTempPath(), "qre-embedded-extraction"));
var offline = endpoint == null;

Console.WriteLine("============================================================================");
Console.WriteLine(" CodexFlow QueryRuntime (QRE) - Embedded Structured Extraction");
Console.WriteLine("============================================================================");

// 1. Host tools: plain AIFunctions over an in-memory document bundle.
var bundle = SampleBundle.Documents;
AIFunction[] functions =
[
    AIFunctionFactory.Create(
        () => bundle.Select(static d => new DocumentSummary(d.Id, d.Title, d.Text.Length)).ToArray(),
        new AIFunctionFactoryOptions { Name = "list_documents", Description = "列出材料包中的全部文档（id、标题、字数）。" }),
    AIFunctionFactory.Create(
        (string id, int max_chars = 4000) =>
        {
            var document = bundle.SingleOrDefault(d => d.Id == id)
                ?? throw new ArgumentException($"文档 '{id}' 不存在。");
            return document.Text.Length <= max_chars ? document.Text : document.Text[..max_chars];
        },
        new AIFunctionFactoryOptions { Name = "open_document", Description = "按 id 打开文档正文，最多返回 max_chars 个字符。" }),
    AIFunctionFactory.Create(
        (string keyword) => bundle
            .Where(d => d.Text.Contains(keyword, StringComparison.Ordinal))
            .Select(d => new KeywordHit(d.Id, Snippet(d.Text, keyword)))
            .ToArray(),
        new AIFunctionFactoryOptions { Name = "find_keyword", Description = "在全部文档中查找关键词，返回命中文档与上下文片段。" })
];
// Read-only, idempotent and parallel-safe are the adapter defaults; the limits are the host's.
var tools = RuntimeAIFunctionTool.FromFunctions(functions, new RuntimeAIFunctionToolOptions
{
    Limits = new RuntimeToolLimits(TimeSpan.FromSeconds(30), 200_000),
    Capabilities = new HashSet<string> { "bundle.read" }
});
var pipeline = new RuntimeToolExecutionPipeline(new RuntimeToolRegistry(tools), new RuntimeAllowToolPolicy());
Console.WriteLine($"[1] Registered {pipeline.Descriptors.Count} host tools: {string.Join(", ", pipeline.Descriptors.Select(static d => d.CanonicalName))}");

// 2. Structured output: the schema applies only to Steps that expose no tools.
using var schema = JsonDocument.Parse(SampleBundle.ResultSchema);
var responseFormat = RuntimeResponseFormat.ForJsonSchema(
    schema.RootElement,
    "rule_result",
    "One planning-rule extraction result with evidence.",
    RuntimeResponseFormatScope.ToolFreeStepsOnly);
Console.WriteLine($"[2] Response format: {responseFormat.Kind}/{responseFormat.Scope}, schema sha256 {responseFormat.ComputeSchemaSha256()![..12]}…");

// 3. Model client: classified errors, empty-stream fallback, error-capturing HttpClient.
var chatClient = offline
    ? new OfflineReviewChatClient()
    : QreModelProviderSelector.CreateDefault().CreateClient(
        endpoint!, apiKey, model, httpClient: QreModelHttpErrorCaptureHandler.CreateHttpClient());
using var modelClient = new MeaiRuntimeModelClient(
    chatClient,
    _ => new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 1024 },
    new MeaiRuntimeModelClientOptions
    {
        ClassifyProviderErrors = true,
        StreamingMode = MeaiStreamingMode.StreamingWithNonStreamingFallbackOnEmpty
    });

// 4. Durable audit in a host-owned directory; the trace-data level is the host's choice.
var dataMode = traceData switch
{
    "public" => RuntimeAuditDataMode.PublicRedacted,
    "sanitized" => RuntimeAuditDataMode.SanitizedFixture,
    "private" => RuntimeAuditDataMode.PrivateDiagnostic,
    _ => throw new ArgumentException("--trace-data must be public, sanitized or private.")
};
var runId = $"rule-r07-{Guid.NewGuid():N}";
var audit = RuntimeJsonlAuditStore.Create(auditRoot, runId, new RuntimeAuditStoreOptions { DataMode = dataMode });
Console.WriteLine($"[3] Audit: {dataMode} -> {audit.AuditFilePath}");

var termination = new JsonRepairPolicy(schema.RootElement);
var request = new RuntimeAgentLoopRequest(
    new RuntimeSessionId(Guid.NewGuid().ToString("N")),
    new RuntimeTurnId(runId),
    "Extract planning rule R-07 from the bundle.",
    [
        new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem(SampleBundle.SystemPrompt)]),
        new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(SampleBundle.RulePrompt)])
    ],
    pipeline.Descriptors,
    new RuntimeModelParameters(Model: model, Temperature: 0.1, MaxOutputTokens: 1024) { ResponseFormat = responseFormat },
    new RuntimePolicySnapshot("bundle-review", "read-only"),
    new RuntimeEnvironmentSnapshot("embedded", null, "bundle-review-demo"),
    new RuntimeBudgetSnapshot(maxSteps: 8, maxToolCalls: 12, maxContinuations: 2, maxModelRetries: 2))
{
    ToolPipeline = pipeline,
    AuditSink = audit,
    ContextManager = new RuntimeContextManager(RuntimeContextOptions.ForModelWindow(64_000) with
    {
        // The rule prompt is evidence: never let the Runtime truncate it silently.
        TruncationPolicy = RuntimeContextTruncationPolicy.FailClosedOnInitialMessages
    }),
    ToolFinalization = new RuntimeToolFinalizationPolicy
    {
        AfterToolCalls = 3,
        Deadline = TimeSpan.FromMinutes(2),
        Instruction = "工具调用预算已用完。不要再调用任何工具，基于已获得的证据，按 rule_result schema 直接输出最终 JSON。"
    },
    TerminationPolicy = termination,
    ModelRetryPolicy = new RuntimeRetryPolicy
    {
        BaseDelay = offline ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(2),
        MaxDelay = offline ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(30)
    }
};

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
RuntimeTurnResult result;
await using (audit)
{
    IAgentRuntime runtime = new AgentRuntime(modelClient);
    result = await runtime.RunAsync(new RuntimeRunRequest(request), new ConsoleEvents(), cancellation.Token);
}

// 5. Outcome.
Console.WriteLine($"\n[4] Status: {result.Status}; steps: {result.Turn.Steps.Count}; tool calls: {result.Turn.Progress.ToolCallCount}; " +
                  $"finalization: {result.Turn.Progress.FinalizationReason?.ToString() ?? "none"}");
LiveExample.Require(result.Status == RuntimeTurnStatus.Completed, $"Turn completed ({result.Error?.Code ?? "no error"})");
LiveExample.Require(JsonRepairPolicy.Validate(schema.RootElement, result.FinalText) == null, "Final text is a JSON object matching rule_result");
Console.WriteLine(result.FinalText);

var warnings = result.Turn.Steps.SelectMany(static s => s.Output?.Warnings ?? []).Select(static w => w.Code).ToArray();
if (offline)
{
    var chat = (OfflineReviewChatClient)chatClient;
    LiveExample.Require(warnings.Contains("model_retry"), "Transient HTTP 503 was classified and retried with backoff");
    LiveExample.Require(warnings.Contains("empty_stream_fallback"), "Empty stream was resent without streaming");
    LiveExample.Require(result.Turn.Progress.FinalizationReason == RuntimeToolFinalizationReason.ToolCallBudget, "Tool budget triggered tool-free finalization");
    LiveExample.Require(chat.ToolStepsWithoutFormat && chat.ToolFreeStepsWithSchema, "Schema sent only on tool-free Steps");
    LiveExample.Require(termination.Repairs == 1, "Invalid JSON was repaired once with ContinueWithoutTools");
}

// 6. Read the audit back; recorded (non-public) audits replay as data without any provider call.
var recording = RuntimeJsonlAuditStore.Read(audit.AuditFilePath, ct: cancellation.Token);
var kinds = recording.Events.Select(static e => e.Kind).ToArray();
Console.WriteLine($"\n[5] Audit events: {recording.Events.Count} ({string.Join(", ", kinds.Distinct())})");
if (offline)
{
    LiveExample.Require(kinds.Contains(RuntimeAuditEventKind.ToolFinalizationTriggered), "Audit records the finalization decision");
    LiveExample.Require(kinds.Contains(RuntimeAuditEventKind.TerminationDecided), "Audit records continue_without_tools");
}
if (recording.ReplayCapability == RuntimeAuditReplayCapability.Recorded)
{
    var replay = RuntimeRecordedReplay.Replay(recording);
    LiveExample.Require(replay.FinalText == result.FinalText && !replay.ProviderCalls && !replay.ToolExecutions,
        $"Strict data-only replay reproduces the result (digest {replay.ReplayDigest[..12]}…)");
}
else
{
    Console.WriteLine("    Public audit is summary-only; use --trace-data sanitized|private to replay.");
}
Console.WriteLine("\n[6] Embedded structured extraction completed.");
return 0;

static string Snippet(string text, string keyword)
{
    var index = text.IndexOf(keyword, StringComparison.Ordinal);
    var start = Math.Max(0, index - 12);
    return text.Substring(start, Math.Min(text.Length - start, keyword.Length + 30));
}

internal sealed record DocumentSummary(string Id, string Title, int Chars);

internal sealed record KeywordHit(string DocumentId, string Snippet);

/// <summary>
/// Accepts a final answer only when it is a JSON object with the schema's required
/// properties; asks once for a tool-free, format-constrained repair, then fails closed.
/// </summary>
internal sealed class JsonRepairPolicy(JsonElement schema) : IRuntimeTerminationPolicy
{
    public int Repairs { get; private set; }

    public ValueTask<RuntimeTerminationDecision> DecideAsync(RuntimeTerminationContext context, CancellationToken ct)
    {
        var problem = Validate(schema, context.Step.Output?.Text ?? string.Empty);
        if (problem == null)
        {
            return ValueTask.FromResult(RuntimeTerminationDecision.Accept());
        }
        if (Repairs == 0 && context.CanContinue)
        {
            Repairs++;
            return ValueTask.FromResult(RuntimeTerminationDecision.ContinueWithoutTools(
                $"上一次输出不是合法的 rule_result JSON（{problem}）。不要调用工具，只输出完整 JSON。"));
        }
        return ValueTask.FromResult(RuntimeTerminationDecision.FailClosed(new RuntimeError(
            RuntimeErrorCategory.ProviderProtocol,
            "invalid_final_json",
            problem)));
    }

    public static string? Validate(JsonElement schema, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "root is not an object";
            }
            var missing = schema.GetProperty("required").EnumerateArray()
                .Select(static name => name.GetString()!)
                .Where(name => !document.RootElement.TryGetProperty(name, out _))
                .ToArray();
            return missing.Length == 0 ? null : $"missing {string.Join(", ", missing)}";
        }
        catch (JsonException ex)
        {
            return $"invalid JSON: {ex.Message.Split('.')[0]}";
        }
    }
}

file sealed class ConsoleEvents : IRuntimeEventSink
{
    public ValueTask OnEventAsync(RuntimePresentationEvent runtimeEvent, CancellationToken ct)
    {
        switch (runtimeEvent.Type)
        {
            case RuntimePresentationEventType.ToolCallRequested:
                Console.WriteLine($"    [tool] {runtimeEvent.ToolName}");
                break;
            case RuntimePresentationEventType.Warning:
                Console.WriteLine($"    [warning] {runtimeEvent.Warning?.Code}: {runtimeEvent.Warning?.Message}");
                break;
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Deterministic offline provider. Each SDK call consumes the next scripted reply;
/// the script covers a transient 503, three tool rounds (one via an empty stream
/// that falls back to a non-streaming call), an invalid final JSON and its repair.
/// </summary>
internal sealed class OfflineReviewChatClient : IChatClient
{
    private readonly Queue<Func<ChatOptions?, bool, ChatResponse>> _script = new(
    [
        static (_, _) => throw new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable),
        static (_, _) => Call("list_documents", new()),
        static (_, _) => Call("open_document", new() { ["id"] = "doc-2" }),
        // An empty stream (finish reason only), then the adapter's non-streaming resend.
        static (_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, [])) { FinishReason = ChatFinishReason.Stop },
        static (_, streaming) => streaming
            ? throw new InvalidOperationException("The fallback resend must not stream.")
            : Call("find_keyword", new() { ["keyword"] = "容积率" }),
        static (_, _) => Text("{\"rule_id\":\"R-07\",\"compliant\":tru"),
        static (_, _) => Text("""{"rule_id":"R-07","compliant":true,"value":2.5,"evidence":[{"document_id":"doc-2","quote":"容积率 2.5"}]}""")
    ]);

    public bool ToolStepsWithoutFormat { get; private set; } = true;

    public bool ToolFreeStepsWithSchema { get; private set; } = true;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Next(options, streaming: false));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var update in Next(options, streaming: true).ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private ChatResponse Next(ChatOptions? options, bool streaming)
    {
        var hasTools = options?.Tools is { Count: > 0 };
        var hasSchema = options?.ResponseFormat is ChatResponseFormatJson { Schema: not null };
        ToolStepsWithoutFormat &= !hasTools || options?.ResponseFormat == null;
        ToolFreeStepsWithSchema &= hasTools || hasSchema;
        return _script.Dequeue()(options, streaming);
    }

    private static ChatResponse Call(string name, Dictionary<string, object?> arguments)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{name}", name, arguments)]))
        {
            FinishReason = ChatFinishReason.ToolCalls,
            Usage = new UsageDetails { InputTokenCount = 400, OutputTokenCount = 20, TotalTokenCount = 420 }
        };

    private static ChatResponse Text(string text)
        => new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 600, OutputTokenCount = 40, TotalTokenCount = 640 }
        };
}

internal static class SampleBundle
{
    public sealed record Document(string Id, string Title, string Text);

    public static IReadOnlyList<Document> Documents { get; } =
    [
        new("doc-1", "项目概况", "本项目位于示例市示例区，为居住用地开发项目，总用地面积 12000 平方米，规划地上总建筑面积 30000 平方米。"),
        new("doc-2", "规划经济技术指标表", "用地面积 12000㎡；计容建筑面积 30000㎡；容积率 2.5；建筑密度 28%；绿地率 35%；机动车停车位 260 个。"),
        new("doc-3", "设计说明", "方案采用围合式布局，沿街布置商业裙房。规划条件要求容积率不大于 2.8，绿地率不小于 30%。")
    ];

    public const string SystemPrompt =
        "你是规划审查助手。只能使用提供的只读工具查阅材料包，结论必须附带文档原文证据。";

    public const string RulePrompt =
        "规则 R-07：项目容积率不得超过规划条件规定的上限。请查阅材料包，抽取实际容积率并判断是否合规，" +
        "最终只输出符合 rule_result schema 的 JSON。";

    public const string ResultSchema = """
    {
      "type": "object",
      "properties": {
        "rule_id": { "type": "string" },
        "compliant": { "type": "boolean" },
        "value": { "type": ["number", "null"] },
        "evidence": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "document_id": { "type": "string" },
              "quote": { "type": "string" }
            },
            "required": ["document_id", "quote"],
            "additionalProperties": false
          }
        }
      },
      "required": ["rule_id", "compliant", "value", "evidence"],
      "additionalProperties": false
    }
    """;
}
