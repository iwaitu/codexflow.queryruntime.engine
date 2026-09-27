using System.Reflection;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

// SkillsWorkflow: Demonstrates configuring and using the Skill system in QRE with VllmChatClient.
//
// Usage:
//   dotnet run --project examples/SkillsWorkflow
//   dotnet run --project examples/SkillsWorkflow -- --endpoint http://localhost:8000/v1 --model qwen-2.5

var endpoint = ArgumentAfter(args, "--endpoint");
var model = ArgumentAfter(args, "--model") ?? "qwen3-coder-30b";
var apiKey = ArgumentAfter(args, "--key") ?? "placeholder-key";

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
    ThinkingEnabled = true
};

Console.WriteLine($"[2] VllmChatOptions Configured: EnableSkills={chatOptions.EnableSkills}, Thinking={chatOptions.ThinkingEnabled}");

// 3. Inspect VllmChatClient two-phase skill injection
var client = new VllmQwen3ChatClient(endpoint ?? "http://localhost:8000/v1", model, apiKey);
var baseType = typeof(VllmBaseChatClient);
var prepareMethod = baseType.GetMethod("PrepareMessagesWithSkills", BindingFlags.NonPublic | BindingFlags.Instance);
var buildSkillToolsMethod = baseType.GetMethod("CreateBuiltInSkillTools", BindingFlags.NonPublic | BindingFlags.Instance);

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
var builtInTools = buildSkillToolsMethod?.Invoke(client, new object[] { skillsDir }) as IEnumerable<AIFunction>;
if (builtInTools != null)
{
    foreach (var tool in builtInTools)
    {
        Console.WriteLine($"  - Tool: {tool.Name} ({tool.Description})");
    }
}

// 4. Execute turn using QRE Engine.V2
Console.WriteLine("\n[5] Executing QRE Runtime Turn...");
var mockResponse = "Verified code-reviewer skill is loaded. Architectural layering and compiler zero-warning standards satisfied.";
IAgentRuntime runtime = new AgentRuntime(new StaticRuntimeModelClient(mockResponse));

var request = new RuntimeAgentLoopRequest(
    new RuntimeSessionId(Guid.NewGuid().ToString("N")),
    new RuntimeTurnId(Guid.NewGuid().ToString("N")),
    "Execute review with skill",
    [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("Review code standards.")])],
    [],
    new RuntimeModelParameters(),
    new RuntimePolicySnapshot("skills-demo", "none"),
    new RuntimeEnvironmentSnapshot("local", skillsDir, "skills-demo"),
    new RuntimeBudgetSnapshot(maxSteps: 3, maxToolCalls: 2));

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var result = await runtime.RunAsync(new RuntimeRunRequest(request), new ConsoleEvents(), cancellation.Token);

Console.WriteLine($"\n[6] Execution Result: Status={result.Status}; Steps={result.Turn.Steps.Count}");
Console.WriteLine("Skills workflow completed successfully.");
return result.Status == RuntimeTurnStatus.Completed ? 0 : 1;

static string? ArgumentAfter(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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
