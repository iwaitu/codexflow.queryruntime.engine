namespace CodexFlow.Core.LanguageServices;

public enum SupportedLanguage
{
    Unknown = 0,
    CSharp = 1,
    Python = 2,
    TypeScript = 3,
    Java = 4
}

public enum LanguageServiceCapability
{
    None = 0,
    GetDiagnostics = 1,
    DocumentSymbols = 2,
    WorkspaceSymbols = 3,
    GoToDefinition = 4,
    FindReferences = 5
}

public enum LanguageServiceStatus
{
    Active = 0,
    Degraded = 1
}

public enum LanguageServiceDegradedReason
{
    None = 0,
    BinaryMissing = 1,
    ServerStartFailed = 2,
    InitializationTimedOut = 3,
    ProtocolError = 4,
    UnsupportedLanguage = 5
}

public enum LanguageServiceDiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public sealed record LanguageServiceProviderDescriptor
{
    public required SupportedLanguage Language { get; init; }
    public required string ProviderName { get; init; }
    public required IReadOnlyList<string> Extensions { get; init; }
    public required IReadOnlyList<LanguageServiceCapability> Capabilities { get; init; }
    public required LanguageServiceStatus Status { get; init; }
    public required double InitializationLatencyMs { get; init; }
    public LanguageServiceDegradedReason DegradedReason { get; init; }
}

public sealed record LanguageServiceSessionKey
{
    public required string WorkspacePath { get; init; }
    public required string WorkerId { get; init; }
    public required SupportedLanguage Language { get; init; }
}

public sealed record LanguageServiceDocumentDiagnostic
{
    public required string RelativePath { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required LanguageServiceDiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? Code { get; init; }
}

public sealed record LanguageServiceSymbolInfo
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string RelativePath { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
}

public sealed record LanguageServiceLocation
{
    public required string RelativePath { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public string? Preview { get; init; }
}

public sealed record LanguageServiceDiagnosticsResult
{
    public required LanguageServiceProviderDescriptor Descriptor { get; init; }
    public required IReadOnlyList<LanguageServiceDocumentDiagnostic> Diagnostics { get; init; }
}

public sealed record LanguageServiceRefreshRequest
{
    public required string WorkspacePath { get; init; }
    public required string WorkerId { get; init; }
    public required IReadOnlyList<string> RelativePaths { get; init; }
}

public sealed record LanguageServiceRuntimeProbeResult
{
    public bool Available { get; init; }
    public string? Command { get; init; }
    public LanguageServiceDegradedReason DegradedReason { get; init; }
}

public interface ILanguageServiceRuntimeProbe
{
    ValueTask<LanguageServiceRuntimeProbeResult> ProbeAsync(
        IReadOnlyList<string> commandCandidates,
        CancellationToken ct = default);
}

public interface ILanguageServiceProvider
{
    SupportedLanguage Language { get; }
    string ProviderName { get; }
    IReadOnlyList<string> Extensions { get; }
    IReadOnlyList<LanguageServiceCapability> Capabilities { get; }

    ValueTask<LanguageServiceProviderDescriptor> InitializeAsync(
        LanguageServiceSessionKey sessionKey,
        CancellationToken ct = default);

    ValueTask<LanguageServiceDiagnosticsResult> GetDiagnosticsAsync(
        LanguageServiceSessionKey sessionKey,
        LanguageServiceProviderDescriptor descriptor,
        string? relativePath = null,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<LanguageServiceSymbolInfo>> GetDocumentSymbolsAsync(
        LanguageServiceSessionKey sessionKey,
        string relativePath,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<LanguageServiceSymbolInfo>> WorkspaceSymbolsAsync(
        LanguageServiceSessionKey sessionKey,
        string query,
        CancellationToken ct = default);

    ValueTask<LanguageServiceLocation?> GoToDefinitionAsync(
        LanguageServiceSessionKey sessionKey,
        string relativePath,
        string symbol,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<LanguageServiceLocation>> FindReferencesAsync(
        LanguageServiceSessionKey sessionKey,
        string relativePath,
        string symbol,
        CancellationToken ct = default);
}

public interface ILanguageServiceRegistry
{
    IReadOnlyList<LanguageServiceProviderDescriptor> GetRegisteredProviders();

    SupportedLanguage DetectLanguage(string? languageOrPath);

    bool TryResolveProvider(
        SupportedLanguage language,
        out ILanguageServiceProvider provider);
}

public interface ILanguageServiceClient
{
    LanguageServiceSessionKey SessionKey { get; }

    LanguageServiceProviderDescriptor Descriptor { get; }

    ValueTask<LanguageServiceDiagnosticsResult> GetDiagnosticsAsync(
        string? relativePath = null,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<LanguageServiceSymbolInfo>> GetDocumentSymbolsAsync(
        string relativePath,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<LanguageServiceSymbolInfo>> WorkspaceSymbolsAsync(
        string query,
        CancellationToken ct = default);

    ValueTask<LanguageServiceLocation?> GoToDefinitionAsync(
        string relativePath,
        string symbol,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<LanguageServiceLocation>> FindReferencesAsync(
        string relativePath,
        string symbol,
        CancellationToken ct = default);

    ValueTask NotifyFilesChangedAsync(
        IReadOnlyList<string> relativePaths,
        CancellationToken ct = default);
}

public interface ILanguageServiceSessionFactory
{
    ValueTask<ILanguageServiceClient> GetOrCreateAsync(
        LanguageServiceSessionKey sessionKey,
        CancellationToken ct = default);
}

public interface ILanguageServiceRefreshNotifier
{
    ValueTask NotifyFilesChangedAsync(
        LanguageServiceRefreshRequest request,
        CancellationToken ct = default);
}
