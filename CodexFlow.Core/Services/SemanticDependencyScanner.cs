using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodexFlow.Core.Models;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Utils;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Services;

/// <summary>
/// A polyglot dependency scanner that uses Roslyn for C# and Regex heuristics for other languages (TS, Py).
/// </summary>
public class SemanticDependencyScanner : ICodeAnalysisService, ICodexCritiqueService
{
    private readonly ILogger<SemanticDependencyScanner> _logger;
    private readonly string[] _ignoredDirectories = { "bin", "obj", ".git", ".vs", "node_modules", "dist", "build", "__pycache__", "shadows" };

    public SemanticDependencyScanner(ILogger<SemanticDependencyScanner> logger)
    {
        _logger = logger;
    }

    public async Task<DependencyGraph> BuildGraphAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var graph = new DependencyGraph();

        // 1. Scan C# (Roslyn)
        await ScanCSharpFilesAsync(rootPath, graph, cancellationToken).ConfigureAwait(false);

        // 2. Scan TypeScript/JS (Regex)
        await ScanFrontendFilesAsync(rootPath, graph, cancellationToken).ConfigureAwait(false);

        // 3. Scan Python (Regex)
        await ScanPythonFilesAsync(rootPath, graph, cancellationToken).ConfigureAwait(false);

        // 4. Scan Java (Regex)
        await ScanJavaFilesAsync(rootPath, graph, cancellationToken).ConfigureAwait(false);

        // 5. Link everything (Cross-file linking happens here)
        LinkDependencies(graph);

