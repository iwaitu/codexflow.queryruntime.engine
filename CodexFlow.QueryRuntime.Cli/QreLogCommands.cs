using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Abstractions;

internal static class QreLogCommands
{
    internal sealed record Entry(string Kind, string RunId, DateTimeOffset? CreatedUtc, DateTimeOffset? UpdatedUtc,
        string Status, long Bytes, string Path, bool CanDelete, string? Error);

    internal sealed record Result(Entry Entry, string Outcome, string? Error);
    internal sealed record Output(string Action, bool DryRun, int Total, List<Result> Results, Entry[] Warnings);

    internal static int Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("qre logs list|delete [-w|--workspace PATH] [--kind all|audit|private|diagnostics]");
            Console.WriteLine("  [--date YYYY-MM-DD | --before YYYY-MM-DD] [--json]");
            Console.WriteLine("  list: [--descending] [--skip N] [--take N]");
            Console.WriteLine("  list: [--from ISO8601 --to ISO8601] matches overlapping run intervals; timezone required.");
            Console.WriteLine("  delete: preview by default; --execute applies deletion. Requires --date or --before.");
            Console.WriteLine("Dates use UTC run creation time; before is exclusive. Deletes whole run directories,");
            Console.WriteLine("including checkpoints/blobs. Active, incomplete diagnostic, invalid and linked runs are preserved.");
            return 0;
        }
        try
        {
            var action = args[0];
            if (action is not ("list" or "delete")) throw new ArgumentException("Expected logs list or delete.");
            var workspace = Directory.GetCurrentDirectory();
            var kind = "all";
            DateTimeOffset? date = null, before = null;
            DateTimeOffset? from = null, to = null;
            var json = false; var execute = false; var descending = false;
            var skip = 0; var take = int.MaxValue;
            for (var i = 1; i < args.Length; i++)
            {
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                switch (args[i])
                {
                    case "-w": case "--workspace": workspace = Value(); break;
                    case "--kind": kind = Value(); break;
                    case "--date": date = ParseDate(Value()); break;
                    case "--before": before = ParseDate(Value()); break;
                    case "--from" when action == "list": from = ParseInstant(Value()); break;
                    case "--to" when action == "list": to = ParseInstant(Value()); break;
                    case "--json": json = true; break;
                    case "--execute" when action == "delete": execute = true; break;
                    case "--descending" when action == "list": descending = true; break;
                    case "--skip" when action == "list": skip = ParseCount(Value()); break;
                    case "--take" when action == "list": take = ParseCount(Value()); break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
            if (kind is not ("all" or "audit" or "private" or "diagnostics")) throw new ArgumentException("Invalid --kind.");
            if (date != null && before != null) throw new ArgumentException("Use either --date or --before.");
            if ((from != null || to != null) && (date != null || before != null)) throw new ArgumentException("Do not combine time ranges with date filters.");
            if (from != null && to != null && from >= to) throw new ArgumentException("--from must precede --to.");
            if (action == "delete" && date == null && before == null) throw new ArgumentException("Deletion requires --date or --before.");
            workspace = Path.GetFullPath(workspace);
            var entries = Scan(workspace).Where(e => kind == "all" || e.Kind == kind).ToArray();
            var warnings = entries.Where(e => e.Error != null).ToArray();
            var matches = entries.Where(e => date == null || e.CreatedUtc >= date && e.CreatedUtc < date.Value.AddDays(1))
                .Where(e => before == null || e.CreatedUtc < before)
                .Where(e => to == null || e.CreatedUtc < to)
                .Where(e => from == null || e.CreatedUtc != null &&
                    (e.Status is "active" or "in_progress" || (e.UpdatedUtc ?? e.CreatedUtc) >= from));
            matches = descending ? matches.OrderByDescending(e => e.CreatedUtc).ThenBy(e => e.Path, StringComparer.Ordinal)
                : matches.OrderBy(e => e.CreatedUtc).ThenBy(e => e.Path, StringComparer.Ordinal);
            var selected = matches.Skip(skip).Take(take).ToArray();
            var results = new List<Result>();
            var failed = false;
            foreach (var entry in selected)
            {
                var outcome = action == "list" ? "listed" : entry.CanDelete ? "would_delete" : "skipped";
                string? error = entry.Error;
                if (action == "delete" && execute && entry.CanDelete)
                {
                    try
                    {
                        var current = Inspect(workspace, entry.Kind, entry.Path);
                        if (!current.CanDelete || current.CreatedUtc != entry.CreatedUtc)
                            throw new IOException("Run changed or is no longer eligible for deletion.");
                        var files = SafeFiles(workspace, entry.Path).ToArray();
                        // Unix maps only FileShare.None to LOCK_EX; even Delete uses LOCK_SH.
                        // Unix permits unlink while holding that exclusive advisory lock. Windows
                        // instead needs Delete sharing to allow our own deletion with open handles.
                        var deleteShare = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None;
                        var leases = new List<FileStream>();
                        try
                        {
                            foreach (var file in files) leases.Add(new FileStream(file, FileMode.Open, FileAccess.Read, deleteShare));
                            QueryRuntimePathSafety.RejectWorkspaceLinks(workspace, entry.Path, "deleted");
                            foreach (var file in files)
                            {
                                QueryRuntimePathSafety.RejectWorkspaceLinks(workspace, file, "deleted");
                                File.Delete(file);
                            }
                            RemoveEmptyDirectories(workspace, entry.Path);
                        }
                        finally { foreach (var lease in leases) lease.Dispose(); }
                        outcome = "deleted";
                    }
                    catch (Exception ex) when (IsExpected(ex)) { outcome = "failed"; error = ex.Message; failed = true; }
                }
                results.Add(new Result(entry, outcome, error));
                if (!json) Console.WriteLine($"{entry.CreatedUtc:O}\t{entry.UpdatedUtc:O}\t{entry.Kind}\t{entry.RunId}\t{entry.Status}\t{entry.Bytes}\t{outcome}\t{entry.Path}{(error == null ? "" : "\t" + error)}");
            }
            if (json) Console.WriteLine(JsonSerializer.Serialize(new Output(action, action == "delete" && !execute, selected.Length, results, warnings), QreLogJsonContext.Default.Output));
            else
            {
                Console.WriteLine($"{selected.Length} matched run(s). Dates: UTC. {(action == "delete" && !execute ? "Preview only; use --execute to delete." : "")}");
                foreach (var warning in warnings) Console.Error.WriteLine($"Preserved unreadable run: {warning.Path}: {warning.Error}");
            }
            return failed || warnings.Length > 0 ? 1 : 0;
        }
        catch (Exception ex) when (IsExpected(ex)) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or FormatException;
    private static DateTimeOffset ParseDate(string value) => new(DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None), TimeSpan.Zero);
    private static DateTimeOffset ParseInstant(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$"))
            throw new ArgumentException("Time must include seconds and timezone, e.g. 2026-09-27T14:00:00+08:00.");
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
    }
    private static int ParseCount(string value) => int.TryParse(value, out var count) && count >= 0 ? count : throw new ArgumentException("Pagination values must be nonnegative integers.");

    internal static IEnumerable<Entry> Scan(string workspace)
    {
        foreach (var (kind, relative) in new[] { ("audit", ".qre/v2/runs"), ("private", ".qre/v2/private/runs"), ("diagnostics", ".qre/v2/diagnostics") })
        {
            var root = QueryRuntimePathSafety.ResolveUnderRoot(workspace, relative);
            QueryRuntimePathSafety.RejectWorkspaceLinks(workspace, root, "listed");
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root)) yield return Inspect(workspace, kind, directory);
        }
    }

    private static Entry Inspect(string workspace, string kind, string directory)
    {
        try
        {
            var files = SafeFiles(workspace, directory).ToArray();
            var manifest = Path.Combine(directory, "manifest.json");
            if (new FileInfo(manifest).Length > 1024 * 1024) throw new IOException("Manifest exceeds 1 MiB.");
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            var diagnostic = kind == "diagnostics";
            string Get(string name) => root.GetProperty(name).GetString()!;
            if (diagnostic ? Get("manifestSchema") != "qre.outbound-diagnostics.manifest/1" : Get("type") != "qre.v2.audit.manifest")
                throw new IOException("Unrecognized manifest schema.");
            var created = root.GetProperty(diagnostic ? "createdUtc" : "createdAt").GetDateTimeOffset().ToUniversalTime();
            var status = Get(diagnostic ? "completionStatus" : "status");
            var updated = diagnostic
                ? root.TryGetProperty("completedUtc", out var completed) && completed.ValueKind != JsonValueKind.Null ? completed.GetDateTimeOffset().ToUniversalTime() : (DateTimeOffset?)null
                : root.GetProperty("updatedAt").GetDateTimeOffset().ToUniversalTime();
            if (updated < created) throw new IOException("Manifest timestamps are inconsistent.");
            var eligible = diagnostic ? status == "complete" : status is "completed" or "failed" or "cancelled";
            return new(kind, Path.GetFileName(directory), created, updated, status, files.Sum(f => new FileInfo(f).Length), directory, eligible, null);
        }
        catch (Exception ex) when (IsExpected(ex) || ex is KeyNotFoundException)
        { return new(kind, Path.GetFileName(directory), null, null, "unreadable", 0, directory, false, ex.Message); }
    }

    private static IEnumerable<string> SafeFiles(string workspace, string directory)
    {
        QueryRuntimePathSafety.RejectWorkspaceLinks(workspace, directory, "accessed");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            QueryRuntimePathSafety.RejectWorkspaceLinks(workspace, path, "accessed");
            if (Directory.Exists(path)) { foreach (var file in SafeFiles(workspace, path)) yield return file; }
            else yield return path;
        }
    }

    private static void RemoveEmptyDirectories(string workspace, string directory)
    {
        QueryRuntimePathSafety.RejectWorkspaceLinks(workspace, directory, "deleted");
        foreach (var child in Directory.EnumerateDirectories(directory)) RemoveEmptyDirectories(workspace, child);
        Directory.Delete(directory, recursive: false);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(QreLogCommands.Output))]
internal partial class QreLogJsonContext : JsonSerializerContext;
