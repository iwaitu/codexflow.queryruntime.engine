using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Experimental;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Experimental;

public sealed class TraceReplaySecurityTests
{
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/tmp/outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("\\\\server\\share\\outside.txt")]
    public void BlobReader_RejectsAbsoluteAndTraversalPaths(string blobPath)
    {
        using var run = TemporaryDirectory.Create();
        var traceFile = WriteModelResponseTrace(run.Path, blobPath, 0, new string('0', 64));

        var ex = Assert.ThrowsAny<Exception>(() =>
            new RecordedReplayModelClient(traceFile));

        Assert.True(ex is InvalidDataException or InvalidOperationException);
    }

    [Fact]
    public void BlobReader_RejectsDigestMismatch()
    {
        using var run = TemporaryDirectory.Create();
        var bytes = Encoding.UTF8.GetBytes("tampered payload");
        var relativePath = Path.Combine("blobs", "payload.txt");
        Directory.CreateDirectory(Path.Combine(run.Path, "blobs"));
        File.WriteAllBytes(Path.Combine(run.Path, relativePath), bytes);
        var traceFile = WriteModelResponseTrace(run.Path, relativePath, bytes.Length, new string('0', 64));

        var ex = Assert.Throws<InvalidDataException>(() =>
            new RecordedReplayModelClient(traceFile));

        Assert.Contains("digest mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlobReader_RejectsDeclaredLengthMismatch()
    {
        using var run = TemporaryDirectory.Create();
        var bytes = Encoding.UTF8.GetBytes("payload");
        var relativePath = Path.Combine("blobs", "payload.txt");
        Directory.CreateDirectory(Path.Combine(run.Path, "blobs"));
        File.WriteAllBytes(Path.Combine(run.Path, relativePath), bytes);
        var traceFile = WriteModelResponseTrace(run.Path, relativePath, bytes.Length + 1, Digest(bytes));

        var ex = Assert.Throws<InvalidDataException>(() =>
            new RecordedReplayModelClient(traceFile));

        Assert.Contains("length mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlobReader_RejectsSymlinkEscape()
    {
        using var run = TemporaryDirectory.Create();
        using var outside = TemporaryDirectory.Create();
        var outsideFile = Path.Combine(outside.Path, "payload.txt");
        var bytes = Encoding.UTF8.GetBytes("outside payload");
        File.WriteAllBytes(outsideFile, bytes);
        var blobs = Path.Combine(run.Path, "blobs");
        Directory.CreateDirectory(blobs);
        var link = Path.Combine(blobs, "payload.txt");
        try
        {
            File.CreateSymbolicLink(link, outsideFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var traceFile = WriteModelResponseTrace(
            run.Path,
            Path.Combine("blobs", "payload.txt"),
            bytes.Length,
            Digest(bytes));

        var error = Assert.Throws<InvalidOperationException>(() =>
            new RecordedReplayModelClient(traceFile));

        Assert.Contains("Symlink traversal outside", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceReader_RejectsOversizedLine()
    {
        using var run = TemporaryDirectory.Create();
        var traceFile = Path.Combine(run.Path, "events.jsonl");
        File.WriteAllText(traceFile, "{\"Type\":\"" + new string('x', 128) + "\"}");

        var ex = Assert.Throws<InvalidDataException>(() => JsonlTraceStore.ReadRecords(
            traceFile,
            new QueryRuntimeTraceReadOptions { MaxLineBytes = 32 },
            TestContext.Current.CancellationToken));

        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraceReader_RejectsTooManyEvents()
    {
        using var run = TemporaryDirectory.Create();
        var traceFile = Path.Combine(run.Path, "events.jsonl");
        File.WriteAllLines(traceFile, ["{\"Type\":\"one\"}", "{\"Type\":\"two\"}"]);

        var ex = Assert.Throws<InvalidDataException>(() => JsonlTraceStore.ReadRecords(
            traceFile,
            new QueryRuntimeTraceReadOptions { MaxEventCount = 1 },
            TestContext.Current.CancellationToken));

        Assert.Contains("event", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraceReader_RejectsExcessiveJsonDepth()
    {
        using var run = TemporaryDirectory.Create();
        var traceFile = Path.Combine(run.Path, "events.jsonl");
        File.WriteAllText(traceFile, "{\"Type\":\"deep\",\"Data\":{\"a\":{\"b\":{\"c\":1}}}}");

        var ex = Assert.Throws<InvalidDataException>(() => JsonlTraceStore.ReadRecords(
            traceFile,
            new QueryRuntimeTraceReadOptions { MaxJsonDepth = 2 },
            TestContext.Current.CancellationToken));

        Assert.Contains("invalid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraceReader_RejectsOversizedJsonStringIndependentlyOfLineLimit()
    {
        using var run = TemporaryDirectory.Create();
        var traceFile = Path.Combine(run.Path, "events.jsonl");
        File.WriteAllText(traceFile, JsonSerializer.Serialize(new
        {
            Type = "model.response",
            Data = new { Text = new string('x', 65) }
        }));

        var ex = Assert.Throws<InvalidDataException>(() => JsonlTraceStore.ReadRecords(
            traceFile,
            new QueryRuntimeTraceReadOptions
            {
                MaxTraceFileBytes = 4096,
                MaxLineBytes = 4096,
                MaxStringBytes = 64
            },
            TestContext.Current.CancellationToken));

        Assert.Contains("string", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplayModelClient_EnforcesAggregateBlobLimit()
    {
        using var run = TemporaryDirectory.Create();
        var bytes = Encoding.UTF8.GetBytes("12345678");
        var relativePath = Path.Combine("blobs", "payload.txt");
        Directory.CreateDirectory(Path.Combine(run.Path, "blobs"));
        File.WriteAllBytes(Path.Combine(run.Path, relativePath), bytes);
        var blob = new
        {
            Algorithm = "sha256",
            Digest = Digest(bytes),
            SizeBytes = bytes.Length,
            Path = relativePath
        };
        var line = JsonSerializer.Serialize(new
        {
            Type = "model.response",
            Data = new { AssistantText = (string?)null, AssistantTextBlob = blob }
        });
        var traceFile = Path.Combine(run.Path, "events.jsonl");
        File.WriteAllLines(traceFile, [line, line]);

        var ex = Assert.Throws<InvalidDataException>(() => new RecordedReplayModelClient(
            traceFile,
            new QueryRuntimeTraceReadOptions { MaxBlobBytes = 16, MaxTotalBlobBytes = 12 }));

        Assert.Contains("aggregate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplayAdapters_ShareOneAggregateBlobBudget()
    {
        using var run = TemporaryDirectory.Create();
        var modelBytes = Encoding.UTF8.GetBytes("model123");
        var toolBytes = Encoding.UTF8.GetBytes("tool-123");
        Directory.CreateDirectory(Path.Combine(run.Path, "blobs"));
        File.WriteAllBytes(Path.Combine(run.Path, "blobs", "model.txt"), modelBytes);
        File.WriteAllBytes(Path.Combine(run.Path, "blobs", "tool.txt"), toolBytes);
        object Blob(string path, byte[] bytes) => new
        {
            Algorithm = "sha256",
            Digest = Digest(bytes),
            SizeBytes = bytes.Length,
            Path = path
        };
        var traceFile = Path.Combine(run.Path, "events.jsonl");
        File.WriteAllLines(traceFile,
        [
            JsonSerializer.Serialize(new
            {
                Type = "tool.call.requested",
                Data = new { CallId = "call-1", ArgumentHash = "hash-1" }
            }),
            JsonSerializer.Serialize(new
            {
                Type = "tool.execution.completed",
                Data = new
                {
                    CallId = "call-1",
                    ToolName = "recorded_tool",
                    Result = (string?)null,
                    ResultBlob = Blob("blobs/tool.txt", toolBytes)
                }
            }),
            JsonSerializer.Serialize(new
            {
                Type = "model.response",
                Data = new
                {
                    AssistantText = (string?)null,
                    AssistantTextBlob = Blob("blobs/model.txt", modelBytes)
                }
            })
        ]);
        var context = new RecordedReplayReadContext(new QueryRuntimeTraceReadOptions
        {
            MaxBlobBytes = 16,
            MaxTotalBlobBytes = 12
        });

        _ = RecordedReplayToolPack.Create(traceFile, context);
        var ex = Assert.Throws<InvalidDataException>(() => new RecordedReplayModelClient(traceFile, context));

        Assert.Contains("aggregate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraceReader_RejectsSymlinkRunRoot()
    {
        using var parent = TemporaryDirectory.Create();
        using var target = TemporaryDirectory.Create();
        var traceFile = Path.Combine(target.Path, "events.jsonl");
        File.WriteAllText(traceFile, "{\"Type\":\"run.started\"}");
        var linkedRoot = Path.Combine(parent.Path, "linked-run");
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, target.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var error = Assert.Throws<InvalidOperationException>(() =>
            JsonlTraceStore.ReadRecords(
                Path.Combine(linkedRoot, "events.jsonl"),
                TestContext.Current.CancellationToken));

        Assert.Contains("root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraceStore_RejectsLinkedQreAncestor()
    {
        using var workspace = TemporaryDirectory.Create();
        using var outside = TemporaryDirectory.Create();
        var outsideRun = Path.Combine(outside.Path, "runs", "external-run");
        Directory.CreateDirectory(outsideRun);
        File.WriteAllText(Path.Combine(outsideRun, "events.jsonl"), "{\"Type\":\"run.completed\"}");
        var qreLink = Path.Combine(workspace.Path, ".qre");
        try
        {
            Directory.CreateSymbolicLink(qreLink, outside.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                JsonlTraceStore.FindLatestTraceFile(workspace.Path));

            Assert.Contains("Symlink traversal outside", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(qreLink))
            {
                Directory.Delete(qreLink);
            }
        }
    }

    private static string WriteModelResponseTrace(string runPath, string path, long sizeBytes, string digest)
    {
        var line = JsonSerializer.Serialize(new
        {
            Type = "model.response",
            Data = new
            {
                AssistantText = (string?)null,
                AssistantTextBlob = new { Algorithm = "sha256", Digest = digest, SizeBytes = sizeBytes, Path = path }
            }
        });
        var traceFile = Path.Combine(runPath, "events.jsonl");
        File.WriteAllText(traceFile, line);
        return traceFile;
    }

    private static string Digest(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "qre-trace-security-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
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
