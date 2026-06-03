using System;
using System.Collections.Generic;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Models;

namespace CodexFlow.Core.Hashline.Services;

/// <summary>
/// 文本规范化器实现。
/// 规范化规则：
/// - 分行时统一识别 \r\n、\n
/// - 行内容规范化时去除结尾 \r
/// - 保留前导空格
/// - 保留中间空格
/// - 保留 tab
/// - 默认保留尾随空格
/// - 空行也要参与哈希计算
/// </summary>
public sealed class TextNormalizer : ITextNormalizer
{
    /// <summary>
    /// 规范化整个文本，返回规范化结果和检测到的换行风格。
    /// </summary>
    public NormalizedTextResult Normalize(string rawText)
    {
        if (rawText == null)
        {
            return new NormalizedTextResult
            {
                NormalizedText = string.Empty,
                DetectedNewLineStyle = "\n",
                Lines = Array.Empty<string>()
            };
        }

        // 检测换行风格
        var detectedNewLineStyle = DetectNewLineStyle(rawText);

        // 处理单独的 \r（转换为 \n 后会被正确分行）
        // 先处理 CRLF，再处理单独的 CR
        var normalizedText = rawText.Replace("\r\n", "\n", StringComparison.Ordinal);

        // 处理文件末尾单独的 \r（Mac 风格换行或残留字符）
        // 只处理末尾的 \r，中间的 \r 视为换行
        if (normalizedText.Length > 0 && normalizedText[normalizedText.Length - 1] == '\r')
        {
            normalizedText = normalizedText.Substring(0, normalizedText.Length - 1);
        }

        // 将中间的单独 \r 视为换行（兼容旧 Mac 格式）
        normalizedText = normalizedText.Replace('\r', '\n');

        // 按行切分
        var lines = SplitLinesInternal(normalizedText);

        return new NormalizedTextResult
        {
            NormalizedText = normalizedText,
            DetectedNewLineStyle = detectedNewLineStyle,
            Lines = lines
        };
    }

    /// <summary>
    /// 规范化单行文本。
    /// 仅去除结尾的 \r，保留其他所有字符。
    /// </summary>
    public string NormalizeLine(string lineText)
    {
        if (lineText == null)
        {
            return string.Empty;
        }

        // 去除结尾的 \r
        return lineText.TrimEnd('\r');
    }

    /// <summary>
    /// 按行切分文本。
    /// </summary>
    public IReadOnlyList<string> SplitLines(string rawText)
    {
        if (rawText == null)
        {
            return Array.Empty<string>();
        }

        // 先统一换行风格
        var normalizedText = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return SplitLinesInternal(normalizedText);
    }

    /// <summary>
    /// 内部方法：按 \n 切分文本。
    /// 注意：末尾换行符不会产生空行，末尾换行信息由 HasTrailingNewline 元数据携带。
    /// </summary>
    private static IReadOnlyList<string> SplitLinesInternal(string normalizedText)
    {
        if (normalizedText.Length == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < normalizedText.Length; i++)
        {
            if (normalizedText[i] == '\n')
            {
                var line = normalizedText.Substring(start, i - start);
                lines.Add(line);
                start = i + 1;
            }
        }

        // 处理最后一行：只有当 start < Length 时才添加（即有内容在最后一个换行符之后）
        // 如果文件以换行符结尾，start == Length，此时不添加空行
        if (start < normalizedText.Length)
        {
            var lastLine = normalizedText.Substring(start);
            lines.Add(lastLine);
        }

        return lines;
    }

    /// <summary>
    /// 检测换行风格。
    /// </summary>
    private static string DetectNewLineStyle(string text)
    {
        if (text == null || text.Length == 0)
        {
            return "\n";
        }

        // 检查是否包含 \r\n
        if (text.Contains("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }

        // 默认返回 \n
        return "\n";
    }
}