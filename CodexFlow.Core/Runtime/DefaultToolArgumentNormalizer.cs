using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

public interface IToolArgumentNormalizer
{
    ToolArgumentNormalizationResult Normalize(ToolArgumentNormalizationRequest request);

    Dictionary<string, object?> NormalizeArguments(
        IDictionary<string, object?>? arguments,
        CodexSession? session);
}

public sealed class DefaultToolArgumentNormalizer : IToolArgumentNormalizer
{
    public static DefaultToolArgumentNormalizer Instance { get; } = new();

    public ToolArgumentNormalizationResult Normalize(ToolArgumentNormalizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prestartedCalls = new HashSet<FunctionCallContent>(
            request.PrestartedStreamingCalls,
            ReferenceEqualityComparer.Instance);
        var calls = new List<FunctionCallContent>(request.Calls.Count);
        var normalizedCount = 0;

        foreach (var call in request.Calls)
        {
            if (prestartedCalls.Contains(call))
            {
                calls.Add(call);
                continue;
            }

            var normalizedArguments = NormalizeArguments(call.Arguments, request.RuntimeRequest.Session);
            calls.Add(new FunctionCallContent(
                call.CallId ?? string.Empty,
                call.Name ?? string.Empty,
                normalizedArguments));
            normalizedCount++;
        }

        return new ToolArgumentNormalizationResult
        {
            Calls = calls,
            NormalizedCallCount = normalizedCount
        };
    }

    public Dictionary<string, object?> NormalizeArguments(
        IDictionary<string, object?>? arguments,
        CodexSession? session)
    {
        var args = arguments != null
            ? new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        ToolArgumentNormalizer.NormalizeInPlace(args);

        if (session == null)
        {
            return args;
        }

        args["session_id"] = session.Id;
        args["workspace_path"] = session.WorkspacePath;
        args["project_root"] = ToolPathResolver.ResolveProjectRoot(
            session.WorkspacePath,
            null,
            session.ProjectUrl,
            session.Metadata);
        return args;
    }
}
