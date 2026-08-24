using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Experimental;

/// <summary>
/// Owns the aggregate artifact-read budget for one replay operation. Pass the
/// same context to the model adapter and recorded tool pack.
/// </summary>
public sealed class RecordedReplayReadContext
{
    internal RecordedReplayReadContext(QueryRuntimeTraceReadOptions options, TraceArtifactReadBudget budget)
    {
        Options = options;
        ArtifactBudget = budget;
    }

    public RecordedReplayReadContext(QueryRuntimeTraceReadOptions? options = null)
    {
        Options = options ?? QueryRuntimeTraceReadOptions.Default;
        Options.Validate();
        ArtifactBudget = new TraceArtifactReadBudget(Options);
    }

    public QueryRuntimeTraceReadOptions Options { get; }

    internal TraceArtifactReadBudget ArtifactBudget { get; }
}

internal sealed class TraceArtifactReadBudget(QueryRuntimeTraceReadOptions options)
{
    private long _consumedBlobBytes;

    public string ReadText(string traceFilePath, JsonElement blob)
    {
        var algorithm = ReadRequiredString(blob, "Algorithm");
        if (!string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported trace blob digest algorithm: {algorithm}.");
        }

        var digest = ReadRequiredString(blob, "Digest");
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Trace blob has an invalid SHA-256 digest.");
        }

        var declaredSize = ReadRequiredInt64(blob, "SizeBytes");
        if (declaredSize < 0 || declaredSize > options.MaxBlobBytes)
        {
            throw new InvalidDataException(
                $"Trace blob size exceeds the {options.MaxBlobBytes} byte read limit.");
        }

        var relativePath = ReadRequiredString(blob, "Path");
        RejectUnsafeRelativePath(relativePath);

        var runDirectory = JsonlTraceStore.GetRunDirectory(traceFilePath);
        JsonlTraceStore.ValidateRunDirectory(runDirectory);
        var blobPath = QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, relativePath);
        if (!File.Exists(blobPath))
        {
            throw new FileNotFoundException("Trace blob was not found.", blobPath);
        }

        if (declaredSize > int.MaxValue)
        {
            throw new InvalidDataException("Trace blob is too large to materialize safely.");
        }

        var info = new FileInfo(blobPath);
        if (info.Length != declaredSize)
        {
            throw new InvalidDataException(
                $"Trace blob length mismatch: declared {declaredSize}, actual {info.Length}.");
        }

        if (checked(_consumedBlobBytes + info.Length) > options.MaxTotalBlobBytes)
        {
            throw new InvalidDataException(
                $"Trace blobs exceed the {options.MaxTotalBlobBytes} byte aggregate read limit.");
        }

        var bytes = new byte[(int)declaredSize];
        using (var stream = JsonlTraceStore.OpenStableReadStream(
                   runDirectory,
                   relativePath,
                   blobPath))
        {
            if (stream.Length != declaredSize)
            {
                throw new InvalidDataException("Trace blob changed before it was read.");
            }

            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    throw new InvalidDataException("Trace blob changed while it was being read.");
                }

                offset += read;
            }

            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException("Trace blob grew while it was being read.");
            }
        }

        QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, relativePath);

        var actualDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualDigest),
                Encoding.ASCII.GetBytes(digest.ToLowerInvariant())))
        {
            throw new InvalidDataException("Trace blob SHA-256 digest mismatch.");
        }

        _consumedBlobBytes += bytes.LongLength;
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Trace blob is not valid UTF-8.", ex);
        }
    }

    private static void RejectUnsafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || Path.IsPathFullyQualified(path) ||
            path.StartsWith('/') || path.StartsWith('\\') ||
            (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'))
        {
            throw new InvalidDataException("Trace blob path must be relative to the run directory.");
        }

        if (path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is "." or ".."))
        {
            throw new InvalidDataException("Trace blob path traversal is not allowed.");
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Trace blob field is missing or invalid: {propertyName}.");

    private static long ReadRequiredInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var result)
            ? result
            : throw new InvalidDataException($"Trace blob field is missing or invalid: {propertyName}.");
}
