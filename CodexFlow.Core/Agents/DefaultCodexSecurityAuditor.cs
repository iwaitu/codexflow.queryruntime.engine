using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Constants;
using System.Diagnostics.CodeAnalysis;

namespace CodexFlow.Core.Agents;

public partial class DefaultCodexSecurityAuditor : ICodexSecurityAuditor
{
    private readonly ICodexAgentKernel _kernel;
    private readonly ILogger<DefaultCodexSecurityAuditor> _logger;
    private const int MaxParseRetries = 1;
    private string? _lastParseError;

    public DefaultCodexSecurityAuditor(
        ICodexAgentKernel kernel,
        ILogger<DefaultCodexSecurityAuditor> logger)
    {
        _kernel = kernel;
        _logger = logger;
    }

#pragma warning disable CA1068 // Preserve legacy public parameter order for compatibility.
    public async Task<SecurityAuditResult> AuditAsync(CodexSession session, string targetPath, CancellationToken ct = default, IEnumerable<string>? changedFiles = null)
#pragma warning restore CA1068
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(targetPath);

        StructuredLog.Information(_logger, "[Ivilson-Guard] Starting security audit for: {Target}", targetPath);

        var activeTask = !string.IsNullOrWhiteSpace(session.ActiveTaskId)
            ? session.Plan?.FirstOrDefault(t => string.Equals(t.Id, session.ActiveTaskId, StringComparison.OrdinalIgnoreCase))
            : null;

        var prompt = CodexPrompts.GetSecurityAuditorPrompt(targetPath, changedFiles, activeTask);

        // Execute Audit with retry on parse failure
        var result = await _kernel.RunLoopAsync(session, prompt, CodexAgentRole.Security, ct).ConfigureAwait(false);

        if (!result.IsComplete)
        {
            StructuredLog.Error(_logger, "[Ivilson-Guard] Security auditor kernel execution failed/incomplete.");
            return new SecurityAuditResult(
                false,
                "Security auditor kernel execution failed",
                new List<string> { "[System] Security auditor kernel returned incomplete result." },
                "");
        }

        var auditData = await TryParseAuditResponseAsync(session, result, ct).ConfigureAwait(false);

        if (auditData == null)
        {
            // All parse retries exhausted — this is an infrastructure/platform failure, NOT a
            // security vulnerability. Return explicit infra failure markers so the orchestrator
            // can distinguish it from a real security finding and skip the self-healing loop.
            StructuredLog.Error(_logger, "[Ivilson-Guard] Security auditor parse failure — returning infra failure result.");
            return new SecurityAuditResult(
                false,
                "Security auditor parse failure",
                new List<string> { "[System] Parse failure" },
                "");
        }

