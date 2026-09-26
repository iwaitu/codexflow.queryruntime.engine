using System.Reflection;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Models.Diagnostics;

/// <summary>Verification status of one Provider × API-mode cell.</summary>
public static class QreCapabilityStatus
{
    /// <summary>Behavior pinned by offline tests against the locked SDK version.</summary>
    public const string Verified = "verified";

    /// <summary>The selector rejects the cell, or no safe capture exists.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>Not verified for the running SDK version; coverage must not be inferred.</summary>
    public const string Unverified = "unverified";
}

/// <summary>
/// Evidence-backed transport facts for one Provider × API-mode cell. Every value
/// here is asserted by <c>SdkTransportCapabilityTests</c> against the locked
/// VllmChatClient version; nothing is inferred from provider names.
/// </summary>
public sealed record QreTransportCapability
{
    public required string ProviderId { get; init; }

    public required string ApiMode { get; init; }

    public required string Status { get; init; }

    public required bool SelectorAccepts { get; init; }

    public string? RouteTemplate { get; init; }

    /// <summary>Whether the SDK sends through the injected HttpClient (and so through the diagnostic handler).</summary>
    public bool InjectedTransportUsed { get; init; }

    /// <summary>Handler-visible sends per streaming call, including on HTTP failure.</summary>
    public int VisibleSendsPerStreamingCall { get; init; }

    /// <summary>Retry behavior observed in the streaming path (the SDK's malformed-tool retry is non-streaming only).</summary>
    public string StreamingRetry { get; init; } = "none_observed";

    /// <summary>Request content behavior: deferred JSON content whose length is not computed up front.</summary>
    public string RequestContent { get; init; } = "json_content_deferred_length_unknown";

    /// <summary>Whether disposing the chat client disposes the injected HttpClient.</summary>
    public bool DisposesInjectedHttpClient { get; init; }

    /// <summary>Whether the SDK writes auth/default headers onto the injected HttpClient.</summary>
    public bool MutatesInjectedDefaultHeaders { get; init; }

    /// <summary>Which finish reasons the streaming path surfaces to MEAI.</summary>
    public string StreamingFinishReason { get; init; } = "tool_calls_only";

    /// <summary>Registered SDK request rewrites; used as comparison rules with this evidence.</summary>
    public IReadOnlyList<string> RegisteredTransforms { get; init; } = [];

    /// <summary>Observed SDK losses of explicit constraints. They remain unexpected changes in comparisons.</summary>
    public IReadOnlyList<string> KnownLimitations { get; init; } = [];

    public string Evidence { get; init; } = "CodexFlow.QueryRuntime.UnitTests/Models/Diagnostics/SdkTransportCapabilityTests";
}

/// <summary>
/// Provider × API-mode capability matrix for the locked SDK. When the running
/// SDK version differs from <see cref="VerifiedSdkVersion"/>, every accepted
/// cell reports <see cref="QreCapabilityStatus.Unverified"/>.
/// </summary>
public static class QreTransportCapabilityMatrix
{
    public const string VerifiedSdkVersion = "2.0.25";

    public const string SharedHostHttpClientScenario = QreCapabilityStatus.Unsupported;

    public static string SdkVersion { get; } = ResolveSdkVersion();

    public static bool IsVerifiedSdk => string.Equals(SdkVersion, VerifiedSdkVersion, StringComparison.Ordinal);

    public const string TemperatureNestedOptions = "chat_temperature_nested_under_options";
    public const string ChatLegacyJsonFormatField = "chat_legacy_format_json_field";
    public const string StructuredOutputsExtraBody = "structured_outputs_extra_field";
    public const string MaxTokensDefaultWhenUnspecified = "max_tokens_default_8192_when_unspecified";
    public const string SdkInjectedSystemPrompt = "sdk_injected_leading_system_prompt";
    public const string SystemHoistedToTopLevel = "system_hoisted_to_top_level";
    public const string ReasoningFlagsExtraBody = "reasoning_flags_extra_fields";

    public const string MaxTokensDropped = "max_tokens_dropped";
    public const string ResponseFormatDropped = "response_format_dropped";
    public const string ToolChoiceDropped = "tool_choice_dropped";
    public const string JsonFormatUnsupportedByProtocol = "json_response_format_unsupported_by_protocol";

