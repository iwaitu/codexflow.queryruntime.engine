using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Experimental;

public sealed class JsonlTraceStore : ITraceStore
{
    public Task<QueryRuntimeTraceSummary> ReadLatestAsync(
        string workspacePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var traceFile = FindLatestTraceFile(workspacePath);
        var records = ReadRecords(traceFile, ct);
        var terminalRecord = records.LastOrDefault(static record => record.Type is "run.completed" or "run.failed");
        var terminationReason = terminalRecord?.TryGetString("TerminationReason") ??
            terminalRecord?.TryGetNestedString("Data", "Reason");

        return Task.FromResult(
            new QueryRuntimeTraceSummary(
                traceFile,
                "trace-summary",
                ProviderCalls: false,
                ToolExecutions: false,
                ModelResponses: records.Count(static record => record.Type == "model.response"),
                ToolResults: records.Count(static record => record.Type == "tool.execution.completed"),
                EventCount: records.Length,
                TerminationReason: terminationReason));
    }

    public static string FindLatestTraceFile(string workspacePath)
    {
        var latestRunDirectory = FindLatestRunDirectory(workspacePath);
        var traceFile = QueryRuntimePathSafety.ResolveUnderRoot(latestRunDirectory, "events.jsonl");
        if (!File.Exists(traceFile))
        {
            throw new FileNotFoundException($"Latest run has no events.jsonl: {latestRunDirectory}", traceFile);
        }

        return traceFile;
    }

    public static string FindLatestRunDirectory(string workspacePath)
    {
        var workspaceRoot = Path.GetFullPath(workspacePath);
        var qreRoot = QueryRuntimePathSafety.ResolveUnderRoot(workspaceRoot, ".qre");
        var candidateRoots = new[]
        {
            QueryRuntimePathSafety.ResolveUnderRoot(qreRoot, "runs"),
            QueryRuntimePathSafety.ResolveUnderRoot(qreRoot, Path.Combine("private", "runs"))
        };
        var existingRoots = candidateRoots.Where(Directory.Exists).ToArray();
        if (existingRoots.Length == 0)
        {
            throw new DirectoryNotFoundException($"No trace runs found under {qreRoot}");
        }

        var latest = existingRoots
            .SelectMany(runsRoot => Directory.EnumerateDirectories(runsRoot)
                .Select(path => QueryRuntimePathSafety.ResolveUnderRoot(runsRoot, Path.GetFileName(path))))
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(GetRunLastWriteTimeUtc)
            .ThenByDescending(info => info.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest == null)
        {
            throw new DirectoryNotFoundException($"No trace runs found under {qreRoot}");
        }

        return latest.FullName;
    }

    private static DateTime GetRunLastWriteTimeUtc(DirectoryInfo runDirectory)
    {
        var manifestPath = Path.Combine(runDirectory.FullName, "manifest.json");
        if (File.Exists(manifestPath))
        {
            return File.GetLastWriteTimeUtc(manifestPath);
        }

        var traceFilePath = Path.Combine(runDirectory.FullName, "events.jsonl");
        return File.Exists(traceFilePath)
            ? File.GetLastWriteTimeUtc(traceFilePath)
            : runDirectory.LastWriteTimeUtc;
    }

