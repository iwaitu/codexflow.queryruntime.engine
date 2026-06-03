using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Hashline.Services;

/// <summary>
/// 原子文件写入器实现。
/// 实现安全的原子写入：
/// 1. 先写入临时文件
/// 2. 然后原子替换原文件
/// 3. 保留原文件的编码和换行风格
/// </summary>
public sealed class AtomicFileWriter : IAtomicFileWriter
{
    private readonly ILogger<AtomicFileWriter> _logger;

    public AtomicFileWriter(ILogger<AtomicFileWriter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 原子写入文件。
    /// </summary>
    public async Task WriteAsync(
        string filePath,
        string content,
        string encodingName,
        bool hasBom,
        string newLineStyle,
        bool hasTrailingNewline,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(filePath))
        {
            throw new HashlineException(HashlineErrorCodes.InvalidRequest, "FilePath 不能为空");
        }

        try
        {
            // 1. 规范化内容中的换行符（内容本身已由 EditApplier 处理末尾换行）
            var normalizedContent = NormalizeLineBreaks(content, newLineStyle);

            // 2. 获取编码
            var encoding = EncodingDetector.GetEncodingFromName(encodingName, hasBom);

            // 3. 编码内容
            var bytes = encoding.GetBytes(normalizedContent);

            // 4. 创建临时文件
            var tempFile = filePath + ".tmp." + Guid.NewGuid().ToString("N");
            var backupFile = filePath + ".bak";

            try
            {
                // 写入临时文件
                await File.WriteAllBytesAsync(tempFile, bytes, ct).ConfigureAwait(false);

                // 5. 原子替换
                if (File.Exists(filePath))
                {
                    // 在 macOS/Linux 上，File.Replace 不可用，使用移动方式
                    if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                    {
                        // 先备份
                        File.Copy(filePath, backupFile, true);
                        // 移动临时文件到目标位置
                        File.Move(tempFile, filePath, true);
                        // 删除备份
                        File.Delete(backupFile);
                    }
                    else
                    {
                        // Windows: 使用原子替换
                        File.Replace(tempFile, filePath, backupFile);
                    }
                }
                else
                {
                    // 文件不存在，直接移动
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.Move(tempFile, filePath);
                }

                _logger.LogInformation(
                    "[Hashline] Atomically wrote file: {FilePath}, Size={Size} bytes, Encoding={Encoding}, NewLine={NewLine}, TrailingNewline={TrailingNewline}",
                    filePath,
                    bytes.Length,
                    encodingName,
                    newLineStyle,
                    hasTrailingNewline);
            }
            finally
            {
                // 清理临时文件（如果还存在）
                if (File.Exists(tempFile))
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch
                    {
                        // 忽略清理失败
                    }
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new HashlineException(HashlineErrorCodes.FileAccessDenied, $"无法访问文件: {filePath}", ex);
        }
        catch (IOException ex)
        {
            throw new HashlineException(HashlineErrorCodes.WriteFailed, $"写入文件失败: {filePath}", ex);
        }
        catch (Exception ex)
        {
            throw new HashlineException(HashlineErrorCodes.UnknownError, $"写入文件时发生未知错误: {filePath}", ex);
        }
    }

    /// <summary>
    /// 规范化换行符。
    /// </summary>
    private static string NormalizeLineBreaks(string content, string newLineStyle)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        // 先统一为 \n
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        // 再转换为目标换行风格
        if (newLineStyle == "\r\n")
        {
            normalized = normalized.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        return normalized;
    }
}