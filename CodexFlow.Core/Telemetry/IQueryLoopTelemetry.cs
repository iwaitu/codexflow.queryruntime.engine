namespace CodexFlow.Core.Telemetry;

public interface IQueryLoopTelemetry
{
    void RecordStart(QueryLoopStarted evt);
    void RecordRound(QueryLoopRoundCompleted evt);
    void RecordTermination(QueryLoopTerminated evt);
    void RecordRecovery(QueryLoopRecovery evt);
}
