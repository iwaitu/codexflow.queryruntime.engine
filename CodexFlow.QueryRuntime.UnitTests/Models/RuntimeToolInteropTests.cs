using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Tools;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Models;

/// <summary>
/// Tool interop addendum: explicit mixed-case canonical names and strict
/// two-member nullable unions produced by MEAI for optional host parameters.
/// </summary>
public sealed class RuntimeToolInteropTests
{
    private static readonly RuntimeAIFunctionToolOptions ReadOnlySerial = new()
    {
        SideEffect = RuntimeToolSideEffect.ReadOnly,
        Idempotency = RuntimeToolIdempotency.Idempotent,
        Concurrency = RuntimeToolConcurrency.Serial
    };

    [Fact]
    public void DefaultRegistry_StillRejectsPascalCaseWhileExplicitOptInKeepsTheOriginalName()
    {
        var tool = new RuntimeAIFunctionTool(ListDocumentsFunction(), ReadOnlySerial);

        Assert.Throws<ArgumentException>(() => new RuntimeToolRegistry([tool]));
        Assert.Throws<ArgumentException>(() => new RuntimeToolRegistry([tool], allowMixedCaseNames: false));
        var registry = new RuntimeToolRegistry([tool], allowMixedCaseNames: true);

        var descriptor = Assert.Single(registry.Descriptors);
        Assert.Equal("ListDocuments", descriptor.CanonicalName);
        Assert.Equal(RuntimeToolSideEffect.ReadOnly, descriptor.SideEffect);
        Assert.Equal(RuntimeToolIdempotency.Idempotent, descriptor.Idempotency);
        Assert.True(registry.TryGet("ListDocuments", out _));
    }

    [Theory]
    [InlineData("1ListDocuments")]
    [InlineData("List Documents")]
    [InlineData("ListDocumentsÜ")]
    [InlineData("_List")]
    public void MixedCaseRegistry_KeepsCharacterRules(string name)
    {
        var tool = new RuntimeAIFunctionTool(ListDocumentsFunction(), ReadOnlySerial with { CanonicalName = name });
        Assert.Throws<ArgumentException>(() => new RuntimeToolRegistry([tool], allowMixedCaseNames: true));
    }

    [Fact]
    public void MixedCaseRegistry_KeepsLengthAndCaseCollisionRules()
    {
        Assert.Throws<ArgumentException>(() => new RuntimeToolRegistry(
            [new RuntimeAIFunctionTool(ListDocumentsFunction(), ReadOnlySerial with { CanonicalName = "L" + new string('a', 128) })],
            allowMixedCaseNames: true));
        _ = new RuntimeToolRegistry(
            [new RuntimeAIFunctionTool(ListDocumentsFunction(), ReadOnlySerial with { CanonicalName = "L" + new string('a', 127) })],
            allowMixedCaseNames: true);
        Assert.Throws<ArgumentException>(() => new RuntimeToolRegistry(
            [
                new RuntimeAIFunctionTool(ListDocumentsFunction(), ReadOnlySerial),
                new RuntimeAIFunctionTool(ListDocumentsFunction(), ReadOnlySerial with { CanonicalName = "listdocuments" })
            ],
            allowMixedCaseNames: true));
    }

    [Fact]
    public void RealMeaiOptionalParameters_EmitTheSupportedNullableUnion()
    {
        var schema = ListDocumentsFunction().JsonSchema;
        var properties = schema.GetProperty("properties");
        AssertNullableUnion(properties.GetProperty("materialType").GetProperty("type"), "string");
        AssertNullableUnion(properties.GetProperty("page").GetProperty("type"), "integer");
        RuntimeToolArgumentNormalizer.ValidateSchema(schema);
    }

    [Theory]
    [InlineData("{}", "type=<null>;page=<null>")]
    [InlineData("{\"materialType\":null,\"page\":null}", "type=<null>;page=<null>")]
    [InlineData("{\"materialType\":\"PlanDocument\",\"page\":2}", "type=PlanDocument;page=2")]
    public async Task OptionalNullableArguments_ExecuteWithTheirBindingSemantics(string arguments, string expected)
    {
        var result = await InvokeAsync(arguments);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData("{\"page\":\"two\"}", "tool_argument_type_mismatch")]
    [InlineData("{\"materialType\":3}", "tool_argument_type_mismatch")]
    [InlineData("{\"page\":1.5}", "tool_argument_type_mismatch")]
    public async Task UnionNonNullBranch_StillValidatesTheType(string arguments, string code)
    {
        var result = await InvokeAsync(arguments);
        Assert.False(result.Success);
        Assert.Equal(code, result.Error!.Code);
    }

