using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeResponseFormatTests
{
    private const string Schema =
        "{\"type\":\"object\",\"properties\":{\"score\":{\"type\":[\"number\",\"null\"]},\"evidence\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"score\",\"evidence\"],\"additionalProperties\":false}";

    private static RuntimeResponseFormat SchemaFormat(
        RuntimeResponseFormatScope scope = RuntimeResponseFormatScope.AllSteps)
        => RuntimeResponseFormat.ForJsonSchema(Json(Schema), "rule_result", "Rule scoring result.", scope);

    [Fact]
    public async Task AllSteps_AppliesFormatToToolAndToolFreeSteps()
    {
        var model = new ScriptedModelClient(
            _ => Tool(ToolCall("call-1", "open_document")),
            _ => Text("{\"score\":1,\"evidence\":[]}"));
        var audit = new InMemoryRuntimeAuditSink();

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            tools: [ToolDescriptor("open_document")],
            parameters: new RuntimeModelParameters { ResponseFormat = SchemaFormat() }) with
        {
            AuditSink = audit
        });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.All(model.Requests, request => Assert.Equal(SchemaFormat(), request.Parameters.ResponseFormat));
        var requests = audit.Events.Select(static e => e.Payload).OfType<RuntimeModelRequestAuditPayload>().ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, payload =>
        {
            Assert.Equal("json_schema", payload.ResponseFormatKind);
            Assert.Equal(SchemaFormat().ComputeSchemaSha256(), payload.ResponseFormatSchemaSha256);
        });
    }

    [Fact]
    public async Task ToolFreeStepsOnly_WithholdsFormatWhileToolsAreExposed()
    {
        var model = new ScriptedModelClient(
            _ => Tool(ToolCall("call-1", "open_document")),
            _ => Text("{\"score\":1,\"evidence\":[]}"));
        var audit = new InMemoryRuntimeAuditSink();
        var tools = new[] { ToolDescriptor("open_document") };

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            tools: tools,
            parameters: new RuntimeModelParameters
            {
                ResponseFormat = SchemaFormat(RuntimeResponseFormatScope.ToolFreeStepsOnly)
            }) with
        {
            AuditSink = audit,
            // Step 0 exposes the catalog; Step 1 exposes no tools.
            ToolCatalogSelector = new FixedToolSelector((step, catalog) => step == 0 ? catalog : [])
        });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Null(model.Requests[0].Parameters.ResponseFormat);
        Assert.Single(model.Requests[0].Tools);
        Assert.Empty(model.Requests[1].Tools);
        Assert.Equal(SchemaFormat(RuntimeResponseFormatScope.ToolFreeStepsOnly), model.Requests[1].Parameters.ResponseFormat);
        var payloads = audit.Events.Select(static e => e.Payload).OfType<RuntimeModelRequestAuditPayload>().ToArray();
        Assert.Null(payloads[0].ResponseFormatKind);
        Assert.Null(payloads[0].ResponseFormatSchemaSha256);
        Assert.Equal("json_schema", payloads[1].ResponseFormatKind);
        // The recorded audit replays and its effective-format fields are verified.
        var replay = RuntimeRecordedReplay.Replay(new RuntimeAuditRecording(
            RuntimeAuditDataMode.PrivateDiagnostic,
            RuntimeAuditReplayCapability.Recorded,
            audit.Events));
        Assert.Equal(RuntimeTurnStatus.Completed, replay.Status);
    }

    [Fact]
    public async Task ToolFreeStepsOnly_WithoutToolsAppliesFromFirstStep()
    {
        var model = new ScriptedModelClient(_ => Text("{}"));

        await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            parameters: new RuntimeModelParameters
            {
                ResponseFormat = RuntimeResponseFormat.JsonObject(RuntimeResponseFormatScope.ToolFreeStepsOnly)
            }));

        Assert.Equal(RuntimeResponseFormatKind.JsonObject, Assert.Single(model.Requests).Parameters.ResponseFormat!.Kind);
    }

    [Fact]
    public async Task Unset_KeepsLegacyRequestAndAuditShape()
    {
        var model = new ScriptedModelClient(_ => Text("done"));
        var audit = new InMemoryRuntimeAuditSink();

        await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            parameters: new RuntimeModelParameters(RequireJsonObject: true)) with { AuditSink = audit });

        var request = Assert.Single(model.Requests);
        Assert.Null(request.Parameters.ResponseFormat);
        Assert.True(request.Parameters.RequireJsonObject);
        var json = JsonSerializer.Serialize(audit.Events.ToArray(), RuntimeAuditJsonContext.Default.RuntimeAuditEnvelopeArray);
        Assert.DoesNotContain("responseFormat", json, StringComparison.Ordinal);
        Assert.DoesNotContain("responseFormat", JsonSerializer.Serialize(request, QueryRuntimeProtocolJsonContext.Default.RuntimeModelRequest), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_RejectsConflictsAndInvalidSchemas()
    {
        var loop = new RuntimeAgentLoop(new ScriptedModelClient());

        async Task<string> CodeAsync(RuntimeModelParameters parameters)
            => (await Assert.ThrowsAsync<RuntimeRequestValidationException>(() =>
                RunLoopAsync(loop, CreateRequest(parameters: parameters)))).Error.Code;

        Assert.Equal("response_format_conflict", await CodeAsync(
            new RuntimeModelParameters(RequireJsonObject: true) { ResponseFormat = RuntimeResponseFormat.JsonObject() }));
        Assert.Equal("response_format_invalid", await CodeAsync(
            new RuntimeModelParameters { ResponseFormat = new RuntimeResponseFormat(RuntimeResponseFormatKind.JsonSchema) }));
        Assert.Equal("response_format_invalid", await CodeAsync(
            new RuntimeModelParameters { ResponseFormat = RuntimeResponseFormat.ForJsonSchema(Json("{\"type\":\"array\"}")) }));
        Assert.Equal("response_format_invalid", await CodeAsync(
            new RuntimeModelParameters { ResponseFormat = RuntimeResponseFormat.ForJsonSchema(Json("{\"type\":\"object\",\"properties\":[]}")) }));
        Assert.Equal("response_format_invalid", await CodeAsync(
            new RuntimeModelParameters { ResponseFormat = new RuntimeResponseFormat(RuntimeResponseFormatKind.JsonObject, Schema: Json(Schema)) }));
        // Still an ArgumentException for existing callers.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => RunLoopAsync(loop, CreateRequest(
            parameters: new RuntimeModelParameters(RequireJsonObject: true) { ResponseFormat = RuntimeResponseFormat.JsonObject() })));
    }

    [Fact]
    public void Equality_ComparesSchemaStructurally()
    {
        var left = SchemaFormat();
        var right = RuntimeResponseFormat.ForJsonSchema(Json(Schema), "rule_result", "Rule scoring result.");
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal(
            new RuntimeModelParameters { ResponseFormat = left },
            new RuntimeModelParameters { ResponseFormat = right });
        Assert.NotEqual(left, left with { Scope = RuntimeResponseFormatScope.ToolFreeStepsOnly });
        Assert.NotEqual(left, RuntimeResponseFormat.ForJsonSchema(Json("{\"type\":\"object\"}"), "rule_result", "Rule scoring result."));
        // Whitespace does not change the digest.
        Assert.Equal(
            left.ComputeSchemaSha256(),
            RuntimeResponseFormat.ForJsonSchema(Json(Schema.Replace(",", " , ", StringComparison.Ordinal))).ComputeSchemaSha256());
    }

    [Fact]
    public async Task Checkpoint_RoundTripsFormatAndReadsLegacyDocuments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qre-format-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RuntimeJsonCheckpointStore(directory);
            var request = CreateRequest(parameters: new RuntimeModelParameters { ResponseFormat = SchemaFormat() }) with
            {
                CheckpointSink = store,
                RecoveryCompatibilityId = "format-test"
            };
            await RunLoopAsync(new RuntimeAgentLoop(new ScriptedModelClient(_ => Text("{}"))), request);

            var restored = RuntimeJsonCheckpointStore.Read(store.CheckpointPath);
            Assert.Equal(SchemaFormat(), restored.Request.ModelParameters.ResponseFormat);
            Assert.Equal(SchemaFormat(), restored.Session.TerminalTurns[^1].Steps[0].Context.ModelRequest.Parameters.ResponseFormat);

            // A request without a format serializes exactly as before, so legacy
            // checkpoints (which never carried the field) keep their fingerprint.
            var legacy = RuntimeCheckpointRequestSnapshot.Capture(CreateRequest());
            var legacyJson = JsonSerializer.Serialize(legacy, RuntimeCheckpointJsonContext.Default.RuntimeCheckpointRequestSnapshot);
            Assert.DoesNotContain("responseFormat", legacyJson, StringComparison.Ordinal);
            var parsed = JsonSerializer.Deserialize(legacyJson, RuntimeCheckpointJsonContext.Default.RuntimeCheckpointRequestSnapshot)!;
            Assert.Null(parsed.ModelParameters.ResponseFormat);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Replay_RejectsTamperedEffectiveFormat()
    {
        var audit = new InMemoryRuntimeAuditSink();
        await RunLoopAsync(new RuntimeAgentLoop(new ScriptedModelClient(_ => Text("{}"))), CreateRequest(
            parameters: new RuntimeModelParameters { ResponseFormat = SchemaFormat() }) with { AuditSink = audit });
        var events = audit.Events.ToArray();
        var index = Array.FindIndex(events, static e => e.Payload is RuntimeModelRequestAuditPayload);
        events[index] = events[index] with
        {
            Payload = ((RuntimeModelRequestAuditPayload)events[index].Payload) with { ResponseFormatKind = "json_object" }
        };

        var error = Assert.Throws<RuntimeAuditReplayException>(() => RuntimeRecordedReplay.Replay(new RuntimeAuditRecording(
            RuntimeAuditDataMode.PrivateDiagnostic,
            RuntimeAuditReplayCapability.Recorded,
            events)));

        Assert.Equal("audit_response_format_mismatch", error.Error.Code);
    }

    [Fact]
    public async Task PublicAudit_RecordsKindWithoutSchema()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qre-format-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var store = RuntimeJsonlAuditStore.Create(directory, "format-run", new RuntimeAuditStoreOptions()))
            {
                await RunLoopAsync(new RuntimeAgentLoop(new ScriptedModelClient(_ => Text("{}"))), CreateRequest(
                    parameters: new RuntimeModelParameters { ResponseFormat = SchemaFormat() }) with { AuditSink = store });
            }
            var text = string.Join('\n', Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(static path => File.ReadAllText(path, Encoding.UTF8)));
            Assert.Contains("\"responseFormatKind\":\"json_schema\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("rule_result", text, StringComparison.Ordinal);
            Assert.DoesNotContain("evidence", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MeaiClient_MapsRuntimeFormatAndKeepsFactoryValueWhenUnset()
    {
        var chat = new CapturingChatClient();
        using var client = new MeaiRuntimeModelClient(chat, static _ => new ChatOptions { ResponseFormat = ChatResponseFormat.Text });

        await DrainAsync(client.StreamAsync(ModelRequest(new RuntimeModelParameters { ResponseFormat = SchemaFormat() }), TestContext.Current.CancellationToken));
        var schema = Assert.IsType<ChatResponseFormatJson>(chat.LastOptions!.ResponseFormat);
        Assert.Equal("rule_result", schema.SchemaName);
        Assert.Equal("Rule scoring result.", schema.SchemaDescription);
        Assert.True(JsonElement.DeepEquals(Json(Schema), schema.Schema!.Value));

        await DrainAsync(client.StreamAsync(ModelRequest(new RuntimeModelParameters { ResponseFormat = RuntimeResponseFormat.JsonObject() }), TestContext.Current.CancellationToken));
        var json = Assert.IsType<ChatResponseFormatJson>(chat.LastOptions!.ResponseFormat);
        Assert.Null(json.Schema);

        await DrainAsync(client.StreamAsync(ModelRequest(new RuntimeModelParameters()), TestContext.Current.CancellationToken));
        Assert.IsType<ChatResponseFormatText>(chat.LastOptions!.ResponseFormat);
    }

    private static RuntimeModelRequest ModelRequest(RuntimeModelParameters parameters)
        => new(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            new RuntimeStepId("step"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hi")])],
            [],
            parameters,
            0);

    private static async Task DrainAsync(IAsyncEnumerable<RuntimeModelStreamEvent> events)
    {
        await foreach (var _ in events.WithCancellation(TestContext.Current.CancellationToken))
        {
        }
    }

    private sealed class CapturingChatClient : IChatClient
    {
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
            LastOptions = options;
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("{}")]);
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = ChatFinishReason.Stop };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
