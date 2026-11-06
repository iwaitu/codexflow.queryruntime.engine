using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models;

public sealed class ToolStrictAdapterTests
{
    private const string StrictKey = "strictJsonSchema";

    private const string CompatibleSchema =
        """{"type":"object","properties":{"city":{"type":"string"},"days":{"type":"integer"}},"required":["city","days"],"additionalProperties":false}""";

    private const string OptionalFieldSchema =
        """{"type":"object","properties":{"city":{"type":"string"},"days":{"type":"integer"}},"required":["city"],"additionalProperties":false}""";

    [Theory]
    [InlineData(CompatibleSchema, true)]
    [InlineData(OptionalFieldSchema, false)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""", false)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":true}""", false)]
    [InlineData("""{"type":"object","properties":{},"additionalProperties":false}""", true)]
    [InlineData("""{"type":"object","additionalProperties":false}""", true)]
    [InlineData("""{"type":"string"}""", false)]
    [InlineData("""{"properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}""", false)]
    [InlineData("""[]""", false)]
    // Nested objects, array items, union branches and definitions must satisfy the rules too.
    [InlineData("""{"type":"object","properties":{"o":{"type":"object","properties":{"x":{"type":"string"}},"required":["x"],"additionalProperties":false}},"required":["o"],"additionalProperties":false}""", true)]
    [InlineData("""{"type":"object","properties":{"o":{"type":"object","properties":{"x":{"type":"string"}},"required":["x"]}},"required":["o"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"l":{"type":"array","items":{"type":"object","properties":{"x":{"type":"string"}},"additionalProperties":false}}},"required":["l"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"l":{"type":"array","items":{"type":"object","properties":{"x":{"type":"string"}},"required":["x"],"additionalProperties":false}}},"required":["l"],"additionalProperties":false}""", true)]
    [InlineData("""{"type":"object","properties":{"u":{"anyOf":[{"type":"string"},{"type":"object","properties":{"x":{"type":"string"}},"additionalProperties":false}]}},"required":["u"],"additionalProperties":false}""", false)]
    [InlineData("""{"type":"object","properties":{"a":{"$ref":"#/$defs/a"}},"required":["a"],"additionalProperties":false,"$defs":{"a":{"type":"object","properties":{"x":{"type":"string"}},"required":["x"],"additionalProperties":false}}}""", true)]
    [InlineData("""{"type":"object","properties":{"a":{"$ref":"#/$defs/a"}},"required":["a"],"additionalProperties":false,"$defs":{"a":{"type":"object","properties":{"x":{"type":"string"}}}}}""", false)]
    [InlineData("""{"type":["object","null"],"properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}""", true)]
    public void SchemaCompatibility_FollowsTheStrictSubset(string schema, bool expected)
        => Assert.Equal(expected, RuntimeToolStrictSchema.IsCompatible(Json(schema)));

    [Fact]
    public void SchemaCompatibility_RejectsPathologicalNesting()
    {
        var schema = """{"type":"string"}""";
        for (var i = 0; i < 100; i++)
        {
            schema = $$"""{"type":"object","properties":{"n":{{schema}}},"required":["n"],"additionalProperties":false}""";
        }

        // The default parser depth is 64; widen it so the schema reaches the compatibility check.
        using var document = JsonDocument.Parse(schema, new JsonDocumentOptions { MaxDepth = 1024 });
        Assert.False(RuntimeToolStrictSchema.IsCompatible(document.RootElement));
    }

    [Fact]
    public async Task Off_LeavesToolsUnchangedAndEmitsNoWarning()
    {
        var chat = new CapturingChatClient();
        using var client = new MeaiRuntimeModelClient(chat, static _ => new ChatOptions());

        var events = await DrainAsync(client, Tool("compatible", CompatibleSchema), Tool("optional", OptionalFieldSchema));

        Assert.All(chat.LastOptions!.Tools!.OfType<AIFunction>(), static f => Assert.False(f.AdditionalProperties.ContainsKey(StrictKey)));
        Assert.DoesNotContain(events, static e => e is RuntimeWarningEvent);
    }

    [Fact]
    public async Task WhenSchemaCompatible_MarksOnlyCompatibleToolsAndReportsTheRest()
    {
        var chat = new CapturingChatClient();
        using var client = new MeaiRuntimeModelClient(
            chat,
            static _ => new ChatOptions(),
            new MeaiRuntimeModelClientOptions { ToolStrict = MeaiToolStrictMode.WhenSchemaCompatible });

        var events = await DrainAsync(client, Tool("compatible", CompatibleSchema), Tool("optional", OptionalFieldSchema));

        var functions = chat.LastOptions!.Tools!.OfType<AIFunction>().ToDictionary(static f => f.Name);
        Assert.Equal(true, functions["compatible"].AdditionalProperties[StrictKey]);
        Assert.False(functions["optional"].AdditionalProperties.ContainsKey(StrictKey));
        // Existing declaration metadata is untouched.
        Assert.Equal("1", functions["compatible"].AdditionalProperties["qre.tool.version"]);

        var warning = Assert.Single(events.OfType<RuntimeWarningEvent>()).Warning;
        Assert.Equal("tool_strict_downgraded", warning.Code);
        Assert.Contains("optional", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("compatible,", warning.Message, StringComparison.Ordinal);
        // The warning is reported before the Step completes and never replaces it.
        Assert.IsType<RuntimeModelCompletedEvent>(events[^1]);
        Assert.IsType<RuntimeWarningEvent>(events[^2]);
    }

    [Fact]
    public async Task WhenSchemaCompatible_WithOnlyCompatibleTools_EmitsNoWarning()
    {
        var chat = new CapturingChatClient();
        using var client = new MeaiRuntimeModelClient(
            chat,
            static _ => new ChatOptions(),
            new MeaiRuntimeModelClientOptions { ToolStrict = MeaiToolStrictMode.WhenSchemaCompatible });

        var events = await DrainAsync(client, Tool("compatible", CompatibleSchema));

        Assert.Equal(true, chat.LastOptions!.Tools!.OfType<AIFunction>().Single().AdditionalProperties[StrictKey]);
        Assert.DoesNotContain(events, static e => e is RuntimeWarningEvent);
    }

    [Fact]
    public async Task WhenSchemaCompatible_WithoutTools_IsANoOp()
    {
        var chat = new CapturingChatClient();
        using var client = new MeaiRuntimeModelClient(
            chat,
            static _ => new ChatOptions(),
            new MeaiRuntimeModelClientOptions { ToolStrict = MeaiToolStrictMode.WhenSchemaCompatible });

        var events = await DrainAsync(client);

        Assert.Empty(chat.LastOptions!.Tools!);
        Assert.DoesNotContain(events, static e => e is RuntimeWarningEvent);
    }

    private static RuntimeToolDescriptor Tool(string name, string schema)
        => new(name, "1", "A tool.", Json(schema), RuntimeToolSideEffect.None, RuntimeToolIdempotency.Idempotent);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<List<RuntimeModelStreamEvent>> DrainAsync(
        MeaiRuntimeModelClient client,
        params RuntimeToolDescriptor[] tools)
    {
        var request = new RuntimeModelRequest(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            new RuntimeStepId("step"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hi")])],
            tools,
            new RuntimeModelParameters(),
            0);
        var events = new List<RuntimeModelStreamEvent>();
        await foreach (var runtimeEvent in client.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(runtimeEvent);
        }
        return events;
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
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]);
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = ChatFinishReason.Stop };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
