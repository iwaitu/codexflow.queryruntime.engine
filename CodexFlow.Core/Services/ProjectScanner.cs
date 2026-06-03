using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodexFlow.Core.Services;

public class ProjectScanner
{
    private readonly ILogger<ProjectScanner> _logger;

    public ProjectScanner(ILogger<ProjectScanner> logger)
    {
        _logger = logger;
    }

    public virtual async Task<string> ScanAndSummarizeAsync(string rootPath)
    {
        if (!Directory.Exists(rootPath)) return "Error: Path not found.";

        var sb = new StringBuilder();
        sb.AppendLine("# 项目技术指纹报告 (Project Fingerprint)");
        sb.AppendLine(FormattableString.Invariant($"生成时间: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\n"));

        // 1. 物理结构深度扫描 (深度 3-4)
        sb.AppendLine("## 1. 物理结构概览");
        try
        {
            var files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(rootPath, p))
                .Where(p => !IsIgnored(p))
                .ToList();

            sb.AppendLine("```text");
            foreach (var f in files.Where(p => p.Split(Path.DirectorySeparatorChar).Length <= 4).Take(50))
            {
                sb.AppendLine(f);
            }
            if (files.Count > 50) sb.AppendLine("...(更多文件已略过)");
            sb.AppendLine("```\n");

            // 2. 自动提取文件后缀分布
            sb.AppendLine("## 2. 技术栈构成 (文件分布)");
            var stats = files.GroupBy(f => NormalizeExtension(Path.GetExtension(f)))
                .Select(g => new { Ext = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(8)
                .ToList();

            sb.AppendLine("| 后缀 | 占比 | 数量 |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var s in stats)
            {
                var percentage = files.Count > 0 ? (double)s.Count / files.Count * 100 : 0;
                sb.AppendLine(FormattableString.Invariant($"| {s.Ext} | {percentage.ToString("F1", CultureInfo.InvariantCulture)}% | {s.Count} |"));
            }
            sb.AppendLine("");
        }
        catch (IOException ex)
        {
            sb.AppendLine("警告：结构扫描失败: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            sb.AppendLine("警告：结构扫描失败: " + ex.Message);
        }
        catch (ArgumentException ex)
        {
            sb.AppendLine("警告：结构扫描失败: " + ex.Message);
        }

        // 3. 识别项目入口
        sb.AppendLine("## 3. 核心入口识别");
        var entries = new[] { "Program.cs", "App.tsx", "index.ts", "main.py", "app.py", "MainActivity.kt", "index.js" };
        foreach (var entry in entries)
        {
            var found = Directory.EnumerateFiles(rootPath, entry, SearchOption.AllDirectories)
                .FirstOrDefault(p => !IsIgnored(p));
            if (found != null)
            {
                sb.AppendLine("- [ENTRY] `" + Path.GetRelativePath(rootPath, found) + "` (已定位)");
            }
        }
        sb.AppendLine("");

        // 4. 读取关键清单文件
        var keyFiles = new[] { "README.md", "package.json", "CodexFlow.sln", "CodexFlow.slnx", "pyproject.toml", "pom.xml", "build.gradle" };
        sb.AppendLine("## 4. 关键清单详情");

        foreach (var fileName in keyFiles)
        {
            var fullPath = Path.Combine(rootPath, fileName);
            if (File.Exists(fullPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
                    if (content.Length > 800) content = string.Concat(content.AsSpan(0, 800), "...(已截断)");
                    sb.AppendLine("### " + fileName);
                    sb.AppendLine("```");
                    sb.AppendLine(content);
                    sb.AppendLine("```\n");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
        }

        return sb.ToString();
    }

    public virtual async Task<List<FileIndexEntry>> GenerateFileIndexAsync(string rootPath)
    {
        var index = new List<FileIndexEntry>();
        if (!Directory.Exists(rootPath)) return index;

        await Task.Run(() =>
        {
            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                };

                var files = Directory.EnumerateFiles(rootPath, "*", options);

                foreach (var file in files)
                {
                    if (IsIgnored(file)) continue;

                    var relativePath = Path.GetRelativePath(rootPath, file).Replace("\\", "/", StringComparison.Ordinal);
                    var info = new FileInfo(file);

                    index.Add(new FileIndexEntry
                    {
                        Path = relativePath,
                        Type = GetFileType(info.Extension),
                        Size = info.Length
                    });

                    // Limit to 10k files to prevent memory explosion
                    if (index.Count >= 10000) break;
                }
            }
            catch (IOException ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to generate file index for {Path}", rootPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to generate file index for {Path}", rootPath);
            }
            catch (ArgumentException ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to generate file index for {Path}", rootPath);
            }
        }).ConfigureAwait(false);

        return index;
    }

    private static string GetFileType(string extension)
    {
        return NormalizeExtension(extension) switch
        {
            ".CS" => "C# Source",
            ".JS" or ".TS" or ".JSX" or ".TSX" => "JavaScript/TypeScript",
            ".PY" => "Python",
            ".JAVA" => "Java",
            ".HTML" or ".CSS" or ".SCSS" => "Web",
            ".JSON" or ".XML" or ".YAML" or ".YML" => "Config",
            ".MD" => "Documentation",
            ".SQL" => "Database",
            _ => "File"
        };
    }

    private static string NormalizeExtension(string? extension) => (extension ?? string.Empty).ToUpperInvariant();

    private static bool IsIgnored(string path)
    {
        var ignored = new[] { ".git", ".venv", "node_modules", "bin/", "obj/", ".next", "dist", "build", ".vs/", ".idea/", "shadows/" };
        // Normalize path separators for consistent checking
        var normalized = path.Replace("\\", "/", StringComparison.Ordinal);
        return ignored.Any(i => normalized.Contains(i, StringComparison.OrdinalIgnoreCase));
    }
}

public class FileIndexEntry
{
    public required string Path { get; set; }
    public required string Type { get; set; }
    public long Size { get; set; }
}

