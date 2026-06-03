namespace CodexFlow.Core.Abstractions
{
    public interface IOutboxSignal
    {
        void Pulse();
        Task WaitAsync(TimeSpan timeout, CancellationToken ct);
    }
}
