using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

// StructuredOutputsJsonSchema: Demonstrates requesting Structured Outputs using strict JSON Schema
// with VllmChatClient and QRE runtime state machines.
//
// Technical distinction:
//   Logits-level grammar constrained sampling (Guided Decoding) is powered by VllmChatClient (via vLLM / outlines / xgrammar).
//   QRE provides Thinking policy orchestration, loop state machines, and fail-closed safety.

Console.WriteLine("============================================================================");
Console.WriteLine(" CodexFlow QueryRuntime (QRE) - Structured Outputs via JSON Schema Demo");
Console.WriteLine("============================================================================");

// 1. Define Strict JSON Schema for the target POCO
var schemaJson = """
{
  "type": "object",
  "properties": {
    "summary": { "type": "string" },
    "severity": { "type": "string", "enum": ["Low", "Medium", "High", "Critical"] },
    "approved": { "type": "boolean" },
    "violations": {
      "type": "array",
      "items": { "type": "string" }
    }
  },
  "required": ["summary", "severity", "approved", "violations"],
  "additionalProperties": false
}
""";

using var schemaDoc = JsonDocument.Parse(schemaJson);
var jsonSchemaFormat = ChatResponseFormat.ForJsonSchema(
    schemaDoc.RootElement,
    schemaName: "code_review_report",
    schemaDescription: "Enforces strict structural compliance for automated code review results.");

Console.WriteLine("[1] JSON Schema ResponseFormat created:");
Console.WriteLine($"    Schema Name: code_review_report");
Console.WriteLine($"    Format Type: {jsonSchemaFormat.GetType().Name}");

if (jsonSchemaFormat is ChatResponseFormatJson jsonFormat && jsonFormat.Schema.HasValue)
{
    Console.WriteLine($"    Schema Root Element Type: {jsonFormat.Schema.Value.ValueKind}");
}

// 2. Configure VllmChatOptions with the strict Schema
var chatOptions = new VllmChatOptions
{
    ResponseFormat = jsonSchemaFormat,
    Temperature = 0.1f,
    MaxOutputTokens = 1024,
    ThinkingEnabled = false // Structured outputs pair with direct guided sampling
};

Console.WriteLine($"\n[2] VllmChatOptions configured:");
Console.WriteLine($"    ResponseFormat is JSON Schema: {chatOptions.ResponseFormat is ChatResponseFormatJson j && j.Schema.HasValue}");
Console.WriteLine($"    Temperature: {chatOptions.Temperature}");
Console.WriteLine($"    MaxOutputTokens: {chatOptions.MaxOutputTokens}");

// 3. Mock simulated model response strictly conforming to schema
var simulatedJsonResponse = """
{
  "summary": "All architecture rules verified. Zero nullable warnings detected.",
  "severity": "Low",
  "approved": true,
  "violations": []
}
""";

Console.WriteLine("\n[3] Simulating QRE Agent Turn Execution with Structured Output...");
IAgentRuntime runtime = new AgentRuntime(new StaticRuntimeModelClient(simulatedJsonResponse));

var request = new RuntimeAgentLoopRequest(
    new RuntimeSessionId(Guid.NewGuid().ToString("N")),
    new RuntimeTurnId(Guid.NewGuid().ToString("N")),
    "Execute structured review",
    [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("Generate code review report.")])],
    [],
    new RuntimeModelParameters(RequireJsonObject: true),
    new RuntimePolicySnapshot("schema-demo", "none"),
    new RuntimeEnvironmentSnapshot("local", Path.GetFullPath("."), "schema-demo"),
    new RuntimeBudgetSnapshot(maxSteps: 2, maxToolCalls: 0));

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var result = await runtime.RunAsync(new RuntimeRunRequest(request), null, cancellation.Token);

// 4. Extract and deserialize structured output into strong-typed C# POCO
Console.WriteLine($"\n[4] Parsing Structured Result (Status: {result.Status}):");
var lastStep = result.Turn.Steps.LastOrDefault();
var responseText = lastStep?.Output?.Items.OfType<RuntimeTextItem>().FirstOrDefault()?.Text ?? simulatedJsonResponse;

var report = JsonSerializer.Deserialize<CodeReviewReport>(responseText, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
});

if (report != null)
{
    Console.WriteLine($"    - Summary: {report.Summary}");
    Console.WriteLine($"    - Severity: {report.Severity}");
    Console.WriteLine($"    - Approved: {report.Approved}");
    Console.WriteLine($"    - Violations Count: {report.Violations.Count}");
}
else
{
    Console.Error.WriteLine("Error: Failed to deserialize report into C# object.");
    return 1;
}

Console.WriteLine("\n[5] JSON Schema structured output demo completed successfully.");
return 0;

/// <summary>
/// Strong-typed POCO corresponding to the strict JSON Schema.
/// </summary>
public sealed class CodeReviewReport
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("approved")]
    public bool Approved { get; set; }

    [JsonPropertyName("violations")]
    public List<string> Violations { get; set; } = [];
}
