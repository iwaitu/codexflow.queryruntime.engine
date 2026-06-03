
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexFlow.Core.Services.Infra
{
    public class NodeSemanticDiffProvider : ILanguageSemanticDiffProvider
    {
        private readonly ILogger<NodeSemanticDiffProvider> _logger;
        private readonly string _scriptPath;
        private readonly string _graphScriptPath;
        private readonly string _skillRoot;

        public NodeSemanticDiffProvider(ILogger<NodeSemanticDiffProvider> logger)
        {
            _logger = logger;
            var baseDir = AppContext.BaseDirectory;

            string? resolvedSkillRoot = null;

            // Candidate 1: output-adjacent skills directory
            var outputSkillRoot = Path.Combine(baseDir, "skills", "node-inspector");
            if (HasUsableSkillRoot(outputSkillRoot))
            {
                resolvedSkillRoot = outputSkillRoot;
            }

            // Candidate 2: search upward for solution root and use source skills directory
            if (string.IsNullOrWhiteSpace(resolvedSkillRoot))
            {
                var current = new DirectoryInfo(baseDir);
                while (current != null)
                {
                    if (current.GetFiles("*.sln").Length > 0 || current.GetFiles("*.slnx").Length > 0)
                    {
                        var candidate = Path.Combine(current.FullName, "CodexFlow", "skills", "node-inspector");
                        if (HasUsableSkillRoot(candidate))
                        {
                            resolvedSkillRoot = candidate;
                            break;
                        }
                    }
                    current = current.Parent;
                }
            }

            // Candidate 3: fallback to output path even if node_modules is missing (for prod image scenarios)
            if (string.IsNullOrWhiteSpace(resolvedSkillRoot))
            {
                resolvedSkillRoot = outputSkillRoot;
            }

            _skillRoot = resolvedSkillRoot;
            var scriptsPath = Path.Combine(_skillRoot, "scripts");
            _scriptPath = Path.Combine(scriptsPath, "analyze_diff.ts");
            _graphScriptPath = Path.Combine(scriptsPath, "build_graph.ts");
        }

        private static bool HasUsableSkillRoot(string root)
        {
            if (!Directory.Exists(root)) return false;

            var scriptsDir = Path.Combine(root, "scripts");
            var packageJson = Path.Combine(root, "package.json");
            var nodeModules = Path.Combine(root, "node_modules");

            return Directory.Exists(scriptsDir)
                && File.Exists(packageJson)
                && Directory.Exists(nodeModules);
        }

        public bool CanHandle(string extension)
        {
            ArgumentNullException.ThrowIfNull(extension);
            return extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<SemanticDiffResult> AnalyzeAsync(string mainPath, string shadowPath, CancellationToken ct)
        {
            var result = new SemanticDiffResult();

            try
            {
                // 1. Analyze Diff using ts-node
                // script usage: analyze_diff.ts <oldFile> <newFile>
                var diffOutput = await RunNodeScriptAsync(_scriptPath, $"\"{shadowPath}\" \"{mainPath}\"", ct).ConfigureAwait(false);
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

                if (result.ChangedSymbols.Count > 0)
                {
                    // 2. Build Dependency Graph
                    var projectDir = Path.GetDirectoryName(mainPath);
                    if (projectDir != null)
                    {
                        var graphOutput = await RunNodeScriptAsync(_graphScriptPath, projectDir, ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(graphOutput))
                        {
                            // Parse graph and find impacted files
                            // For simplicity, we just look for edges pointing to the modified file
                            using var graphDoc = JsonDocument.Parse(graphOutput);
                            // TODO: Implement proper graph traversal
                            // For now, we assume if A imports B, and B changed, A is impacted.
                            if (graphDoc.RootElement.TryGetProperty("edges", out var edges))
                            {
                                var relativeMain = Path.GetRelativePath(projectDir, mainPath).Replace("\\", "/", StringComparison.Ordinal);
                                foreach (var edge in edges.EnumerateArray())
                                {
                                    var target = edge.GetProperty("target").GetString();
                                    var source = edge.GetProperty("source").GetString();

                                    // If dependency (target) matches our modified file (relativeMain)
                                    // Then the dependent (source) is impacted
                                    if (target != null && (target == relativeMain || target.EndsWith(relativeMain, StringComparison.Ordinal)))
                                    {
                                        result.ImpactedFiles.Add(source!);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                StructuredLog.Error(_logger, ex, "Node.js semantic analysis failed");
            }
            catch (InvalidOperationException ex)
            {
                StructuredLog.Error(_logger, ex, "Node.js semantic analysis failed");
            }
            catch (JsonException ex)
            {
                StructuredLog.Error(_logger, ex, "Node.js semantic analysis failed");
            }
            catch (Win32Exception ex)
            {
                StructuredLog.Error(_logger, ex, "Node.js semantic analysis failed");
            }

            return result;
        }

        private async Task<string> RunNodeScriptAsync(string scriptPath, string args, CancellationToken ct)
        {
            var workingDir = _skillRoot;
            var projectFile = Path.Combine(_skillRoot, "tsconfig.json");

            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd" : "npx",
                Arguments = isWindows
                    ? $"/c npx --yes --prefix \"{_skillRoot}\" ts-node --project \"{projectFile}\" \"{scriptPath}\" {args}"
                    : $"--yes --prefix \"{_skillRoot}\" ts-node --project \"{projectFile}\" \"{scriptPath}\" {args}",
                WorkingDirectory = workingDir,
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
                StructuredLog.Error(_logger, $"Node script failed: {error}");
                return "";
            }

            return output;
        }
    }
}

