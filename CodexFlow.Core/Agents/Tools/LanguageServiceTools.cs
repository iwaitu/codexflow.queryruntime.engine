using System.Text;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

internal static class LanguageServiceToolSupport
{
    public static async ValueTask<(ILanguageServiceClient? Client, CodexToolResult? Error)> ResolveClientAsync(
        Dictionary<string, object?> arguments,
        ILanguageServiceRegistry registry,
        ILanguageServiceSessionFactory sessionFactory,
        CancellationToken ct)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrWhiteSpace(baseRoot))
        {
            return (null, CodexToolResult.Error("Missing workspace_path."));
        }

        var language = registry.DetectLanguage(
            arguments.GetValueOrDefault("language")?.ToString()
            ?? arguments.GetValueOrDefault("path")?.ToString());
        if (language == SupportedLanguage.Unknown)
        {
            return (null, CodexToolResult.Error("Unable to resolve language from 'language' or 'path'."));
        }

        var workerId = arguments.GetValueOrDefault("worker_id")?.ToString()
            ?? arguments.GetValueOrDefault("session_id")?.ToString()
            ?? "default";

        var client = await sessionFactory.GetOrCreateAsync(new LanguageServiceSessionKey
        {
            WorkspacePath = baseRoot,
            WorkerId = workerId,
            Language = language
        }, ct).ConfigureAwait(false);

        return (client, null);
    }

    public static string FormatProviderHeader(LanguageServiceProviderDescriptor descriptor, string languageLabel)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"language: {languageLabel}");
        sb.AppendLine($"provider: {descriptor.ProviderName}");
        sb.AppendLine($"status: {descriptor.Status.ToString().ToLowerInvariant()}");
        sb.AppendLine($"initialization_latency_ms: {descriptor.InitializationLatencyMs:F2}");
        if (descriptor.DegradedReason != LanguageServiceDegradedReason.None)
        {
            sb.AppendLine($"degraded_reason: {ToToken(descriptor.DegradedReason)}");
        }

        return sb.ToString();
    }

    public static object BuildMetadata(
        LanguageServiceProviderDescriptor descriptor,
        int diagnosticsCount = 0)
        => new
        {
            language = descriptor.Language.ToString().ToLowerInvariant(),
            provider = descriptor.ProviderName,
            diagnosticsCount,
            initializationLatencyMs = descriptor.InitializationLatencyMs,
            degradedReason = descriptor.DegradedReason == LanguageServiceDegradedReason.None
                ? null
                : ToToken(descriptor.DegradedReason)
        };

    public static string ToToken(LanguageServiceDegradedReason reason)
        => reason switch
        {
            LanguageServiceDegradedReason.BinaryMissing => "binary-missing",
            LanguageServiceDegradedReason.ServerStartFailed => "server-start-failed",
            LanguageServiceDegradedReason.InitializationTimedOut => "initialization-timeout",
            LanguageServiceDegradedReason.ProtocolError => "protocol-error",
            LanguageServiceDegradedReason.UnsupportedLanguage => "unsupported-language",
            _ => "none"
        };
}