    public static string GetRunDirectory(string traceFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceFilePath);
        return Path.GetDirectoryName(Path.GetFullPath(traceFilePath)) ??
            throw new ArgumentException("Trace file path must include a run directory.", nameof(traceFilePath));
    }

    public static string? TryReadRunId(JsonlTraceNodeRecord[] records)
        => records.FirstOrDefault(static record => record.Type == "run.started")?.TryGetString("RunId") ??
           records.FirstOrDefault(static record => record.Type is "run.completed" or "run.failed")?.TryGetString("RunId");

    public static JsonElement? TryReadManifest(
        string runDirectory,
        QueryRuntimeTraceReadOptions? options = null)
    {
        options ??= QueryRuntimeTraceReadOptions.Default;
        options.Validate();
        ValidateRunDirectory(runDirectory);
        var manifestPath = QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(manifestPath);
            if (info.Length > options.MaxManifestBytes)
            {
                return null;
            }

            var bytes = ReadBoundedFile(
                runDirectory,
                "manifest.json",
                options.MaxManifestBytes,
                "Trace manifest");
            QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, "manifest.json");
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = options.MaxJsonDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonlTraceNodeRecord.ValidateStringLengths(
                doc.RootElement,
                options.MaxStringBytes,
                "Trace manifest");
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static JsonlTraceNodeRecord[] ReadRecords(
        string traceFilePath,
        CancellationToken ct = default)
        => ReadRecords(traceFilePath, QueryRuntimeTraceReadOptions.Default, ct);

    public static JsonlTraceNodeRecord[] ReadRecords(
        string traceFilePath,
        QueryRuntimeTraceReadOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceFilePath);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var runDirectory = GetRunDirectory(traceFilePath);
        ValidateRunDirectory(runDirectory);
        var resolvedTraceFile = QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, Path.GetFileName(traceFilePath));
        if (!string.Equals(Path.GetFullPath(traceFilePath), resolvedTraceFile, GetPathComparison()))
        {
            throw new InvalidOperationException("Trace file must be a direct child of its run directory.");
        }

        var info = new FileInfo(resolvedTraceFile);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Trace file was not found.", traceFilePath);
        }

        if (info.Length > options.MaxTraceFileBytes)
        {
            throw new InvalidDataException(
                $"Trace file exceeds the {options.MaxTraceFileBytes} byte read limit.");
        }

        var records = new List<JsonlTraceNodeRecord>();
        var lineBytes = new List<byte>(Math.Min(options.MaxLineBytes, 16 * 1024));
        var buffer = new byte[16 * 1024];
        var lineNumber = 0;
        var firstLine = true;
        using var stream = OpenStableReadStream(
            runDirectory,
            Path.GetFileName(resolvedTraceFile),
            resolvedTraceFile);
        long totalBytesRead = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            totalBytesRead = checked(totalBytesRead + read);
            if (totalBytesRead > options.MaxTraceFileBytes)
            {
                throw new InvalidDataException(
                    $"Trace file exceeds the {options.MaxTraceFileBytes} byte read limit.");
            }
            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (value == (byte)'\n')
                {
                    ParseLine();
                    continue;
                }

                if (lineBytes.Count >= options.MaxLineBytes)
                {
                    throw new InvalidDataException(
                        $"Trace line {lineNumber + 1} exceeds the {options.MaxLineBytes} byte read limit.");
                }

                lineBytes.Add(value);
            }
        }

        if (lineBytes.Count > 0)
        {
            ParseLine();
        }

        QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, Path.GetFileName(resolvedTraceFile));
        return records.ToArray();

        void ParseLine()
        {
            lineNumber++;
            if (lineBytes.Count > 0 && lineBytes[^1] == (byte)'\r')
            {
                lineBytes.RemoveAt(lineBytes.Count - 1);
            }

            if (firstLine && lineBytes.Count >= 3 &&
                lineBytes[0] == 0xEF && lineBytes[1] == 0xBB && lineBytes[2] == 0xBF)
            {
                lineBytes.RemoveRange(0, 3);
            }
            firstLine = false;

            if (lineBytes.Count == 0)
            {
                return;
            }

            string line;
            try
            {
                line = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(CollectionsMarshal.AsSpan(lineBytes));
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException($"Trace line {lineNumber} is not valid UTF-8.", ex);
            }
            finally
            {
                lineBytes.Clear();
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var record = JsonlTraceNodeRecord.Parse(
                line,
                options.MaxJsonDepth,
                options.MaxStringBytes,
                lineNumber);
            if (records.Count >= options.MaxEventCount)
            {
                throw new InvalidDataException(
                    $"Trace contains more than the {options.MaxEventCount} event read limit.");
            }

            records.Add(record);
        }
    }

    internal static void ValidateRunDirectory(string runDirectory)
    {
        var fullRunDirectory = Path.GetFullPath(runDirectory);
        var current = new DirectoryInfo(fullRunDirectory);
        while (current.Parent != null)
        {
            if (current.Name.Equals(".qre", StringComparison.OrdinalIgnoreCase))
            {
                var workspaceRoot = current.Parent.FullName;
                _ = QueryRuntimePathSafety.ResolveUnderRoot(
                    workspaceRoot,
                    Path.GetRelativePath(workspaceRoot, fullRunDirectory));
                return;
            }

            current = current.Parent;
        }

        _ = QueryRuntimePathSafety.ResolveUnderRoot(fullRunDirectory, ".");
    }

    internal static FileStream OpenStableReadStream(
        string rootPath,
        string relativePath,
        string? expectedPath = null)
    {
        var resolved = QueryRuntimePathSafety.ResolveUnderRoot(rootPath, relativePath);
        if (expectedPath != null &&
            !string.Equals(Path.GetFullPath(expectedPath), resolved, GetPathComparison()))
        {
            throw new InvalidOperationException("Trace artifact path changed before open.");
        }

        var stream = new FileStream(
            resolved,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        try
        {
            var afterOpen = QueryRuntimePathSafety.ResolveUnderRoot(rootPath, relativePath);
            if (!string.Equals(resolved, afterOpen, GetPathComparison()))
            {
                throw new InvalidOperationException("Trace artifact path changed while it was being opened.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static byte[] ReadBoundedFile(
        string rootPath,
        string relativePath,
        long maxBytes,
        string description)
    {
        if (maxBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Bounded JSON artifacts cannot exceed 2 GiB.");
        }

        using var stream = OpenStableReadStream(rootPath, relativePath);
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException($"{description} exceeds the {maxBytes} byte read limit.");
        }

        using var buffer = new MemoryStream((int)Math.Min(stream.Length, maxBytes));
        var chunk = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total = checked(total + read);
            if (total > maxBytes)
            {
                throw new InvalidDataException($"{description} exceeds the {maxBytes} byte read limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

public sealed record QueryRuntimeTraceReadOptions
{
    public static QueryRuntimeTraceReadOptions Default { get; } = new();

    public long MaxTraceFileBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxLineBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxEventCount { get; init; } = 100_000;
    public int MaxJsonDepth { get; init; } = 64;
    public int MaxStringBytes { get; init; } = 1024 * 1024;
    public long MaxManifestBytes { get; init; } = 1024 * 1024;
    public long MaxBlobBytes { get; init; } = 16L * 1024 * 1024;
    public long MaxTotalBlobBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate()
    {
        if (MaxTraceFileBytes <= 0 || MaxLineBytes <= 0 || MaxEventCount <= 0 ||
            MaxJsonDepth <= 0 || MaxStringBytes <= 0 || MaxManifestBytes <= 0 || MaxBlobBytes <= 0 ||
            MaxTotalBlobBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(QueryRuntimeTraceReadOptions), "Trace read limits must be positive.");
        }
    }
}

public sealed record JsonlTraceNodeRecord(string Type, JsonElement Root)
{
    public static JsonlTraceNodeRecord Parse(string line, int maxDepth, int lineNumber = 0)
        => Parse(line, maxDepth, QueryRuntimeTraceReadOptions.Default.MaxStringBytes, lineNumber);

    public static JsonlTraceNodeRecord Parse(
        string line,
        int maxDepth,
        int maxStringBytes,
        int lineNumber = 0)
    {
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                MaxDepth = maxDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            ValidateStringLengths(doc.RootElement, maxStringBytes, $"Trace line {lineNumber}");
            if ((!doc.RootElement.TryGetProperty("Type", out var typeElement) &&
                 !doc.RootElement.TryGetProperty("type", out typeElement)) ||
                typeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                throw new InvalidDataException($"Trace line {lineNumber} has no valid event type.");
            }

            return new JsonlTraceNodeRecord(typeElement.GetString()!, doc.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Trace line {lineNumber} contains invalid JSON.", ex);
        }
    }

    internal static void ValidateStringLengths(JsonElement element, int maxStringBytes, string source)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    ValidateString(property.Name, maxStringBytes, source);
                    ValidateStringLengths(property.Value, maxStringBytes, source);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ValidateStringLengths(item, maxStringBytes, source);
                }
                break;
            case JsonValueKind.String:
                ValidateString(element.GetString() ?? string.Empty, maxStringBytes, source);
                break;
        }
    }

    private static void ValidateString(string value, int maxStringBytes, string source)
    {
        if (Encoding.UTF8.GetByteCount(value) > maxStringBytes)
        {
            throw new InvalidDataException(
                $"{source} contains a string that exceeds the {maxStringBytes} byte read limit.");
        }
    }

    public static JsonlTraceNodeRecord? TryParse(string line)
    {
        try
        {
            return Parse(line, QueryRuntimeTraceReadOptions.Default.MaxJsonDepth);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public string? TryGetString(string propertyName)
        => TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    public long? TryGetLong(string propertyName)
        => TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Number &&
           element.TryGetInt64(out var value)
            ? value
            : null;

    public bool TryGetData(out JsonElement data)
    {
        if (TryGetProperty("Data", out var element) && element.ValueKind == JsonValueKind.Object)
        {
            data = element;
            return true;
        }

        data = default;
        return false;
    }

    public string? TryGetNestedString(string propertyName, string nestedPropertyName)
        => TryGetProperty(propertyName, out var element) &&
           element.ValueKind == JsonValueKind.Object &&
           TryGetProperty(element, nestedPropertyName, out var nested) &&
           nested.ValueKind == JsonValueKind.String
            ? nested.GetString()
            : null;

    private bool TryGetProperty(string propertyName, out JsonElement element)
        => TryGetProperty(Root, propertyName, out element);

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement element)
        => root.TryGetProperty(propertyName, out element) ||
           root.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(propertyName), out element);
}
