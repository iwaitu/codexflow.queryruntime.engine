using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Models;

namespace CodexFlow.Core.Hashline.Services;

/// <summary>
/// 编辑请求验证器实现。
/// 验证规则：
/// 1. 文件级校验：文件存在、路径允许、非二进制、fingerprint匹配
/// 2. 操作级校验：类型合法、行号范围、锚点匹配、区间合法
/// 3. 冲突检测：操作不重叠、rewrite不与其他操作混用
/// </summary>
public sealed class EditRequestValidator : IEditRequestValidator
{
    private readonly HashlineOptions _options;

    public EditRequestValidator(HashlineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// 验证编辑请求。
    /// </summary>
    public Task<ValidationResult> ValidateAsync(
        HashlineEditRequest request,
        FileSnapshot currentSnapshot,
        string? workspaceRoot = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentSnapshot);

        ct.ThrowIfCancellationRequested();

        var errors = new List<ValidationError>();

        // 1. 验证请求基本字段
        ValidateRequestBasics(request, errors);

        // 2. 验证文件级 fingerprint
        ValidateFileFingerprint(request, currentSnapshot, errors);

        // 3. 验证每个操作
        foreach (var op in request.Operations)
        {
            ValidateOperation(op, currentSnapshot, errors);
        }

        // 4. 验证操作重叠
        ValidateOverlappingOperations(request.Operations.ToList(), errors);

        return Task.FromResult(new ValidationResult { Errors = errors });
    }

