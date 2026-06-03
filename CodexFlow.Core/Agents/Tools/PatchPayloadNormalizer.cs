using System.Text;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Agents.Tools;

internal static class PatchPayloadNormalizer
{
    public static string NormalizeTraditionalPatch(string patch, out int removedDuplicateEndPatchCount)
    {
        var normalizedLineEndings = NormalizeLineEndings(patch);
        var deduplicated = RemoveDuplicateCodexEndMarkers(normalizedLineEndings, out removedDuplicateEndPatchCount);
        return StripMarkdownCodeFences(deduplicated);
    }

    public static string NormalizeLineEndings(string text)
        => string.IsNullOrEmpty(text)
            ? text
            : text.Replace("\r\n", "\n", StringComparison.Ordinal)
                  .Replace('\r', '\n');

    public static bool LooksLikeCodexPatchEnvelope(string patch)
    {
        if (string.IsNullOrWhiteSpace(patch)) return false;
        return patch.Contains("*** Begin Patch", StringComparison.Ordinal)
               || patch.Contains("*** Update File:", StringComparison.Ordinal)
               || patch.Contains("*** Add File:", StringComparison.Ordinal)
               || patch.Contains("*** Delete File:", StringComparison.Ordinal);
    }

    public static bool LooksLikeUnifiedDiff(string patch)
    {
        if (string.IsNullOrWhiteSpace(patch)) return false;

        var hasOldFileHeader = patch.StartsWith("--- ", StringComparison.Ordinal) || patch.Contains("\n--- ", StringComparison.Ordinal);
        var hasNewFileHeader = patch.StartsWith("+++ ", StringComparison.Ordinal) || patch.Contains("\n+++ ", StringComparison.Ordinal);
        var hasFileHeaders = hasOldFileHeader && hasNewFileHeader;
        var hasHunk = patch.StartsWith("@@", StringComparison.Ordinal) || patch.Contains("\n@@", StringComparison.Ordinal);
        var hasGitHeader = patch.Contains("diff --git ", StringComparison.Ordinal);

        return hasGitHeader || (hasFileHeaders && hasHunk);
    }

    public static bool TryValidateUnifiedDiff(string patch, out string error)
    {
        error = string.Empty;
        if (!LooksLikeUnifiedDiff(patch))
        {
            error = "补丁缺少有效的 unified diff 头部或 hunk 标记。";
            return false;
        }

        var normalized = NormalizeLineEndings(patch).TrimEnd('\n');
        var lines = normalized.Split('\n');
        var sawOldHeader = false;
        var sawNewHeader = false;
        var sawHunk = false;
        var insideHunk = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal) ||
                line.StartsWith("old mode ", StringComparison.Ordinal) ||
                line.StartsWith("new mode ", StringComparison.Ordinal) ||
                line.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
                line.StartsWith("new file mode ", StringComparison.Ordinal) ||
                line.StartsWith("similarity index ", StringComparison.Ordinal) ||
                line.StartsWith("rename from ", StringComparison.Ordinal) ||
                line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                insideHunk = false;
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                sawOldHeader = true;
                insideHunk = false;
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                sawNewHeader = true;
                insideHunk = false;
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                sawHunk = true;
                insideHunk = true;
                continue;
            }

            if (!insideHunk)
            {
                continue;
            }

            if (line.Length == 0)
            {
                error = $"补丁 hunk 在第 {index + 1} 行包含空行，缺少 unified diff 行前缀。";
                return false;
            }

            var prefix = line[0];
            if (prefix is ' ' or '+' or '-' or '\\')
            {
                continue;
            }

            error = $"补丁 hunk 在第 {index + 1} 行使用了无效前缀 '{prefix}'。";
            return false;
        }

        if (!sawOldHeader || !sawNewHeader || !sawHunk)
        {
            error = "补丁缺少完整的 --- / +++ / @@ 结构。";
            return false;
        }

        return true;
    }

    public static string StripMarkdownCodeFences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var trimmed = text.Trim();
        var fencedPattern = @"\A```[a-zA-Z0-9_-]*\s*\r?\n(?<body>[\s\S]*?)\r?\n```\s*\z";
        var match = Regex.Match(trimmed, fencedPattern, RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["body"].Value : text;
    }

    private static string RemoveDuplicateCodexEndMarkers(string patch, out int removedCount)
    {
        removedCount = 0;
        if (string.IsNullOrEmpty(patch)) return patch;

        var sb = new StringBuilder(patch.Length);
        using var reader = new StringReader(patch);
        string? line;
        var previousWasEndPatch = false;

        while ((line = reader.ReadLine()) is not null)
        {
            var isEndPatch = string.Equals(line.Trim(), "*** End Patch", StringComparison.Ordinal);
            if (isEndPatch && previousWasEndPatch)
            {
                removedCount++;
                continue;
            }

            sb.Append(line);
            sb.Append('\n');
            previousWasEndPatch = isEndPatch;
        }

        return sb.ToString();
    }
}
