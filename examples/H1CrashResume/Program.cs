using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Experimental;
using CodexFlow.QueryRuntime.Protocol;

if (args.Length is not (1 or 4))
{
    Console.Error.WriteLine("Usage: H1CrashResume <workspace> [api-url model api-mode]");
    return 2;
}

var workspace = Path.GetFullPath(args[0]);
Directory.CreateDirectory(workspace);
var runDirectory = Path.Combine(workspace, ".qre", "v2", "runs", "h1-crash-source");
var fileStore = new RuntimeJsonCheckpointStore(runDirectory);
var checkpointSink = new FailFastAfterPreparedCheckpointSink(fileStore);
const string objective = "Return exactly H1_REAL_RESUME_OK and no other text.";
var model = args.Length == 4 ? args[2] : null;
var providerIdentity = args.Length == 4
    ? string.Join('|', args[1], args[2], args[3])
    : "static";
var providerDigest = providerIdentity == "static"
    ? "static"
    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerIdentity)))
        .ToLowerInvariant();
var runnerDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("local")))
    .ToLowerInvariant();
var toolCompositionDigest = ExperimentalV2ToolComposition.CreateRuntime(
    QueryRuntimeToolProfile.None,
    workspace).RecoveryCompatibilityDigest;
var request = new RuntimeAgentLoopRequest(
    new RuntimeSessionId("h1-crash-session"),
    new RuntimeTurnId("h1-crash-turn"),
    objective,
    [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(objective)])],
    [],
    new RuntimeModelParameters(Model: model),
    new RuntimePolicySnapshot("v2", "none"),
    new RuntimeEnvironmentSnapshot("local", workspace, "v2:none"),
    new RuntimeBudgetSnapshot(3, 12, maxModelRetries: 1, maxContinuations: 1))
{
    Attempt = RuntimeRunAttempt.Create("attempt-h1-crash-source"),
    CheckpointSink = checkpointSink,
    RecoveryCompatibilityId = $"qre-cli-h1:v3:provider={providerDigest}:runner=local:runner-config={runnerDigest}:storage=sanitizedfixture:profile=none:external=false:tool-composition={toolCompositionDigest}:tool-search=false:thinking=auto:approval=none"
};

await new RuntimeAgentLoop(new MustNotCompleteModelClient()).RunAsync(request);
return 3;

file sealed class FailFastAfterPreparedCheckpointSink(IRuntimeCheckpointSink inner)
    : IRuntimeCheckpointSink
{
    public async ValueTask SaveAsync(RuntimeCheckpointDocument checkpoint, CancellationToken ct)
    {
        await inner.SaveAsync(checkpoint, ct).ConfigureAwait(false);
        if (checkpoint.Kind == RuntimeCheckpointKind.StepPrepared)
        {
            Environment.FailFast("H1 crash injection after durable StepPrepared checkpoint.");
        }
    }
}

file sealed class MustNotCompleteModelClient : IRuntimeModelClient
{
    public async IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
        RuntimeModelRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        throw new InvalidOperationException("Crash injection did not run before model sampling.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
