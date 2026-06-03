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
        var traceFile = Path.Combine(latestRunDirectory, "events.jsonl");
        if (!File.Exists(traceFile))
        {
            throw new FileNotFoundException($"Latest run has no events.jsonl: {latestRunDirectory}", traceFile);
        }

        return traceFile;
    }

    public static string FindLatestRunDirectory(string workspacePath)
    {
        var runsRoot = Path.Combine(Path.GetFullPath(workspacePath), ".qre", "runs");
        if (!Directory.Exists(runsRoot))
        {
            throw new DirectoryNotFoundException($"No trace runs found under {runsRoot}");
        }

        var latest = Directory.EnumerateDirectories(runsRoot)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(GetRunLastWriteTimeUtc)
            .ThenByDescending(info => info.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest == null)
        {
            throw new DirectoryNotFoundException($"No trace runs found under {runsRoot}");
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

    public static JsonElement? TryReadManifest(string runDirectory)
    {
        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonlTraceNodeRecord[] ReadRecords(string traceFilePath, CancellationToken ct = default)
        => File.ReadLines(traceFilePath)
            .Select(line =>
            {
                ct.ThrowIfCancellationRequested();
                return line;
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(static line => JsonlTraceNodeRecord.TryParse(line))
            .Where(static record => record != null)
            .Select(static record => record!)
            .ToArray();
}

public sealed record JsonlTraceNodeRecord(string Type, JsonElement Root)
{
    public static JsonlTraceNodeRecord? TryParse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("Type", out var typeElement) &&
                !doc.RootElement.TryGetProperty("type", out typeElement))
            {
                return null;
            }

            var type = typeElement.GetString();
            return string.IsNullOrWhiteSpace(type)
                ? null
                : new JsonlTraceNodeRecord(type, doc.RootElement.Clone());
        }
        catch (JsonException)
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
