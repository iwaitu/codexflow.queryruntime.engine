using System.Reflection;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

// SkillsWorkflow: Demonstrates configuring and using the Skill system in QRE with VllmChatClient.
//
// Usage:
//   dotnet run --project examples/SkillsWorkflow
//   dotnet run --project examples/SkillsWorkflow -- --endpoint http://localhost:8000/v1 --model qwen-2.5

var (endpoint, model, apiKey) = LiveExample.Configuration(args);
var evidenceDirectory = LiveExample.Argument(args, "--evidence-dir");

// 1. Resolve skills directory
var baseDir = AppContext.BaseDirectory;
var skillsDir = Path.Combine(baseDir, "skills");
if (!Directory.Exists(skillsDir))
{
    // Fallback to project source directory if running directly
    var sourceSkillsDir = Path.Combine(Directory.GetCurrentDirectory(), "examples", "SkillsWorkflow", "skills");
    if (Directory.Exists(sourceSkillsDir))
    {
        skillsDir = sourceSkillsDir;
    }
}

Console.WriteLine("================================================================");
Console.WriteLine(" CodexFlow QueryRuntime (QRE) - Skill System & Workflows Demo");
Console.WriteLine("================================================================");
Console.WriteLine($"[1] Resolved Skills Directory: {skillsDir}");

// 2. Configure VllmChatOptions with Skills enabled
var chatOptions = new VllmChatOptions
{
    EnableSkills = true,
    SkillDirectoryPath = skillsDir,
    ThinkingEnabled = false
};

Console.WriteLine($"[2] VllmChatOptions Configured: EnableSkills={chatOptions.EnableSkills}, Thinking={chatOptions.ThinkingEnabled}");

// 3. Inspect VllmChatClient two-phase skill injection
using var evidence = new LiveEvidenceHandler(evidenceDirectory);
using var http = new HttpClient(evidence);
using var client = QreModelProviderSelector.CreateDefault().CreateClient(
    endpoint ?? "http://localhost:8000/v1", apiKey, model, httpClient: http);
var baseType = typeof(VllmBaseChatClient);
var prepareMethod = baseType.GetMethod("PrepareMessagesWithSkills", BindingFlags.NonPublic | BindingFlags.Instance);
var buildSkillToolsMethod = baseType.GetMethod("CreateBuiltInSkillTools", BindingFlags.NonPublic | BindingFlags.Static);

var inputMessages = new[] { new ChatMessage(ChatRole.User, "Please review the newly added controller code.") };
var preparedMessages = prepareMethod?.Invoke(client, new object[] { inputMessages, chatOptions }) as IEnumerable<ChatMessage>;

Console.WriteLine("\n[3] Two-Phase On-Demand Metadata Discovery:");
if (preparedMessages != null)
{
    foreach (var msg in preparedMessages)
    {
        if (msg.Role == ChatRole.System)
        {
            Console.WriteLine("--- System Message Injected by VllmChatClient ---");
            Console.WriteLine(msg.Text?.Trim());
            Console.WriteLine("-------------------------------------------------");
        }
    }
}

Console.WriteLine("\n[4] Built-in Skill Tools Registered by Client:");
var builtInTools = (buildSkillToolsMethod?.Invoke(null, new object[] { skillsDir }) as IEnumerable<AITool>)?
    .OfType<AIFunction>().Where(t => t.Name is "ListSkillFiles" or "ReadSkillFile").ToArray()
    ?? throw new InvalidOperationException("The locked SDK's skill tool discovery API is unavailable.");
foreach (var tool in builtInTools)
    Console.WriteLine($"  - Tool: {tool.Name} ({tool.Description})");
LiveExample.Require(builtInTools.Length == 2, "Both SDK skill tools discovered");

// 4. Execute turn using QRE Engine.V2
Console.WriteLine("\n[5] Executing QRE Runtime Turn...");
var mockResponse = "Verified code-reviewer skill is loaded. Architectural layering and compiler zero-warning standards satisfied.";
using var liveModel = endpoint == null ? null : new MeaiRuntimeModelClient(client, _ => new VllmChatOptions
{
    EnableSkills = true, SkillDirectoryPath = skillsDir, ThinkingEnabled = false, MaxOutputTokens = 2048
});
IAgentRuntime runtime = new AgentRuntime(liveModel is null ? new StaticRuntimeModelClient(mockResponse) : liveModel);
var executor = new SkillExecutor(builtInTools);
var prompt = "Use the code-reviewer skill. First call ListSkillFiles, then ReadSkillFile to read code-reviewer/SKILL.md. " +
    "Review this synthetic code: public string Fetch(HttpClient client) => client.GetStringAsync(\"https://example.invalid\").Result; " +
    "Explain the violated skill rule and suggest the asynchronous fix. Do not claim a compiler build was run.";

