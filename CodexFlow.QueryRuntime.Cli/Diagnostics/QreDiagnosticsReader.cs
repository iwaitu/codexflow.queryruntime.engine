using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Models.Diagnostics;

namespace CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>A loaded diagnostic run or bundle. Records are data, never instructions.</summary>
internal sealed record QreDiagnosticsDocument(
    string Source,
    QreDiagnosticsManifest? Manifest,
    IReadOnlyList<QreOutboundDiagnosticRecord> Records,
    QreDiagnosticsIntegrity Integrity);

internal sealed class QreDiagnosticsInputException(string reasonCode, string message) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
}

/// <summary>
/// Bounded, fail-closed reader for diagnostic run directories and exported zip
/// bundles. It rejects path escapes, links, unexpected or oversized entries,
/// suspicious compression ratios and unsupported schema versions; a truncated
/// final JSONL line is reported as an incomplete tail instead of failing.
/// </summary>
internal static class QreDiagnosticsReader
{
    public const long MaxManifestBytes = 64 * 1024;
    public const long MaxEventsBytes = 64L * 1024 * 1024;
    public const int MaxLineBytes = 1024 * 1024;
    public const int MaxRecords = 200_000;
    private const double MaxCompressionRatio = 100;

    /// <summary>Resolves <c>latest</c>, a run id, a run directory or a <c>.zip</c> bundle.</summary>
    public static string Resolve(string reference, string workspace)
    {
        if (string.Equals(reference, "latest", StringComparison.Ordinal))
        {
            var root = QreDiagnosticsStore.DiagnosticsRoot(workspace);
            if (!Directory.Exists(root))
            {
                throw new QreDiagnosticsInputException("no_diagnostic_runs", "No outbound diagnostic runs exist in this workspace.");
            }
            var latest = Directory.EnumerateDirectories(root, "diag-*")
                .Where(static path => !new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Where(static path => File.Exists(Path.Combine(path, QreDiagnosticsStore.ManifestFileName)))
                .OrderByDescending(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .FirstOrDefault();
            return latest ?? throw new QreDiagnosticsInputException("no_diagnostic_runs", "No outbound diagnostic runs exist in this workspace.");
        }
        if (reference.StartsWith("diag-", StringComparison.Ordinal) &&
            reference.IndexOfAny(['/', '\\']) < 0 &&
            !reference.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return QueryRuntimePathSafety.ResolveUnderRoot(QreDiagnosticsStore.DiagnosticsRoot(workspace), reference);
        }
        return Path.GetFullPath(reference);
    }

    public static QreDiagnosticsDocument Load(string path)
    {
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return LoadBundle(path);
        }
        if (!Directory.Exists(path))
        {
            throw new QreDiagnosticsInputException("input_not_found", "Diagnostic run or bundle was not found.");
        }
        var info = new DirectoryInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new QreDiagnosticsInputException("input_link_rejected", "Diagnostic run directories must not be links.");
        }
        var manifestPath = Path.Combine(path, QreDiagnosticsStore.ManifestFileName);
        var eventsPath = Path.Combine(path, QreDiagnosticsStore.EventsFileName);
        foreach (var file in new[] { manifestPath, eventsPath })
        {
            if (File.Exists(file) && new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new QreDiagnosticsInputException("input_link_rejected", "Diagnostic files must not be links.");
            }
        }
        var manifest = File.Exists(manifestPath) ? ParseManifest(ReadBounded(File.OpenRead(manifestPath), MaxManifestBytes)) : null;
        var events = File.Exists(eventsPath) ? ReadBounded(File.OpenRead(eventsPath), MaxEventsBytes) : [];
        return Build(path, manifest, events);
    }

