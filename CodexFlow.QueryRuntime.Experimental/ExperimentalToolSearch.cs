using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Abstractions;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public sealed class ExperimentalToolSearchSession
{
    private static readonly IReadOnlySet<string> WriteIntent = new HashSet<string>(
        ["apply", "commit", "create", "delete", "edit", "modify", "patch", "remove", "revert", "update", "write"],
        StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, AIFunction> _tools;
    private readonly Dictionary<string, QueryRuntimeToolDescriptor> _descriptors;
    private readonly HashSet<string> _activeToolNames;
    private readonly QueryRuntimeToolProfile _profile;
    private readonly QueryRuntimeToolSearchOptions _options;
    private readonly int? _stage;
    private readonly AIFunction _toolSearchFunction;

    public ExperimentalToolSearchSession(
        QueryRuntimeToolProfile profile,
        IReadOnlyList<AIFunction> tools,
        IReadOnlyList<QueryRuntimeToolDescriptor> descriptors,
        QueryRuntimeToolSearchOptions options,
        int? stage = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(options);

        _profile = profile;
        _options = options;
        _stage = stage;
        _tools = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        _descriptors = descriptors.ToDictionary(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (!_descriptors.ContainsKey(tool.Name))
            {
                _descriptors[tool.Name] = CreateDescriptor(tool, profile, QueryRuntimeToolLoading.Deferred);
            }
        }

        _activeToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in _descriptors.Values)
        {
            if (descriptor.Loading == QueryRuntimeToolLoading.AlwaysOn ||
                descriptor.Discovery?.PreferAlwaysVisible == true ||
                options.AlwaysOnToolNames.Contains(descriptor.Name, StringComparer.OrdinalIgnoreCase))
            {
                _activeToolNames.Add(descriptor.Name);
            }
        }

        foreach (var deferredToolName in options.DeferredToolNames)
        {
            _activeToolNames.Remove(deferredToolName);
        }

        _toolSearchFunction = CreateToolSearchFunction();
    }

    public IReadOnlySet<string> ActiveToolNames => _activeToolNames;

    public string GetCapabilityCatalog()
    {
        var deferred = _descriptors.Values
            .Where(descriptor => !_activeToolNames.Contains(descriptor.Name))
            .OrderBy(descriptor => descriptor.Discovery?.Capability ?? "general", StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .GroupBy(descriptor => descriptor.Discovery?.Capability ?? "general", StringComparer.OrdinalIgnoreCase)
            .Select(group => $"- {group.Key}: {string.Join(", ", group.Select(descriptor => descriptor.Name))}");
        var lines = deferred.ToArray();
        if (lines.Length == 0)
        {
            return "Deferred tools: none.";
        }

        return "Deferred tools are available by name but their schemas are not loaded yet. Use tool_search with a capability query or select:<tool_name> to activate one.\n" +
            string.Join('\n', lines);
    }

    public IReadOnlyList<AIFunction> GetActiveTools()
    {
        var activeTools = _activeToolNames
            .Where(name => _tools.ContainsKey(name))
            .Select(name => _tools[name])
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        activeTools.Insert(0, _toolSearchFunction);
        return activeTools;
    }

    public IReadOnlyList<AIFunction> GetAllTools()
    {
        var tools = _tools.Values
            .OrderBy(static tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        tools.Insert(0, _toolSearchFunction);
        return tools;
    }

    public string Search(
        string query,
        int top_k = 0,
        bool include_unavailable = false,
        int stage = 0)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Serialize(
                new ToolSearchResponse(
                    0,
                    query ?? string.Empty,
                    [],
                    "Provide a non-empty query, for example tool_search({\"query\":\"git diff\"})."));
        }

        if (TryHandleSelect(query, out var selectedResponse))
        {
            return Serialize(selectedResponse);
        }

        var effectiveTopK = top_k <= 0 ? _options.TopK : top_k;
        var effectiveStage = stage <= 0 ? _stage : stage;
        var request = new QueryRuntimeToolSearchRequest
        {
            Query = query,
            Profile = _profile,
            Stage = effectiveStage,
            TopK = effectiveTopK,
            IncludeAlreadyActive = _options.IncludeAlreadyActive,
            IncludeUnavailable = include_unavailable || _options.IncludeUnavailable,
            ActiveToolNames = _activeToolNames
        };
        var hits = QueryRuntimeToolSearch.Search(_descriptors.Values, request);
        var hasWriteIntent = QueryRuntimeToolSearch.Tokenize(query).Any(token => WriteIntent.Contains(token));
        var results = new List<ToolSearchToolResult>();
        foreach (var hit in hits)
        {
            var activated = false;
            if (ShouldActivate(hit, hasWriteIntent))
            {
                activated = _activeToolNames.Add(hit.Tool.Name);
            }

            var discovery = hit.Tool.Discovery ?? QueryRuntimeToolDiscoveryMetadata.FromDescription(hit.Tool.Description);
            results.Add(new ToolSearchToolResult(
                hit.Tool.Name,
                hit.Score,
                activated,
                hit.IsAlreadyActive,
                _activeToolNames.Contains(hit.Tool.Name),
                hit.IsAvailableInCurrentStage,
                hit.AvailableStages,
                discovery.Capability,
                discovery.SearchHint,
                discovery.RequiredArgs,
                discovery.OptionalArgs,
                hit.MatchedFields,
                hit.Risk.ToString(),
                hit.Reason));
        }

        var response = new ToolSearchResponse(
            results.Count,
            query,
            results,
            results.Count == 0
                ? "No tools matched. Try a broader capability query such as file, git, test, patch, or search."
                : "Call one of the activated tools directly if it matches the task.");
        return Serialize(response);
    }

    private static string Serialize(ToolSearchResponse response)
        => JsonSerializer.Serialize(response, ExperimentalToolSearchJsonContext.Default.ToolSearchResponse);

    private bool TryHandleSelect(string query, out ToolSearchResponse response)
    {
        response = new ToolSearchResponse(0, query, [], string.Empty);
        if (!query.StartsWith("select:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var names = query["select:".Length..]
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<ToolSearchToolResult>();
        foreach (var name in names)
        {
            if (!_descriptors.TryGetValue(name, out var descriptor))
            {
                results.Add(new ToolSearchToolResult(
                    name,
                    0,
                    false,
                    false,
                    false,
                    false,
                    [],
                    "unknown",
                    "No matching deferred tool was found.",
                    [],
                    [],
                    ["select"],
                    QueryRuntimeToolRiskLevel.SafeRead.ToString(),
                    "tool name not found"));
                continue;
            }

            var alreadyActive = _activeToolNames.Contains(descriptor.Name);
            var available = descriptor.AllowedStages is not { Count: > 0 } stages ||
                _stage == null ||
                stages.Contains(_stage.Value);
            var activated = false;
            if (available && !alreadyActive)
            {
                activated = _activeToolNames.Add(descriptor.Name);
            }

            var discovery = descriptor.Discovery ?? QueryRuntimeToolDiscoveryMetadata.FromDescription(descriptor.Description);
            results.Add(new ToolSearchToolResult(
                descriptor.Name,
                100,
                activated,
                alreadyActive,
                _activeToolNames.Contains(descriptor.Name),
                available,
                descriptor.AllowedStages ?? [],
                discovery.Capability,
                discovery.SearchHint,
                discovery.RequiredArgs,
                discovery.OptionalArgs,
                ["select"],
                QueryRuntimeToolSearch.InferRisk(descriptor).ToString(),
                available ? "selected by exact tool name" : $"selected but not available in current stage {_stage}"));
        }

        response = new ToolSearchResponse(
            results.Count,
            query,
            results,
            results.Any(result => result.Activated)
                ? "Selected tools were activated and will be callable in the next round."
                : "No new tools were activated.");
        return true;
    }

    public static IReadOnlyList<QueryRuntimeToolDescriptor> CreateDescriptors(
        QueryRuntimeToolProfile profile,
        IReadOnlyList<AIFunction> tools,
        IReadOnlyList<QueryRuntimeToolDescriptor>? descriptors = null)
    {
        var descriptorByName = descriptors?.ToDictionary(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase) ??
            new Dictionary<string, QueryRuntimeToolDescriptor>(StringComparer.OrdinalIgnoreCase);
        return tools
            .Select(tool => descriptorByName.TryGetValue(tool.Name, out var descriptor)
                ? descriptor
                : CreateDescriptor(tool, profile, QueryRuntimeToolLoading.Deferred))
            .ToArray();
    }

    private static bool ShouldActivate(QueryRuntimeToolSearchHit hit, bool hasWriteIntent)
    {
        if (!hit.IsAvailableInCurrentStage || hit.IsAlreadyActive)
        {
            return false;
        }

        if (hit.Tool.Loading == QueryRuntimeToolLoading.AlwaysOn)
        {
            return true;
        }

        return hit.Risk switch
        {
            QueryRuntimeToolRiskLevel.LocalWrite => hasWriteIntent,
            QueryRuntimeToolRiskLevel.Destructive or QueryRuntimeToolRiskLevel.RequiresConfirmation => false,
            _ => true
        };
    }

    private AIFunction CreateToolSearchFunction()
        => AIFunctionFactory.Create(
            (string query, int top_k = 0, bool include_unavailable = false, int stage = 0) =>
                Search(query, top_k, include_unavailable, stage),
            new AIFunctionFactoryOptions
            {
                Name = "tool_search",
                Description = "Search and activate deferred QRE tools. Arguments: query, top_k, include_unavailable, stage.",
                MarshalResult = static (result, _, _) => ValueTask.FromResult(result)
            });

    private static QueryRuntimeToolDescriptor CreateDescriptor(
        AIFunction tool,
        QueryRuntimeToolProfile profile,
        QueryRuntimeToolLoading loading)
        => new(
            tool.Name,
            tool.Description,
            new HashSet<string>(StringComparer.Ordinal),
            profile,
            QueryRuntimeToolDiscoveryMetadata.FromDescription(tool.Description),
            loading);

    internal sealed record ToolSearchResponse(
        int Found,
        string Query,
        IReadOnlyList<ToolSearchToolResult> Tools,
        string NextStepHint);

    internal sealed record ToolSearchToolResult(
        string Name,
        double Score,
        bool Activated,
        bool AlreadyActive,
        bool AvailableNow,
        bool AvailableInCurrentStage,
        IReadOnlyList<int> AvailableStages,
        string Capability,
        string Summary,
        IReadOnlyList<string> RequiredArgs,
        IReadOnlyList<string> OptionalArgs,
        IReadOnlyList<string> Matched,
        string Risk,
        string Reason);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ExperimentalToolSearchSession.ToolSearchResponse))]
[JsonSerializable(typeof(ExperimentalToolSearchSession.ToolSearchToolResult))]
internal sealed partial class ExperimentalToolSearchJsonContext : JsonSerializerContext;
