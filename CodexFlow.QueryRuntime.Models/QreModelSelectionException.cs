namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// Base type for explicit, recoverable model-adapter selection failures. These
/// are surfaced as clear CLI errors rather than leaking provider-internal
/// exceptions or silently falling back to an assumed provider shape.
/// </summary>
public abstract class QreModelSelectionException : Exception
{
    private protected QreModelSelectionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown when no registered provider adapter claims the requested model.
/// </summary>
public sealed class QreUnknownModelException : QreModelSelectionException
{
    public QreUnknownModelException(string model, IReadOnlyCollection<string> knownProviders)
        : base($"No model adapter handles model '{model}'. " +
               $"Known adapters: {string.Join(", ", knownProviders)}. " +
               "Pass a supported --model, or add a provider adapter.")
    {
        Model = model;
        KnownProviders = knownProviders;
    }

    /// <summary>The unresolved model identifier.</summary>
    public string Model { get; }

    /// <summary>Identifiers of the registered provider adapters.</summary>
    public IReadOnlyCollection<string> KnownProviders { get; }
}

/// <summary>
/// Thrown when the selected provider adapter does not support the requested
/// <see cref="QreModelApiMode"/>.
/// </summary>
public sealed class QreUnsupportedApiModeException : QreModelSelectionException
{
    public QreUnsupportedApiModeException(
        string model,
        string providerId,
        QreModelApiMode requestedMode,
        IReadOnlyCollection<QreModelApiMode> supportedModes)
        : base($"Model adapter '{providerId}' (model '{model}') does not support api-mode '{requestedMode}'. " +
               $"Supported: {string.Join(", ", supportedModes)}.")
    {
        Model = model;
        ProviderId = providerId;
        RequestedMode = requestedMode;
        SupportedModes = supportedModes;
    }

    /// <summary>The requested model identifier.</summary>
    public string Model { get; }

    /// <summary>The adapter that owns the model.</summary>
    public string ProviderId { get; }

    /// <summary>The unsupported api-mode that was requested.</summary>
    public QreModelApiMode RequestedMode { get; }

    /// <summary>The api-modes the adapter does support.</summary>
    public IReadOnlyCollection<QreModelApiMode> SupportedModes { get; }
}

/// <summary>
/// Thrown when the textual api-mode value cannot be parsed into a
/// <see cref="QreModelApiMode"/>.
/// </summary>
public sealed class QreUnsupportedApiModeValueException : QreModelSelectionException
{
    public QreUnsupportedApiModeValueException(string value, string detail)
        : base($"Unsupported QRE api mode value '{value}'. {detail}".TrimEnd())
    {
        Value = value;
    }

    /// <summary>The unparseable api-mode text.</summary>
    public string Value { get; }
}
