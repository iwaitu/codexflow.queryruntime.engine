using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

public interface IPlannerSummaryPublisher
{
    Task PublishAsync(CodexSession session, PlannerSummaryUpdate update, CancellationToken ct = default);
}
