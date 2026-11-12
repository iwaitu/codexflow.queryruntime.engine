using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

/// <summary>
/// Some providers number tool calls per response ("read_section:0"), so the same
/// invocation id comes back in a later Step.
/// </summary>
public sealed class RuntimeDuplicateInvocationIdTests
{
    [Fact]
    public async Task IdReusedInALaterStep_IsReassignedAndBothResultsReachTheModel()
    {
        var executor = new RecordingToolExecutor();
        var model = new ScriptedModelClient(
            _ => Tool(ToolCall("read_section:0", "open_document", "{\"n\":1}")),
            _ => Tool(ToolCall("read_section:0", "open_document", "{\"n\":2}")),
            _ => Text("done"));
        var request = CreateRequest(
            tools: [ToolDescriptor("open_document")],
            executor: executor,
            budget: new RuntimeBudgetSnapshot(5, 5));

        // AgentRuntime adds the presentation layer, which used to throw on the repeated id.
        var result = await new AgentRuntime(model).RunAsync(
            new RuntimeRunRequest(request),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(["read_section:0", "read_section:0_s1"], executor.Calls.Select(static call => call.InvocationId.Value));
        var final = model.Requests[2].Messages.SelectMany(static message => message.Items).ToArray();
        Assert.Equal(
            ["read_section:0", "read_section:0_s1"],
            final.OfType<RuntimeToolCallItem>().Select(static item => item.Call.InvocationId.Value));
        Assert.Equal(
            ["read_section:0", "read_section:0_s1"],
            final.OfType<RuntimeToolResultItem>().Select(static item => item.Result.InvocationId.Value));
        // Nothing was replaced by a "[duplicate tool call omitted]" placeholder.
        Assert.DoesNotContain(final.OfType<RuntimeTextItem>(), static item => item.Text.Contains("duplicate", StringComparison.Ordinal));
        Assert.Contains(
            result.Turn.Steps[1].Output!.Warnings,
            static warning => warning.Code == "tool_call_id_reassigned");
    }

    [Fact]
    public async Task IdRepeatedWithinOneResponse_IsReassignedInsteadOfFailingTheTurn()
    {
        var executor = new RecordingToolExecutor();
        var model = new ScriptedModelClient(
            _ => Tool(
                ToolCall("call", "open_document", "{\"n\":1}"),
                ToolCall("call", "open_document", "{\"n\":2}")),
            _ => Text("done"));
        var request = CreateRequest(
            tools: [ToolDescriptor("open_document")],
            executor: executor,
            budget: new RuntimeBudgetSnapshot(5, 5));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), request);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(
            ["call", "call_s0"],
            executor.Calls.Select(static call => call.InvocationId.Value).Order(StringComparer.Ordinal));
    }
}