    [Fact]
    public void UnionBranches_KeepRecursiveRequiredEnumItemsAndAdditionalProperties()
    {
        var schema = Json("""
            {"type":"object","additionalProperties":false,"required":["path","filter"],
             "properties":{
               "path":{"type":["string","null"]},
               "mode":{"type":["null","string"],"enum":["fast","full",null]},
               "strict":{"type":["string","null"],"enum":["only"]},
               "pages":{"type":["array","null"],"items":{"type":["integer","null"]}},
               "filter":{"type":["object","null"],"additionalProperties":false,"required":["kind"],
                         "properties":{"kind":{"type":"string"}}}}}
            """);
        RuntimeToolArgumentNormalizer.ValidateSchema(schema);

        Accept(schema, "{\"path\":null,\"filter\":null}");
        Accept(schema, "{\"path\":\"a\",\"mode\":null,\"pages\":[1,null],\"filter\":{\"kind\":\"k\"}}");
        Reject(schema, "{\"filter\":null}", "tool_argument_required_missing");
        Reject(schema, "{\"path\":null}", "tool_argument_required_missing");
        Reject(schema, "{\"path\":null,\"filter\":null,\"extra\":1}", "tool_argument_unknown_property");
        Reject(schema, "{\"path\":null,\"filter\":null,\"mode\":\"slow\"}", "tool_argument_enum_mismatch");
        Reject(schema, "{\"path\":null,\"filter\":null,\"strict\":null}", "tool_argument_enum_mismatch");
        Reject(schema, "{\"path\":null,\"filter\":null,\"pages\":[\"1\"]}", "tool_argument_type_mismatch");
        Reject(schema, "{\"path\":null,\"filter\":{}}", "tool_argument_required_missing");
        Reject(schema, "{\"path\":null,\"filter\":{\"kind\":\"k\",\"x\":1}}", "tool_argument_unknown_property");
        Reject(schema, "{\"path\":null,\"filter\":\"k\"}", "tool_argument_type_mismatch");
    }

    [Theory]
    [InlineData("[\"string\",\"string\"]")]
    [InlineData("[\"null\",\"null\"]")]
    [InlineData("[\"string\",\"integer\"]")]
    [InlineData("[\"string\",\"null\",\"integer\"]")]
    [InlineData("[\"string\"]")]
    [InlineData("[]")]
    [InlineData("[\"date\",\"null\"]")]
    [InlineData("[1,\"null\"]")]
    [InlineData("[\"null\"]")]
    public void UnsupportedTypeUnions_AreRejectedAtRegistration(string type)
    {
        var schema = Json($"{{\"type\":\"object\",\"properties\":{{\"value\":{{\"type\":{type}}}}}}}");
        Assert.Throws<ArgumentException>(() => RuntimeToolArgumentNormalizer.ValidateSchema(schema));
    }