        return new SecurityAuditResult(
            auditData.IsPassed,
            auditData.Summary ?? "Audit completed",
            auditData.Risks ?? new List<string>(),
            "",
            auditData.LegacyRisks ?? new List<string>(),
            auditData.DeferredRisks ?? new List<string>()
        );
    }

    /// <summary>
    /// Attempt to parse the LLM response as an AuditDto. Retries up to MaxParseRetries times
    /// by feeding the parsing error back to the LLM for correction.
    /// </summary>
    private async Task<AuditDto?> TryParseAuditResponseAsync(CodexSession session, CodexResponse result, CancellationToken ct)
    {
        var lastRawText = result.Text;

        for (int attempt = 0; attempt <= MaxParseRetries; attempt++)
        {
            if (attempt > 0)
            {
                StructuredLog.Warning(_logger,
                    "[Ivilson-Guard] Parse attempt {Attempt}/{Max} failed, requesting LLM correction. Raw length: {RawLength}",
                    attempt + 1, MaxParseRetries + 1, lastRawText?.Length ?? 0);

                var retryPrompt = $@"你的上一次回复无法解析为有效的 JSON。请严格按以下格式重新输出纯 JSON（不要包含 ```json 或任何其他 Markdown 标记）：
{{
  ""IsPassed"": true,
  ""Summary"": ""一句话总结"",
  ""Risks"": [],
  ""DeferredRisks"": [],
  ""LegacyRisks"": []
}}

上次回复内容（截断）：{Truncate(lastRawText, 500)}

错误信息：{_lastParseError ?? "JSON 解析失败"}

请直接输出 JSON，不要有任何额外文字。";

                var retryResult = await _kernel.RunLoopAsync(session, retryPrompt, CodexAgentRole.Security, ct).ConfigureAwait(false);
                if (!retryResult.IsComplete)
                {
                    StructuredLog.Error(_logger, "[Ivilson-Guard] Retry attempt {Attempt} also returned incomplete.", attempt + 1);
                    return null;
                }
                lastRawText = retryResult.Text;
            }

            var parseResult = TryExtractJson(lastRawText);
            if (parseResult.IsOk)
            {
                return parseResult.Data;
            }
            _lastParseError = parseResult.ErrorMessage;
        }

        // All retries exhausted — fail closed
        StructuredLog.Error(_logger,
            "[Ivilson-Guard] All {MaxAttempts} parse attempts exhausted. Raw (first 300 chars): {RawPreview}",
            MaxParseRetries + 1, Truncate(lastRawText, 300));
        return null;
    }

    /// <summary>
    /// Extract and deserialize JSON from LLM output. Handles markdown code blocks,
    /// surrounding text, and nested JSON objects.
    /// </summary>
    private static ParseResult<AuditDto?> TryExtractJson(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return ParseResult<AuditDto?>.Fail("Empty response from LLM");
        }

        // Step 1: Strip Markdown code fences (```json, ```, etc.)
        var cleaned = MarkdownCodeFenceRegex().Replace(rawText, string.Empty).Trim();

        // Step 2: Find the most likely JSON object by matching braces from the first '{'
        var jsonCandidate = ExtractBalancedJson(cleaned);
        if (jsonCandidate == null)
        {
            return ParseResult<AuditDto?>.Fail("No balanced JSON object found");
        }

        // Step 3: Validate it's not just empty braces
        var trimmed = jsonCandidate.Trim();
        if (trimmed == "{}" || string.IsNullOrWhiteSpace(trimmed))
        {
            return ParseResult<AuditDto?>.Fail("Empty JSON object (no audit data)");
        }

        // Step 4: Deserialize with lenient options
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
            };
            var auditData = System.Text.Json.JsonSerializer.Deserialize<AuditDto>(trimmed, options);
            if (auditData == null)
            {
                return ParseResult<AuditDto?>.Fail("Deserialized to null (JSON structure mismatch)");
            }
            return ParseResult<AuditDto?>.Ok(auditData);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ParseResult<AuditDto?>.Fail($"JSON parse error: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            return ParseResult<AuditDto?>.Fail($"JSON not supported: {ex.Message}");
        }
    }

    /// <summary>
    /// Extract a balanced JSON object starting from the first '{' character.
    /// Tracks brace depth to find the matching closing '}'.
    /// </summary>
    private static string? ExtractBalancedJson(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = start; i < text.Length; i++)
        {
            var ch = text[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (ch == '\\')
            {
                escape = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString) continue;

            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, i - start + 1);
                }
            }
        }

        return null; // Unbalanced braces
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by System.Text.Json deserialization.")]
    private sealed class AuditDto
    {
        public bool IsPassed { get; set; }
        public string? Summary { get; set; }
        public List<string>? Risks { get; set; }
        public List<string>? LegacyRisks { get; set; }
        public List<string>? DeferredRisks { get; set; }
    }

    private readonly record struct ParseResult<T>(bool IsOk, T Data, string? ErrorMessage)
    {
        public static ParseResult<T> Ok(T data) => new(true, data, null);
        public static ParseResult<T> Fail(string error) => new(false, default!, error);
    }

    [GeneratedRegex(@"```(?:json)?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownCodeFenceRegex();
}

