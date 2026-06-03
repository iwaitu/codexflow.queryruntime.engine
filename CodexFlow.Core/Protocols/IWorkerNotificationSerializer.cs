using System;

namespace CodexFlow.Core.Protocols;

public interface IWorkerNotificationSerializer
{
    string Serialize(WorkerNotificationEnvelope envelope);
    string Serialize(VerificationReportEnvelope envelope);
    string Serialize(WaitingUserEnvelope envelope);
}
