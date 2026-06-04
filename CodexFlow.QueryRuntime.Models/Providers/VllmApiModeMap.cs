using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Models.Providers;

/// <summary>
/// Maps the provider-neutral <see cref="QreModelApiMode"/> onto the
/// <see cref="VllmApiMode"/> understood by the underlying VllmChatClient package.
/// </summary>
internal static class VllmApiModeMap
{
    public static VllmApiMode ToVllmApiMode(this QreModelApiMode mode) => mode switch
    {
        QreModelApiMode.ChatCompletions => VllmApiMode.ChatCompletions,
        QreModelApiMode.Responses => VllmApiMode.Responses,
        QreModelApiMode.AnthropicMessages => VllmApiMode.AnthropicMessages,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unmapped QreModelApiMode.")
    };
}
