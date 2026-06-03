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
/// 快照读取器实现。
/// 负责读取文件并生成带行锚点的快照。
/// </summary>
public sealed class SnapshotReader : ISnapshotReader
{
    private readonly ITextNormalizer _normalizer;
    private readonly ILineHasher _lineHasher;
    private readonly IFileFingerprintProvider _fingerprintProvider;
    private readonly IFileSystemGuard _fileSystemGuard;
    private readonly IEncodingDetector _encodingDetector;
    private readonly IAuditLogger _auditLogger;
    private readonly HashlineOptions _options;
    private readonly ILogger<SnapshotReader> _logger;

    public SnapshotReader(
        ITextNormalizer normalizer,
        ILineHasher lineHasher,
        IFileFingerprintProvider fingerprintProvider,
        IFileSystemGuard fileSystemGuard,
        IEncodingDetector encodingDetector,
        IAuditLogger auditLogger,
        HashlineOptions options,
        ILogger<SnapshotReader> logger)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _lineHasher = lineHasher ?? throw new ArgumentNullException(nameof(lineHasher));
        _fingerprintProvider = fingerprintProvider ?? throw new ArgumentNullException(nameof(fingerprintProvider));
        _fileSystemGuard = fileSystemGuard ?? throw new ArgumentNullException(nameof(fileSystemGuard));
        _encodingDetector = encodingDetector ?? throw new ArgumentNullException(nameof(encodingDetector));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 读取文件并生成快照。
    /// </summary>
    public async Task<FileSnapshot> ReadAsync(
        string filePath,
        string? workspaceRoot = null,
        int? windowStartLine = null,
        int? windowLineCount = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 1. 校验路径是否允许
        var effectiveAllowedRoots = GetEffectiveAllowedRoots(workspaceRoot);
        var validation = _fileSystemGuard.ValidateFileForRead(filePath, effectiveAllowedRoots, _options);
        if (!validation.IsValid)
        {
            var error = validation.Errors[0];
            throw new HashlineException(error.Code, error.Message);
        }

        // 2. 读取文件字节
        var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);

        // 3. 检测编码 / BOM
        var encodingInfo = _encodingDetector.DetectEncoding(bytes);

        // 检查二进制文件
        if (_fileSystemGuard.IsBinaryFile(bytes))
        {
            throw new HashlineException(
                HashlineErrorCodes.FileBinaryNotSupported,
                $"文件是二进制文件: {filePath}");
        }

        // 4. 解码文本
        var rawText = encodingInfo.Encoding.GetString(bytes);

        // 处理 BOM 前缀（如果存在）
        if (encodingInfo.HasBom && encodingInfo.Encoding == Encoding.UTF8)
        {
            // UTF-8 BOM 是 3 个字节，解码后会变成一个特殊字符，需要移除
            if (rawText.Length > 0 && rawText[0] == '\uFEFF')
            {
                rawText = rawText.Substring(1);
            }
        }

        // 5. 文本规范化
        var normalized = _normalizer.Normalize(rawText);

        // 6. 检查行数限制
        if (normalized.Lines.Count > _options.MaxLineCount)
        {
            throw new HashlineException(
                HashlineErrorCodes.FileTooLarge,
                $"文件行数超过限制 ({normalized.Lines.Count} > {_options.MaxLineCount}): {filePath}");
        }

        // 7. 为每一行计算 anchorId
        var allLines = new List<HashlineRecord>();
        for (var i = 0; i < normalized.Lines.Count; i++)
        {
            var rawLine = normalized.Lines[i];
            var normalizedLine = _normalizer.NormalizeLine(rawLine);
            var anchorId = _lineHasher.ComputeAnchorId(normalizedLine);

            allLines.Add(new HashlineRecord
            {
                LineNumber = i + 1, // 1-based
                AnchorId = anchorId,
                RawText = rawLine,
                NormalizedText = normalizedLine
            });
        }

        // 8. 生成 fileFingerprint
        var fileFingerprint = _fingerprintProvider.ComputeFingerprint(normalized.NormalizedText);

        // 9. 组装 FileSnapshot
        var snapshotId = $"snap_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";

        var (windowLines, effectiveWindowStartLine, effectiveWindowEndLine, isPartialWindow) =
            SliceWindow(allLines, windowStartLine, windowLineCount);

        // 10. 渲染 RenderedText
        var renderedText = RenderHashlineText(windowLines);

