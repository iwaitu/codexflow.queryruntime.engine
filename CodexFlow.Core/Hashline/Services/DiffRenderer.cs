using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CodexFlow.Core.Hashline.Abstractions;

namespace CodexFlow.Core.Hashline.Services;

/// <summary>
/// Unified Diff 渲染器实现。
/// 生成标准 unified diff 格式。
/// </summary>
public sealed class DiffRenderer : IDiffRenderer
{
    /// <summary>
    /// 渲染 Unified Diff。
    /// </summary>
    public string RenderUnifiedDiff(string filePath, string oldContent, string newContent)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var oldLines = SplitLines(oldContent);
        var newLines = SplitLines(newContent);

        // 生成 diff header
        var sb = new StringBuilder();
        sb.AppendLine($"--- {filePath}");
        sb.AppendLine($"+++ {filePath}");

        // 计算差异区间（使用简化的 LCS 算法）
        var hunks = ComputeHunks(oldLines, newLines);

        foreach (var hunk in hunks)
        {
            sb.Append("@@ ");
            sb.Append($"-{hunk.OldStart},{hunk.OldCount}");
            sb.Append(' ');
            sb.Append($"+{hunk.NewStart},{hunk.NewCount}");
            sb.AppendLine(" @@");

            // 输出删除的行
            for (var i = hunk.OldStart - 1; i < hunk.OldStart - 1 + hunk.OldCount && i < oldLines.Count; i++)
            {
                sb.Append('-');
                sb.AppendLine(oldLines[i]);
            }

            // 输出新增的行
            for (var i = hunk.NewStart - 1; i < hunk.NewStart - 1 + hunk.NewCount && i < newLines.Count; i++)
            {
                sb.Append('+');
                sb.AppendLine(newLines[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 计算差异区间。
    /// 使用简化的差异检测算法。
    /// </summary>
    private static List<DiffHunk> ComputeHunks(List<string> oldLines, List<string> newLines)
    {
        var hunks = new List<DiffHunk>();

        if (oldLines.Count == 0 && newLines.Count == 0)
        {
            return hunks;
        }

        // 全部替换场景
        if (oldLines.Count == 0)
        {
            hunks.Add(new DiffHunk
            {
                OldStart = 0,
                OldCount = 0,
                NewStart = 1,
                NewCount = newLines.Count
            });
            return hunks;
        }

        if (newLines.Count == 0)
        {
            hunks.Add(new DiffHunk
            {
                OldStart = 1,
                OldCount = oldLines.Count,
                NewStart = 0,
                NewCount = 0
            });
            return hunks;
        }

        // 使用 LCS（最长公共子序列）算法计算差异
        var lcs = ComputeLCS(oldLines, newLines);

        // 根据 LCS 生成 hunks
        var oldIndex = 0;
        var newIndex = 0;
        var lcsIndex = 0;

        while (oldIndex < oldLines.Count || newIndex < newLines.Count)
        {
            // 找到下一个 LCS 点
            var nextLcsOld = lcsIndex < lcs.Count ? lcs[lcsIndex].OldIndex : oldLines.Count;
            var nextLcsNew = lcsIndex < lcs.Count ? lcs[lcsIndex].NewIndex : newLines.Count;

            // 如果有差异区间
            if (oldIndex < nextLcsOld || newIndex < nextLcsNew)
            {
                var hunk = new DiffHunk
                {
                    OldStart = oldIndex + 1,
                    OldCount = nextLcsOld - oldIndex,
                    NewStart = newIndex + 1,
                    NewCount = nextLcsNew - newIndex
                };
                hunks.Add(hunk);
            }

            // 移动到 LCS 点之后
            oldIndex = nextLcsOld + 1;
            newIndex = nextLcsNew + 1;
            lcsIndex++;
        }

        return hunks;
    }

    /// <summary>
    /// 计算 LCS（最长公共子序列）。
    /// 使用动态规划算法。
    /// </summary>
    private static List<LCSPoint> ComputeLCS(List<string> oldLines, List<string> newLines)
    {
        var m = oldLines.Count;
        var n = newLines.Count;

        // 构建 DP 表（使用 jagged array）
        var dp = new int[m + 1][];
        for (var row = 0; row <= m; row++)
        {
            dp[row] = new int[n + 1];
        }

        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                if (string.Equals(oldLines[i - 1], newLines[j - 1], StringComparison.Ordinal))
                {
                    dp[i][j] = dp[i - 1][j - 1] + 1;
                }
                else
                {
                    dp[i][j] = Math.Max(dp[i - 1][j], dp[i][j - 1]);
                }
            }
        }

        // 回溯找到 LCS 点
        var lcs = new List<LCSPoint>();
        var oldIdx = m;
        var newIdx = n;

        while (oldIdx > 0 && newIdx > 0)
        {
            if (string.Equals(oldLines[oldIdx - 1], newLines[newIdx - 1], StringComparison.Ordinal))
            {
                lcs.Add(new LCSPoint { OldIndex = oldIdx - 1, NewIndex = newIdx - 1 });
                oldIdx--;
                newIdx--;
            }
            else if (dp[oldIdx - 1][newIdx] > dp[oldIdx][newIdx - 1])
            {
                oldIdx--;
            }
            else
            {
                newIdx--;
            }
        }

        // 反转（因为我们是从后往前回溯的）
        lcs.Reverse();

        return lcs;
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

        if (start <= normalized.Length)
        {
            lines.Add(normalized.Substring(start));
        }

        return lines;
    }

    /// <summary>
    /// Diff Hunk 信息。
    /// </summary>
    private sealed class DiffHunk
    {
        public int OldStart { get; set; }
        public int OldCount { get; set; }
        public int NewStart { get; set; }
        public int NewCount { get; set; }
    }

    /// <summary>
    /// LCS 点信息。
    /// </summary>
    private sealed class LCSPoint
    {
        public int OldIndex { get; set; }
        public int NewIndex { get; set; }
    }
}