using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Abstractions;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public static class ExternalStdioToolPack
{
    private const string RecoveryImplementationVersion = "external-stdio-v1";

    public static IReadOnlyList<AIFunction> Create(string workspacePath)
        => LoadManifests(workspacePath)
            .Select(manifest => CreateFunction(workspacePath, manifest))
            .ToArray();

    private static AIFunction CreateFunction(string workspacePath, ExternalStdioToolManifest manifest)
    {
        var invoker = new ExternalStdioAIFunction(workspacePath, manifest);
        return AIFunctionFactory.Create(
            (
                string extension = "",
                int max_files = 1000,
                int max_chars = 4000,
                string message = "",
                string path = "",
                string pattern = "",
                CancellationToken cancellationToken = default)
                => invoker.InvokeAsync(extension, max_files, max_chars, message, path, pattern, cancellationToken).AsTask().GetAwaiter().GetResult(),
            new AIFunctionFactoryOptions
            {
                Name = manifest.Name,
                Description = manifest.Description ?? "External stdio tool.",
                MarshalResult = static (result, _, _) => ValueTask.FromResult(result)
            });
    }

    public static IReadOnlyList<QueryRuntimeToolDescriptor> ListDescriptors(
        QueryRuntimeToolProfile profile,
        string workspacePath)
        => LoadManifests(workspacePath)
            .Select(manifest => new QueryRuntimeToolDescriptor(
                manifest.Name,
                manifest.Description,
                manifest.Capabilities,
                profile))
            .ToArray();

    internal static ExternalStdioToolComposition CreateComposition(
        QueryRuntimeToolProfile profile,
        string workspacePath)
    {
        var manifests = LoadManifests(workspacePath);
        return new ExternalStdioToolComposition(
            manifests.Select(manifest => CreateFunction(workspacePath, manifest)).ToArray(),
            manifests.Select(manifest => new QueryRuntimeToolDescriptor(
                manifest.Name,
                manifest.Description,
                manifest.Capabilities,
                profile)).ToArray(),
            ComputeRecoveryCompatibilityDigest(manifests));
    }

    public static string GetRecoveryCompatibilityDigest(string workspacePath)
        => ComputeRecoveryCompatibilityDigest(LoadManifests(workspacePath));

    private static string ComputeRecoveryCompatibilityDigest(
        IReadOnlyList<ExternalStdioToolManifest> manifests)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("implementation", RecoveryImplementationVersion);
            writer.WritePropertyName("manifests");
            writer.WriteStartArray();
            foreach (var manifest in manifests)
            {
                writer.WriteStartObject();
                writer.WriteString("name", manifest.Name);
                writer.WriteString("description", manifest.Description);
                writer.WriteString("transport", manifest.Transport);
                writer.WriteString("command", manifest.Command);
                writer.WritePropertyName("args");
                writer.WriteStartArray();
                foreach (var argument in manifest.Args)
                {
                    writer.WriteStringValue(argument);
                }
                writer.WriteEndArray();
                writer.WritePropertyName("capabilities");
                writer.WriteStartArray();
                foreach (var capability in manifest.Capabilities.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(capability);
                }
                writer.WriteEndArray();
                writer.WritePropertyName("inputSchema");
                WriteCanonicalJson(writer, manifest.InputSchema);
                writer.WriteNumber("timeoutSeconds", manifest.TimeoutSeconds);
                writer.WriteNumber("maxOutputBytes", manifest.MaxOutputBytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static IReadOnlyList<ExternalStdioToolManifest> LoadManifests(string workspacePath)
    {
        var toolsDirectory = Path.Combine(Path.GetFullPath(workspacePath), ".qre", "tools");
        if (!Directory.Exists(toolsDirectory))
        {
            return [];
        }

        var manifests = new List<ExternalStdioToolManifest>();
        foreach (var manifestPath in Directory.EnumerateFiles(toolsDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            var manifest = ExternalStdioToolManifest.TryRead(manifestPath);
            if (manifest != null)
            {
                manifests.Add(manifest);
            }
        }

        return manifests;
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("External tool input schema contains an unsupported JSON value.");
        }
    }
}

internal sealed record ExternalStdioToolComposition(
    IReadOnlyList<AIFunction> Functions,
    IReadOnlyList<QueryRuntimeToolDescriptor> Descriptors,
    string RecoveryCompatibilityDigest);

internal sealed class ExternalStdioAIFunction(
    string workspacePath,
    ExternalStdioToolManifest manifest)
{
    private readonly string _workspacePath = Path.GetFullPath(workspacePath);

    public ValueTask<string> InvokeAsync(
        string extension = "",
        int maxFiles = 1000,
        int maxChars = 4000,
        string message = "",
        string path = "",
        string pattern = "",
        CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent(arguments, "extension", extension);
        AddIfPresent(arguments, "maxFiles", maxFiles);
        AddIfPresent(arguments, "maxChars", maxChars);
        AddIfPresent(arguments, "message", message);
        AddIfPresent(arguments, "path", path);
        AddIfPresent(arguments, "pattern", pattern);

        return InvokeExternalAsync(new AIFunctionArguments(arguments), cancellationToken);
    }

    private static void AddIfPresent(IDictionary<string, object?> arguments, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments[name] = value;
        }
    }

    private static void AddIfPresent(IDictionary<string, object?> arguments, string name, int value)
        => arguments[name] = value;

    private async ValueTask<string> InvokeExternalAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = manifest.Command,
            WorkingDirectory = _workspacePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in manifest.Args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment.Clear();
        foreach (var pair in TrustedLocalSandboxEnvironment.Create())
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(manifest.TimeoutSeconds));
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Failed to start external stdio tool {manifest.Name}: {ex.Message}", ex);
        }

        var stdoutBuffer = new BoundedOutputBuffer(manifest.MaxOutputBytes);
        var stderrBuffer = new BoundedOutputBuffer(manifest.MaxOutputBytes);
        var stdoutTask = DrainAsync(process.StandardOutput, stdoutBuffer, timeoutCts.Token);
        var stderrTask = DrainAsync(process.StandardError, stderrBuffer, timeoutCts.Token);
        var stdin = manifest.Transport.Equals("mcp-stdio", StringComparison.OrdinalIgnoreCase)
            ? BuildMcpToolCallJson(manifest.Name, arguments)
            : BuildRequestJson(manifest.Name, _workspacePath, arguments);
        var timedOut = false;

        try
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (IOException ex) when (IsBrokenPipe(ex))
        {
            CloseStandardInputAfterBrokenPipe(process);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            throw;
        }
        catch
        {
            TryKill(process);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            throw;
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timedOut)
        {
        }

        var stdout = stdoutBuffer.ToString();
        var stderr = stderrBuffer.ToString();

        if (timedOut)
        {
            throw new TimeoutException(
                $"External stdio tool {manifest.Name} timed out after {manifest.TimeoutSeconds} seconds. stderr: {stderr}");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"External stdio tool {manifest.Name} exited with {process.ExitCode}: {stderr}");
        }

        return manifest.Transport.Equals("mcp-stdio", StringComparison.OrdinalIgnoreCase)
            ? ExtractMcpResult(stdout)
            : ExtractResult(stdout);
    }

    private static bool IsBrokenPipe(IOException exception)
    {
        for (var current = exception; current != null; current = current.InnerException as IOException)
        {
            if (current.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("Pipe is broken", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.InnerException?.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase) == true ||
                current.InnerException?.Message.Contains("Pipe is broken", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static void CloseStandardInputAfterBrokenPipe(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException ex) when (IsBrokenPipe(ex))
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string BuildRequestJson(
        string name,
        string workspacePath,
        AIFunctionArguments arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("workspacePath", workspacePath);
            writer.WritePropertyName("arguments");
            writer.WriteStartObject();
            foreach (var pair in arguments.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                WriteJsonValue(writer, pair.Value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildMcpToolCallJson(string name, AIFunctionArguments arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", 1);
            writer.WriteString("method", "tools/call");
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WritePropertyName("arguments");
            writer.WriteStartObject();
            foreach (var pair in arguments.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                WriteJsonValue(writer, pair.Value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static string ExtractResult(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("result", out var result)
                ? result.ValueKind == JsonValueKind.String
                    ? result.GetString() ?? string.Empty
                    : result.GetRawText()
                : stdout;
        }
        catch (JsonException)
        {
            return stdout;
        }
    }

    private static string ExtractMcpResult(string stdout)
    {
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("result", out var result))
                {
                    continue;
                }

                if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var parts = content.EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.Object &&
                            item.TryGetProperty("type", out var type) &&
                            type.ValueKind == JsonValueKind.String &&
                            type.GetString() == "text" &&
                            item.TryGetProperty("text", out var text) &&
                            text.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetProperty("text").GetString())
                        .Where(static text => text != null);
                    return string.Join(Environment.NewLine, parts)!;
                }

                return result.GetRawText();
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return stdout;
    }


    private static async Task DrainAsync(
        StreamReader reader,
        BoundedOutputBuffer buffer,
        CancellationToken ct)
    {
        var chars = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(chars, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            buffer.Append(chars.AsSpan(0, read));
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
        }
    }

    private sealed class BoundedOutputBuffer(int maxBytes)
    {
        private readonly StringBuilder _builder = new();
        private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();
        private int _bytes;

        public void Append(ReadOnlySpan<char> value)
        {
            if (_bytes >= maxBytes)
            {
                return;
            }

            var byteCount = _encoder.GetByteCount(value, flush: false);
            var remaining = maxBytes - _bytes;
            if (byteCount <= remaining)
            {
                _builder.Append(value);
                _bytes += byteCount;
                return;
            }

            var includedChars = 0;
            var includedBytes = 0;
            foreach (var ch in value)
            {
                var bytes = Encoding.UTF8.GetByteCount([ch]);
                if (includedBytes + bytes > remaining)
                {
                    break;
                }

                includedBytes += bytes;
                includedChars++;
            }

            _builder.Append(value[..includedChars]);
            _bytes += includedBytes;
        }

        public override string ToString() => _builder.ToString();
    }
}

internal sealed record ExternalStdioToolManifest(
    string Name,
    string? Description,
    string Transport,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlySet<string> Capabilities,
    JsonElement InputSchema,
    int TimeoutSeconds,
    int MaxOutputBytes)
{
    public static ExternalStdioToolManifest? TryRead(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            var name = TryGetString(root, "name");
            var command = TryGetString(root, "command");
            var transport = TryGetString(root, "transport") ?? "stdio";
            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(command) ||
                !IsSupportedTransport(transport))
            {
                return null;
            }

            return new ExternalStdioToolManifest(
                name,
                TryGetString(root, "description"),
                transport,
                command,
                ReadStringArray(root, "args"),
                ReadStringSet(root, "capabilities"),
                ReadSchema(root),
                ReadInt(root, "timeoutSeconds", 30, 1, 600),
                ReadInt(root, "maxOutputBytes", 200_000, 1_000, 2_000_000));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsSupportedTransport(string transport)
        => transport.Equals("stdio", StringComparison.OrdinalIgnoreCase) ||
           transport.Equals("mcp-stdio", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var element) &&
           element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(static item => item.GetString()!)
            .ToArray();
    }

    private static IReadOnlySet<string> ReadStringSet(JsonElement root, string propertyName)
        => new HashSet<string>(ReadStringArray(root, propertyName), StringComparer.Ordinal);

    private static JsonElement ReadSchema(JsonElement root)
    {
        if (root.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object)
        {
            return schema.Clone();
        }

        using var doc = JsonDocument.Parse("""{"type":"object","additionalProperties":true}""");
        return doc.RootElement.Clone();
    }

    private static int ReadInt(JsonElement root, string propertyName, int defaultValue, int min, int max)
        => root.TryGetProperty(propertyName, out var element) &&
           element.ValueKind == JsonValueKind.Number &&
           element.TryGetInt32(out var value)
            ? Math.Clamp(value, min, max)
            : defaultValue;
}
