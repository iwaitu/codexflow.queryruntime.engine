using System.Text;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using CodexFlow.QueryRuntime.Experimental;
using CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.IntegrationTests;

public sealed class ExperimentalHarnessRealLlmPhaseTests(ITestOutputHelper output)
{
    [Fact]
    public void Phase0_ProjectLlmConfiguration_LoadsFromAppsettings()
    {
        var liveHost = CreateHostOrSkip();
        using (liveHost)
        {
            output.WriteLine($"Model: {liveHost.Settings.Model}");
            output.WriteLine($"ApiMode: {liveHost.Settings.ApiMode ?? "(default)"}");

            Assert.False(string.IsNullOrWhiteSpace(liveHost.Settings.ApiUrl));
            Assert.False(string.IsNullOrWhiteSpace(liveHost.Settings.ApiKey));
            Assert.False(string.IsNullOrWhiteSpace(liveHost.Settings.Model));
        }
    }

    [Fact]
    public async Task Phase1_ProjectLlmProvider_StreamsResponse()
    {
        var liveHost = CreateHostOrSkip();
        using (liveHost)
        {
            var ct = TestContext.Current.CancellationToken;
            var responseText = new StringBuilder();
            var thinkingText = new StringBuilder();
            var updates = 0;

            await foreach (var update in liveHost.StreamResponseAsync(
                               [
                                   new ChatMessage(ChatRole.System, "你是 QueryRuntime 真实集成测试助手。只回答一个短句。"),
                                   new ChatMessage(ChatRole.User, "用一句中文回答：真实 provider streaming 是否可用？")
                               ],
                               liveHost.CreateChatOptions(maxOutputTokens: 64),
                               ct))
            {
                updates++;
                ChatClientAudit.AppendStreamingUpdate(update, responseText, thinkingText);
            }

            output.WriteLine($"Streaming updates: {updates}");
            output.WriteLine($"Response: {responseText}");
            output.WriteLine($"Thinking length: {thinkingText.Length}");

            Assert.True(updates > 0, "Expected at least one streaming update from the configured LLM provider.");
            Assert.False(string.IsNullOrWhiteSpace(responseText.ToString()), "Expected non-empty streamed response text.");
        }
    }

