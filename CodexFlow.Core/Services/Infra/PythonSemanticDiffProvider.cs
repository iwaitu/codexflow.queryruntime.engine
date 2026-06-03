using System.Diagnostics;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Utils;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services.Infra;

public class PythonSemanticDiffProvider : ILanguageSemanticDiffProvider
{
    private readonly ILogger<PythonSemanticDiffProvider> _logger;
    private readonly string _pythonScriptPath;
    private readonly Dictionary<string, DateTime> _warningThrottleState = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan WarningThrottleWindow = TimeSpan.FromMinutes(1);

    public PythonSemanticDiffProvider(ILogger<PythonSemanticDiffProvider> logger)
    {
        _logger = logger;
        // Assuming scripts are copied to output directory under skills/python-inspector/scripts/
        _pythonScriptPath = Path.Combine(AppContext.BaseDirectory, "skills", "python-inspector", "scripts");
    }

    public bool CanHandle(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return extension.Equals(".py", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SemanticDiffResult> AnalyzeAsync(string mainPath, string shadowPath, CancellationToken ct)
    {
        var result = new SemanticDiffResult();
        if (!File.Exists(shadowPath)) return result;

        // 1. AST Diff
        var diffScript = Path.Combine(_pythonScriptPath, "analyze_diff.py");
        if (File.Exists(diffScript))
        {
            var output = await RunPythonScriptAsync(diffScript, $"\"{mainPath}\" \"{shadowPath}\"", ct).ConfigureAwait(false);
            if (!TryDeserializeJson(output, out PythonDiffResult? diffData, out var parseError))
            {
                if (!string.IsNullOrWhiteSpace(parseError))
                {
                    LogWarningThrottled("diff-parse", "Skip Python diff parsing: {Reason}", parseError);
                }
            }
            else if (diffData != null)
            {
                if (diffData.ChangedSymbols != null) result.ChangedSymbols.AddRange(diffData.ChangedSymbols);
                if (diffData.AddedSymbols != null) result.ChangedSymbols.AddRange(diffData.AddedSymbols.Select(s => $"Added: {s}"));
                if (diffData.RemovedSymbols != null) result.ChangedSymbols.AddRange(diffData.RemovedSymbols.Select(s => $"Removed: {s}"));
            }
        }

        result.HasChanges = result.ChangedSymbols.Count > 0;

        // 2. Impact Analysis
        if (result.HasChanges)
        {
            var rootDir = FindProjectRoot(shadowPath);
            var graphScript = Path.Combine(_pythonScriptPath, "build_graph.py");

            if (!string.IsNullOrEmpty(rootDir) && File.Exists(graphScript))
            {
                var graphOutput = await RunPythonScriptAsync(graphScript, $"\"{rootDir}\"", ct).ConfigureAwait(false);
                if (TryDeserializeJson(graphOutput, out Dictionary<string, PythonNode>? graph, out var graphParseError))
                {
                    // Graph: key = module_path (dotted), value = { path, imports[] }
                    if (graph != null)
                    {
                        var changedFileRel = Path.GetRelativePath(rootDir, shadowPath).Replace("\\", "/", StringComparison.Ordinal);
                        var changedModule = changedFileRel.Replace(".py", string.Empty, StringComparison.Ordinal).Replace("/", ".", StringComparison.Ordinal);

                        var impacted = new List<string>();

                        // Find who imports changedModule
                        foreach (var node in graph)
                        {
                            if (node.Value.Imports.Contains(changedModule, StringComparer.Ordinal))
                            {
                                impacted.Add(node.Value.Path);
                            }
                        }

                        result.ImpactedFiles.AddRange(impacted);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(graphParseError))
                {
                    LogWarningThrottled("graph-parse", "Skip Python graph parsing: {Reason}", graphParseError);
                }
            }

            if (result.ImpactedFiles.Count > 0)
            {
                result.Recommendations = $"Detected semantic changes impacting {result.ImpactedFiles.Count} Python files.";
            }
        }

        return result;
    }

    private async Task<string> RunPythonScriptAsync(string scriptPath, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python3", // Or python based on env
            Arguments = $"{scriptPath} {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Fallback to 'python' if 'python3' is not found? 
        // For simplicity assuming python3 is available or mapped.
        // On Windows it might be just python.
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            psi.FileName = "python";
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return string.Empty;

            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var error = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                StructuredLog.Warning(_logger, "Python script failed: {Error}", error);
                return string.Empty;
            }

            return output?.Trim() ?? string.Empty;
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to execute python script");
            return string.Empty;
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to execute python script");
            return string.Empty;
        }
        catch (Win32Exception ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to execute python script");
            return string.Empty;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static bool TryDeserializeJson<T>(string? rawOutput, out T? value, out string? reason)
    {
        value = default;
        reason = null;

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            reason = "Python script output is empty.";
            return false;
        }

        var candidate = ExtractJsonObject(rawOutput);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            reason = "Python script output does not contain a JSON object.";
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(candidate, JsonOptions);
            return value != null;
        }
        catch (JsonException ex)
        {
            var preview = rawOutput.Length > 200 ? rawOutput[..200] + "..." : rawOutput;
            reason = $"JSON parse failed ({ex.Message}). Output preview: {preview}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            var preview = rawOutput.Length > 200 ? rawOutput[..200] + "..." : rawOutput;
            reason = $"JSON parse failed ({ex.Message}). Output preview: {preview}";
            return false;
        }
    }

    private static string ExtractJsonObject(string rawOutput)
    {
        var text = rawOutput.Trim();
        if (text.StartsWith('{') && text.EndsWith('}'))
        {
            return text;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return text[start..(end + 1)].Trim();
        }

        return string.Empty;
    }

    private void LogWarningThrottled(string key, string template, string reason)
    {
        var now = DateTime.UtcNow;
        if (_warningThrottleState.TryGetValue(key, out var lastAt) && now - lastAt < WarningThrottleWindow)
        {
            return;
        }

        _warningThrottleState[key] = now;
        StructuredLog.Warning(_logger, template, reason);
    }

    private static string? FindProjectRoot(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "requirements.txt")) || File.Exists(Path.Combine(dir, "pyproject.toml")) || Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.GetDirectoryName(filePath);
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by JSON deserialization.")]
    private sealed class PythonDiffResult
    {
        [JsonPropertyName("changed_symbols")]
        public List<string>? ChangedSymbols { get; set; }
        [JsonPropertyName("added_symbols")]
        public List<string>? AddedSymbols { get; set; }
        [JsonPropertyName("removed_symbols")]
        public List<string>? RemovedSymbols { get; set; }
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by JSON deserialization.")]
    private sealed class PythonNode
    {
        public string Path { get; set; } = "";
        public List<string> Imports { get; set; } = new();
    }
}

