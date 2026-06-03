using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO.Compression;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 将工作区目录打包为 zip 文件（替代 MCP zip_directory）。
/// </summary>
public class ZipDirectoryTool(ILogger<ZipDirectoryTool> logger) : ICodexTool
{
    public string Name => "zip_directory";
    public string Description => "将工作区中的某个目录打包为 zip 文件。参数：source_dir (源目录相对路径), zip_path (输出 zip 相对路径)。Few-shot: zip_directory({\"source_dir\":\"src\",\"zip_path\":\"artifacts/src.zip\"})。";
    public ToolCategory Category => ToolCategory.Forge;
    public IReadOnlyList<int> AllowedStages => [3, 4, 5];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var sourceDir = arguments.GetValueOrDefault("source_dir")?.ToString() ?? ".";
        var zipPath = arguments.GetValueOrDefault("zip_path")?.ToString();

        if (string.IsNullOrEmpty(workspacePath))
            return CodexToolResult.Error("Missing workspace_path.");
        if (string.IsNullOrEmpty(zipPath))
            return CodexToolResult.Error("Missing zip_path.");

        // [Path Normalization Fix] 使用 ToolPathResolver 解析正确的工作区根目录
        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot))
            return CodexToolResult.Error("Failed to resolve workspace root.");

        // 归一化源目录和输出路径
        var normalizedSource = ToolPathResolver.NormalizeDuplicateRepoPrefix(sourceDir, baseRoot);
        var normalizedZip = ToolPathResolver.NormalizeDuplicateRepoPrefix(zipPath, baseRoot);

        var fullSourceDir = Path.GetFullPath(Path.Combine(baseRoot, normalizedSource));
        var fullZipPath = Path.GetFullPath(Path.Combine(baseRoot, normalizedZip));

        // Fallback: 如果归一化后源目录不存在，尝试原始路径
        if (!Directory.Exists(fullSourceDir))
        {
            var fallbackSource = Path.GetFullPath(Path.Combine(baseRoot, sourceDir));
            if (Directory.Exists(fallbackSource))
            {
                StructuredLog.Warning(logger, "Normalized source dir not found, using fallback: {Path}", sourceDir);
                fullSourceDir = fallbackSource;
            }
            else
            {
                return CodexToolResult.Error($"Source directory not found: {sourceDir}");
            }
        }

        try
        {
            var dir = Path.GetDirectoryName(fullZipPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(fullZipPath)) File.Delete(fullZipPath);

            await ZipFile.CreateFromDirectoryAsync(fullSourceDir, fullZipPath, CompressionLevel.Optimal, includeBaseDirectory: false, cancellationToken: ct).ConfigureAwait(false);

            var info = new FileInfo(fullZipPath);
            StructuredLog.Information(logger, "zip_directory: created {ZipPath} ({Size} bytes)", zipPath, info.Length);
            return CodexToolResult.Succeeded(
                FormattableString.Invariant($"✅ 已打包：{zipPath} ({info.Length / 1024.0:F1} KB)"),
                new { ZipPath = zipPath, Size = info.Length });
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "zip_directory failed");
            return CodexToolResult.Error($"zip_directory 异常：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "zip_directory failed");
            return CodexToolResult.Error($"zip_directory 异常：{ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            StructuredLog.Error(logger, ex, "zip_directory failed");
            return CodexToolResult.Error($"zip_directory 异常：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(logger, ex, "zip_directory failed");
            return CodexToolResult.Error($"zip_directory 异常：{ex.Message}");
        }
    }
}

/// <summary>
/// 下载工作区产物文件（替代 MCP download_artifact）。
/// </summary>
public class DownloadArtifactTool(ILogger<DownloadArtifactTool> logger) : ICodexTool
{
    public string Name => "download_artifact";
    public string Description => "下载工作区中的产物文件。文本文件直接返回内容，二进制文件以 base64 返回。参数：filename (文件名), as_text (bool, 默认 false)。Few-shot: download_artifact({\"filename\":\"artifacts/src.zip\",\"as_text\":false})。";
    public ToolCategory Category => ToolCategory.Read;
    public IReadOnlyList<int> AllowedStages => [3, 4, 5];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var filename = arguments.GetValueOrDefault("filename")?.ToString();
        var asText = arguments.GetValueOrDefault("as_text")?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

        if (string.IsNullOrEmpty(filename))
            return CodexToolResult.Error("Missing filename.");

        // [Path Normalization Fix] 使用 ToolPathResolver 解析正确的工作区根目录
        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
            return CodexToolResult.Error("Workspace root not found.");

        // Search in workspace artifacts directory or workspace root (with path normalization)
        var normalizedFilename = ToolPathResolver.NormalizeDuplicateRepoPrefix(filename, baseRoot);
        var searchPaths = new List<string>
        {
            Path.Combine(baseRoot, "artifacts", normalizedFilename),
            Path.Combine(baseRoot, normalizedFilename),
            // Fallback: try original filename
            Path.Combine(baseRoot, "artifacts", filename),
            Path.Combine(baseRoot, filename)
        };

        var filePath = searchPaths.FirstOrDefault(File.Exists);
        if (filePath is null)
            return CodexToolResult.Error($"Artifact not found: {filename}");

        try
        {
            if (asText || IsTextFile(filePath))
            {
                var content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                return CodexToolResult.Succeeded(content, new
                {
                    Filename = filename,
                    ContentType = "text/plain",
                    Encoding = "utf-8",
                    Data = content
                });
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
                var base64 = Convert.ToBase64String(bytes);
                var contentType = GetContentType(filePath);
                return CodexToolResult.Succeeded($"[Binary file: {filename}, {bytes.Length} bytes]", new
                {
                    Filename = filename,
                    ContentType = contentType,
                    Encoding = "base64",
                    Data = base64
                });
            }
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "download_artifact failed: {Filename}", filename);
            return CodexToolResult.Error($"download_artifact 异常：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "download_artifact failed: {Filename}", filename);
            return CodexToolResult.Error($"download_artifact 异常：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(logger, ex, "download_artifact failed: {Filename}", filename);
            return CodexToolResult.Error($"download_artifact 异常：{ex.Message}");
        }
    }

    private static bool IsTextFile(string path)
    {
        var ext = Path.GetExtension(path).ToUpperInvariant();
        return ext is ".TXT" or ".MD" or ".JSON" or ".XML" or ".CSV" or ".YAML" or ".YML"
            or ".CS" or ".PY" or ".JS" or ".TS" or ".HTML" or ".CSS" or ".JAVA" or ".GO"
            or ".RS" or ".TOML" or ".CFG" or ".INI" or ".LOG" or ".SH" or ".BAT" or ".SQL"
            or ".CSPROJ" or ".SLN" or ".PROPS" or ".TARGETS" or ".FSPROJ";
    }

    private static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToUpperInvariant();
        return ext switch
        {
            ".ZIP" => "application/zip",
            ".PDF" => "application/pdf",
            ".PNG" => "image/png",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".GIF" => "image/gif",
            ".SVG" => "image/svg+xml",
            ".JSON" => "application/json",
            ".XML" => "application/xml",
            _ => "application/octet-stream"
        };
    }
}

