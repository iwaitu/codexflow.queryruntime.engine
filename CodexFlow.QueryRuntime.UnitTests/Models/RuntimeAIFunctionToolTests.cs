using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models.Tools;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models;

public sealed class RuntimeAIFunctionToolTests
{
    [Fact]
    public void Definition_UsesReadOnlyDefaultsAndFunctionMetadata()
    {
        var function = AIFunctionFactory.Create(
            (string path) => path,
            new AIFunctionFactoryOptions { Name = "open_document", Description = "Open a document." });

        var tool = new RuntimeAIFunctionTool(function);

        var definition = tool.Definition;
        Assert.Equal("open_document", definition.Descriptor.CanonicalName);
        Assert.Equal("1.0.0", definition.Descriptor.Version);
        Assert.Equal("Open a document.", definition.Descriptor.Description);
        Assert.Equal(RuntimeToolSideEffect.ReadOnly, definition.Descriptor.SideEffect);
        Assert.Equal(RuntimeToolIdempotency.Idempotent, definition.Descriptor.Idempotency);
        Assert.Equal(RuntimeToolConcurrency.ParallelSafe, definition.Concurrency);
        Assert.Equal(RuntimeToolLimits.Default, definition.Limits);
        Assert.Equal(RuntimeSandboxRequirements.None, definition.Sandbox);
        Assert.Empty(definition.Capabilities);
        Assert.Equal(
            function.JsonSchema.GetRawText(),
            definition.Descriptor.InputSchema.GetRawText());
    }

    [Fact]
    public void Definition_AppliesHostOptions()
    {
        var function = AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions { Name = "original", Description = "Test tool." });
        var limits = new RuntimeToolLimits(TimeSpan.FromMinutes(2), 40_000);

        var tool = new RuntimeAIFunctionTool(function, new RuntimeAIFunctionToolOptions
        {
            CanonicalName = "renamed",
            Version = "2.1.0",
            SideEffect = RuntimeToolSideEffect.External,
            Idempotency = RuntimeToolIdempotency.NonIdempotent,
            Concurrency = RuntimeToolConcurrency.Serial,
            Limits = limits,
            Capabilities = new HashSet<string> { "documents.read" }
        });

