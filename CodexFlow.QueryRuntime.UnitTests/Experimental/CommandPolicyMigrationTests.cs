using CodexFlow.Core.Agents.Tools;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Experimental;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Experimental;

public sealed class CommandPolicyMigrationTests
{
    [Theory]
    [MemberData(nameof(LegacyVerifyWorkerDeniedCommands))]
    public void QrePolicy_DoesNotAllowLegacyVerifyWorkerDeniedSubcommands(
        IReadOnlyList<string> command)
    {
        var commandCapabilities = ExperimentalCommandCapabilityClassifier.Classify(
            command,
            SandboxMountPolicy.WorkspaceReadWrite);
        var decision = new ExperimentalCapabilityPolicy().Evaluate(
            new QueryRuntimeCapabilityRequest
            {
                Profile = QueryRuntimeToolProfile.Verify,
                ToolName = "qre_sandbox_exec",
                Capabilities = ExperimentalCommandToolCapabilityMapper.InferToolCapabilities(commandCapabilities),
                Command = command,
                CommandCapabilities = commandCapabilities,
                WorkspacePath = "/workspace",
                Network = SandboxNetworkPolicy.Deny,
                Mounts = SandboxMountPolicy.WorkspaceReadWrite
            });

        Assert.NotEqual(QueryRuntimeCapabilityDecisionKind.Allow, decision.Kind);
    }

    public static IEnumerable<object[]> LegacyVerifyWorkerDeniedCommands()
    {
        var deniedSubcommands = CommandExecutionPolicy.VerifyWorker.DeniedSubcommands ??
            new Dictionary<string, IReadOnlySet<string>>();
        foreach (var (executable, subcommands) in deniedSubcommands)
        {
            foreach (var subcommand in subcommands)
            {
                yield return [new[] { executable, subcommand }];
            }
        }
    }

}
