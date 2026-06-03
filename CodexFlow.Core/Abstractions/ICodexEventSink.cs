using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// 事件匯流排接收端，用於將內核事件推送到外部系統（如 Dashboard）。
/// </summary>
public interface ICodexEventSink
{
    /// <summary>
    /// 發送一個事件。
    /// </summary>
    Task PublishAsync(CodexEvent e, CancellationToken ct = default);
}