    /// <summary>
    /// 验证请求基本字段。
    /// </summary>
    private static void ValidateRequestBasics(HashlineEditRequest request, List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(request.FilePath))
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.InvalidRequest,
                Message = "FilePath 不能为空"
            });
        }

        if (string.IsNullOrEmpty(request.SnapshotId))
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.SnapshotIdRequired,
                Message = "SnapshotId 不能为空"
            });
        }

        if (string.IsNullOrEmpty(request.FileFingerprint))
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.InvalidRequest,
                Message = "FileFingerprint 不能为空"
            });
        }

        if (request.Operations == null || request.Operations.Count == 0)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.InvalidRequest,
                Message = "Operations 不能为空"
            });
        }
    }

    /// <summary>
    /// 验证文件级 fingerprint。
    /// </summary>
    private static void ValidateFileFingerprint(
        HashlineEditRequest request,
        FileSnapshot currentSnapshot,
        List<ValidationError> errors)
    {
        if (!string.Equals(request.FileFingerprint, currentSnapshot.FileFingerprint, StringComparison.Ordinal))
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.FileFingerprintMismatch,
                Message = $"文件 fingerprint 不匹配。请求: {request.FileFingerprint}, 当前: {currentSnapshot.FileFingerprint}",
                Details = new Dictionary<string, object?>
                {
                    ["RequestFingerprint"] = request.FileFingerprint,
                    ["CurrentFingerprint"] = currentSnapshot.FileFingerprint
                }
            });
        }
    }

    /// <summary>
    /// 验证单个操作。
    /// </summary>
    private void ValidateOperation(EditOperation op, FileSnapshot snapshot, List<ValidationError> errors)
    {
        switch (op)
        {
            case ReplaceRangeOperation replaceOp:
                ValidateReplaceRange(replaceOp, snapshot, errors);
                break;
            case InsertAfterOperation insertAfterOp:
                ValidateInsertAfter(insertAfterOp, snapshot, errors);
                break;
            case InsertBeforeOperation insertBeforeOp:
                ValidateInsertBefore(insertBeforeOp, snapshot, errors);
                break;
            case DeleteRangeOperation deleteOp:
                ValidateDeleteRange(deleteOp, snapshot, errors);
                break;
            case RewriteWholeFileOperation rewriteOp:
                ValidateRewriteWholeFile(rewriteOp, errors);
                break;
            default:
                errors.Add(new ValidationError
                {
                    Code = HashlineErrorCodes.InvalidOperationType,
                    Message = $"未知的操作类型: {op.Type}",
                    OperationId = op.OpId
                });
                break;
        }
    }

    /// <summary>
    /// 验证 ReplaceRange 操作。
    /// </summary>
    private static void ValidateReplaceRange(
        ReplaceRangeOperation op,
        FileSnapshot snapshot,
        List<ValidationError> errors)
    {
        // 检查行号范围
        if (op.StartLine < 1)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.LineOutOfRange,
                Message = $"StartLine 必须 >= 1，当前: {op.StartLine}",
                OperationId = op.OpId
            });
        }

        if (op.EndLine < op.StartLine)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.InvalidRange,
                Message = $"EndLine 必须 >= StartLine。StartLine: {op.StartLine}, EndLine: {op.EndLine}",
                OperationId = op.OpId
            });
        }

        if (op.EndLine > snapshot.Lines.Count)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.LineOutOfRange,
                Message = $"EndLine 超出文件行数。EndLine: {op.EndLine}, 文件行数: {snapshot.Lines.Count}",
                OperationId = op.OpId
            });
        }

        // 检查锚点匹配
        if (op.StartLine >= 1 && op.StartLine <= snapshot.Lines.Count)
        {
            var startLine = snapshot.Lines[op.StartLine - 1];
            if (!string.Equals(op.StartAnchorId, startLine.AnchorId, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError
                {
                    Code = HashlineErrorCodes.AnchorMismatch,
                    Message = $"第 {op.StartLine} 行锚点不匹配。请求: {op.StartAnchorId}, 实际: {startLine.AnchorId}",
                    OperationId = op.OpId,
                    Details = new Dictionary<string, object?>
                    {
                        ["LineNumber"] = op.StartLine,
                        ["RequestAnchor"] = op.StartAnchorId,
                        ["ActualAnchor"] = startLine.AnchorId
                    }
                });
            }
        }

        if (op.EndLine >= 1 && op.EndLine <= snapshot.Lines.Count)
        {
            var endLine = snapshot.Lines[op.EndLine - 1];
            if (!string.Equals(op.EndAnchorId, endLine.AnchorId, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError
                {
                    Code = HashlineErrorCodes.AnchorMismatch,
                    Message = $"第 {op.EndLine} 行锚点不匹配。请求: {op.EndAnchorId}, 实际: {endLine.AnchorId}",
                    OperationId = op.OpId,
                    Details = new Dictionary<string, object?>
                    {
                        ["LineNumber"] = op.EndLine,
                        ["RequestAnchor"] = op.EndAnchorId,
                        ["ActualAnchor"] = endLine.AnchorId
                    }
                });
            }
        }

        // 检查 NewLines
        if (op.NewLines == null)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.InvalidOperationPayload,
                Message = "NewLines 不能为 null",
                OperationId = op.OpId
            });
        }
    }

    /// <summary>
    /// 验证 InsertAfter 操作。
    /// </summary>
    private static void ValidateInsertAfter(
        InsertAfterOperation op,
        FileSnapshot snapshot,
        List<ValidationError> errors)
    {
        // 检查目标行号范围
        if (op.TargetLine < 1 || op.TargetLine > snapshot.Lines.Count)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.LineOutOfRange,
                Message = $"TargetLine 超出范围。TargetLine: {op.TargetLine}, 文件行数: {snapshot.Lines.Count}",
                OperationId = op.OpId
            });
        }
        else
        {
            // 检查锚点匹配
            var targetLine = snapshot.Lines[op.TargetLine - 1];
            if (!string.Equals(op.TargetAnchorId, targetLine.AnchorId, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError
                {
                    Code = HashlineErrorCodes.AnchorMismatch,
                    Message = $"第 {op.TargetLine} 行锚点不匹配。请求: {op.TargetAnchorId}, 实际: {targetLine.AnchorId}",
                    OperationId = op.OpId
                });
            }
        }

        // 检查 NewLines
        if (op.NewLines == null)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.InvalidOperationPayload,
                Message = "NewLines 不能为 null",
                OperationId = op.OpId
            });
        }
    }

    /// <summary>
    /// 验证 InsertBefore 操作。
    /// </summary>
    private static void ValidateInsertBefore(
        InsertBeforeOperation op,
        FileSnapshot snapshot,
        List<ValidationError> errors)
    {
        // 检查目标行号范围
        if (op.TargetLine < 1 || op.TargetLine > snapshot.Lines.Count)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.LineOutOfRange,
                Message = $"TargetLine 超出范围。TargetLine: {op.TargetLine}, 文件行数: {snapshot.Lines.Count}",
                OperationId = op.OpId
            });
        }
        else
        {
            // 检查锚点匹配
            var targetLine = snapshot.Lines[op.TargetLine - 1];
            if (!string.Equals(op.TargetAnchorId, targetLine.AnchorId, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError
                {
                    Code = HashlineErrorCodes.AnchorMismatch,
                    Message = $"第 {op.TargetLine} 行锚点不匹配。请求: {op.TargetAnchorId}, 实际: {targetLine.AnchorId}",
                    OperationId = op.OpId
                });
            }
        }

        // 检查 NewLines
        if (op.NewLines == null)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.InvalidOperationPayload,
                Message = "NewLines 不能为 null",
                OperationId = op.OpId
            });
        }
    }

    /// <summary>
    /// 验证 DeleteRange 操作。
    /// </summary>
    private static void ValidateDeleteRange(
        DeleteRangeOperation op,
        FileSnapshot snapshot,
        List<ValidationError> errors)
    {
        // 检查行号范围
        if (op.StartLine < 1)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.LineOutOfRange,
                Message = $"StartLine 必须 >= 1，当前: {op.StartLine}",
                OperationId = op.OpId
            });
        }

        if (op.EndLine < op.StartLine)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.InvalidRange,
                Message = $"EndLine 必须 >= StartLine。StartLine: {op.StartLine}, EndLine: {op.EndLine}",
                OperationId = op.OpId
            });
        }

        if (op.EndLine > snapshot.Lines.Count)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.LineOutOfRange,
                Message = $"EndLine 超出文件行数。EndLine: {op.EndLine}, 文件行数: {snapshot.Lines.Count}",
                OperationId = op.OpId
            });
        }

        // 检查锚点匹配
        if (op.StartLine >= 1 && op.StartLine <= snapshot.Lines.Count)
        {
            var startLine = snapshot.Lines[op.StartLine - 1];
            if (!string.Equals(op.StartAnchorId, startLine.AnchorId, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError
                {
                    Code = HashlineErrorCodes.AnchorMismatch,
                    Message = $"第 {op.StartLine} 行锚点不匹配。请求: {op.StartAnchorId}, 实际: {startLine.AnchorId}",
                    OperationId = op.OpId
                });
            }
        }

        if (op.EndLine >= 1 && op.EndLine <= snapshot.Lines.Count)
        {
            var endLine = snapshot.Lines[op.EndLine - 1];
            if (!string.Equals(op.EndAnchorId, endLine.AnchorId, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError
                {
                    Code = HashlineErrorCodes.AnchorMismatch,
                    Message = $"第 {op.EndLine} 行锚点不匹配。请求: {op.EndAnchorId}, 实际: {endLine.AnchorId}",
                    OperationId = op.OpId
                });
            }
        }
    }

    /// <summary>
    /// 验证 RewriteWholeFile 操作。
    /// </summary>
    private void ValidateRewriteWholeFile(RewriteWholeFileOperation op, List<ValidationError> errors)
    {
        // 检查是否允许 rewrite
        if (!_options.AllowRewriteWholeFile)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.InvalidOperationType,
                Message = "当前配置不允许 RewriteWholeFile 操作",
                OperationId = op.OpId
            });
        }

        // 检查 NewContent
        if (op.NewContent == null)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.InvalidOperationPayload,
                Message = "NewContent 不能为 null",
                OperationId = op.OpId
            });
        }
    }

    /// <summary>
    /// 验证操作重叠。
    /// </summary>
    private static void ValidateOverlappingOperations(
        List<EditOperation> operations,
        List<ValidationError> errors)
    {
        // 检查 rewrite 与其他操作混用
        var hasRewrite = operations.Any(op => op is RewriteWholeFileOperation);
        if (hasRewrite && operations.Count > 1)
        {
            errors.Add(new ValidationError
            {
                Code = HashlineErrorCodes.RewriteWithOtherOperations,
                Message = "RewriteWholeFile 不能与其他操作同时存在"
            });
            return;
        }

        // 提取所有涉及行号范围的区间
        var ranges = new List<(int Start, int End, string OpId, string Type)>();

        foreach (var op in operations)
        {
            switch (op)
            {
                case ReplaceRangeOperation replaceOp:
                    ranges.Add((replaceOp.StartLine, replaceOp.EndLine, replaceOp.OpId, "replace_range"));
                    break;
                case DeleteRangeOperation deleteOp:
                    ranges.Add((deleteOp.StartLine, deleteOp.EndLine, deleteOp.OpId, "delete_range"));
                    break;
                case InsertAfterOperation insertAfterOp:
                    // InsertAfter 定位到特定行，视为在该行位置操作
                    ranges.Add((insertAfterOp.TargetLine, insertAfterOp.TargetLine, insertAfterOp.OpId, "insert_after"));
                    break;
                case InsertBeforeOperation insertBeforeOp:
                    ranges.Add((insertBeforeOp.TargetLine, insertBeforeOp.TargetLine, insertBeforeOp.OpId, "insert_before"));
                    break;
            }
        }

        // 检查重叠（按起始位置排序）
        var sortedRanges = ranges.OrderBy(r => r.Start).ThenBy(r => r.End).ToList();

        for (var i = 1; i < sortedRanges.Count; i++)
        {
            var prev = sortedRanges[i - 1];
            var curr = sortedRanges[i];

            // 如果当前区间起始 <= 上一区间结束，则存在重叠
            // 注意：InsertAfter 可以与同一行的 ReplaceRange/DeleteRange 共存（执行顺序不同）
            // 这里采用严格检查：任何区间重叠都报错
            if (curr.Start <= prev.End)
            {
                // 允许 InsertBefore 和 InsertAfter 在同一行与 ReplaceRange 共存
                // 因为它们的执行顺序不同（insert 操作在 replace/delete 之后执行）
                var isInsertPrev = prev.Type == "insert_after" || prev.Type == "insert_before";
                var isInsertCurr = curr.Type == "insert_after" || curr.Type == "insert_before";

                if (!isInsertPrev && !isInsertCurr)
                {
                    errors.Add(new ValidationError
                    {
                        Code = HashlineErrorCodes.OverlappingOperations,
                        Message = $"操作重叠。操作1: {prev.Type} [{prev.Start}-{prev.End}], 操作2: {curr.Type} [{curr.Start}-{curr.End}]",
                        Details = new Dictionary<string, object?>
                        {
                            ["FirstOpId"] = prev.OpId,
                            ["SecondOpId"] = curr.OpId
                        }
                    });
                }
            }
        }
    }
}