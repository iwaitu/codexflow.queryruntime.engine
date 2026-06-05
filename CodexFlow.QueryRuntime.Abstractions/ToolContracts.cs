namespace CodexFlow.QueryRuntime.Abstractions;

public interface IToolRegistry
{
    IReadOnlyList<QueryRuntimeToolDescriptor> ListTools(QueryRuntimeToolProfile profile);

    IReadOnlyList<QueryRuntimeToolSearchHit> SearchTools(QueryRuntimeToolSearchRequest request)
        => QueryRuntimeToolSearch.Search(ListTools(request.Profile), request);
}

public sealed record QueryRuntimeToolDescriptor(
    string Name,
    string? Description,
    IReadOnlySet<string> Capabilities,
    QueryRuntimeToolProfile Profile,
    QueryRuntimeToolDiscoveryMetadata? Discovery = null,
    QueryRuntimeToolLoading Loading = QueryRuntimeToolLoading.AlwaysOn,
    IReadOnlyList<int>? AllowedStages = null);

public enum QueryRuntimeToolLoading
{
    AlwaysOn = 0,
    Deferred = 1
}

public sealed record QueryRuntimeToolDiscoveryMetadata(
    string SearchHint,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> RequiredArgs,
    IReadOnlyList<string> OptionalArgs,
    IReadOnlyList<string> Examples,
    string Capability,
    bool PreferAlwaysVisible = false)
{
    public static QueryRuntimeToolDiscoveryMetadata FromDescription(
        string? description,
        string capability = "general")
        => new(
            description ?? string.Empty,
            [],
            [],
            [],
            [],
            capability);
}

public sealed record QueryRuntimeToolSearchOptions
{
    public bool Enabled { get; set; }

    public int TopK { get; set; } = 5;

    public IReadOnlyList<string> AlwaysOnToolNames { get; set; } = [];

    public IReadOnlyList<string> DeferredToolNames { get; set; } = [];

    public bool IncludeAlreadyActive { get; set; } = true;

    public bool IncludeUnavailable { get; set; }
}

public sealed record QueryRuntimeToolSearchRequest
{
    public required string Query { get; init; }

    public QueryRuntimeToolProfile Profile { get; init; } = QueryRuntimeToolProfile.None;

    public int? Stage { get; init; }

    public int TopK { get; init; } = 5;

    public bool IncludeAlreadyActive { get; init; } = true;

    public bool IncludeUnavailable { get; init; }

    public IReadOnlySet<string> ActiveToolNames { get; init; } = EmptyStringSet.Value;
}

public sealed record QueryRuntimeToolSearchHit(
    QueryRuntimeToolDescriptor Tool,
    double Score,
    IReadOnlyList<string> MatchedFields,
    bool IsAlreadyActive,
    bool IsAvailableInCurrentStage,
    IReadOnlyList<int> AvailableStages,
    string Reason,
    QueryRuntimeToolRiskLevel Risk);

public enum QueryRuntimeToolRiskLevel
{
    SafeRead = 0,
    LocalWrite = 1,
    CommandExecution = 2,
    ExternalNetwork = 3,
    Destructive = 4,
    RequiresConfirmation = 5
}

