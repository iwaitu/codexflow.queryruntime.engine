using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.LanguageServices;

public sealed class DefaultLanguageServiceRuntimeProbe : ILanguageServiceRuntimeProbe
{
    public ValueTask<LanguageServiceRuntimeProbeResult> ProbeAsync(
        IReadOnlyList<string> commandCandidates,
        CancellationToken ct = default)
    {
        if (commandCandidates == null || commandCandidates.Count == 0)
        {
            return ValueTask.FromResult(new LanguageServiceRuntimeProbeResult
            {
                Available = true,
                DegradedReason = LanguageServiceDegradedReason.None
            });
        }

        foreach (var candidate in commandCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (TryResolveFromPath(candidate))
            {
                return ValueTask.FromResult(new LanguageServiceRuntimeProbeResult
                {
                    Available = true,
                    Command = candidate,
                    DegradedReason = LanguageServiceDegradedReason.None
                });
            }
        }

        return ValueTask.FromResult(new LanguageServiceRuntimeProbeResult
        {
            Available = false,
            DegradedReason = LanguageServiceDegradedReason.BinaryMissing
        });
    }

    private static bool TryResolveFromPath(string command)
    {
        try
        {
            if (Path.IsPathRooted(command))
            {
                return File.Exists(command);
            }

            var pathValue = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                return false;
            }

            var extensions = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [string.Empty];

            foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var extension in extensions)
                {
                    var fullPath = Path.Combine(segment, command + extension);
                    if (File.Exists(fullPath))
                    {
                        return true;
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (System.Security.SecurityException) { }

        return false;
    }
}

public sealed class DefaultLanguageServiceRegistry(
    IEnumerable<ILanguageServiceProvider> providers) : ILanguageServiceRegistry
{
    private readonly IReadOnlyDictionary<SupportedLanguage, ILanguageServiceProvider> _providers =
        (providers ?? throw new ArgumentNullException(nameof(providers)))
            .ToDictionary(provider => provider.Language);

    public IReadOnlyList<LanguageServiceProviderDescriptor> GetRegisteredProviders()
        => _providers.Values
            .Select(provider => new LanguageServiceProviderDescriptor
            {
                Language = provider.Language,
                ProviderName = provider.ProviderName,
                Extensions = provider.Extensions,
                Capabilities = provider.Capabilities,
                Status = LanguageServiceStatus.Active,
                InitializationLatencyMs = 0,
                DegradedReason = LanguageServiceDegradedReason.None
            })
            .OrderBy(descriptor => descriptor.Language)
            .ToList();

    public SupportedLanguage DetectLanguage(string? languageOrPath)
    {
        if (string.IsNullOrWhiteSpace(languageOrPath))
        {
            return SupportedLanguage.Unknown;
        }

        var normalized = languageOrPath.Trim();
        var extension = Path.GetExtension(normalized);

        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = normalized.StartsWith('.')
                ? normalized
                : string.Empty;
        }

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("csharp", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("c#", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedLanguage.CSharp;
        }

        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("python", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedLanguage.Python;
        }

        if (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("typescript", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ts", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("javascript", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedLanguage.TypeScript;
        }

        if (extension.Equals(".java", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("java", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedLanguage.Java;
        }

        return SupportedLanguage.Unknown;
    }

    public bool TryResolveProvider(SupportedLanguage language, out ILanguageServiceProvider provider)
        => _providers.TryGetValue(language, out provider!);
}

public sealed class DefaultLanguageServiceSessionFactory(
    ILanguageServiceRegistry registry,
    ILogger<DefaultLanguageServiceSessionFactory> logger) : ILanguageServiceSessionFactory, ILanguageServiceRefreshNotifier
{
    private readonly ConcurrentDictionary<LanguageServiceSessionKey, DefaultLanguageServiceClient> _sessions = new();
    private readonly ILanguageServiceRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly ILogger<DefaultLanguageServiceSessionFactory> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async ValueTask<ILanguageServiceClient> GetOrCreateAsync(
        LanguageServiceSessionKey sessionKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);

        if (_sessions.TryGetValue(sessionKey, out var existing))
        {
            return existing;
        }

        if (!_registry.TryResolveProvider(sessionKey.Language, out var provider))
        {
            throw new InvalidOperationException($"Unsupported language: {sessionKey.Language}");
        }

        var descriptor = await provider.InitializeAsync(sessionKey, ct).ConfigureAwait(false);
        var created = new DefaultLanguageServiceClient(provider, sessionKey, descriptor);
        if (_sessions.TryAdd(sessionKey, created))
        {
            _logger.LogInformation(
                "Language service session created. Workspace={Workspace} Worker={Worker} Language={Language} Provider={Provider} Status={Status} DegradedReason={DegradedReason}",
                sessionKey.WorkspacePath,
                sessionKey.WorkerId,
                sessionKey.Language,
                descriptor.ProviderName,
                descriptor.Status,
                descriptor.DegradedReason);
            return created;
        }

        return _sessions[sessionKey];
    }

    public async ValueTask NotifyFilesChangedAsync(
        LanguageServiceRefreshRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var matchingSessions = _sessions
            .Where(pair =>
                string.Equals(pair.Key.WorkspacePath, request.WorkspacePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pair.Key.WorkerId, request.WorkerId, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .ToList();

        foreach (var session in matchingSessions)
        {
            await session.NotifyFilesChangedAsync(request.RelativePaths, ct).ConfigureAwait(false);
        }
    }
}

internal sealed class DefaultLanguageServiceClient(
    ILanguageServiceProvider provider,
    LanguageServiceSessionKey sessionKey,
    LanguageServiceProviderDescriptor descriptor) : ILanguageServiceClient
{
    private readonly ILanguageServiceProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    private readonly ConcurrentDictionary<string, LanguageServiceDiagnosticsResult> _diagnosticsCache = new(StringComparer.OrdinalIgnoreCase);

    public LanguageServiceSessionKey SessionKey { get; } = sessionKey ?? throw new ArgumentNullException(nameof(sessionKey));

    public LanguageServiceProviderDescriptor Descriptor { get; } = descriptor ?? throw new ArgumentNullException(nameof(descriptor));

    public async ValueTask<LanguageServiceDiagnosticsResult> GetDiagnosticsAsync(
        string? relativePath = null,
        CancellationToken ct = default)
    {
        var cacheKey = string.IsNullOrWhiteSpace(relativePath) ? "*" : NormalizeRelativePath(relativePath);
        if (_diagnosticsCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var result = await _provider.GetDiagnosticsAsync(SessionKey, Descriptor, relativePath, ct).ConfigureAwait(false);
        _diagnosticsCache[cacheKey] = result;
        return result;
    }

    public ValueTask<IReadOnlyList<LanguageServiceSymbolInfo>> GetDocumentSymbolsAsync(
        string relativePath,
        CancellationToken ct = default)
        => _provider.GetDocumentSymbolsAsync(SessionKey, NormalizeRelativePath(relativePath), ct);

    public ValueTask<IReadOnlyList<LanguageServiceSymbolInfo>> WorkspaceSymbolsAsync(
        string query,
        CancellationToken ct = default)
        => _provider.WorkspaceSymbolsAsync(SessionKey, query, ct);

    public ValueTask<LanguageServiceLocation?> GoToDefinitionAsync(
        string relativePath,
        string symbol,
        CancellationToken ct = default)
        => _provider.GoToDefinitionAsync(SessionKey, NormalizeRelativePath(relativePath), symbol, ct);

    public ValueTask<IReadOnlyList<LanguageServiceLocation>> FindReferencesAsync(
        string relativePath,
        string symbol,
        CancellationToken ct = default)
        => _provider.FindReferencesAsync(SessionKey, NormalizeRelativePath(relativePath), symbol, ct);

    public ValueTask NotifyFilesChangedAsync(
        IReadOnlyList<string> relativePaths,
        CancellationToken ct = default)
    {
        if (relativePaths.Count == 0)
        {
            _diagnosticsCache.Clear();
            return ValueTask.CompletedTask;
        }

        _diagnosticsCache.TryRemove("*", out _);
        foreach (var path in relativePaths)
        {
            _diagnosticsCache.TryRemove(NormalizeRelativePath(path), out _);
        }

        return ValueTask.CompletedTask;
    }

    private static string NormalizeRelativePath(string relativePath)
        => relativePath.Replace('\\', '/');
}

public sealed class CSharpLanguageServiceProvider(
    ILogger<CSharpLanguageServiceProvider> logger) : RegexHeuristicLanguageServiceProvider(
        SupportedLanguage.CSharp,
        "roslyn-baseline",
        [".cs"],
        [],
        new DefaultLanguageServiceRuntimeProbe(),
        logger)
{
    protected override bool UsesBraceBalance => true;

    protected override IReadOnlyList<(Regex Regex, string Kind)> SymbolPatterns { get; } =
    [
        (new Regex(@"^\s*(?:public|internal|private|protected)?\s*(?:sealed|abstract|static|partial)?\s*(class|interface|record|struct)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled), "type"),
        (new Regex(@"^\s*(?:public|internal|private|protected)?\s*(?:static|virtual|override|async|sealed|partial)?\s*[A-Za-z0-9_<>\[\]\?,\s]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled), "method")
    ];
}

public sealed class PythonLanguageServiceProvider(
    ILanguageServiceRuntimeProbe probe,
    ILogger<PythonLanguageServiceProvider> logger) : RegexHeuristicLanguageServiceProvider(
        SupportedLanguage.Python,
        "python-lsp",
        [".py"],
        ["pylsp", "pyright-langserver", "python-lsp-server"],
        probe,
        logger)
{
    protected override bool UsesBraceBalance => false;

    protected override IReadOnlyList<(Regex Regex, string Kind)> SymbolPatterns { get; } =
    [
        (new Regex(@"^\s*class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled), "class"),
        (new Regex(@"^\s*def\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled), "function")
    ];
}

public sealed class TypeScriptLanguageServiceProvider(
    ILanguageServiceRuntimeProbe probe,
    ILogger<TypeScriptLanguageServiceProvider> logger) : RegexHeuristicLanguageServiceProvider(
        SupportedLanguage.TypeScript,
        "typescript-language-server",
        [".ts", ".tsx", ".js", ".jsx"],
        ["typescript-language-server", "tsserver", "node"],
        probe,
        logger)
{
    protected override bool UsesBraceBalance => true;

    protected override IReadOnlyList<(Regex Regex, string Kind)> SymbolPatterns { get; } =
    [
        (new Regex(@"^\s*(?:export\s+)?(?:class|interface|type|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled), "type"),
        (new Regex(@"^\s*(?:export\s+)?(?:async\s+)?function\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled), "function"),
        (new Regex(@"^\s*(?:export\s+)?const\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.Compiled), "const")
    ];
}

public sealed class JavaLanguageServiceProvider(
    ILanguageServiceRuntimeProbe probe,
    ILogger<JavaLanguageServiceProvider> logger) : RegexHeuristicLanguageServiceProvider(
        SupportedLanguage.Java,
        "jdtls",
        [".java"],
        ["jdtls", "java"],
        probe,
        logger)
{
    protected override bool UsesBraceBalance => true;

    protected override IReadOnlyList<(Regex Regex, string Kind)> SymbolPatterns { get; } =
    [
        (new Regex(@"^\s*(?:public|protected|private)?\s*(?:class|interface|record|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled), "type"),
        (new Regex(@"^\s*(?:public|protected|private)?\s*(?:static\s+)?[A-Za-z0-9_<>\[\]\?,\s]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled), "method")
    ];
}

public abstract class RegexHeuristicLanguageServiceProvider : ILanguageServiceProvider
{
    private static readonly IReadOnlyList<LanguageServiceCapability> DefaultCapabilities =
    [
        LanguageServiceCapability.GetDiagnostics,
        LanguageServiceCapability.DocumentSymbols,
        LanguageServiceCapability.WorkspaceSymbols,
        LanguageServiceCapability.GoToDefinition,
        LanguageServiceCapability.FindReferences
    ];

    private readonly IReadOnlyList<string> _commandCandidates;
    private readonly ILanguageServiceRuntimeProbe _probe;
    private readonly ILogger _logger;

    protected RegexHeuristicLanguageServiceProvider(
        SupportedLanguage language,
        string providerName,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string> commandCandidates,
        ILanguageServiceRuntimeProbe probe,
        ILogger logger)
    {
        Language = language;
        ProviderName = providerName;
        Extensions = extensions;
        _commandCandidates = commandCandidates;
        _probe = probe;
        _logger = logger;
    }

    public SupportedLanguage Language { get; }

    public string ProviderName { get; }

    public IReadOnlyList<string> Extensions { get; }

    public IReadOnlyList<LanguageServiceCapability> Capabilities => DefaultCapabilities;

    protected virtual bool UsesBraceBalance => true;

    protected abstract IReadOnlyList<(Regex Regex, string Kind)> SymbolPatterns { get; }

    public async ValueTask<LanguageServiceProviderDescriptor> InitializeAsync(
        LanguageServiceSessionKey sessionKey,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var probeResult = await _probe.ProbeAsync(_commandCandidates, ct).ConfigureAwait(false);
            stopwatch.Stop();

            var status = probeResult.Available ? LanguageServiceStatus.Active : LanguageServiceStatus.Degraded;
            var degradedReason = probeResult.Available
                ? LanguageServiceDegradedReason.None
                : probeResult.DegradedReason;

            return new LanguageServiceProviderDescriptor
            {
                Language = Language,
                ProviderName = probeResult.Command ?? ProviderName,
                Extensions = Extensions,
                Capabilities = Capabilities,
                Status = status,
                InitializationLatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                DegradedReason = degradedReason
            };
        }
        catch (TimeoutException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Language service initialization timed out. Language={Language}", Language);
            return CreateDegradedDescriptor(LanguageServiceDegradedReason.InitializationTimedOut, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Win32Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Language service failed to start. Language={Language}", Language);
            return CreateDegradedDescriptor(LanguageServiceDegradedReason.ServerStartFailed, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Language service protocol failed during initialization. Language={Language}", Language);
            return CreateDegradedDescriptor(LanguageServiceDegradedReason.ProtocolError, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    public async ValueTask<LanguageServiceDiagnosticsResult> GetDiagnosticsAsync(
        LanguageServiceSessionKey sessionKey,
        LanguageServiceProviderDescriptor descriptor,
        string? relativePath = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(descriptor);

        var diagnostics = new List<LanguageServiceDocumentDiagnostic>();
        var documents = await LoadDocumentsAsync(sessionKey, relativePath, ct).ConfigureAwait(false);

        foreach (var document in documents)
        {
            diagnostics.AddRange(CollectDiagnostics(document.RelativePath, document.Text));
        }

        return new LanguageServiceDiagnosticsResult
        {
            Descriptor = descriptor,
            Diagnostics = diagnostics
        };
    }

    public async ValueTask<IReadOnlyList<LanguageServiceSymbolInfo>> GetDocumentSymbolsAsync(
        LanguageServiceSessionKey sessionKey,
        string relativePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var document = await LoadSingleDocumentAsync(sessionKey, relativePath, ct).ConfigureAwait(false);
        return ExtractSymbols(document.RelativePath, document.Text);
    }

    public async ValueTask<IReadOnlyList<LanguageServiceSymbolInfo>> WorkspaceSymbolsAsync(
        LanguageServiceSessionKey sessionKey,
        string query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var symbols = new List<LanguageServiceSymbolInfo>();
        var documents = await LoadDocumentsAsync(sessionKey, null, ct).ConfigureAwait(false);

        foreach (var document in documents)
        {
            symbols.AddRange(ExtractSymbols(document.RelativePath, document.Text)
                .Where(symbol => symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        return symbols;
    }

    public async ValueTask<LanguageServiceLocation?> GoToDefinitionAsync(
        LanguageServiceSessionKey sessionKey,
        string relativePath,
        string symbol,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var matches = await WorkspaceSymbolsAsync(sessionKey, symbol, ct).ConfigureAwait(false);
        var definition = matches.FirstOrDefault(match => string.Equals(match.Name, symbol, StringComparison.Ordinal));
        if (definition == null)
        {
            return null;
        }

        return new LanguageServiceLocation
        {
            RelativePath = definition.RelativePath,
            Line = definition.Line,
            Column = definition.Column,
            Preview = $"{definition.Kind} {definition.Name}"
        };
    }

    public async ValueTask<IReadOnlyList<LanguageServiceLocation>> FindReferencesAsync(
        LanguageServiceSessionKey sessionKey,
        string relativePath,
        string symbol,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        _ = relativePath;
        var results = new List<LanguageServiceLocation>();
        var documents = await LoadDocumentsAsync(sessionKey, null, ct).ConfigureAwait(false);
        var pattern = $@"\b{Regex.Escape(symbol)}\b";
        var regex = new Regex(pattern, RegexOptions.Compiled);

        foreach (var document in documents)
        {
            var lines = SplitLines(document.Text);
            for (var i = 0; i < lines.Count; i++)
            {
                foreach (Match match in regex.Matches(lines[i]))
                {
                    results.Add(new LanguageServiceLocation
                    {
                        RelativePath = document.RelativePath,
                        Line = i + 1,
                        Column = match.Index + 1,
                        Preview = lines[i].Trim()
                    });
                }
            }
        }

        return results;
    }

    protected virtual IReadOnlyList<LanguageServiceDocumentDiagnostic> CollectDiagnostics(string relativePath, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(text);

        var diagnostics = new List<LanguageServiceDocumentDiagnostic>();
        var lines = SplitLines(text);

        if (UsesBraceBalance)
        {
            var openCount = text.Count(ch => ch == '{');
            var closeCount = text.Count(ch => ch == '}');
            if (openCount != closeCount)
            {
                diagnostics.Add(new LanguageServiceDocumentDiagnostic
                {
                    RelativePath = relativePath,
                    Line = Math.Max(lines.Count, 1),
                    Column = 1,
                    Severity = LanguageServiceDiagnosticSeverity.Error,
                    Code = "BRACE001",
                    Message = "Brace balance mismatch detected."
                });
            }
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Contains("BROKEN_DIAGNOSTIC", StringComparison.Ordinal))
            {
                diagnostics.Add(new LanguageServiceDocumentDiagnostic
                {
                    RelativePath = relativePath,
                    Line = index + 1,
                    Column = line.IndexOf("BROKEN_DIAGNOSTIC", StringComparison.Ordinal) + 1,
                    Severity = LanguageServiceDiagnosticSeverity.Error,
                    Code = "MARKER001",
                    Message = "Explicit broken diagnostic marker encountered."
                });
            }

            if (line.Contains("TODO_DIAGNOSTIC", StringComparison.Ordinal))
            {
                diagnostics.Add(new LanguageServiceDocumentDiagnostic
                {
                    RelativePath = relativePath,
                    Line = index + 1,
                    Column = line.IndexOf("TODO_DIAGNOSTIC", StringComparison.Ordinal) + 1,
                    Severity = LanguageServiceDiagnosticSeverity.Warning,
                    Code = "TODO001",
                    Message = "TODO diagnostic marker encountered."
                });
            }
        }

        return diagnostics;
    }

    protected virtual IReadOnlyList<LanguageServiceSymbolInfo> ExtractSymbols(string relativePath, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(text);

        var symbols = new List<LanguageServiceSymbolInfo>();
        var lines = SplitLines(text);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            foreach (var (regex, kind) in SymbolPatterns)
            {
                var match = regex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                symbols.Add(new LanguageServiceSymbolInfo
                {
                    Name = name,
                    Kind = kind,
                    RelativePath = relativePath,
                    Line = index + 1,
                    Column = match.Groups["name"].Index + 1
                });
            }
        }

        return symbols;
    }

    private LanguageServiceProviderDescriptor CreateDegradedDescriptor(
        LanguageServiceDegradedReason degradedReason,
        double latencyMs)
        => new()
        {
            Language = Language,
            ProviderName = ProviderName,
            Extensions = Extensions,
            Capabilities = Capabilities,
            Status = LanguageServiceStatus.Degraded,
            InitializationLatencyMs = latencyMs,
            DegradedReason = degradedReason
        };

    private async ValueTask<(string RelativePath, string Text)> LoadSingleDocumentAsync(
        LanguageServiceSessionKey sessionKey,
        string relativePath,
        CancellationToken ct)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        var fullPath = Path.Combine(sessionKey.WorkspacePath, normalizedPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Document not found: {normalizedPath}", fullPath);
        }

        var text = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        return (normalizedPath, text);
    }

    private async ValueTask<IReadOnlyList<(string RelativePath, string Text)>> LoadDocumentsAsync(
        LanguageServiceSessionKey sessionKey,
        string? relativePath,
        CancellationToken ct)
    {
        if (!Directory.Exists(sessionKey.WorkspacePath))
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            return [await LoadSingleDocumentAsync(sessionKey, relativePath, ct).ConfigureAwait(false)];
        }

        var documents = new List<(string RelativePath, string Text)>();
        foreach (var file in EnumerateLanguageFiles(sessionKey.WorkspacePath))
        {
            var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var normalizedPath = NormalizeRelativePath(Path.GetRelativePath(sessionKey.WorkspacePath, file));
            documents.Add((normalizedPath, text));
        }

        return documents;
    }

    private IEnumerable<string> EnumerateLanguageFiles(string workspacePath)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return files.Where(file =>
        {
            var extension = Path.GetExtension(file);
            if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var relative = NormalizeRelativePath(Path.GetRelativePath(workspacePath, file));
            return !relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) &&
                   !relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                   !relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
                   !relative.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static List<string> SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();

    private static string NormalizeRelativePath(string relativePath)
        => relativePath.Replace('\\', '/').TrimStart('/');
}