        return graph;
    }

    private async Task ScanCSharpFilesAsync(string rootPath, DependencyGraph graph, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsIgnored(f));

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var relativePath = Path.GetRelativePath(rootPath, file);
                var code = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: ct);
                var root = await tree.GetRootAsync(ct).ConfigureAwait(false);

                var node = new CodeNode
                {
                    FilePath = relativePath,
                    Language = "C#",
                    TypeName = ExtractPrimaryTypeName(root)
                };
                node.ImportedNamespaces.AddRange(ExtractUsings(root));
                node.InheritedBaseClasses.AddRange(ExtractInheritance(root));
                graph.AddNode(node);
            }
            catch (IOException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse C# file: {FilePath}", file); }
            catch (UnauthorizedAccessException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse C# file: {FilePath}", file); }
            catch (ArgumentException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse C# file: {FilePath}", file); }
            catch (InvalidOperationException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse C# file: {FilePath}", file); }
        }
    }

    private async Task ScanFrontendFilesAsync(string rootPath, DependencyGraph graph, CancellationToken ct)
    {
        // Matches: import { X } from './path'; or import X from "path";
        var importRegex = new Regex(@"import\s+.*?from\s+['""](.+?)['""]", RegexOptions.Compiled);

        var files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
            .Where(f =>
                (f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase))
                && !IsIgnored(f));

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var relativePath = Path.GetRelativePath(rootPath, file);
                var code = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var matches = importRegex.Matches(code);

                var node = new CodeNode
                {
                    FilePath = relativePath,
                    Language = "TypeScript",
                    TypeName = Path.GetFileNameWithoutExtension(file)
                };

                // Store raw import paths temporarily in 'ImportedNamespaces' to be resolved later
                foreach (Match match in matches)
                {
                    node.ImportedNamespaces.Add(match.Groups[1].Value);
                }
                graph.AddNode(node);
            }
            catch (IOException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse TS/JS file: {FilePath}", file); }
            catch (UnauthorizedAccessException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse TS/JS file: {FilePath}", file); }
            catch (ArgumentException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse TS/JS file: {FilePath}", file); }
            catch (InvalidOperationException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse TS/JS file: {FilePath}", file); }
        }
    }

    private async Task ScanPythonFilesAsync(string rootPath, DependencyGraph graph, CancellationToken ct)
    {
        // Matches: import module or from module import x
        var importRegex = new Regex(@"^(?:from|import)\s+([\w\.]+)", RegexOptions.Compiled | RegexOptions.Multiline);

        var files = Directory.EnumerateFiles(rootPath, "*.py", SearchOption.AllDirectories)
            .Where(f => !IsIgnored(f));

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var relativePath = Path.GetRelativePath(rootPath, file);
                var code = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var matches = importRegex.Matches(code);

                var node = new CodeNode
                {
                    FilePath = relativePath,
                    Language = "Python",
                    TypeName = Path.GetFileNameWithoutExtension(file)
                };

                foreach (Match match in matches)
                {
                    node.ImportedNamespaces.Add(match.Groups[1].Value);
                }
                graph.AddNode(node);
            }
            catch (IOException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse Python file: {FilePath}", file); }
            catch (UnauthorizedAccessException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse Python file: {FilePath}", file); }
            catch (ArgumentException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse Python file: {FilePath}", file); }
            catch (InvalidOperationException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse Python file: {FilePath}", file); }
        }
    }

    private async Task ScanJavaFilesAsync(string rootPath, DependencyGraph graph, CancellationToken ct)
    {
        var packageRegex = new Regex(@"package\s+([\w\.]+);", RegexOptions.Compiled);
        var importRegex = new Regex(@"import\s+(?:static\s+)?([\w\.]+);", RegexOptions.Compiled);
        var classRegex = new Regex(@"(?:public|protected|private)?\s*(?:static\s+)?(?:final\s+)?(?:class|interface|enum)\s+(\w+)(?:\s+extends\s+([\w\.]+))?(?:\s+implements\s+([\w\s,]+))?", RegexOptions.Compiled);

        var files = Directory.EnumerateFiles(rootPath, "*.java", SearchOption.AllDirectories)
            .Where(f => !IsIgnored(f));

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var relativePath = Path.GetRelativePath(rootPath, file);
                var code = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);

                var node = new CodeNode
                {
                    FilePath = relativePath,
                    Language = "Java"
                };

                var pkgMatch = packageRegex.Match(code);
                var clsMatch = classRegex.Match(code);
                if (clsMatch.Success)
                {
                    var pkg = pkgMatch.Success ? pkgMatch.Groups[1].Value : "";
                    node.TypeName = string.IsNullOrEmpty(pkg) ? clsMatch.Groups[1].Value : $"{pkg}.{clsMatch.Groups[1].Value}";

                    if (clsMatch.Groups[2].Success) node.InheritedBaseClasses.Add(clsMatch.Groups[2].Value);
                    if (clsMatch.Groups[3].Success)
                    {
                        var interfaces = clsMatch.Groups[3].Value.Split(',').Select(i => i.Trim());
                        node.InheritedBaseClasses.AddRange(interfaces);
                    }
                }

                var importMatches = importRegex.Matches(code);
                foreach (Match match in importMatches)
                {
                    node.ImportedNamespaces.Add(match.Groups[1].Value);
                }
                graph.AddNode(node);
            }
            catch (IOException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse Java file: {FilePath}", file); }
            catch (UnauthorizedAccessException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse Java file: {FilePath}", file); }
            catch (ArgumentException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse Java file: {FilePath}", file); }
            catch (InvalidOperationException ex) { StructuredLog.Warning(_logger, ex, "Failed to parse Java file: {FilePath}", file); }
        }
    }

    private bool IsIgnored(string path)
    {
        return _ignoredDirectories.Any(d => path.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || path.Contains(Path.DirectorySeparatorChar + "target" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void LinkDependencies(DependencyGraph graph)
    {
        // 1. C# & Java Linking (Type-based)
        var typedNodes = graph.Nodes.Values.Where(n =>
            n.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || n.FilePath.EndsWith(".java", StringComparison.OrdinalIgnoreCase)).ToList();
        var typeLookup = typedNodes
            .Where(n => !string.IsNullOrEmpty(n.TypeName) && n.TypeName != "Unknown")
            .GroupBy(n => n.TypeName)
            .ToDictionary(g => g.Key, g => g.First().FilePath);

        foreach (var node in typedNodes)
        {
            foreach (var baseClass in node.InheritedBaseClasses)
            {
                string? targetPath = null;
                // 优先匹配全限定名 (FQN)
                if (typeLookup.TryGetValue(baseClass, out var fqnPath))
                {
                    targetPath = fqnPath;
                }
                // 兜底匹配简单类名 (Simple Name)
                else
                {
                    targetPath = typedNodes.FirstOrDefault(n => Path.GetFileNameWithoutExtension(n.FilePath) == baseClass)?.FilePath;
                }

                if (targetPath != null)
                {
                    Link(graph, node.FilePath, targetPath);
                }
            }

            foreach (var import in node.ImportedNamespaces)
            {
                if (typeLookup.TryGetValue(import, out var targetPath))
                {
                    Link(graph, node.FilePath, targetPath);
                }
            }
        }

        // 2. Frontend/Python Linking (Path-based / Module-based)
        var scriptNodes = graph.Nodes.Values.Where(n =>
            !n.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && !n.FilePath.EndsWith(".java", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var node in scriptNodes)
        {
            foreach (var importPath in node.ImportedNamespaces) // We stored import paths here
            {
                // Naive resolution: Try to find a file that ends with this name
                // e.g. import './components/Button' -> matches 'src/components/Button.tsx'

                string targetName = Path.GetFileName(importPath);
                // Remove extension if present in import (unlikely for TS, likely for Py)
                if (targetName.Contains('.', StringComparison.Ordinal)) targetName = Path.GetFileNameWithoutExtension(targetName);

                // Find a node that has this filename (simplistic, but works for "thumbanil" purpose)
                var targetNode = graph.Nodes.Values.FirstOrDefault(n =>
                    Path.GetFileNameWithoutExtension(n.FilePath).Equals(targetName, StringComparison.OrdinalIgnoreCase)
                    && IsRelatedExtension(node.FilePath, n.FilePath));

                if (targetNode != null)
                {
                    Link(graph, node.FilePath, targetNode.FilePath);
                }
            }
        }
    }

    private static bool IsRelatedExtension(string source, string target)
    {
        // Only link JS/TS to JS/TS, and Py to Py
        bool sourceIsJs = source.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase);
        bool targetIsJs = target.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase);
        if (sourceIsJs && targetIsJs) return true;

        bool sourceIsPy = source.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
        bool targetIsPy = target.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
        if (sourceIsPy && targetIsPy) return true;

        return false;
    }

    private static void Link(DependencyGraph graph, string sourcePath, string targetPath)
    {
        if (graph.Nodes.TryGetValue(sourcePath, out var sourceNode))
        {
            sourceNode.References.Add(targetPath);
        }
        if (graph.Nodes.TryGetValue(targetPath, out var targetNode))
        {
            targetNode.ReferencedBy.Add(sourcePath);
        }
    }

    // Roslyn Helpers
    private static string ExtractPrimaryTypeName(SyntaxNode root)
    {
        var typeDecl = root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return typeDecl?.Identifier.Text ?? "Unknown";
    }

    private static List<string> ExtractUsings(SyntaxNode root)
    {
        return root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name?.ToString() ?? "")
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
    }

    private static List<string> ExtractInheritance(SyntaxNode root)
    {
        var typeDecl = root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl?.BaseList == null) return new List<string>();
        return typeDecl.BaseList.Types.Select(t => t.Type.ToString()).ToList();
    }

    // Interface compliance stubs
    public Task<List<CodeDiagnostic>> AnalyzeProjectAsync(string projectPath) => Task.FromResult(new List<CodeDiagnostic>());
    public Task<List<CodeDiagnostic>> AnalyzeCodeAsync(string code, string language = "C#") => Task.FromResult(new List<CodeDiagnostic>());

    public Task<GuardrailResult> CheckGuardrailAsync(DependencyGraph graph, string targetFilePath, string taskRiskLevel)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(targetFilePath);
        ArgumentNullException.ThrowIfNull(taskRiskLevel);

        // 1. 标准化路径
        var normalizedPath = targetFilePath.Replace("\\", "/", StringComparison.Ordinal).TrimStart('/');
        var node = graph.Nodes.Values.FirstOrDefault(n => n.FilePath.Replace("\\", "/", StringComparison.Ordinal).EndsWith(normalizedPath, StringComparison.OrdinalIgnoreCase));

        if (node == null) return Task.FromResult(new GuardrailResult(false, null));

        // 2. 熔断逻辑：如果文件 Criticality >= 5 (高入度) 且 任务风险等级不是 High
        if (node.CriticalityScore >= 5 && !string.Equals(taskRiskLevel, "High", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new GuardrailResult(true,
                $"[Level 4 熔断] 文件 '{node.FilePath}' 属于核心依赖节点 (引用数: {node.CriticalityScore})，" +
                $"但当前任务风险等级为 '{taskRiskLevel}'。为了系统安全，已物理禁止此次修改。请提升任务风险等级并增加回归测试后再试。"));
        }

        return Task.FromResult(new GuardrailResult(false, null));
    }

    public List<string> GetImpactedFiles(DependencyGraph graph, string changedFilePath)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(changedFilePath);

        var normalizedPath = changedFilePath.Replace("\\", "/", StringComparison.Ordinal).TrimStart('/');
        var node = graph.Nodes.Values.FirstOrDefault(n => n.FilePath.Replace("\\", "/", StringComparison.Ordinal).EndsWith(normalizedPath, StringComparison.OrdinalIgnoreCase));

        return node?.ReferencedBy.ToList() ?? new List<string>();
    }

    // ICodexCritiqueService Implementation Stub
    public Task<CritiqueResult> ReviewAsync(CodexSession session, string proposedActions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proposedActions);

        return Task.FromResult(new CritiqueResult(true, "Automatic approval by SemanticDependencyScanner stub."));
    }
}