        // 11. 检测文件是否以换行符结尾
        var hasTrailingNewline = rawText.Length > 0 && rawText[rawText.Length - 1] == '\n';

        var snapshot = new FileSnapshot
        {
            SnapshotId = snapshotId,
            FilePath = filePath,
            FileFingerprint = fileFingerprint,
            TotalLineCount = allLines.Count,
            WindowStartLine = effectiveWindowStartLine,
            WindowEndLine = effectiveWindowEndLine,
            IsPartialWindow = isPartialWindow,
            EncodingName = EncodingDetector.GetEncodingName(encodingInfo.Encoding),
            HasBom = encodingInfo.HasBom,
            NewLineStyle = normalized.DetectedNewLineStyle,
            HasTrailingNewline = hasTrailingNewline,
            ReadAtUtc = DateTimeOffset.UtcNow,
            Lines = windowLines,
            RenderedText = renderedText
        };

        // 12. 记录审计日志
        if (_options.EnableAuditLogging)
        {
            await _auditLogger.LogReadAsync(snapshot, ct).ConfigureAwait(false);
        }

        return snapshot;
    }

    /// <summary>
    /// 获取有效的允许根目录列表。
    /// 合并配置中的 AllowedRoots 和运行时传入的 workspaceRoot。
    /// </summary>
    private List<string> GetEffectiveAllowedRoots(string? workspaceRoot)
    {
        var roots = new List<string>();

        // 添加运行时传入的 workspace root
        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            roots.Add(workspaceRoot);
        }

        // 添加配置中的 AllowedRoots
        if (_options.AllowedRoots != null)
        {
            roots.AddRange(_options.AllowedRoots.Where(r => !string.IsNullOrEmpty(r))!);
        }

        return roots;
    }

    /// <summary>
    /// 渲染 Hashline 文本格式。
    /// 格式：LineNumber#AnchorId|LineContent
    /// </summary>
    private static string RenderHashlineText(List<HashlineRecord> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            // 格式：1#7A12F3|using System;
            sb.AppendLine($"{line.LineNumber}#{line.AnchorId}|{line.RawText}");
        }
        return sb.ToString();
    }

    private static (List<HashlineRecord> Lines, int WindowStartLine, int WindowEndLine, bool IsPartialWindow) SliceWindow(
        List<HashlineRecord> allLines,
        int? windowStartLine,
        int? windowLineCount)
    {
        if (!windowStartLine.HasValue && !windowLineCount.HasValue)
        {
            var fullEnd = allLines.Count == 0 ? 0 : allLines[^1].LineNumber;
            return (allLines, allLines.Count == 0 ? 0 : 1, fullEnd, false);
        }

        if (!windowStartLine.HasValue || !windowLineCount.HasValue)
        {
            throw new HashlineException(
                HashlineErrorCodes.InvalidRequest,
                "分段读取必须同时提供 windowStartLine 和 windowLineCount。");
        }

        if (windowStartLine.Value < 1 || windowLineCount.Value < 1)
        {
            throw new HashlineException(
                HashlineErrorCodes.InvalidRequest,
                "windowStartLine 和 windowLineCount 必须大于 0。");
        }

        if (allLines.Count == 0)
        {
            return (allLines, 0, 0, false);
        }

        if (windowStartLine.Value > allLines.Count)
        {
            throw new HashlineException(
                HashlineErrorCodes.WindowOutOfRange,
                $"读取窗口起始行超出文件范围。请求: {windowStartLine.Value}, 文件总行数: {allLines.Count}");
        }

        var startIndex = windowStartLine.Value - 1;
        var takeCount = Math.Min(windowLineCount.Value, allLines.Count - startIndex);
        var windowLines = allLines.GetRange(startIndex, takeCount);
        return (windowLines, windowLines[0].LineNumber, windowLines[^1].LineNumber, true);
    }
}

/// <summary>
/// Hashline 异常类。
/// </summary>
public sealed class HashlineException : Exception
{
    public string ErrorCode { get; }

    public HashlineException() : base()
    {
        ErrorCode = HashlineErrorCodes.UnknownError;
    }

    public HashlineException(string message) : base(message)
    {
        ErrorCode = HashlineErrorCodes.UnknownError;
    }

    public HashlineException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public HashlineException(string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = HashlineErrorCodes.UnknownError;
    }

    public HashlineException(string errorCode, string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
