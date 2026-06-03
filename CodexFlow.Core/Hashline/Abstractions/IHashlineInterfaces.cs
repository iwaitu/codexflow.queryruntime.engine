using CodexFlow.Core.Hashline.Models;

namespace CodexFlow.Core.Hashline.Abstractions;

/// <summary>
/// 文本规范化器接口。
/// </summary>
public interface ITextNormalizer
{
    /// <summary>
    /// 规范化整个文本，返回规范化结果和检测到的换行风格。
    /// </summary>
    NormalizedTextResult Normalize(string rawText);

    /// <summary>
    /// 规范化单行文本。
    /// </summary>
    string NormalizeLine(string lineText);

    /// <summary>
    /// 按行切分文本。
    /// </summary>
    IReadOnlyList<string> SplitLines(string rawText);
}

/// <summary>
/// 行哈希计算器接口。
/// </summary>
public interface ILineHasher
{
    /// <summary>
    /// 计算行锚点 ID（短哈希）。
    /// </summary>
    string ComputeAnchorId(string normalizedLineText);
}

/// <summary>
/// 文件指纹计算器接口。
/// </summary>
public interface IFileFingerprintProvider
{
    /// <summary>
    /// 计算文件指纹（完整哈希）。
    /// </summary>
    string ComputeFingerprint(string normalizedFullText);
}

/// <summary>
/// 快照读取器接口。
/// </summary>
public interface ISnapshotReader
{
    /// <summary>
    /// 读取文件并生成快照。
    /// </summary>
    /// <param name="filePath">文件路径（绝对路径）</param>
    /// <param name="workspaceRoot">工作区根目录（用于路径安全检查，可选）</param>
    /// <param name="ct">取消令牌</param>
    Task<FileSnapshot> ReadAsync(
        string filePath,
        string? workspaceRoot = null,
        int? windowStartLine = null,
        int? windowLineCount = null,
        CancellationToken ct = default);
}

/// <summary>
/// 编辑请求验证器接口。
/// </summary>
public interface IEditRequestValidator
{
    /// <summary>
    /// 验证编辑请求。
    /// </summary>
    /// <param name="request">编辑请求</param>
    /// <param name="currentSnapshot">当前文件快照</param>
    /// <param name="workspaceRoot">工作区根目录（用于路径安全检查，可选）</param>
    /// <param name="ct">取消令牌</param>
    Task<ValidationResult> ValidateAsync(
        HashlineEditRequest request,
        FileSnapshot currentSnapshot,
        string? workspaceRoot = null,
        CancellationToken ct = default);
}

/// <summary>
/// 编辑应用器接口。
/// </summary>
public interface IEditApplier
{
    /// <summary>
    /// 在内存中应用编辑操作。
    /// </summary>
    ApplyResult Apply(FileSnapshot snapshot, IReadOnlyList<EditOperation> operations);
}

/// <summary>
/// Diff 渲染器接口。
/// </summary>
public interface IDiffRenderer
{
    /// <summary>
    /// 渲染 Unified Diff。
    /// </summary>
    string RenderUnifiedDiff(string filePath, string oldContent, string newContent);
}

/// <summary>
/// 原子文件写入器接口。
/// </summary>
public interface IAtomicFileWriter
{
    /// <summary>
    /// 原子写入文件。
    /// </summary>
    Task WriteAsync(
        string filePath,
        string content,
        string encodingName,
        bool hasBom,
        string newLineStyle,
        bool hasTrailingNewline,
        CancellationToken ct = default);
}

/// <summary>
/// 审计日志接口。
/// </summary>
public interface IAuditLogger
{
    /// <summary>
    /// 记录读取操作。
    /// </summary>
    Task LogReadAsync(FileSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// 记录编辑请求。
    /// </summary>
    Task LogEditRequestAsync(HashlineEditRequest request, CancellationToken ct = default);

    /// <summary>
    /// 记录编辑结果。
    /// </summary>
    Task LogEditResultAsync(HashlineEditResult result, CancellationToken ct = default);
}

/// <summary>
/// 文件系统守卫接口。
/// </summary>
public interface IFileSystemGuard
{
    /// <summary>
    /// 检查路径是否在允许的根目录范围内。
    /// </summary>
    bool IsPathAllowed(string filePath, IEnumerable<string> allowedRoots);

    /// <summary>
    /// 检查文件是否为二进制文件。
    /// </summary>
    bool IsBinaryFile(byte[] content);

    /// <summary>
    /// 检查文件大小是否在限制范围内。
    /// </summary>
    bool IsFileSizeAllowed(long fileSize, int maxFileSizeBytes);

    /// <summary>
    /// 检查行数是否在限制范围内。
    /// </summary>
    bool IsLineCountAllowed(int lineCount, int maxLineCount);

    /// <summary>
    /// 验证文件是否可以读取，并返回验证结果。
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="effectiveAllowedRoots">有效允许的根目录列表（已合并配置和运行时参数）</param>
    /// <param name="options">Hashline 配置选项</param>
    ValidationResult ValidateFileForRead(string filePath, IEnumerable<string> effectiveAllowedRoots, HashlineOptions options);
}

/// <summary>
/// 编码检测器接口。
/// </summary>
public interface IEncodingDetector
{
    /// <summary>
    /// 检测文件编码。
    /// </summary>
    HashlineEncodingInfo DetectEncoding(byte[] content);
}

/// <summary>
/// Hashline 编码信息。
/// </summary>
public sealed class HashlineEncodingInfo
{
    public string Name { get; set; } = "utf-8";
    public bool HasBom { get; set; }
    public System.Text.Encoding Encoding { get; set; } = System.Text.Encoding.UTF8;
}
