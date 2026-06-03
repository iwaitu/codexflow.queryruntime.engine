using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Models;

namespace CodexFlow.Core.Hashline.Services;

/// <summary>
/// 编辑应用器实现。
/// 在内存中应用编辑操作，生成新内容。
/// 关键设计：
/// 1. 操作按受影响行号倒序应用，避免行号漂移
/// 2. 先处理 delete/replace，再处理 insert
/// </summary>
public sealed class EditApplier : IEditApplier
{
    /// <summary>
    /// 在内存中应用编辑操作。
    /// </summary>
    public ApplyResult Apply(FileSnapshot snapshot, IReadOnlyList<EditOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (operations == null || operations.Count == 0)
        {
            // 无操作，返回原内容
            var originalContent = JoinLines(snapshot.Lines.Select(l => l.RawText), snapshot.NewLineStyle, snapshot.HasTrailingNewline);
            return new ApplyResult
            {
                OldContent = originalContent,
                NewContent = originalContent,
                Hunks = new List<ChangedHunk>()
            };
        }

        // 获取原始行列表
        var oldLines = snapshot.Lines.Select(l => l.RawText).ToList();
        var workingLines = new List<string>(oldLines);

        // 分离操作类型：先处理影响行数的操作，再处理插入操作
        var rangeOperations = new List<EditOperation>();
        var insertOperations = new List<EditOperation>();

        foreach (var op in operations)
        {
            if (op is InsertAfterOperation || op is InsertBeforeOperation)
            {
                insertOperations.Add(op);
            }
            else
            {
                rangeOperations.Add(op);
            }
        }

        // 按起始行号倒序排序 range 操作（从后往前应用，避免行号漂移）
        var sortedRangeOps = rangeOperations
            .OrderByDescending(op => GetOperationStartLine(op))
            .ToList();

        // 按目标行号倒序排序 insert 操作
        var sortedInsertOps = insertOperations
            .OrderByDescending(op => GetOperationTargetLine(op))
            .ToList();

        // 记录变更区间
        var changedHunks = new List<ChangedHunk>();

        // 应用 range 操作
        foreach (var op in sortedRangeOps)
        {
            ApplyRangeOperation(op, workingLines, changedHunks);
        }

        // 应用 insert 操作
        foreach (var op in sortedInsertOps)
        {
            ApplyInsertOperation(op, workingLines, changedHunks);
        }

        // 组装结果
        var oldContent = JoinLines(oldLines, snapshot.NewLineStyle, snapshot.HasTrailingNewline);
        var newContent = JoinLines(workingLines, snapshot.NewLineStyle, snapshot.HasTrailingNewline);

        // 计算 hunks（简化版：将所有变更区间合并）
        var mergedHunks = MergeHunks(changedHunks, oldLines.Count, workingLines.Count);

        return new ApplyResult
        {
            OldContent = oldContent,
            NewContent = newContent,
            Hunks = mergedHunks
        };
    }

    /// <summary>
    /// 获取操作的起始行号。
    /// </summary>
    private static int GetOperationStartLine(EditOperation op)
    {
        return op switch
        {
            ReplaceRangeOperation replaceOp => replaceOp.StartLine,
            DeleteRangeOperation deleteOp => deleteOp.StartLine,
            InsertAfterOperation insertAfterOp => insertAfterOp.TargetLine,
            InsertBeforeOperation insertBeforeOp => insertBeforeOp.TargetLine,
            RewriteWholeFileOperation => 1, // 从头开始
            _ => 0
        };
    }

    /// <summary>
    /// 获取操作的目标行号（用于 insert 操作）。
    /// </summary>
    private static int GetOperationTargetLine(EditOperation op)
    {
        return op switch
        {
            InsertAfterOperation insertAfterOp => insertAfterOp.TargetLine,
            InsertBeforeOperation insertBeforeOp => insertBeforeOp.TargetLine,
            _ => 0
        };
    }

    /// <summary>
    /// 应用 range 操作。
    /// </summary>
    private static void ApplyRangeOperation(
        EditOperation op,
        List<string> workingLines,
        List<ChangedHunk> changedHunks)
    {
        switch (op)
        {
            case ReplaceRangeOperation replaceOp:
                ApplyReplaceRange(replaceOp, workingLines, changedHunks);
                break;
            case DeleteRangeOperation deleteOp:
                ApplyDeleteRange(deleteOp, workingLines, changedHunks);
                break;
            case RewriteWholeFileOperation rewriteOp:
                ApplyRewriteWholeFile(rewriteOp, workingLines, changedHunks);
                break;
        }
    }

    /// <summary>
    /// 应用 ReplaceRange 操作。
    /// </summary>
    private static void ApplyReplaceRange(
        ReplaceRangeOperation op,
        List<string> workingLines,
        List<ChangedHunk> changedHunks)
    {
        var startIndex = op.StartLine - 1; // 转为 0-based
        var endIndex = op.EndLine - 1;
        var oldLineCount = endIndex - startIndex + 1;

        // 删除原有区间
        workingLines.RemoveRange(startIndex, oldLineCount);

        // 插入新行
        workingLines.InsertRange(startIndex, op.NewLines);

        // 记录 hunk
        changedHunks.Add(new ChangedHunk
        {
            OldStartLine = op.StartLine,
            OldLineCount = oldLineCount,
            NewStartLine = op.StartLine,
            NewLineCount = op.NewLines.Count,
            Preview = GeneratePreview(op.NewLines)
        });
    }

