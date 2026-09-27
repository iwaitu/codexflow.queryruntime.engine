using System.Text.Json;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Cli;

[Collection("QreCliConsole")]
public sealed class QreLogCommandsTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "qre-log-tests-" + Guid.NewGuid().ToString("N"));

    private string Add(string id, string start, string end, string status = "completed", string kind = "audit")
    {
        var relative = kind == "diagnostics" ? ".qre/v2/diagnostics" : kind == "private" ? ".qre/v2/private/runs" : ".qre/v2/runs";
        var path = Path.Combine(_workspace, relative, id);
        Directory.CreateDirectory(path);
        object manifest = kind == "diagnostics"
            ? new { manifestSchema = "qre.outbound-diagnostics.manifest/1", createdUtc = start, completedUtc = end, completionStatus = status }
            : new { type = "qre.v2.audit.manifest", createdAt = start, updatedAt = end, status };
        File.WriteAllText(Path.Combine(path, "manifest.json"), JsonSerializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(path, "audit.v1.jsonl"), "fixture");
        return path;
    }

    private (int Code, string Output) Run(params string[] args)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var code = QreLogCommands.Run([.. args, "--workspace", _workspace, "--json"]);
            return (code, output.ToString());
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public void RangeIncludesOverlappingRunsAndNormalizesTimezone()
    {
        Add("earlier", "2026-09-27T05:00:00Z", "2026-09-27T05:59:59Z");
        Add("overlap", "2026-09-27T05:30:00Z", "2026-09-27T06:15:00Z");
        Add("inside", "2026-09-27T06:10:00Z", "2026-09-27T06:11:00Z", kind: "private");
        Add("end-exclusive", "2026-09-27T07:00:00Z", "2026-09-27T07:10:00Z");
        var result = Run("list", "--from", "2026-09-27T14:00:00+08:00", "--to", "2026-09-27T15:00:00+08:00");
        Assert.Equal(0, result.Code);
        using var doc = JsonDocument.Parse(result.Output);
        var ids = doc.RootElement.GetProperty("results").EnumerateArray().Select(x => x.GetProperty("entry").GetProperty("runId").GetString()!).ToArray();
        Assert.Equal(["overlap", "inside"], ids);
    }

    [Theory]
    [InlineData("2026-09-27T14:00:00", "2026-09-27T15:00:00Z")]
    [InlineData("2026-09-27T16:00:00Z", "2026-09-27T15:00:00Z")]
    public void InvalidRangesFail(string from, string to) => Assert.Equal(1, Run("list", "--from", from, "--to", to).Code);

    [Fact]
    public void DeletionPreviewsAndPreservesActiveAndBoundaryRuns()
    {
        var old = Add("old", "2026-09-26T00:00:00Z", "2026-09-26T00:01:00Z");
        var active = Add("active", "2026-09-26T00:00:00Z", "2026-09-26T00:01:00Z", "active");
        var boundary = Add("boundary", "2026-09-27T00:00:00Z", "2026-09-27T00:01:00Z");
        Assert.Equal(0, Run("delete", "--before", "2026-09-27").Code);
        Assert.True(Directory.Exists(old));
        Assert.Equal(0, Run("delete", "--before", "2026-09-27", "--execute").Code);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(boundary));
    }

    [Fact]
    public void LockedRunFailsWithoutDeletingFiles()
    {
        var path = Add("locked", "2026-09-26T00:00:00Z", "2026-09-26T00:01:00Z");
        using var writer = new FileStream(Path.Combine(path, "audit.v1.jsonl"), FileMode.Open, FileAccess.Write, FileShare.Read);
        Assert.Equal(1, Run("delete", "--date", "2026-09-26", "--execute").Code);
        Assert.True(File.Exists(Path.Combine(path, "manifest.json")));
    }

    [Fact]
    public void CorruptRunIsReportedAndPreserved()
    {
        var path = Add("corrupt", "2026-09-26T00:00:00Z", "2026-09-26T00:01:00Z");
        File.WriteAllText(Path.Combine(path, "manifest.json"), "{}");
        var result = Run("delete", "--before", "2026-09-27", "--execute");
        Assert.Equal(1, result.Code);
        Assert.Contains("unreadable", result.Output);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void DiagnosticsAndPaginationAreSupported()
    {
        Add("first", "2026-09-26T00:00:00Z", "2026-09-26T00:01:00Z", "complete", "diagnostics");
        Add("second", "2026-09-27T00:00:00Z", "2026-09-27T00:01:00Z", "incomplete", "diagnostics");
        var result = Run("list", "--kind", "diagnostics", "--descending", "--take", "1");
        Assert.Equal(0, result.Code);
        Assert.Contains("second", result.Output);
        Assert.DoesNotContain("first", result.Output);
    }

    [Fact]
    public async Task RealCliRunCanBeSelectedForReplay()
    {
        Directory.CreateDirectory(_workspace);
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, await QreCli.RunAsync(["run", "-w", _workspace, "--response", "fixture answer", "--trace-data", "sanitized", "--json", "fixture prompt"], TestContext.Current.CancellationToken));
            var entry = Assert.Single(QreLogCommands.Scan(_workspace));
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await QreCli.RunAsync(["replay", "latest", "-w", _workspace, "--audit-file", Path.Combine(entry.Path, "audit.v1.jsonl"), "--json"], TestContext.Current.CancellationToken));
            Assert.Contains("replay", output.ToString());
        }
        finally { Console.SetOut(original); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }
}
