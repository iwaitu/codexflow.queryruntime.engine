using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// An explicit, provider-neutral model adapter. Each provider knows which model
/// identifiers it owns, which <see cref="QreModelApiMode"/> shapes it supports,
/// and how to construct the corresponding <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// Providers replace the previous CLI-local, name-based heuristics. Selection is
/// explicit and fail-closed: a model that no provider claims, or an api-mode a
/// provider does not support, produces a clear error rather than a silent
/// fallback to an assumed endpoint shape.
/// </remarks>
public interface IQreModelProvider
{
    /// <summary>Stable adapter identifier used in diagnostics and errors.</summary>
    string Id { get; }

    /// <summary>Wire shapes this adapter can speak.</summary>
    IReadOnlyCollection<QreModelApiMode> SupportedApiModes { get; }

    /// <summary>
    /// Returns <c>true</c> when this adapter owns <paramref name="normalizedModel"/>.
    /// The selector passes a trimmed, lower-invariant model identifier.
    /// </summary>
    bool CanHandle(string normalizedModel);

    /// <summary>
    /// Constructs the chat client. Callers must ensure <see cref="CanHandle"/> is
    /// <c>true</c> and the requested mode is in <see cref="SupportedApiModes"/>;
    /// the selector enforces both before calling this method.
    /// </summary>
    IChatClient CreateClient(QreModelClientDescriptor descriptor);
}