    private static QreDiagnosticsDocument LoadBundle(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 8)
        {
            throw new QreDiagnosticsInputException("bundle_too_many_entries", "Diagnostic bundle has unexpected entries.");
        }
        byte[]? manifestBytes = null;
        byte[]? eventBytes = null;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Contains("..", StringComparison.Ordinal) || name.Contains('/') || name.Contains('\\') ||
                Path.IsPathRooted(name))
            {
                throw new QreDiagnosticsInputException("bundle_path_rejected", "Diagnostic bundle entry paths must be plain file names.");
            }
            var limit = name switch
            {
                QreDiagnosticsStore.ManifestFileName => MaxManifestBytes,
                QreDiagnosticsStore.EventsFileName => MaxEventsBytes,
                _ => throw new QreDiagnosticsInputException("bundle_entry_rejected", "Diagnostic bundle contains an unsupported entry.")
            };
            if (entry.Length > limit)
            {
                throw new QreDiagnosticsInputException("bundle_entry_too_large", "Diagnostic bundle entry exceeds its size limit.");
            }
            if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > MaxCompressionRatio && entry.Length > 1024 * 1024)
            {
                throw new QreDiagnosticsInputException("bundle_compression_rejected", "Diagnostic bundle entry has a suspicious compression ratio.");
            }
            var bytes = ReadBounded(entry.Open(), limit);
            if (name == QreDiagnosticsStore.ManifestFileName)
            {
                manifestBytes = bytes;
            }
            else
            {
                eventBytes = bytes;
            }
        }
        var manifest = manifestBytes == null ? null : ParseManifest(manifestBytes);
        return Build(path, manifest, eventBytes ?? []);
    }

    private static QreDiagnosticsManifest ParseManifest(byte[] bytes)
    {
        QreDiagnosticsManifest? manifest;
        try
        {
            // Check the version tags before binding, so a future schema is reported
            // as unsupported rather than as a malformed current manifest.
            using (var probe = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }))
            {
                var root = probe.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("manifestSchema", out var schema) ||
                    schema.ValueKind != JsonValueKind.String ||
                    !string.Equals(schema.GetString(), QreDiagnosticsManifest.CurrentSchema, StringComparison.Ordinal))
                {
                    throw new QreDiagnosticsInputException("schema_unsupported", "Diagnostic manifest uses an unsupported schema version.");
                }
            }
            manifest = JsonSerializer.Deserialize(bytes, QreDiagnosticsJsonContext.Default.QreDiagnosticsManifest);
        }
        catch (JsonException)
        {
            throw new QreDiagnosticsInputException("manifest_invalid", "Diagnostic manifest is not valid JSON.");
        }
        if (manifest == null)
        {
            throw new QreDiagnosticsInputException("manifest_invalid", "Diagnostic manifest is empty.");
        }
        if (!string.Equals(manifest.ManifestSchema, QreDiagnosticsManifest.CurrentSchema, StringComparison.Ordinal) ||
            !string.Equals(manifest.EventSchema, QreOutboundDiagnosticSchema.SchemaVersion, StringComparison.Ordinal))
        {
            throw new QreDiagnosticsInputException("schema_unsupported", "Diagnostic manifest uses an unsupported schema version.");
        }
        return manifest;
    }

    private static QreDiagnosticsDocument Build(string source, QreDiagnosticsManifest? manifest, byte[] events)
    {
        var records = new List<QreOutboundDiagnosticRecord>();
        var invalid = 0;
        var truncated = false;
        var versionsSupported = manifest == null ||
                                string.Equals(manifest.NormalizerVersion, QreOutboundDiagnosticSchema.NormalizerVersion, StringComparison.Ordinal);
        var span = events.AsSpan();
        while (!span.IsEmpty)
        {
            var newline = span.IndexOf((byte)'\n');
            var terminated = newline >= 0;
            var line = terminated ? span[..newline] : span;
            span = terminated ? span[(newline + 1)..] : [];
            if (line.IsEmpty || (line.Length == 1 && line[0] == (byte)'\r'))
            {
                continue;
            }
            if (line.Length > MaxLineBytes || records.Count >= MaxRecords)
            {
                invalid++;
                continue;
            }
            QreOutboundDiagnosticRecord? record = null;
            try
            {
                record = JsonSerializer.Deserialize(line, QreOutboundDiagnosticsJsonContext.Default.QreOutboundDiagnosticRecord);
            }
            catch (JsonException)
            {
            }
            if (record == null)
            {
                if (!terminated)
                {
                    truncated = true;
                }
                else
                {
                    invalid++;
                }
                continue;
            }
            if (!string.Equals(record.SchemaVersion, QreOutboundDiagnosticSchema.SchemaVersion, StringComparison.Ordinal))
            {
                throw new QreDiagnosticsInputException("schema_unsupported", "Diagnostic events use an unsupported schema version.");
            }
            if (!string.Equals(record.NormalizerVersion, QreOutboundDiagnosticSchema.NormalizerVersion, StringComparison.Ordinal) ||
                !string.Equals(record.ProjectionPolicyVersion, QreOutboundDiagnosticSchema.ProjectionPolicyVersion, StringComparison.Ordinal))
            {
                versionsSupported = false;
            }
            records.Add(record);
        }

        records.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        var gaps = 0;
        for (var i = 1; i < records.Count; i++)
        {
            var delta = records[i].Sequence - records[i - 1].Sequence;
            if (delta > 1)
            {
                gaps += (int)Math.Min(int.MaxValue, delta - 1);
            }
        }
        if (records.Count > 0 && records[0].Sequence > 1)
        {
            gaps += (int)Math.Min(int.MaxValue, records[0].Sequence - 1);
        }

        var status = manifest?.CompletionStatus ?? "missing_manifest";
        return new QreDiagnosticsDocument(
            source,
            manifest,
            records,
            new QreDiagnosticsIntegrity
            {
                ManifestPresent = manifest != null,
                CompletionStatus = status,
                TruncatedTail = truncated,
                InvalidLines = invalid,
                SequenceGaps = gaps,
                VersionsSupported = versionsSupported,
                EvidenceIncomplete = manifest == null || status != "complete" || truncated || invalid > 0 || gaps > 0 ||
                                     manifest.Counters?.EvidenceIncomplete == true
            });
    }

    private static byte[] ReadBounded(Stream stream, long limit)
    {
        using (stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    throw new QreDiagnosticsInputException("input_too_large", "Diagnostic input exceeds its size limit.");
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }

    internal static string Describe(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    internal static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
