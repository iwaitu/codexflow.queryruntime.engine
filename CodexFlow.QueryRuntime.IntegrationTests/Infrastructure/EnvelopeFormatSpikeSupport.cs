using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;

internal enum EnvelopeFormatKind
{
    Json,
    Xml,
    Markdown
}

internal sealed class EnvelopeFormatSample
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("scenario")]
    public string Scenario { get; init; } = string.Empty;

    [JsonPropertyName("workerType")]
    public string WorkerType { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; init; }

    [JsonPropertyName("resumeToken")]
    public string? ResumeToken { get; init; }

    [JsonPropertyName("waitingReason")]
    public string? WaitingReason { get; init; }

    [JsonPropertyName("expected")]
    public EnvelopeFormatExpected Expected { get; init; } = new();
}

internal sealed class EnvelopeFormatExpected
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("summaryKeywords")]
    public List<string> SummaryKeywords { get; init; } = [];

    [JsonPropertyName("rootCauseKeywords")]
    public List<string> RootCauseKeywords { get; init; } = [];

    [JsonPropertyName("nextWorkerType")]
    public string NextWorkerType { get; init; } = string.Empty;
}

internal sealed class CoordinatorAssessment
{
    [JsonPropertyName("worker_summary")]
    public string WorkerSummary { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("root_cause")]
    public string RootCause { get; init; } = string.Empty;

    [JsonPropertyName("next_worker_type")]
    public string NextWorkerType { get; init; } = string.Empty;

    [JsonPropertyName("next_worker_task")]
    public string NextWorkerTask { get; init; } = string.Empty;
}

internal sealed class EnvelopeEvaluation
{
    public required string SampleId { get; init; }

    public required string Scenario { get; init; }

    public required EnvelopeFormatKind Format { get; init; }

    public required string RawResponse { get; init; }

    public CoordinatorAssessment? ParsedAssessment { get; init; }

    public bool StructureOk { get; init; }

    public bool SummaryOk { get; init; }

    public bool StatusOk { get; init; }

    public bool RootCauseOk { get; init; }

    public bool NextWorkerOk { get; init; }

    public double QuestionAccuracy => (new[] { SummaryOk, StatusOk, RootCauseOk, NextWorkerOk }.Count(x => x)) / 4.0;
}

internal sealed class EnvelopeAggregate
{
    public required EnvelopeFormatKind Format { get; init; }

    public int Samples { get; init; }

    public double OverallAccuracy { get; init; }

    public double StructureIntegrity { get; init; }

    public double FormatInterferenceRate { get; init; }

    public double SpecialCharacterBreakageRate { get; init; }
}

internal sealed class EnvelopeSpikeSummary
{
    public required List<EnvelopeAggregate> Aggregates { get; init; }

    public required string Recommendation { get; init; }

    public required string DecisionReason { get; init; }

    public double BestOverallAccuracy => Aggregates.Max(x => x.OverallAccuracy);
}

internal static class EnvelopeFormatSampleLoader
{
    public static IReadOnlyList<EnvelopeFormatSample> Load(int? limit = null)
    {
        var path = RepositoryPathHelper.FindRepositoryFile(Path.Combine("docs", "spike-data", "envelope-format-samples.json"))
            ?? throw new FileNotFoundException("Could not locate docs/spike-data/envelope-format-samples.json.");

        var json = File.ReadAllText(path, Encoding.UTF8);
        var samples = JsonSerializer.Deserialize<List<EnvelopeFormatSample>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Envelope format sample dataset is empty.");

        return limit is > 0
            ? samples.Take(limit.Value).ToArray()
            : samples;
    }
}

