using System;
using System.Collections.Generic;
using System.IO;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Models;

namespace CodexFlow.Core.Hashline.Infrastructure;

/// <summary>
/// 文件系统守卫实现。
/// 负责检查文件路径、大小、类型等安全性约束。
/// </summary>
public sealed class FileSystemGuard : IFileSystemGuard
{
    // 常见的二进制文件扩展名
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".bin", ".dat",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".zip", ".tar", ".gz", ".rar", ".7z",
        ".mp3", ".mp4", ".wav", ".avi", ".mkv", ".mov",
        ".sqlite", ".db", ".mdb"
    };

    /// <summary>
    /// 检查路径是否在允许的根目录范围内。
    /// 如果 allowedRoots 为空或没有任何有效根，则拒绝所有路径（严格模式）。
    /// 调用方应负责合并配置和运行时的 workspace root。
    /// </summary>
    public bool IsPathAllowed(string filePath, IEnumerable<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);

        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var normalizedFullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar);

            foreach (var root in allowedRoots)
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

                // 检查路径是否完全匹配根目录，或者在根目录下（以 root + separator 开头）
                if (string.Equals(normalizedFullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
                if (normalizedFullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 严格模式：如果没有匹配的根目录，拒绝访问
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 检查文件是否为二进制文件。
    /// 使用多种方法检测：
    /// 1. 扩展名检测
    /// 2. 内容检测（查找 NULL 字符）
    /// </summary>
    public bool IsBinaryFile(byte[] content)
    {
        if (content == null || content.Length == 0)
        {
            return false;
        }

        // 检查内容中是否包含 NULL 字符（常见的二进制文件特征）
        // 只检查前 8KB，避免大文件性能问题
        var checkLength = Math.Min(content.Length, 8192);
        for (var i = 0; i < checkLength; i++)
        {
            if (content[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 检查文件是否为二进制文件（通过扩展名）。
    /// </summary>
    public static bool IsBinaryExtension(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        return BinaryExtensions.Contains(extension);
    }

    /// <summary>
    /// 检查文件大小是否在限制范围内。
    /// </summary>
    public bool IsFileSizeAllowed(long fileSize, int maxFileSizeBytes)
    {
        return fileSize > 0 && fileSize <= maxFileSizeBytes;
    }

    /// <summary>
    /// 检查行数是否在限制范围内。
    /// </summary>
    public bool IsLineCountAllowed(int lineCount, int maxLineCount)
    {
        return lineCount > 0 && lineCount <= maxLineCount;
    }

    /// <summary>
    /// 验证文件是否可以读取，并返回验证结果。
    /// </summary>
    public ValidationResult ValidateFileForRead(string filePath, IEnumerable<string> effectiveAllowedRoots, HashlineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<ValidationError>();

        // 检查文件是否存在
        if (!File.Exists(filePath))
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.FileNotFound,
                Message = $"文件不存在: {filePath}"
            });
            return new ValidationResult { Errors = errors };
        }

        // 检查路径是否允许（使用传入的有效 allowed roots）
        if (!IsPathAllowed(filePath, effectiveAllowedRoots))
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.FilePathNotAllowed,
                Message = $"路径不在允许的根目录范围内: {filePath}"
            });
        }

        // 检查文件大小
        var fileInfo = new FileInfo(filePath);
        if (!IsFileSizeAllowed(fileInfo.Length, options.MaxFileSizeBytes))
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.FileTooLarge,
                Message = $"文件大小超过限制 ({fileInfo.Length} > {options.MaxFileSizeBytes}): {filePath}"
            });
        }

        // 检查扩展名是否为二进制
        if (IsBinaryExtension(filePath))
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.FileBinaryNotSupported,
                Message = $"不支持二进制文件类型: {filePath}"
            });
        }

        return new ValidationResult { Errors = errors };
    }
}