    private static readonly string[] Providers =
        ["openai-gpt-oss", "openai-gpt", "gemini", "claude", "kimi", "minimax", "glm", "qwen", "deepseek"];

    public static IReadOnlyList<QreTransportCapability> All { get; } = Build();

    public static QreTransportCapability Get(string providerId, string apiMode)
        => All.FirstOrDefault(cell =>
               string.Equals(cell.ProviderId, providerId, StringComparison.Ordinal) &&
               string.Equals(cell.ApiMode, apiMode, StringComparison.Ordinal))
           ?? new QreTransportCapability
           {
               ProviderId = providerId,
               ApiMode = apiMode,
               Status = QreCapabilityStatus.Unverified,
               SelectorAccepts = false
           };

    private static List<QreTransportCapability> Build()
    {
        var cells = new List<QreTransportCapability>();
        foreach (var provider in Providers)
        {
            foreach (var mode in new[] { QreApiModeNames.ChatCompletions, QreApiModeNames.Responses, QreApiModeNames.AnthropicMessages })
            {
                if (provider == "gemini" && mode != QreApiModeNames.ChatCompletions)
                {
                    // GeminiModelProvider declares ChatCompletions only; the selector rejects the cell.
                    cells.Add(new QreTransportCapability
                    {
                        ProviderId = provider,
                        ApiMode = mode,
                        Status = QreCapabilityStatus.Unsupported,
                        SelectorAccepts = false
                    });
                    continue;
                }

                var transforms = new List<string>();
                var limitations = new List<string>();
                var reasoningFlags = provider is "openai-gpt-oss" or "openai-gpt";
                switch (mode)
                {
                    case QreApiModeNames.ChatCompletions:
                        transforms.Add(TemperatureNestedOptions);
                        transforms.Add(ChatLegacyJsonFormatField);
                        if (provider != "gemini")
                        {
                            transforms.Add(StructuredOutputsExtraBody);
                        }
                        if (provider == "claude")
                        {
                            transforms.Add(MaxTokensDefaultWhenUnspecified);
                        }
                        if (provider == "gemini")
                        {
                            limitations.Add(MaxTokensDropped);
                            limitations.Add(ResponseFormatDropped);
                            limitations.Add(ToolChoiceDropped);
                        }
                        if (provider == "deepseek")
                        {
                            limitations.Add(ResponseFormatDropped);
                        }
                        break;
                    case QreApiModeNames.Responses:
                        transforms.Add(StructuredOutputsExtraBody);
                        if (provider == "deepseek")
                        {
                            limitations.Add(ResponseFormatDropped);
                        }
                        break;
                    case QreApiModeNames.AnthropicMessages:
                        transforms.Add(MaxTokensDefaultWhenUnspecified);
                        transforms.Add(SystemHoistedToTopLevel);
                        limitations.Add(JsonFormatUnsupportedByProtocol);
                        break;
                }
                if (reasoningFlags && mode != QreApiModeNames.AnthropicMessages)
                {
                    transforms.Add(ReasoningFlagsExtraBody);
                }
                if (provider == "openai-gpt-oss")
                {
                    transforms.Add(SdkInjectedSystemPrompt);
                }

                cells.Add(new QreTransportCapability
                {
                    ProviderId = provider,
                    ApiMode = mode,
                    Status = IsVerifiedSdk ? QreCapabilityStatus.Verified : QreCapabilityStatus.Unverified,
                    SelectorAccepts = true,
                    RouteTemplate = mode switch
                    {
                        QreApiModeNames.Responses => "{endpoint}/responses",
                        QreApiModeNames.AnthropicMessages => "{endpoint}/messages",
                        _ => "{endpoint}/chat/completions"
                    },
                    InjectedTransportUsed = true,
                    VisibleSendsPerStreamingCall = 1,
                    DisposesInjectedHttpClient = true,
                    MutatesInjectedDefaultHeaders = true,
                    RegisteredTransforms = transforms,
                    KnownLimitations = limitations
                });
            }
        }
        return cells;
    }

    private static string ResolveSdkVersion()
    {
        var assembly = typeof(VllmBaseChatClient).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }
        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