var request = new RuntimeAgentLoopRequest(
    new RuntimeSessionId(Guid.NewGuid().ToString("N")),
    new RuntimeTurnId(Guid.NewGuid().ToString("N")),
    "Execute review with skill",
    [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(prompt)])],
    endpoint == null ? [] : builtInTools.Select(tool => new RuntimeToolDescriptor(tool.Name, "1", tool.Description,
        tool.JsonSchema, RuntimeToolSideEffect.ReadOnly, RuntimeToolIdempotency.Idempotent)).ToArray(),
    new RuntimeModelParameters(Model: model, MaxOutputTokens: 2048),
    new RuntimePolicySnapshot("skills-demo", "none"),
    new RuntimeEnvironmentSnapshot("local", skillsDir, "skills-demo"),
    new RuntimeBudgetSnapshot(maxSteps: 6, maxToolCalls: 5)) { ToolExecutor = executor };

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(180));
var result = await runtime.RunAsync(new RuntimeRunRequest(request), new ConsoleEvents(), cancellation.Token);

Console.WriteLine($"\n[6] Execution Result: Status={result.Status}; Steps={result.Turn.Steps.Count}");
LiveExample.Require(result.Status == RuntimeTurnStatus.Completed, $"Runtime completed: {result.Error}");
if (endpoint != null)
{
    var firstMessages = evidence.Requests.FirstOrDefault()?["messages"]?.ToJsonString() ?? "";
    LiveExample.Require(firstMessages.Contains("code-reviewer", StringComparison.Ordinal) &&
        !firstMessages.Contains("Prevent blocking calls", StringComparison.Ordinal), "First HTTP request contains skill metadata, not its full body");
    LiveExample.Require(evidence.StatusCodes.Count >= 2 && evidence.StatusCodes.All(s => s == 200), "Multiple real HTTP 200 responses observed");
    LiveExample.Require(executor.Invocations.Contains("ListSkillFiles") && executor.Invocations.Contains("ReadSkillFile"), "QRE executed both skill tools");
    LiveExample.Require(executor.ReadContent?.Contains("Prevent blocking calls", StringComparison.Ordinal) == true, "ReadSkillFile returned the bundled skill body");
    LiveExample.Require(evidence.Requests.Skip(1).Any(r => r["messages"]!.ToJsonString().Contains("Prevent blocking calls", StringComparison.Ordinal)), "Skill body sent back to the real model");
    LiveExample.Require(!string.IsNullOrWhiteSpace(result.FinalText) && result.FinalText.Contains("async", StringComparison.OrdinalIgnoreCase), "Model produced an asynchronous review recommendation");
    if (evidenceDirectory != null) await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "response.txt"), result.FinalText);
}
Console.WriteLine("Skills workflow completed successfully.");
return result.Status == RuntimeTurnStatus.Completed ? 0 : 1;

file sealed class SkillExecutor(AIFunction[] tools) : IRuntimeToolExecutor
{
    public List<string> Invocations { get; } = [];
    public string? ReadContent { get; private set; }

    public async ValueTask<RuntimeToolResult> ExecuteAsync(RuntimeToolDescriptor descriptor, RuntimeToolCall call,
        RuntimeToolExecutionContext context, CancellationToken ct)
    {
        var tool = tools.Single(t => t.Name == descriptor.CanonicalName);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments.GetRawText())!;
        var value = await tool.InvokeAsync(new AIFunctionArguments(arguments), ct);
        var text = value as string ?? JsonSerializer.Serialize(value);
        Invocations.Add(tool.Name);
        if (tool.Name == "ReadSkillFile") ReadContent = text;
        Console.WriteLine($"\n[Tool executed] {tool.Name}");
        return new RuntimeToolResult(call.InvocationId, text, true);
    }
}

file sealed class ConsoleEvents : IRuntimeEventSink
{
    public ValueTask OnEventAsync(RuntimePresentationEvent runtimeEvent, CancellationToken ct)
    {
        if (runtimeEvent.Type == RuntimePresentationEventType.TextDelta)
        {
            Console.Write(runtimeEvent.Text);
        }
        return ValueTask.CompletedTask;
    }
}