public static class QueryRuntimeToolSearch
{
    private static readonly char[] TokenSeparators = [' ', '\t', '\r', '\n', '_', '-', '/', '.', ':'];
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly IReadOnlySet<string> Stopwords = new HashSet<string>(
        ["a", "an", "and", "for", "find", "please", "search", "the", "tool", "use", "with"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> DestructiveIntent = new HashSet<string>(
        ["apply", "commit", "create", "delete", "edit", "modify", "patch", "remove", "revert", "update", "write"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> ReadOnlyIntent = new HashSet<string>(
        ["analyze", "diagnose", "find", "inspect", "list", "read", "search", "show", "status"],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<QueryRuntimeToolSearchHit> Search(
        IEnumerable<QueryRuntimeToolDescriptor> tools,
        QueryRuntimeToolSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(request);

        var query = (request.Query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var topK = Math.Clamp(request.TopK, 1, 20);
        var activeToolNames = new HashSet<string>(request.ActiveToolNames, StringComparer.OrdinalIgnoreCase);
        var regexMode = query.StartsWith("regex:", StringComparison.OrdinalIgnoreCase);
        var queryTokens = regexMode ? [] : Tokenize(query);
        if (!regexMode && queryTokens.Count == 0)
        {
            return [];
        }

        var destructiveIntent = queryTokens.Any(token => DestructiveIntent.Contains(token));
        var readOnlyIntent = queryTokens.Any(token => ReadOnlyIntent.Contains(token));
        return tools
            .Select(tool => ScoreTool(tool, query, regexMode, queryTokens, activeToolNames, request.Stage, destructiveIntent, readOnlyIntent))
            .Where(hit => hit != null)
            .Select(hit => hit!)
            .Where(hit => request.IncludeAlreadyActive || !hit.IsAlreadyActive)
            .Where(hit => request.IncludeUnavailable || hit.IsAvailableInCurrentStage)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToArray();
    }

    public static IReadOnlyList<string> Tokenize(string text)
        => text.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => token.Trim().ToLowerInvariant())
            .Where(token => token.Length > 0 && !Stopwords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static QueryRuntimeToolSearchHit? ScoreTool(
        QueryRuntimeToolDescriptor tool,
        string query,
        bool regexMode,
        IReadOnlyList<string> queryTokens,
        IReadOnlySet<string> activeToolNames,
        int? stage,
        bool destructiveIntent,
        bool readOnlyIntent)
    {
        var discovery = tool.Discovery ?? QueryRuntimeToolDiscoveryMetadata.FromDescription(tool.Description);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var score = 0d;

        if (regexMode)
        {
            var pattern = query["regex:".Length..].Trim();
            if (pattern.Length is 0 or > 120)
            {
                return null;
            }

            try
            {
                var regex = new System.Text.RegularExpressions.Regex(
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    RegexTimeout);
                if (!regex.IsMatch(BuildIndexText(tool, discovery)))
                {
                    return null;
                }

                score += 80;
                matched.Add("regex");
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return null;
            }
        }
        else
        {
            var nameTokens = Tokenize(tool.Name);
            var hintTokens = Tokenize(discovery.SearchHint);
            var descriptionTokens = Tokenize(tool.Description ?? string.Empty);
            var capabilityTokens = Tokenize(discovery.Capability);
            var keywordSet = new HashSet<string>(discovery.Keywords, StringComparer.OrdinalIgnoreCase);

            foreach (var token in queryTokens)
            {
                if (string.Equals(tool.Name, token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                    matched.Add("name");
                }
                if (nameTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    score += 70;
                    matched.Add("name");
                }
                else if (tool.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 60;
                    matched.Add("name");
                }

                if (keywordSet.Contains(token))
                {
                    score += 50;
                    matched.Add("keyword");
                }
                else if (discovery.Keywords.Any(keyword => keyword.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 35;
                    matched.Add("keyword");
                }

                if (hintTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    score += 30;
                    matched.Add("searchHint");
                }
                if (capabilityTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    score += 25;
                    matched.Add("capability");
                }
                if (descriptionTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    score += 10;
                    matched.Add("description");
                }
            }
        }

        if (score <= 0)
        {
            return null;
        }

        var availableInCurrentStage = IsAvailableInStage(tool, stage);
        if (availableInCurrentStage)
        {
            score += 20;
        }
        else
        {
            score -= 50;
        }

        var alreadyActive = activeToolNames.Contains(tool.Name);
        if (alreadyActive)
        {
            score += 10;
        }

        var risk = InferRisk(tool);
        if (risk == QueryRuntimeToolRiskLevel.SafeRead)
        {
            score += readOnlyIntent ? 12 : 5;
        }
        if (risk is QueryRuntimeToolRiskLevel.LocalWrite or QueryRuntimeToolRiskLevel.Destructive or QueryRuntimeToolRiskLevel.RequiresConfirmation)
        {
            score += destructiveIntent ? 5 : -20;
        }

        var reason = availableInCurrentStage
            ? $"matched {string.Join(", ", matched.Order(StringComparer.OrdinalIgnoreCase))}"
            : $"matched query but not available in current stage {stage}";
        return new QueryRuntimeToolSearchHit(
            tool,
            Math.Round(score, 2),
            matched.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            alreadyActive,
            availableInCurrentStage,
            tool.AllowedStages ?? [],
            reason,
            risk);
    }

    private static string BuildIndexText(
        QueryRuntimeToolDescriptor tool,
        QueryRuntimeToolDiscoveryMetadata discovery)
        => string.Join(' ', new[]
        {
            tool.Name,
            tool.Description ?? string.Empty,
            discovery.SearchHint,
            discovery.Capability,
            string.Join(' ', discovery.Keywords)
        });

    private static bool IsAvailableInStage(QueryRuntimeToolDescriptor tool, int? stage)
    {
        if (tool.AllowedStages is not { Count: > 0 } stages || stage == null)
        {
            return true;
        }

        return stages.Contains(stage.Value);
    }

    public static QueryRuntimeToolRiskLevel InferRisk(QueryRuntimeToolDescriptor tool)
    {
        if (tool.Capabilities.Contains(QueryRuntimeCapabilities.WriteFileSystem))
        {
            return QueryRuntimeToolRiskLevel.LocalWrite;
        }
        if (tool.Capabilities.Contains(QueryRuntimeCapabilities.ExecuteProcess))
        {
            return QueryRuntimeToolRiskLevel.CommandExecution;
        }

        return QueryRuntimeToolRiskLevel.SafeRead;
    }
}

public interface IQueryRuntimeCapabilityPolicy
{
    QueryRuntimeCapabilityDecision Evaluate(QueryRuntimeCapabilityRequest request);
}

public interface IQueryRuntimePolicyDecisionSink
{
    Task OnPolicyDecisionAsync(
        QueryRuntimePolicyDecisionRecord decision,
        CancellationToken ct = default);
}

public sealed record QueryRuntimePolicyDecisionRecord(
    string Profile,
    string ToolName,
    IReadOnlySet<string> Capabilities,
    IReadOnlyList<string> Command,
    string Network,
    string Mount,
    string Decision,
    bool Allowed,
    string Reason,
    DateTimeOffset Timestamp);

public sealed record QueryRuntimeCapabilityRequest
{
    public required QueryRuntimeToolProfile Profile { get; init; }

    public required string ToolName { get; init; }

    public required IReadOnlySet<string> Capabilities { get; init; }

    public IReadOnlyList<string> Command { get; init; } = [];

    public IReadOnlySet<string> CommandCapabilities { get; init; } = EmptyStringSet.Value;

    public bool ExplicitApproval { get; init; }

    public string? ApprovalReason { get; init; }

    public string? WorkspacePath { get; init; }

    public SandboxNetworkPolicy Network { get; init; } = SandboxNetworkPolicy.Deny;

    public SandboxMountPolicy Mounts { get; init; } = SandboxMountPolicy.WorkspaceReadOnly;
}

public sealed record QueryRuntimeCapabilityDecision(
    QueryRuntimeCapabilityDecisionKind Kind,
    string Reason)
{
    public static QueryRuntimeCapabilityDecision Allow(string reason = "allowed")
        => new(QueryRuntimeCapabilityDecisionKind.Allow, reason);

    public static QueryRuntimeCapabilityDecision Deny(string reason)
        => new(QueryRuntimeCapabilityDecisionKind.Deny, reason);

    public static QueryRuntimeCapabilityDecision RequireApproval(string reason)
        => new(QueryRuntimeCapabilityDecisionKind.RequireApproval, reason);
}

public enum QueryRuntimeCapabilityDecisionKind
{
    Allow = 0,
    Deny = 1,
    RequireApproval = 2
}

public static class QueryRuntimeCapabilities
{
    public const string ReadFileSystem = "read_fs";
    public const string WriteFileSystem = "write_fs";
    public const string WriteArtifacts = "write_artifacts";
    public const string ExecuteProcess = "execute_process";
    public const string GitRead = "git_read";
    public const string RunTests = "run_tests";
    public const string Build = "build";
}

public static class QueryRuntimeCommandCapabilities
{
    public const string ReadWorkspace = "command.read_workspace";
    public const string WriteWorkspace = "command.write_workspace";
    public const string NetworkAccess = "command.network_access";
    public const string PackageInstall = "command.package_install";
    public const string PackageRestore = "command.package_restore";
    public const string PackagePublish = "command.package_publish";
    public const string GitPush = "command.git_push";
    public const string GitWrite = "command.git_write";
    public const string Destructive = "command.destructive";
    public const string Deploy = "command.deploy";
    public const string ArbitraryExecution = "command.arbitrary_execution";
    public const string UnknownProcess = "command.unknown_process";
}

internal static class EmptyStringSet
{
    public static readonly IReadOnlySet<string> Value = new HashSet<string>(StringComparer.Ordinal);
}
