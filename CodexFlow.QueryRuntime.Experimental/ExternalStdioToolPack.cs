using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Abstractions;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public static class ExternalStdioToolPack
{
    public static IReadOnlyList<AIFunction> Create(string workspacePath)
        => LoadManifests(workspacePath)
            .Select(manifest => new ExternalStdioAIFunction(workspacePath, manifest))
            .Cast<AIFunction>()
            .ToArray();

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
}

internal sealed class ExternalStdioAIFunction(
    string workspacePath,
    ExternalStdioToolManifest manifest) : AIFunction
{
    private readonly string _workspacePath = Path.GetFullPath(workspacePath);

    public override string Name => manifest.Name;

    public override string Description => manifest.Description ?? "External stdio tool.";

    public override JsonElement JsonSchema => manifest.InputSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
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