    /// <summary>
    /// 应用 DeleteRange 操作。
    /// </summary>
    private static void ApplyDeleteRange(
        DeleteRangeOperation op,
        List<string> workingLines,
        List<ChangedHunk> changedHunks)
    {
        var startIndex = op.StartLine - 1; // 转为 0-based
        var endIndex = op.EndLine - 1;
        var oldLineCount = endIndex - startIndex + 1;

        // 删除区间
        workingLines.RemoveRange(startIndex, oldLineCount);

        // 记录 hunk
        changedHunks.Add(new ChangedHunk
        {
            OldStartLine = op.StartLine,
            OldLineCount = oldLineCount,
            NewStartLine = op.StartLine,
            NewLineCount = 0,
            Preview = string.Empty
        });
    }

    /// <summary>
    /// 应用 RewriteWholeFile 操作。
    /// </summary>
    private static void ApplyRewriteWholeFile(
        RewriteWholeFileOperation op,
        List<string> workingLines,
        List<ChangedHunk> changedHunks)
    {
        // 清空原有内容
        workingLines.Clear();

        // 解析新内容
        var newLines = SplitLines(op.NewContent);
        workingLines.AddRange(newLines);

        // 记录 hunk（整文件替换）
        changedHunks.Add(new ChangedHunk
        {
            OldStartLine = 1,
            OldLineCount = 0, // 未知原行数
            NewStartLine = 1,
            NewLineCount = newLines.Count,
            Preview = GeneratePreview(newLines)
        });
    }

    /// <summary>
    /// 应用 insert 操作。
    /// </summary>
    private static void ApplyInsertOperation(
        EditOperation op,
        List<string> workingLines,
        List<ChangedHunk> changedHunks)
    {
        switch (op)
        {
            case InsertAfterOperation insertAfterOp:
                ApplyInsertAfter(insertAfterOp, workingLines, changedHunks);
                break;
            case InsertBeforeOperation insertBeforeOp:
                ApplyInsertBefore(insertBeforeOp, workingLines, changedHunks);
                break;
        }
    }

    /// <summary>
    /// 应用 InsertAfter 操作。
    /// </summary>
    private static void ApplyInsertAfter(
        InsertAfterOperation op,
        List<string> workingLines,
        List<ChangedHunk> changedHunks)
    {
        var targetIndex = op.TargetLine; // 在 TargetLine 之后插入，即索引为 TargetLine（0-based 时）
        workingLines.InsertRange(targetIndex, op.NewLines);

        changedHunks.Add(new ChangedHunk
        {
            OldStartLine = op.TargetLine + 1,
            OldLineCount = 0,
            NewStartLine = op.TargetLine + 1,
            NewLineCount = op.NewLines.Count,
            Preview = GeneratePreview(op.NewLines)
        });
    }

    /// <summary>
    /// 应用 InsertBefore 操作。
    /// </summary>
    private static void ApplyInsertBefore(
        InsertBeforeOperation op,
        List<string> workingLines,
        List<ChangedHunk> changedHunks)
    {
        var targetIndex = op.TargetLine - 1; // 在 TargetLine 之前插入，即索引为 TargetLine - 1
        workingLines.InsertRange(targetIndex, op.NewLines);

        changedHunks.Add(new ChangedHunk
        {
            OldStartLine = op.TargetLine,
            OldLineCount = 0,
            NewStartLine = op.TargetLine,
            NewLineCount = op.NewLines.Count,
            Preview = GeneratePreview(op.NewLines)
        });
    }

    /// <summary>
    /// 合并 hunks（简化版）。
    /// </summary>
    private static List<ChangedHunk> MergeHunks(
        List<ChangedHunk> hunks,
        int oldLineCount,
        int newLineCount)
    {
        if (hunks.Count == 0)
        {
            return hunks;
        }

        // 按起始行号排序
        var sortedHunks = hunks.OrderBy(h => h.OldStartLine).ToList();

        // 这里简化处理，不进行复杂的合并逻辑
        return sortedHunks;
    }

    /// <summary>
    /// 生成预览文本。
    /// </summary>
    private static string GeneratePreview(List<string> lines)
    {
        if (lines == null || lines.Count == 0)
        {
            return string.Empty;
        }

        // 取前 3 行作为预览
        var previewLines = lines.Take(3).ToList();
        var preview = string.Join("\n", previewLines);

        if (lines.Count > 3)
        {
            preview += "...";
        }

        return preview;
    }

    /// <summary>
    /// 按行切分文本。
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new List<string>();
        }

        // 统一换行风格
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == '\n')
            {
                lines.Add(normalized.Substring(start, i - start));
                start = i + 1;
            }
        }

        // 处理最后一行
        if (start <= normalized.Length)
        {
            lines.Add(normalized.Substring(start));
        }

        return lines;
    }

    /// <summary>
    /// 合并行列表为文本。
    /// </summary>
    private static string JoinLines(IEnumerable<string> lines, string newLineStyle, bool hasTrailingNewline = true)
    {
        var separator = newLineStyle == "\r\n" ? "\r\n" : "\n";
        var sb = new StringBuilder();

        var linesList = lines.ToList();
        for (var i = 0; i < linesList.Count; i++)
        {
            sb.Append(linesList[i]);
            // 只在非最后一行添加换行符，或者如果原文件有尾部换行则也添加
            if (i < linesList.Count - 1 || hasTrailingNewline)
            {
                sb.Append(separator);
            }
        }

        return sb.ToString();
    }
}