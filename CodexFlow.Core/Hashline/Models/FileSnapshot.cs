namespace CodexFlow.Core.Hashline.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// 文件快照，包含所有行的锚点信息。
/// </summary>
public sealed class FileSnapshot
{
    public string SnapshotId { get; set; } = default!;
    public string FilePath { get; set; } = default!;
    public string FileFingerprint { get; set; } = default!;
    public int TotalLineCount { get; set; }
    public int WindowStartLine { get; set; } = 1;
    public int WindowEndLine { get; set; }
    public bool IsPartialWindow { get; set; }
    public string EncodingName { get; set; } = "utf-8";
    public bool HasBom { get; set; }
    public string NewLineStyle { get; set; } = "\n";
    public bool HasTrailingNewline { get; set; } = true;
    public DateTimeOffset ReadAtUtc { get; set; }

    public List<HashlineRecord> Lines { get; set; } = new();

    /// <summary>
    /// 便于直接喂给 LLM 的渲染结果，例如：
    /// 1#A1B2C3|using System;
    /// 2#D4E5F6|
    /// </summary>
    public string RenderedText { get; set; } = string.Empty;
}

/// <summary>
/// 单行的锚点信息。
/// </summary>
public sealed class HashlineRecord
{
    /// <summary>
    /// 行号，1-based。
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// 行锚点 ID，基于规范化后行内容的短哈希。
    /// </summary>
    public string AnchorId { get; set; } = default!;

    /// <summary>
    /// 原始行文本（不含换行符）。
    /// </summary>
    public string RawText { get; set; } = default!;

    /// <summary>
    /// 规范化后的行文本。
    /// </summary>
    public string NormalizedText { get; set; } = default!;
}

/// <summary>
/// Hashline 编辑请求。
/// </summary>
public sealed class HashlineEditRequest
{
    public string FilePath { get; set; } = default!;
    public string SnapshotId { get; set; } = default!;
    public string FileFingerprint { get; set; } = default!;
    public bool DryRun { get; set; }
    public List<EditOperation> Operations { get; set; } = new();
}

/// <summary>
/// Hashline 编辑结果。
/// </summary>
public sealed class HashlineEditResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public bool DryRun { get; set; }

    public string? OldFingerprint { get; set; }
    public string? NewFingerprint { get; set; }

    public string? UnifiedDiff { get; set; }
    public List<ChangedHunk> Hunks { get; set; } = new();
}

/// <summary>
/// 变更的 Hunk 信息。
/// </summary>
public sealed class ChangedHunk
{
    public int OldStartLine { get; set; }
    public int OldLineCount { get; set; }
    public int NewStartLine { get; set; }
    public int NewLineCount { get; set; }
    public string Preview { get; set; } = string.Empty;
}

/// <summary>
/// 编辑操作基类。
/// </summary>
public abstract class EditOperation
{
    public string OpId { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = default!;
}

/// <summary>
/// 替换区间操作。
/// </summary>
public sealed class ReplaceRangeOperation : EditOperation
{
    public int StartLine { get; set; }
    public string StartAnchorId { get; set; } = default!;
    public int EndLine { get; set; }
    public string EndAnchorId { get; set; } = default!;
    public List<string> NewLines { get; set; } = new();
}

/// <summary>
/// 在指定行后插入操作。
/// </summary>
public sealed class InsertAfterOperation : EditOperation
{
    public int TargetLine { get; set; }
    public string TargetAnchorId { get; set; } = default!;
    public List<string> NewLines { get; set; } = new();
}

/// <summary>
/// 在指定行前插入操作。
/// </summary>
public sealed class InsertBeforeOperation : EditOperation
{
    public int TargetLine { get; set; }
    public string TargetAnchorId { get; set; } = default!;
    public List<string> NewLines { get; set; } = new();
}

/// <summary>
/// 删除区间操作。
/// </summary>
public sealed class DeleteRangeOperation : EditOperation
{
    public int StartLine { get; set; }
    public string StartAnchorId { get; set; } = default!;
    public int EndLine { get; set; }
    public string EndAnchorId { get; set; } = default!;
}

/// <summary>
/// 整文件重写操作。
/// 注意：仅用于新建文件首次写入或明确允许整文件替换的低风险文件。
/// </summary>
public sealed class RewriteWholeFileOperation : EditOperation
{
    public string NewContent { get; set; } = string.Empty;
}

/// <summary>
/// 验证错误。
/// </summary>
public sealed class ValidationError
{
    public string Code { get; set; } = default!;
    public string Message { get; set; } = default!;
    public string? OperationId { get; set; }
    public Dictionary<string, object?> Details { get; set; } = new();
}

/// <summary>
/// 验证结果。
/// </summary>
public sealed class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<ValidationError> Errors { get; set; } = new();
}

/// <summary>
/// 文本规范化结果。
/// </summary>
public sealed class NormalizedTextResult
{
    public string NormalizedText { get; set; } = string.Empty;
    public string DetectedNewLineStyle { get; set; } = "\n";
    public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 编辑应用结果。
/// </summary>
public sealed class ApplyResult
{
    public string OldContent { get; set; } = string.Empty;
    public string NewContent { get; set; } = string.Empty;
    public List<ChangedHunk> Hunks { get; set; } = new();
}