public sealed class LspGetDiagnosticsTool(
    ILanguageServiceRegistry registry,
    ILanguageServiceSessionFactory sessionFactory,
    ILogger<LspGetDiagnosticsTool> logger) : ICodexTool
{
    public string Name => "lsp_get_diagnostics";
    public string Description => "读取当前工作区的语言服务诊断信息。参数: path(可选), language(可选), worker_id(可选), session_id/workspace_path/project_root。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages => [1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var (client, error) = await LanguageServiceToolSupport.ResolveClientAsync(arguments, registry, sessionFactory, ct).ConfigureAwait(false);
        if (error != null)
        {
            return error;
        }

        var relativePath = arguments.GetValueOrDefault("path")?.ToString();
        var result = await client!.GetDiagnosticsAsync(relativePath, ct).ConfigureAwait(false);
        var diagnostics = result.Diagnostics;
        var visibleDiagnostics = diagnostics.Take(20).ToList();

        var sb = new StringBuilder();
        sb.Append(LanguageServiceToolSupport.FormatProviderHeader(
            result.Descriptor,
            result.Descriptor.Language.ToString().ToLowerInvariant()));
        sb.AppendLine($"diagnostics_count: {diagnostics.Count}");

        if (visibleDiagnostics.Count == 0)
        {
            sb.AppendLine("diagnostics: none");
        }
        else
        {
            sb.AppendLine("diagnostics:");
            foreach (var diagnostic in visibleDiagnostics)
            {
                sb.AppendLine($"- [{diagnostic.Severity.ToString().ToLowerInvariant()}] {diagnostic.RelativePath}:{diagnostic.Line}:{diagnostic.Column} {diagnostic.Message}");
            }

            if (diagnostics.Count > visibleDiagnostics.Count)
            {
                sb.AppendLine($"- ... truncated {diagnostics.Count - visibleDiagnostics.Count} additional diagnostics");
            }
        }

        logger.LogInformation(
            "LSP diagnostics queried. Language={Language} Provider={Provider} Count={Count} Degraded={Degraded}",
            result.Descriptor.Language,
            result.Descriptor.ProviderName,
            diagnostics.Count,
            result.Descriptor.DegradedReason);

        return CodexToolResult.Succeeded(sb.ToString(), LanguageServiceToolSupport.BuildMetadata(result.Descriptor, diagnostics.Count));
    }
}

public sealed class LspDocumentSymbolsTool(
    ILanguageServiceRegistry registry,
    ILanguageServiceSessionFactory sessionFactory) : ICodexTool
{
    public string Name => "lsp_document_symbols";
    public string Description => "列出单个文档的符号。参数: path(必填), language(可选), worker_id(可选), session_id/workspace_path/project_root。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages => [1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var relativePath = arguments.GetValueOrDefault("path")?.ToString();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return CodexToolResult.Error("Missing path.");
        }

        var (client, error) = await LanguageServiceToolSupport.ResolveClientAsync(arguments, registry, sessionFactory, ct).ConfigureAwait(false);
        if (error != null)
        {
            return error;
        }

        var symbols = await client!.GetDocumentSymbolsAsync(relativePath, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.Append(LanguageServiceToolSupport.FormatProviderHeader(
            client.Descriptor,
            client.Descriptor.Language.ToString().ToLowerInvariant()));
        sb.AppendLine($"symbol_count: {symbols.Count}");

        foreach (var symbol in symbols.Take(50))
        {
            sb.AppendLine($"- {symbol.Kind} {symbol.Name} @ {symbol.RelativePath}:{symbol.Line}:{symbol.Column}");
        }

        if (symbols.Count > 50)
        {
            sb.AppendLine($"- ... truncated {symbols.Count - 50} additional symbols");
        }

        return CodexToolResult.Succeeded(sb.ToString(), LanguageServiceToolSupport.BuildMetadata(client.Descriptor));
    }
}

public sealed class LspWorkspaceSymbolsTool(
    ILanguageServiceRegistry registry,
    ILanguageServiceSessionFactory sessionFactory) : ICodexTool
{
    public string Name => "lsp_workspace_symbols";
    public string Description => "按查询字符串搜索工作区符号。参数: query(必填), language(必填或可由 path 推断), worker_id(可选), session_id/workspace_path/project_root。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages => [1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var query = arguments.GetValueOrDefault("query")?.ToString();
        if (string.IsNullOrWhiteSpace(query))
        {
            return CodexToolResult.Error("Missing query.");
        }

        var (client, error) = await LanguageServiceToolSupport.ResolveClientAsync(arguments, registry, sessionFactory, ct).ConfigureAwait(false);
        if (error != null)
        {
            return error;
        }

        var symbols = await client!.WorkspaceSymbolsAsync(query, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.Append(LanguageServiceToolSupport.FormatProviderHeader(
            client.Descriptor,
            client.Descriptor.Language.ToString().ToLowerInvariant()));
        sb.AppendLine($"workspace_symbol_count: {symbols.Count}");

        foreach (var symbol in symbols.Take(50))
        {
            sb.AppendLine($"- {symbol.Kind} {symbol.Name} @ {symbol.RelativePath}:{symbol.Line}:{symbol.Column}");
        }

        if (symbols.Count > 50)
        {
            sb.AppendLine($"- ... truncated {symbols.Count - 50} additional symbols");
        }

        return CodexToolResult.Succeeded(sb.ToString(), LanguageServiceToolSupport.BuildMetadata(client.Descriptor));
    }
}

