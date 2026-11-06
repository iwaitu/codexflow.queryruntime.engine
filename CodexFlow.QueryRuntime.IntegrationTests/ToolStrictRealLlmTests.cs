using System.Text.Json;
using CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.IntegrationTests;

/// <summary>
/// Live check of <see cref="MeaiToolStrictMode.WhenSchemaCompatible"/> through the QRE adapter and the SDK
/// against a real endpoint. It proves the request is accepted and tool calls round-trip; it does not claim the
/// server enforces the schema, which depends on the backend.
/// </summary>
public sealed class ToolStrictRealLlmTests(ITestOutputHelper output)
{
    private const string CompatibleSchema =
        """{"type":"object","properties":{"city":{"type":"string"},"party_size":{"type":"integer"}},"required":["city","party_size"],"additionalProperties":false}""";

    private const string OptionalFieldSchema =
        """{"type":"object","properties":{"note":{"type":"string"}},"additionalProperties":false}""";

    [Fact]
    public async Task WhenSchemaCompatible_RoundTripsToolCallAndReportsDowngradedTool()
    {
        if (!RealQueryRuntimeTestHost.TryCreate(out var host, out var reason))
        {
            output.WriteLine(reason);
            Assert.Skip(reason);
        }

        using var liveHost = host!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var model = liveHost.CreateRuntimeModelClient(new MeaiRuntimeModelClientOptions
        {
            ToolStrict = MeaiToolStrictMode.WhenSchemaCompatible
        });
        var request = new RuntimeModelRequest(
            new RuntimeSessionId("strict-live-session"),
            new RuntimeTurnId("strict-live-turn"),
            new RuntimeStepId("strict-live-step"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("Book a table in Paris for 4 people using the book_table tool.")])],
            [
                Tool("book_table", "Book a restaurant table.", CompatibleSchema),
                Tool("add_note", "Attach a free-form note.", OptionalFieldSchema)
            ],
            new RuntimeModelParameters(Temperature: 0, MaxOutputTokens: 256),
            HistoryVersion: 0);

        var validator = new RuntimeModelStreamValidator();
        var warnings = new List<RuntimeWarning>();
        RuntimeToolCall? call = null;
        await foreach (var runtimeEvent in model.StreamAsync(request, timeout.Token))
        {
            validator.Apply(runtimeEvent);
            if (runtimeEvent is RuntimeWarningEvent warning)
            {
                warnings.Add(warning.Warning);
            }
            else if (runtimeEvent is RuntimeToolCallEvent toolCall)
            {
                call = toolCall.Call;
            }
        }
        validator.Complete();

        output.WriteLine(
            "model={0}; stop={1}; call={2} {3}; warnings={4}",
            liveHost.Settings.Model,
            validator.StopReason?.ToString() ?? "none",
            call?.Name ?? "none",
            call?.Arguments.GetRawText() ?? string.Empty,
            string.Join(" | ", warnings.Select(static w => w.Code)));

        Assert.NotNull(call);
        Assert.Equal("book_table", call!.Name);
        Assert.Equal("Paris", call.Arguments.GetProperty("city").GetString());
        Assert.Equal(4, call.Arguments.GetProperty("party_size").GetInt32());
        Assert.Equal(RuntimeModelStopReason.ToolCall, validator.StopReason);

        var downgraded = Assert.Single(warnings, static w => w.Code == "tool_strict_downgraded");
        Assert.Contains("add_note", downgraded.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("book_table", downgraded.Message, StringComparison.Ordinal);
    }

    private static RuntimeToolDescriptor Tool(string name, string description, string schema)
        => new(
            name,
            "1",
            description,
            JsonDocument.Parse(schema).RootElement.Clone(),
            RuntimeToolSideEffect.None,
            RuntimeToolIdempotency.Idempotent);
}
