using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Models.Providers;

/// <summary>
/// Base class for model adapters backed by the VllmChatClient package. Concrete
/// adapters declare which model identifiers they own and how to construct the
/// underlying client; api-mode support and argument plumbing are shared here.
/// </summary>
public abstract class VllmModelProvider : IQreModelProvider
{
    /// <summary>The full set of wire shapes the VllmChatClient package exposes.</summary>
    protected static readonly IReadOnlyCollection<QreModelApiMode> AllApiModes =
    [
        QreModelApiMode.ChatCompletions,
        QreModelApiMode.Responses,
        QreModelApiMode.AnthropicMessages
    ];

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public virtual IReadOnlyCollection<QreModelApiMode> SupportedApiModes => AllApiModes;

    /// <inheritdoc />
    public abstract bool CanHandle(string normalizedModel);

    /// <inheritdoc />
    public IChatClient CreateClient(QreModelClientDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return CreateClientCore(descriptor, descriptor.ApiUrl.ToString());
    }

    /// <summary>Constructs the underlying client for a validated descriptor.</summary>
    protected abstract IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText);
}