    [Fact]
    public async Task Phase1_ProjectLlmProvider_AnthropicMessagesThinkingOff_DoesNotStreamThinking()
    {
        var liveHost = CreateHostOrSkip();
        using (liveHost)
        {
            if (!string.Equals(liveHost.Settings.ApiMode, "AnthropicMessages", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(liveHost.Settings.ApiMode, "anthropic-messages", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Skip($"Configured provider ApiMode is {liveHost.Settings.ApiMode ?? "(default)"}, not AnthropicMessages.");
            }

            var responseText = new StringBuilder();
            var thinkingText = new StringBuilder();
            var updates = 0;

            await foreach (var update in liveHost.StreamResponseAsync(
                               [
                                   new ChatMessage(ChatRole.System, "只按用户要求输出固定文本。"),
                                   new ChatMessage(ChatRole.User, "只输出以下固定文本，不要添加任何其它字符：QRE_ANTHROPIC_THINKING_OFF_OK")
                               ],
                               new VllmChatOptions
                               {
                                   Temperature = 0,
                                   TopP = liveHost.Settings.TopP,
                                   MaxOutputTokens = 64,
                                   ThinkingEnabled = false
                               },
                               TestContext.Current.CancellationToken))
            {
                updates++;
                ChatClientAudit.AppendStreamingUpdate(update, responseText, thinkingText);
            }

            output.WriteLine($"Streaming updates: {updates}");
            output.WriteLine($"Response: {responseText}");
            output.WriteLine($"Thinking length: {thinkingText.Length}");

            Assert.True(updates > 0, "Expected at least one streaming update from the configured LLM provider.");
            Assert.Contains("QRE_ANTHROPIC_THINKING_OFF_OK", responseText.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, thinkingText.Length);
        }
    }

    [Fact]
    public async Task Phase2_ExperimentalHarness_NoToolRun_WritesRealJsonlTrace()
    {
        var liveHost = CreateHostOrSkip();
        using (liveHost)
        using (var workspace = TemporaryWorkspace.Create())
        {
            var ct = TestContext.Current.CancellationToken;
            var harness = new ExperimentalQueryRuntimeHarness(liveHost.CreateExperimentalModelClient());

            var result = await harness.RunAsync(
                new ExperimentalQueryRuntimeRequest
                {
                    Prompt = "用一句中文说明 QueryRuntime harness 的作用。不要调用工具。",
                    WorkspacePath = workspace.Path,
                    RunId = "real-no-tool",
                    MaxRounds = 2,
                    Options = liveHost.CreateChatOptions(maxOutputTokens: 96)
                },
                ct);

            output.WriteLine($"Final text: {result.FinalText}");
            output.WriteLine($"Trace: {result.TraceFilePath}");
            output.WriteLine($"Termination: {result.TerminationReason}");

            Assert.False(string.IsNullOrWhiteSpace(result.FinalText));
            Assert.True(File.Exists(result.TraceFilePath));

            var traceLines = ReadTraceLines(result.TraceFilePath);
            Assert.Contains(traceLines, line => line.Contains("\"Type\":\"run.started\"", StringComparison.Ordinal));
            Assert.Contains(traceLines, line => line.Contains("\"Type\":\"model.request\"", StringComparison.Ordinal));
            Assert.Contains(traceLines, line => line.Contains("\"Type\":\"model.response\"", StringComparison.Ordinal));
            Assert.Contains(traceLines, line => line.Contains("\"Type\":\"run.completed\"", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Phase3_ExperimentalHarness_ReadOnlyToolRun_ExecutesWorkspaceToolWithRealProvider()
    {
        var liveHost = CreateHostOrSkip();
        using (liveHost)
        using (var workspace = TemporaryWorkspace.Create())
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace.Path, "README.md"),
                "# qre-real-test\n\nThis file proves the readonly tool can inspect the workspace.\n",
                TestContext.Current.CancellationToken);

            var readonlyTools = ExperimentalReadOnlyToolPack.Create(workspace.Path);
            var listFilesTool = readonlyTools.Single(tool => tool.Name == "qre_list_files");
            var modelClient = liveHost.CreateExperimentalModelClient(enableRequiredToolChoiceInjection: false);
            using var _ = modelClient as IDisposable;
            var harness = new ExperimentalQueryRuntimeHarness(modelClient);

            var result = await harness.RunAsync(
                new ExperimentalQueryRuntimeRequest
                {
                    Prompt = "请调用 qre_list_files 工具查看 workspace 根目录。不要猜测。工具返回后，用一句中文说明是否看到了 README.md。",
                    WorkspacePath = workspace.Path,
                    RunId = "real-readonly-tool",
                    MaxRounds = 3,
                    EnableTools = true,
                    Tools = [listFilesTool],
                    Options = new VllmChatOptions
                    {
                        Temperature = 0,
                        TopP = liveHost.Settings.TopP,
                        MaxOutputTokens = 128,
                        ThinkingEnabled = false,
                        Tools = [listFilesTool]
                    }
                },
                TestContext.Current.CancellationToken);

            output.WriteLine($"Final text: {result.FinalText}");
            output.WriteLine($"Trace: {result.TraceFilePath}");
            output.WriteLine($"Tool calls: {result.TotalToolCalls}");
            output.WriteLine($"Termination: {result.TerminationReason}");

            var traceLines = ReadTraceLines(result.TraceFilePath);
            foreach (var errorLine in traceLines.Where(static line => line.Contains("\"Type\":\"runtime.error\"", StringComparison.Ordinal)))
            {
                output.WriteLine(errorLine);
            }

            Assert.True(result.TotalToolCalls >= 1, "Expected the live provider to execute the qre_list_files tool through auto-tool mode.");
            Assert.False(string.IsNullOrWhiteSpace(result.FinalText));
            Assert.Contains(traceLines, line => line.Contains("\"Type\":\"tool.call.requested\"", StringComparison.Ordinal));
            Assert.Contains(traceLines, line => line.Contains("\"Type\":\"tool.execution.completed\"", StringComparison.Ordinal));
            Assert.Contains(traceLines, line => line.Contains("README.md", StringComparison.Ordinal));
            Assert.Contains(traceLines, line => line.Contains("\"Type\":\"run.completed\"", StringComparison.Ordinal));
        }
    }

    private static RealQueryRuntimeTestHost CreateHostOrSkip()
    {
        if (!RealQueryRuntimeTestHost.TryCreate(out var liveHost, out var reason))
        {
            Assert.Skip(reason);
        }

        return liveHost!;
    }

    private static string[] ReadTraceLines(string traceFilePath)
        => File.ReadAllLines(traceFilePath)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryWorkspace Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "codexflow-qre-real",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryWorkspace(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
