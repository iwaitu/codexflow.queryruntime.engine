using CodexFlow.QueryRuntime.Abstractions;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public static class QreModelExecutionPolicy
{
    public static ChatOptions Apply(
        ChatOptions? options,
        bool toolsEnabled,
        bool structuredOutputRequired,
        QreThinkingPolicy thinkingPolicy)
    {
        var shouldDisableThinking = thinkingPolicy == QreThinkingPolicy.ForceDisabled ||
            (thinkingPolicy == QreThinkingPolicy.Auto && (toolsEnabled || structuredOutputRequired || options?.ResponseFormat != null));
        var shouldEnableThinking = thinkingPolicy == QreThinkingPolicy.ForceEnabled;

        if (!shouldDisableThinking && !shouldEnableThinking)
        {
            return options is VllmChatOptions
                ? CopyToVllmOptions(options)
                : options ?? new ChatOptions();
        }

        var runtimeOptions = CopyToVllmOptions(options);
        runtimeOptions.ThinkingEnabled = shouldEnableThinking;
        return runtimeOptions;
    }

    private static VllmChatOptions CopyToVllmOptions(ChatOptions? source)
    {
        var destination = new VllmChatOptions();
        if (source == null)
        {
            return destination;
        }

        destination.ConversationId = source.ConversationId;
        destination.Instructions = source.Instructions;
        destination.Temperature = source.Temperature;
        destination.MaxOutputTokens = source.MaxOutputTokens;
        destination.TopP = source.TopP;
        destination.TopK = source.TopK;
        destination.FrequencyPenalty = source.FrequencyPenalty;
        destination.PresencePenalty = source.PresencePenalty;
        destination.Seed = source.Seed;
        destination.Reasoning = source.Reasoning;
        destination.ResponseFormat = source.ResponseFormat;
        destination.ModelId = source.ModelId;
        destination.StopSequences = source.StopSequences?.ToArray();
        destination.AllowMultipleToolCalls = source.AllowMultipleToolCalls;
        destination.ToolMode = source.ToolMode;
        destination.Tools = source.Tools?.ToList();
        destination.RawRepresentationFactory = source.RawRepresentationFactory;
        destination.AdditionalProperties = source.AdditionalProperties is null
            ? null
            : new AdditionalPropertiesDictionary(source.AdditionalProperties);

        if (source is VllmChatOptions vllmSource)
        {
            destination.ThinkingEnabled = vllmSource.ThinkingEnabled;
            destination.EnableSkills = vllmSource.EnableSkills;
            destination.SkillDirectoryPath = vllmSource.SkillDirectoryPath;
            destination.EnableLegacyToolCallTextFallback = vllmSource.EnableLegacyToolCallTextFallback;
        }

        return destination;
    }
}
