using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

// StructuredOutputsJsonSchema: Demonstrates requesting Structured Outputs using strict JSON Schema
// with VllmChatClient and QRE runtime state machines.
//
// The SDK serializes the schema; enforcement depends on the remote backend.
var (endpoint, model, apiKey) = LiveExample.Configuration(args);
var evidenceDirectory = LiveExample.Argument(args, "--evidence-dir");

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

Console.WriteLine("\n[3] Executing QRE Agent Turn with Structured Output...");
using var evidence = new LiveEvidenceHandler(evidenceDirectory);
using var http = new HttpClient(evidence);
using var liveModel = endpoint == null ? null : new MeaiRuntimeModelClient(
    QreModelProviderSelector.CreateDefault().CreateClient(endpoint, apiKey, model, httpClient: http),
    _ => new VllmChatOptions
    {
        ResponseFormat = jsonSchemaFormat, Temperature = 0.1f, MaxOutputTokens = 1024,
        ThinkingEnabled = false, EnableSkills = false
    });
IAgentRuntime runtime = new AgentRuntime(liveModel is null ? new StaticRuntimeModelClient(simulatedJsonResponse) : liveModel);

var request = new RuntimeAgentLoopRequest(
    new RuntimeSessionId(Guid.NewGuid().ToString("N")),
    new RuntimeTurnId(Guid.NewGuid().ToString("N")),
    "Execute structured review",
    [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(
        "Return only a JSON code review report for this synthetic code: public string Fetch(HttpClient client) => " +
        "client.GetStringAsync(\"https://example.invalid\").Result; Blocking on an asynchronous task is a violation. " +
        "Set approved=false and include at least one violation. Do not claim to have compiled the code.")])],
    [],
    new RuntimeModelParameters(Model: model, MaxOutputTokens: 1024, RequireJsonObject: true),
    new RuntimePolicySnapshot("schema-demo", "none"),
    new RuntimeEnvironmentSnapshot("local", Path.GetFullPath("."), "schema-demo"),
    new RuntimeBudgetSnapshot(maxSteps: 2, maxToolCalls: 0));

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(180));
var result = await runtime.RunAsync(new RuntimeRunRequest(request), null, cancellation.Token);

// 4. Extract and deserialize structured output into strong-typed C# POCO
Console.WriteLine($"\n[4] Parsing Structured Result (Status: {result.Status}):");
LiveExample.Require(result.Status == RuntimeTurnStatus.Completed, $"Runtime completed: {result.Error}");
var responseText = result.FinalText;
if (endpoint != null && evidenceDirectory != null)
    await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "response.json"), responseText);
Console.WriteLine(responseText);
if (endpoint != null)
{
    LiveExample.Require(evidence.StatusCodes.Count > 0 && evidence.StatusCodes.All(s => s == 200), "Real HTTP 200 response observed");
    LiveExample.Require(evidence.Requests.All(r => r["response_format"]?["type"]?.GetValue<string>() == "json_schema" &&
        r["response_format"]?["json_schema"]?["strict"]?.GetValue<bool>() == true &&
        System.Text.Json.Nodes.JsonNode.DeepEquals(r["response_format"]?["json_schema"]?["schema"], System.Text.Json.Nodes.JsonNode.Parse(schemaJson))),
        "Actual HTTP body contains the exact schema with json_schema and strict=true");
}
LiveExample.Require(!string.IsNullOrWhiteSpace(responseText), "Runtime returned nonempty model output (no fallback)");
using var responseDoc = JsonDocument.Parse(responseText);
var root = responseDoc.RootElement;
LiveExample.Require(root.ValueKind == JsonValueKind.Object, $"Schema requires an object root; actual root: {root.ValueKind}");
LiveExample.Require(root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "approved", "severity", "summary", "violations" }),
    "All required properties present, no duplicates or additional properties");
LiveExample.Require(root.GetProperty("summary").ValueKind == JsonValueKind.String &&
    root.GetProperty("severity").ValueKind == JsonValueKind.String &&
    new[] { "Low", "Medium", "High", "Critical" }.Contains(root.GetProperty("severity").GetString()) &&
    root.GetProperty("approved").ValueKind is JsonValueKind.True or JsonValueKind.False &&
    root.GetProperty("violations").ValueKind == JsonValueKind.Array &&
    root.GetProperty("violations").EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
    "Response conforms to every constraint in the example schema");

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

if (endpoint != null)
{
    LiveExample.Require(!report.Approved && report.Violations.Count > 0, "Live model identified the fixture violation");
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
