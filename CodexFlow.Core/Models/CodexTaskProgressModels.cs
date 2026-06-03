using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Models;

public enum TaskChecklistItemStatus
{
    Pending,
    Completed,
    Failed,
    Blocked
}

public sealed class TaskChecklistItem
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public TaskChecklistItemStatus Status { get; set; } = TaskChecklistItemStatus.Pending;
    public Collection<string> Evidence { get; } = new();
    public Collection<string> RelatedAssertions { get; } = new();

    public void ReplaceEvidence(IEnumerable<string>? evidence) => ReplaceCollection(Evidence, evidence);
    public void ReplaceRelatedAssertions(IEnumerable<string>? relatedAssertions) => ReplaceCollection(RelatedAssertions, relatedAssertions);

    private static void ReplaceCollection<T>(Collection<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source == null)
        {
            return;
        }

        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}

public sealed class ChecklistItemEvaluation
{
    public string ItemId { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public TaskChecklistItemStatus Status { get; init; } = TaskChecklistItemStatus.Pending;
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> RelatedAssertionsMatched { get; init; } = [];
}

public sealed class ChecklistEvaluation
{
    public IReadOnlyList<ChecklistItemEvaluation> CompletedItems { get; init; } = [];
    public IReadOnlyList<ChecklistItemEvaluation> FailedItems { get; init; } = [];
    public IReadOnlyList<ChecklistItemEvaluation> PendingItems { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> EvidenceByItem { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RelatedAssertionsMatched { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public static class CodexTaskProgressNormalizer
{
    private static readonly Regex SectionHeadingRegex = new(@"^\s*#{2,}\s*(?<title>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex NumberedLineRegex = new(@"^\s*(?:\*\*)?(?<index>\d+)\.(?:\*\*)?\s+(?<text>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex BulletLineRegex = new(@"^\s*[-*]\s+(?<text>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex PathRegex = new(@"(?:[A-Za-z0-9_.-]+[\\/])+[A-Za-z0-9_.-]+\.[A-Za-z0-9_.-]+", RegexOptions.Compiled);

    public static void NormalizeTaskChecklist(CodexTask? task)
    {
        if (task == null)
        {
            return;
        }

        if (task.ChecklistItems.Count > 0)
        {
            NormalizeExistingChecklist(task);
            return;
        }

        var parsedItems = ParseExecutionOrderChecklist(task);
        if (parsedItems.Count == 0)
        {
            return;
        }

        task.ReplaceChecklistItems(parsedItems);
    }

    internal static IReadOnlyList<TaskChecklistItem> ParseExecutionOrderChecklist(CodexTask? task)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.Description))
        {
            return [];
        }

        var sectionLines = ExtractTaskExecutionOrderLines(task.Description);
        if (sectionLines.Count == 0)
        {
            return [];
        }

        var items = new List<TaskChecklistItem>();
        foreach (var rawLine in sectionLines)
        {
            var normalizedText = TryParseChecklistLine(rawLine);
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                continue;
            }

            items.Add(new TaskChecklistItem
            {
                Id = BuildChecklistItemId(items.Count + 1),
                Text = normalizedText,
                Status = TaskChecklistItemStatus.Pending
            });
        }

        if (items.Count < 2)
        {
            return [];
        }

        foreach (var item in items)
        {
            item.ReplaceRelatedAssertions(MatchRelatedAssertions(task, item.Text));
        }

        return items;
    }

    private static void NormalizeExistingChecklist(CodexTask task)
    {
        for (var i = 0; i < task.ChecklistItems.Count; i++)
        {
            var item = task.ChecklistItems[i];
            item.Id = string.IsNullOrWhiteSpace(item.Id) ? BuildChecklistItemId(i + 1) : item.Id.Trim();
            item.Text = item.Text?.Trim() ?? string.Empty;

            if (item.RelatedAssertions.Count == 0)
            {
                item.ReplaceRelatedAssertions(MatchRelatedAssertions(task, item.Text));
            }
        }
    }

    private static List<string> ExtractTaskExecutionOrderLines(string description)
    {
        var lines = description.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sectionLines = new List<string>();
        var inExecutionOrderSection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var heading = SectionHeadingRegex.Match(line);
            if (heading.Success)
            {
                var title = heading.Groups["title"].Value.Trim();
                if (title.Contains("任务执行顺序", StringComparison.OrdinalIgnoreCase))
                {
                    inExecutionOrderSection = true;
                    continue;
                }

                if (inExecutionOrderSection)
                {
                    break;
                }
            }

            if (!inExecutionOrderSection || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            sectionLines.Add(line);
        }

        return sectionLines;
    }

    private static string? TryParseChecklistLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var normalized = line.Trim().Replace("**", string.Empty, StringComparison.Ordinal).Trim();

        var numberedMatch = NumberedLineRegex.Match(normalized);
        if (numberedMatch.Success)
        {
            return NormalizeChecklistText(numberedMatch.Groups["text"].Value);
        }

        var bulletMatch = BulletLineRegex.Match(normalized);
        if (bulletMatch.Success)
        {
            return NormalizeChecklistText(bulletMatch.Groups["text"].Value);
        }

        return null;
    }

    private static string NormalizeChecklistText(string text)
    {
        return Regex.Replace(text.Trim(), @"\s+", " ");
    }

    private static string[] MatchRelatedAssertions(CodexTask task, string checklistText)
    {
        if (string.IsNullOrWhiteSpace(checklistText))
        {
            return [];
        }

        var pathCandidates = ExtractPathCandidates(checklistText);
        if (pathCandidates.Count == 0)
        {
            return [];
        }

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assertion in task.RequiredArtifacts.Concat(task.ForbiddenStates))
        {
            var assertionPath = CanonicalizePath(assertion.Path);
            if (string.IsNullOrWhiteSpace(assertionPath))
            {
                continue;
            }

            if (pathCandidates.Any(candidate => PathsMatch(candidate, assertionPath)))
            {
                matches.Add(BuildAssertionKey(assertion));
            }
        }

        return matches.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static List<string> ExtractPathCandidates(string text)
    {
        var matches = PathRegex.Matches(text);
        if (matches.Count == 0)
        {
            return [];
        }

        return matches
            .Select(match => CanonicalizePath(match.Value))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool PathsMatch(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
               left.EndsWith("/" + right, StringComparison.OrdinalIgnoreCase) ||
               right.EndsWith("/" + left, StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalizePath(string? path)
        => (path ?? string.Empty).Replace('\\', '/').Trim().Trim('`');

    private static string BuildAssertionKey(ArtifactAssertion assertion)
        => $"{assertion.Type.Trim().ToLowerInvariant()}|{CanonicalizePath(assertion.Path)}|{assertion.Text?.Trim() ?? string.Empty}";

    private static string BuildChecklistItemId(int ordinal)
        => $"CHK_{ordinal:000}";
}

public static class CodexTaskProgressMerger
{
    public static void MergeChecklistEvaluation(CodexTask? task, ChecklistEvaluation? evaluation)
    {
        if (task == null || evaluation == null || task.ChecklistItems.Count == 0)
        {
            return;
        }

        var incomingItems = evaluation.CompletedItems
            .Concat(evaluation.FailedItems)
            .Concat(evaluation.PendingItems)
            .ToList();

        if (incomingItems.Count == 0)
        {
            return;
        }

        var byId = new Dictionary<string, ChecklistItemEvaluation>(StringComparer.OrdinalIgnoreCase);
        var byText = new Dictionary<string, ChecklistItemEvaluation>(StringComparer.OrdinalIgnoreCase);

        foreach (var incoming in incomingItems)
        {
            if (!string.IsNullOrWhiteSpace(incoming.ItemId))
            {
                byId[incoming.ItemId.Trim()] = incoming;
            }

            if (!string.IsNullOrWhiteSpace(incoming.Text))
            {
                byText[incoming.Text.Trim()] = incoming;
            }
        }

        foreach (var item in task.ChecklistItems)
        {
            ChecklistItemEvaluation? matched = null;
            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                byId.TryGetValue(item.Id.Trim(), out matched);
            }

            if (matched == null && !string.IsNullOrWhiteSpace(item.Text))
            {
                byText.TryGetValue(item.Text.Trim(), out matched);
            }

            if (matched == null)
            {
                continue;
            }

            item.Status = matched.Status;
            item.ReplaceEvidence(matched.Evidence);
        }
    }
}