    [Fact]
    public void StringTypedSchemas_KeepTheirCanonicalBytesAndDigest()
    {
        var schema = Json("{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"integer\"},\"a\":{\"type\":\"string\"}}}");
        var normalized = RuntimeToolArgumentNormalizer.NormalizeAndValidate(schema, Json("{ \"b\" : 1, \"a\" : \"x\" }"));
        const string canonical = "{\"a\":\"x\",\"b\":1}";
        Assert.Equal(canonical, normalized.Value.GetRawText());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
            normalized.Sha256Digest);
    }

    [Fact]
    public async Task FullLoop_CallsAndAuditsThePascalCaseToolUnderItsOriginalName()
    {
        var calls = new List<string>();
        var pipeline = Pipeline(calls);
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient(
            _ => Tool(ToolCall("c1", "ListDocuments", "{\"materialType\":null}")),
            _ => Text("done"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(pipeline) with { AuditSink = audit });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(["type=<null>;page=<null>"], calls);
        Assert.Equal("ListDocuments", Assert.Single(model.Requests[0].Tools).CanonicalName);
        var observation = audit.Events
            .Select(static e => e.Payload)
            .OfType<RuntimeToolObservationAuditPayload>()
            .Single();
        Assert.Equal("ListDocuments", observation.Call.Name);
        Assert.True(observation.Result.Success);
        Assert.Equal(RuntimeTurnStatus.Completed, RuntimeRecordedReplay.Replay(Recording(audit)).Status);
    }

    [Fact]
    public async Task ToolCallRepair_MatchesOnlyTheExactExposedName()
    {
        var calls = new List<string>();
        var wrongCase = new ScriptedModelClient(
            _ => Malformed(),
            _ => JsonText("{\"tool_calls\":[{\"name\":\"listdocuments\",\"arguments\":{}}]}"));
        var rejected = await RunLoopAsync(new RuntimeAgentLoop(wrongCase), Request(Pipeline(calls)) with
        {
            ToolCallRepair = new RuntimeToolCallRepairPolicy { Instruction = "Restate the call as JSON." }
        });
        Assert.Equal(RuntimeToolCallRepairCodes.RejectedErrorCode, rejected.Error!.Code);
        Assert.Contains(RuntimeToolCallRepairCodes.OutcomeUnknownTool, rejected.Error.Message, StringComparison.Ordinal);
        Assert.Empty(calls);

        var exact = new ScriptedModelClient(
            _ => Malformed(),
            _ => JsonText("{\"tool_calls\":[{\"name\":\"ListDocuments\",\"arguments\":{\"page\":null}}]}"),
            _ => Text("done"));
        var recovered = await RunLoopAsync(new RuntimeAgentLoop(exact), Request(Pipeline(calls)) with
        {
            ToolCallRepair = new RuntimeToolCallRepairPolicy { Instruction = "Restate the call as JSON." }
        });
        Assert.Equal(RuntimeTurnStatus.Completed, recovered.Status);
        Assert.Equal(["type=<null>;page=<null>"], calls);
    }

    private static AIFunction ListDocumentsFunction()
        => AIFunctionFactory.Create(
            (string? materialType = null, int? page = null) =>
                $"type={materialType ?? "<null>"};page={(page.HasValue ? page.Value.ToString() : "<null>")}",
            new AIFunctionFactoryOptions { Name = "ListDocuments", Description = "List readable documents." });

    private static RuntimeToolExecutionPipeline Pipeline(List<string> calls)
    {
        var function = AIFunctionFactory.Create(
            (string? materialType = null, int? page = null) =>
            {
                var text = $"type={materialType ?? "<null>"};page={(page.HasValue ? page.Value.ToString() : "<null>")}";
                calls.Add(text);
                return text;
            },
            new AIFunctionFactoryOptions { Name = "ListDocuments", Description = "List readable documents." });
        return new RuntimeToolExecutionPipeline(
            new RuntimeToolRegistry([new RuntimeAIFunctionTool(function, ReadOnlySerial)], allowMixedCaseNames: true),
            new RuntimeAllowToolPolicy());
    }

    private static RuntimeAgentLoopRequest Request(RuntimeToolExecutionPipeline pipeline)
        => CreateRequest(budget: new RuntimeBudgetSnapshot(3, 3)) with
        {
            Tools = pipeline.Descriptors,
            ToolPipeline = pipeline,
            ToolExecutor = null
        };

    private static async Task<RuntimeToolResult> InvokeAsync(string arguments)
    {
        var pipeline = new RuntimeToolExecutionPipeline(
            new RuntimeToolRegistry([new RuntimeAIFunctionTool(ListDocumentsFunction(), ReadOnlySerial)], allowMixedCaseNames: true),
            new RuntimeAllowToolPolicy());
        var context = new RuntimeToolExecutionContext(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            new RuntimeStepId("step"),
            new RuntimePolicySnapshot("policy", "host"),
            new RuntimeEnvironmentSnapshot("local", null, "capabilities"),
            new RuntimeBudgetSnapshot(3, 5));
        var prepared = await pipeline.PrepareAsync(
            ToolCall("invocation", "ListDocuments", arguments),
            context,
            TestContext.Current.CancellationToken);
        if (prepared.Kind != RuntimeToolPreparationKind.Ready)
        {
            return prepared.Observation!;
        }
        return await pipeline.ExecuteAsync(prepared, context, TestContext.Current.CancellationToken);
    }

    private static void Accept(JsonElement schema, string arguments)
        => RuntimeToolArgumentNormalizer.NormalizeAndValidate(schema, Json(arguments));

    private static void Reject(JsonElement schema, string arguments, string code)
    {
        var failure = Assert.Throws<RuntimeToolPreparationException>(() =>
            RuntimeToolArgumentNormalizer.NormalizeAndValidate(schema, Json(arguments)));
        Assert.Equal(code, failure.Error.Code);
    }

    private static void AssertNullableUnion(JsonElement type, string nonNull)
    {
        Assert.Equal(JsonValueKind.Array, type.ValueKind);
        var members = type.EnumerateArray().Select(static value => value.GetString()).ToArray();
        Assert.Equal(2, members.Length);
        Assert.Contains("null", members);
        Assert.Contains(nonNull, members);
    }

    private static RuntimeAuditRecording Recording(InMemoryRuntimeAuditSink audit)
        => new(RuntimeAuditDataMode.SanitizedFixture, RuntimeAuditReplayCapability.Recorded, audit.Events);

    private static IAsyncEnumerable<RuntimeModelStreamEvent> Malformed()
        => Throw(new RuntimeModelClientException(new RuntimeError(
            RuntimeErrorCategory.ProviderProtocol,
            QreModelErrorClassifier.MalformedToolCallCode,
            "Provider returned 1 tool_calls with empty function.name in non-stream response.")));

    private static IAsyncEnumerable<RuntimeModelStreamEvent> JsonText(string json)
        => Events(
            new RuntimeTextDeltaEvent(json),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn));
}
