using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Governance;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace CodexFlow.Core.Services;

/// <summary>
/// Phase 7: Default implementation of <see cref="ILLMExecutor"/>.
/// <para>
/// When <see cref="LLMExecutionRequest.Session"/> is provided the executor
/// calls <see cref="IMemoryContextAssembler.AssembleAsync"/> and, if the
/// resulting envelope is non-empty, prepends a fenced memory block to the
/// first system message (or inserts a new system message at position 0).
/// </para>
/// <para>
/// Actual transport is always delegated to the injected <see cref="IChatClient"/>,
/// so retries, circuit breakers, and tool-call loops remain in the calling agent.
/// </para>
/// </summary>
[ApprovedNonStreamingLlm(
    "Facade over IChatClient for governed non-streaming call sites that still require prompt enrichment.",
    Ticket = "non-streaming-llm-migration",
    ReviewBy = "2026-06-30",
    Scope = ApprovedNonStreamingLlmScope.Facade)]
public sealed class DefaultLLMExecutor(
    IChatClient chatClient,
    IMemoryContextAssembler memoryAssembler,
    ILogger<DefaultLLMExecutor> logger) : ILLMExecutor
{
    /// <inheritdoc/>
    public async Task<ChatResponse> ExecuteAsync(
        LLMExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = await EnrichMessagesAsync(request, ct).ConfigureAwait(false);
        return await chatClient.GetResponseAsync(messages, request.Options, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        LLMExecutionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = await EnrichMessagesAsync(request, ct).ConfigureAwait(false);
        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, request.Options, ct)
                                               .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<List<ChatMessage>> EnrichMessagesAsync(
        LLMExecutionRequest request,
        CancellationToken ct)
    {
        if (request.Session == null)
            return [.. request.Messages];

        var envelope = await memoryAssembler.AssembleAsync(
            new MemoryContextRequest(request.Session, request.Scenario),
            ct).ConfigureAwait(false);

        if (envelope.IsEmpty)
            return [.. request.Messages];

        var block = envelope.AsSystemPromptBlock();
        logger.LogDebug(
            "LLMExecutor [{Caller}]: Injecting memory context block ({Scenario}, {Chars} chars).",
            request.Caller ?? "unknown",
            request.Scenario,
            block.Length);

        return PrependToSystemMessage(request.Messages, block);
    }

    /// <summary>
    /// Prepends <paramref name="prefix"/> to the first system message, or inserts a new
    /// system message at index 0 when the list starts with a non-system message.
    /// Returns a new list; the original is never mutated.
    /// </summary>
    private static List<ChatMessage> PrependToSystemMessage(
        IReadOnlyList<ChatMessage> messages,
        string prefix)
    {
        var result = new List<ChatMessage>(messages.Count + 1);

        if (messages.Count > 0 && messages[0].Role == ChatRole.System)
        {
            // Merge prefix into existing system message.
            var existing = messages[0];
            var merged = prefix + "\n\n" + (existing.Text ?? string.Empty);
            result.Add(new ChatMessage(ChatRole.System, merged));
            result.AddRange(messages.Skip(1));
        }
        else
        {
            // No leading system message — insert one.
            result.Add(new ChatMessage(ChatRole.System, prefix));
            result.AddRange(messages);
        }

        return result;
    }
}