public sealed class LspGoToDefinitionTool(
    ILanguageServiceRegistry registry,
    ILanguageServiceSessionFactory sessionFactory) : ICodexTool
{
    public string Name => "lsp_go_to_definition";
    public string Description => "定位符号定义。参数: path(必填), symbol(必填), language(可选), worker_id(可选), session_id/workspace_path/project_root。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages => [1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var relativePath = arguments.GetValueOrDefault("path")?.ToString();
        var symbol = arguments.GetValueOrDefault("symbol")?.ToString();
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(symbol))
        {
            return CodexToolResult.Error("Missing path or symbol.");
        }

        var (client, error) = await LanguageServiceToolSupport.ResolveClientAsync(arguments, registry, sessionFactory, ct).ConfigureAwait(false);
        if (error != null)
        {
            return error;
        }

        var location = await client!.GoToDefinitionAsync(relativePath, symbol, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.Append(LanguageServiceToolSupport.FormatProviderHeader(
            client.Descriptor,
            client.Descriptor.Language.ToString().ToLowerInvariant()));
        if (location == null)
        {
            sb.AppendLine($"definition: not found for '{symbol}'");
        }
        else
        {
            sb.AppendLine($"definition: {location.RelativePath}:{location.Line}:{location.Column}");
            if (!string.IsNullOrWhiteSpace(location.Preview))
            {
                sb.AppendLine($"preview: {location.Preview}");
            }
        }

        return CodexToolResult.Succeeded(sb.ToString(), LanguageServiceToolSupport.BuildMetadata(client.Descriptor));
    }
}

public sealed class LspFindReferencesTool(
    ILanguageServiceRegistry registry,
    ILanguageServiceSessionFactory sessionFactory) : ICodexTool
{
    public string Name => "lsp_find_references";
    public string Description => "查找符号引用。参数: path(必填), symbol(必填), language(可选), worker_id(可选), session_id/workspace_path/project_root。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages => [1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var relativePath = arguments.GetValueOrDefault("path")?.ToString();
        var symbol = arguments.GetValueOrDefault("symbol")?.ToString();
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(symbol))
        {
            return CodexToolResult.Error("Missing path or symbol.");
        }

        var (client, error) = await LanguageServiceToolSupport.ResolveClientAsync(arguments, registry, sessionFactory, ct).ConfigureAwait(false);
        if (error != null)
        {
            return error;
        }

        var references = await client!.FindReferencesAsync(relativePath, symbol, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.Append(LanguageServiceToolSupport.FormatProviderHeader(
            client.Descriptor,
            client.Descriptor.Language.ToString().ToLowerInvariant()));
        sb.AppendLine($"reference_count: {references.Count}");

        foreach (var reference in references.Take(50))
        {
            sb.AppendLine($"- {reference.RelativePath}:{reference.Line}:{reference.Column} {reference.Preview}");
        }

        if (references.Count > 50)
        {
            sb.AppendLine($"- ... truncated {references.Count - 50} additional references");
        }

        return CodexToolResult.Succeeded(sb.ToString(), LanguageServiceToolSupport.BuildMetadata(client.Descriptor));
    }
}