        Assert.Equal("renamed", tool.Definition.Descriptor.CanonicalName);
        Assert.Equal("2.1.0", tool.Definition.Descriptor.Version);
        Assert.Equal(RuntimeToolSideEffect.External, tool.Definition.Descriptor.SideEffect);
        Assert.Equal(RuntimeToolIdempotency.NonIdempotent, tool.Definition.Descriptor.Idempotency);
        Assert.Equal(RuntimeToolConcurrency.Serial, tool.Definition.Concurrency);
        Assert.Same(limits, tool.Definition.Limits);
        Assert.Equal(["documents.read"], tool.Definition.Capabilities);
    }

    [Fact]
    public void Definition_DescriptionOptionOverridesFunctionDescription()
    {
        var function = AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions { Name = "undocumented" });

        var tool = new RuntimeAIFunctionTool(function, new RuntimeAIFunctionToolOptions { Description = "Host description." });

        Assert.Equal("Host description.", tool.Definition.Descriptor.Description);
        Assert.Throws<ArgumentException>(() => new RuntimeToolRegistry([new RuntimeAIFunctionTool(function)]));
    }

    [Fact]
    public void FromFunctions_RejectsSharedCanonicalName()
    {
        AIFunction[] functions =
        [
            AIFunctionFactory.Create(() => "a", new AIFunctionFactoryOptions { Name = "a", Description = "Test tool." }),
            AIFunctionFactory.Create(() => "b", new AIFunctionFactoryOptions { Name = "b", Description = "Test tool." })
        ];

        Assert.Equal(
            ["a", "b"],
            RuntimeAIFunctionTool.FromFunctions(functions)
                .Select(static tool => tool.Definition.Descriptor.CanonicalName));
        Assert.Throws<ArgumentException>(() => RuntimeAIFunctionTool.FromFunctions(
            functions,
            new RuntimeAIFunctionToolOptions { CanonicalName = "shared" }));
    }

    [Fact]
    public async Task Invoke_PassesNormalizedArgumentsAndReturnsStringVerbatim()
    {
        var function = AIFunctionFactory.Create(
            (string keyword, int limit) => $"{keyword}:{limit}:容积率",
            new AIFunctionFactoryOptions { Name = "find_keyword", Description = "Test tool." });

        var result = await InvokeAsync(function, "{\"keyword\":\"绿地率\",\"limit\":3}");

        Assert.True(result.Success);
        Assert.Equal("绿地率:3:容积率", result.Text);
        Assert.Equal(RuntimeToolOutcome.Succeeded, result.Details!.Outcome);
        Assert.Equal("绿地率:3:容积率", result.Details.StandardOutput);
        Assert.False(result.Details.Truncated);
    }

    [Fact]
    public async Task Invoke_SerializesObjectResultWithoutEscapingChinese()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.MakeReadOnly();
        var function = new ValueFunction(
            "indicator",
            new Dictionary<string, string> { ["名称"] = "容积率", ["unit"] = "%" },
            options);

        var result = await InvokeAsync(function, "{}");

        Assert.True(result.Success);
        Assert.Equal("{\"名称\":\"容积率\",\"unit\":\"%\"}", result.Text);
    }

    [Fact]
    public async Task Invoke_FactoryMarshalledObjectResultIsJson()
    {
        var function = AIFunctionFactory.Create(
            () => new Dictionary<string, int> { ["页数"] = 12 },
            new AIFunctionFactoryOptions { Name = "count_pages", Description = "Test tool." });

        var result = await InvokeAsync(function, "{}");

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(result.Text!);
        Assert.Equal(12, document.RootElement.GetProperty("页数").GetInt32());
        Assert.DoesNotContain("\\u", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_JsonElementResultKeepsRawTextAndUnescapesChinese()
    {
        using var raw = JsonDocument.Parse("{ \"a\" : [1, 2] }");
        var rawResult = await InvokeAsync(new ValueFunction("raw", raw.RootElement.Clone()), "{}");
        Assert.Equal("{ \"a\" : [1, 2] }", rawResult.Text);

        using var escaped = JsonDocument.Parse("{\"name\":\"\\u5BB9\\u79EF\\u7387\"}");
        var escapedResult = await InvokeAsync(new ValueFunction("escaped", escaped.RootElement.Clone()), "{}");
        Assert.Equal("{\"name\":\"容积率\"}", escapedResult.Text);

        using var text = JsonDocument.Parse("\"纯文本\"");
        var textResult = await InvokeAsync(new ValueFunction("text", text.RootElement.Clone()), "{}");
        Assert.Equal("纯文本", textResult.Text);
    }

    [Fact]
    public async Task Invoke_NullResultIsEmptyText()
    {
        var result = await InvokeAsync(new ValueFunction("nothing", null), "{}");

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task Invoke_TruncationDetectorMarksObservation()
    {
        var function = AIFunctionFactory.Create(() => "body ... [output truncated]", new AIFunctionFactoryOptions { Name = "big", Description = "Test tool." });

        var result = await InvokeAsync(function, "{}", new RuntimeAIFunctionToolOptions
        {
            TruncationDetector = static text => text.EndsWith("[output truncated]", StringComparison.Ordinal)
        });

        Assert.True(result.Details!.Truncated);
    }

    [Fact]
    public async Task Invoke_ExceptionBecomesModelVisibleToolFailure()
    {
        var function = AIFunctionFactory.Create(
            new Func<string>(static () => throw new InvalidOperationException("文档不存在")),
            new AIFunctionFactoryOptions { Name = "broken", Description = "Test tool." });

        var result = await InvokeAsync(function, "{}");

        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Equal(RuntimeErrorCategory.ToolFailed, result.Error!.Category);
        Assert.Equal("ai_function_failed", result.Error.Code);
        Assert.Equal("文档不存在", result.Error.Message);
        Assert.Equal(RuntimeToolOutcome.Failed, result.Details!.Outcome);
        Assert.Equal("文档不存在", result.Details.StandardError);

        var custom = await InvokeAsync(function, "{}", new RuntimeAIFunctionToolOptions { FailureCode = "host_failed" });
        Assert.Equal("host_failed", custom.Error!.Code);
    }

    [Fact]
    public async Task Invoke_CallerCancellationPropagates()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var function = AIFunctionFactory.Create(
            async (CancellationToken ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            },
            new AIFunctionFactoryOptions { Name = "slow", Description = "Test tool." });
        using var cts = new CancellationTokenSource();

        var invocation = InvokeWithTokenAsync(function, "{}", null, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
    }

    private static Task<RuntimeToolResult> InvokeAsync(
        AIFunction function,
        string arguments,
        RuntimeAIFunctionToolOptions? options = null)
        => InvokeWithTokenAsync(function, arguments, options, TestContext.Current.CancellationToken);

    private static async Task<RuntimeToolResult> InvokeWithTokenAsync(
        AIFunction function,
        string arguments,
        RuntimeAIFunctionToolOptions? options,
        CancellationToken token)
    {
        var pipeline = new RuntimeToolExecutionPipeline(
            new RuntimeToolRegistry([new RuntimeAIFunctionTool(function, options)]),
            new RuntimeAllowToolPolicy());
        using var document = JsonDocument.Parse(arguments);
        var context = new RuntimeToolExecutionContext(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            new RuntimeStepId("step"),
            new RuntimePolicySnapshot("policy", "host"),
            new RuntimeEnvironmentSnapshot("local", null, "capabilities"),
            new RuntimeBudgetSnapshot(3, 5));
        var prepared = await pipeline.PrepareAsync(
            new RuntimeToolCall(new RuntimeInvocationId("invocation"), function.Name, document.RootElement.Clone()),
            context,
            token);
        Assert.Equal(RuntimeToolPreparationKind.Ready, prepared.Kind);
        return await pipeline.ExecuteAsync(prepared, context, token);
    }

    private sealed class ValueFunction(string name, object? value, JsonSerializerOptions? options = null) : AIFunction
    {
        public override string Name => name;

        public override string Description => "Test tool.";

        public override JsonElement JsonSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();

        public override JsonSerializerOptions JsonSerializerOptions => options ?? base.JsonSerializerOptions;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => ValueTask.FromResult(value);
    }
}
