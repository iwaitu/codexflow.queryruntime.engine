
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services.Infra
{
    public class JavaSemanticDiffProvider : ILanguageSemanticDiffProvider
    {
        private readonly ILogger<JavaSemanticDiffProvider> _logger;
        private readonly string _javaInspectorPath;

        public JavaSemanticDiffProvider(ILogger<JavaSemanticDiffProvider> logger)
        {
            _logger = logger;
            // Locate the java-inspector directory
            var solutionRoot = FindSolutionRoot(Directory.GetCurrentDirectory());
            _javaInspectorPath = Path.Combine(solutionRoot, "CodexFlow", "skills", "java-inspector");
        }

        public bool CanHandle(string extension)
        {
            ArgumentNullException.ThrowIfNull(extension);
            return extension.Equals(".java", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<SemanticDiffResult> AnalyzeAsync(string mainPath, string shadowPath, CancellationToken ct)
        {
            var result = new SemanticDiffResult();

            try
            {
                // Ensure dependencies are present
                await EnsureDependenciesAsync(ct).ConfigureAwait(false);

                // 1. Analyze Diff using AnalyzeDiff.java
                var diffOutput = await RunJavaToolAsync("AnalyzeDiff", $"\"{shadowPath}\" \"{mainPath}\"", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(diffOutput))
                {
                    using var doc = JsonDocument.Parse(diffOutput);
                    if (doc.RootElement.TryGetProperty("changed_symbols", out var changes))
                    {
                        foreach (var change in changes.EnumerateArray())
                        {
                            result.ChangedSymbols.Add(change.GetString() ?? "");
                        }
                    }
                }
                result.HasChanges = result.ChangedSymbols.Count > 0;

                // 2. Build Graph and Identify Impact using BuildGraph.java
                if (result.HasChanges)
                {
                    var projectDir = Path.GetDirectoryName(mainPath);
                    var graphOutput = await RunJavaToolAsync("BuildGraph", $"\"{projectDir}\"", ct).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(graphOutput))
                    {
                        // Parse graph and find dependents (simplified logic: find edge source where target is current file)
                        using var doc = JsonDocument.Parse(graphOutput);
                        if (doc.RootElement.TryGetProperty("edges", out var edges))
                        {
                            var currentFileName = Path.GetFileName(mainPath);
                            foreach (var edge in edges.EnumerateArray())
                            {
                                var target = edge.GetProperty("target").GetString();
                                var source = edge.GetProperty("source").GetString();
                                if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(source) && target == currentFileName)
                                {
                                    result.ImpactedFiles.Add(source);
                                }
                            }
                        }
                    }
                }

                if (result.HasChanges)
                {
                    result.Recommendations = "Detected semantic changes in Java code. Please review impacted files and verify method signatures matches.";
                }

            }
            catch (IOException ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to analyze Java semantic diff.");
            }
            catch (InvalidOperationException ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to analyze Java semantic diff.");
            }
            catch (JsonException ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to analyze Java semantic diff.");
            }
            catch (Win32Exception ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to analyze Java semantic diff.");
            }

            return result;
        }

        private async Task EnsureDependenciesAsync(CancellationToken ct)
        {
            var libDir = Path.Combine(_javaInspectorPath, "lib");
            if (!Directory.Exists(libDir) || Directory.GetFiles(libDir, "*.jar").Length == 0)
            {
                StructuredLog.Information(_logger, "Downloading Java dependencies...");
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-ExecutionPolicy Bypass -File download_deps.ps1",
                    WorkingDirectory = _javaInspectorPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                }
            }

            // Check if classes are compiled
            var analyzeClass = Path.Combine(_javaInspectorPath, "AnalyzeDiff.class");
            if (!File.Exists(analyzeClass))
            {
                StructuredLog.Information(_logger, "Compiling Java tools...");
                var psi = new ProcessStartInfo
                {
                    FileName = "javac",
                    Arguments = $"-cp \"lib/*\" AnalyzeDiff.java BuildGraph.java",
                    WorkingDirectory = _javaInspectorPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var process = Process.Start(psi);
                if (process != null)
                {
                    var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);

                    if (process.ExitCode != 0)
                    {
                        StructuredLog.Error(_logger, $"Failed to compile Java tools: {error}");
                        throw new InvalidOperationException($"Failed to compile Java tools: {error}");
                    }
                }
            }
        }

        private async Task<string> RunJavaToolAsync(string toolName, string args, CancellationToken ct)
        {
            var libDir = Path.Combine(_javaInspectorPath, "lib");
            var jars = Directory.GetFiles(libDir, "*.jar");

            // Cross-platform: Windows uses ';' separator, Unix/macOS uses ':'
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var separator = isWindows ? ";" : ":";
            var classpath = string.Join(separator, jars) + separator + ".";

            var psi = new ProcessStartInfo
            {
                FileName = "java",
                Arguments = $"-cp \"{classpath}\" {toolName} {args}",
                WorkingDirectory = _javaInspectorPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return "";

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                StructuredLog.Error(_logger, $"Java tool {toolName} failed: {error}");
                return "";
            }

            return output.Trim();
        }

        private static string FindSolutionRoot(string startPath)
        {
            var current = new DirectoryInfo(startPath);
            while (current != null)
            {
                if (current.GetFiles("*.sln").Length > 0 || current.GetFiles("*.slnx").Length > 0)
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
            // Fallback (should not happen in dev env)
            return startPath;
        }
    }
}

