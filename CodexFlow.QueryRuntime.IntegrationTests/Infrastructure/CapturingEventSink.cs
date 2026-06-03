using CodexFlow.Core.Runtime;

namespace CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;

internal sealed class CapturingEventSink : IQueryRuntimeEventSink
{
    private readonly List<QueryRuntimeEvent> _events = [];

    public IReadOnlyList<QueryRuntimeEvent> Events => _events;

    public bool IsEnabled(QueryRuntimeEventType eventType) => true;

    public ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
    {
        _events.Add(runtimeEvent);
        return ValueTask.CompletedTask;
    }
}