internal static class EnvelopeFormatRenderer
{
    public static string Render(EnvelopeFormatSample sample, EnvelopeFormatKind format)
    {
        return format switch
        {
            EnvelopeFormatKind.Json => RenderJson(sample),
            EnvelopeFormatKind.Xml => RenderXml(sample),
            EnvelopeFormatKind.Markdown => RenderMarkdown(sample),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    private static string RenderJson(EnvelopeFormatSample sample)
    {
        var payload = new Dictionary<string, object?>
        {
            ["task_id"] = sample.Id,
            ["worker_type"] = sample.WorkerType,
            ["status"] = sample.Status,
            ["summary"] = sample.Summary,
            ["result"] = sample.Result,
            ["usage"] = new Dictionary<string, object?>
            {
                ["duration_ms"] = sample.DurationMs
            }
        };

        if (!string.IsNullOrWhiteSpace(sample.ResumeToken))
        {
            payload["resume_token"] = sample.ResumeToken;
        }

        if (!string.IsNullOrWhiteSpace(sample.WaitingReason))
        {
            payload["waiting_reason"] = sample.WaitingReason;
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string RenderXml(EnvelopeFormatSample sample)
    {
        var root = new XElement("task-notification",
            new XElement("task-id", sample.Id),
            new XElement("worker-type", sample.WorkerType),
            new XElement("status", sample.Status),
            new XElement("summary", sample.Summary),
            new XElement("result", new XCData(SanitizeCData(sample.Result))),
            new XElement("usage", new XElement("duration-ms", sample.DurationMs)));

        if (!string.IsNullOrWhiteSpace(sample.ResumeToken))
        {
            root.Add(new XElement("resume-token", sample.ResumeToken));
        }

        if (!string.IsNullOrWhiteSpace(sample.WaitingReason))
        {
            root.Add(new XElement("waiting-reason", sample.WaitingReason));
        }

        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static string RenderMarkdown(EnvelopeFormatSample sample)
    {
        var builder = new StringBuilder()
            .AppendLine("--- task-notification ---")
            .AppendLine($"task-id: {sample.Id}")
            .AppendLine($"worker-type: {sample.WorkerType}")
            .AppendLine($"status: {sample.Status}")
            .AppendLine($"summary: {sample.Summary}");

        if (!string.IsNullOrWhiteSpace(sample.ResumeToken))
        {
            builder.AppendLine($"resume-token: {sample.ResumeToken}");
        }

        if (!string.IsNullOrWhiteSpace(sample.WaitingReason))
        {
            builder.AppendLine($"waiting-reason: {sample.WaitingReason}");
        }

        builder
            .AppendLine()
            .AppendLine("### result")
            .AppendLine(sample.Result)
            .AppendLine()
            .AppendLine("### usage")
            .AppendLine($"duration-ms: {sample.DurationMs}")
            .AppendLine("--- end ---");

        return builder.ToString().TrimEnd();
    }

    private static string SanitizeCData(string text)
    {
        return text.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal);
    }
}

internal static class CoordinatorAssessmentParser
{
    public static CoordinatorAssessment? Parse(string rawText)
    {
        var candidate = ExtractJson(rawText);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CoordinatorAssessment>(candidate, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed is null ||
                string.IsNullOrWhiteSpace(parsed.WorkerSummary) ||
                string.IsNullOrWhiteSpace(parsed.Status) ||
                string.IsNullOrWhiteSpace(parsed.RootCause) ||
                string.IsNullOrWhiteSpace(parsed.NextWorkerType) ||
                string.IsNullOrWhiteSpace(parsed.NextWorkerTask))
            {
                return null;
            }

            return parsed;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractJson(string rawText)
    {
        var trimmed = rawText.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start
            ? trimmed[start..(end + 1)]
            : string.Empty;
    }
}

internal static class EnvelopeFormatAnalyzer
{
    public static EnvelopeEvaluation Evaluate(EnvelopeFormatSample sample, EnvelopeFormatKind format, string rawResponse)
    {
        var parsed = CoordinatorAssessmentParser.Parse(rawResponse);
        var structureOk = parsed is not null;
        var summaryOk = parsed is not null && ContainsAny(parsed.WorkerSummary, sample.Expected.SummaryKeywords);
        var statusOk = parsed is not null && string.Equals(Normalize(parsed.Status), Normalize(sample.Expected.Status), StringComparison.OrdinalIgnoreCase);
        var rootCauseOk = parsed is not null && ContainsAny(parsed.RootCause, sample.Expected.RootCauseKeywords);
        var nextWorkerOk = parsed is not null && string.Equals(Normalize(parsed.NextWorkerType), Normalize(sample.Expected.NextWorkerType), StringComparison.OrdinalIgnoreCase);

        return new EnvelopeEvaluation
        {
            SampleId = sample.Id,
            Scenario = sample.Scenario,
            Format = format,
            RawResponse = rawResponse,
            ParsedAssessment = parsed,
            StructureOk = structureOk,
            SummaryOk = summaryOk,
            StatusOk = statusOk,
            RootCauseOk = rootCauseOk,
            NextWorkerOk = nextWorkerOk
        };
    }

    public static EnvelopeSpikeSummary Summarize(IReadOnlyList<EnvelopeEvaluation> evaluations)
    {
        var aggregates = evaluations
            .GroupBy(x => x.Format)
            .Select(group =>
            {
                var special = group.Where(x => x.Scenario == "special_chars").ToList();
                var specialBreakage = special.Count == 0
                    ? 0.0
                    : special.Count(x => !x.StructureOk || !x.StatusOk) / (double)special.Count;

                return new EnvelopeAggregate
                {
                    Format = group.Key,
                    Samples = group.Count(),
                    OverallAccuracy = group.Average(x => x.QuestionAccuracy),
                    StructureIntegrity = group.Count(x => x.StructureOk) / (double)group.Count(),
                    FormatInterferenceRate = group.Count(x => !x.StructureOk) / (double)group.Count(),
                    SpecialCharacterBreakageRate = specialBreakage
                };
            })
            .OrderByDescending(x => x.OverallAccuracy)
            .ToList();

        var best = aggregates[0];
        var second = aggregates.Count > 1 ? aggregates[1] : null;

        string recommendation;
        string reason;

        if (aggregates.All(x => x.OverallAccuracy < 0.70))
        {
            recommendation = "No recommendation";
            reason = "All formats stayed below the 70% accuracy floor. Protocol selection should pause until coordinator prompting is improved.";
        }
        else if (second is not null && best.OverallAccuracy - second.OverallAccuracy > 0.10)
        {
            recommendation = best.Format.ToString();
            reason = $"{best.Format} led by more than 10 percentage points over the runner-up.";
        }
        else
        {
            recommendation = EnvelopeFormatKind.Markdown.ToString();
            reason = "Accuracy deltas stayed within 10 percentage points, so the plan defaults to Markdown-fenced for lower implementation cost.";
        }

        if (recommendation == EnvelopeFormatKind.Xml.ToString() && best.SpecialCharacterBreakageRate > 0)
        {
            recommendation = EnvelopeFormatKind.Markdown.ToString();
            reason = "XML had the top raw accuracy, but it also showed special-character breakage, so the recommendation downgrades to Markdown.";
        }

        return new EnvelopeSpikeSummary
        {
            Aggregates = aggregates,
            Recommendation = recommendation,
            DecisionReason = reason
        };
    }

    public static string ToMarkdown(
        IReadOnlyList<EnvelopeFormatSample> samples,
        IReadOnlyList<EnvelopeEvaluation> evaluations,
        EnvelopeSpikeSummary summary)
    {
        var builder = new StringBuilder()
            .AppendLine("# Envelope Format Spike Results")
            .AppendLine()
            .AppendLine($"- Samples: {samples.Count}")
            .AppendLine($"- Formats: {string.Join(", ", Enum.GetNames<EnvelopeFormatKind>())}")
            .AppendLine($"- Recommendation: {summary.Recommendation}")
            .AppendLine($"- Reason: {summary.DecisionReason}")
            .AppendLine()
            .AppendLine("## Aggregates")
            .AppendLine()
            .AppendLine("| Format | Samples | Overall Accuracy | Structure Integrity | Format Interference | Special Char Breakage |")
            .AppendLine("|---|---:|---:|---:|---:|---:|");

        foreach (var aggregate in summary.Aggregates)
        {
            builder.AppendLine($"| {aggregate.Format} | {aggregate.Samples} | {aggregate.OverallAccuracy:P1} | {aggregate.StructureIntegrity:P1} | {aggregate.FormatInterferenceRate:P1} | {aggregate.SpecialCharacterBreakageRate:P1} |");
        }

        builder
            .AppendLine()
            .AppendLine("## Scenario Accuracy")
            .AppendLine()
            .AppendLine("| Scenario | Format | Accuracy |")
            .AppendLine("|---|---|---:|");

        foreach (var row in evaluations
                     .GroupBy(x => new { x.Scenario, x.Format })
                     .OrderBy(x => x.Key.Scenario)
                     .ThenBy(x => x.Key.Format.ToString()))
        {
            builder.AppendLine($"| {row.Key.Scenario} | {row.Key.Format} | {row.Average(x => x.QuestionAccuracy):P1} |");
        }

        return builder.ToString();
    }

    private static bool ContainsAny(string haystack, IReadOnlyList<string> keywords)
    {
        var normalized = Normalize(haystack);
        return keywords.Any(keyword => normalized.Contains(Normalize(keyword), StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string value)
    {
        return value.Trim()
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
