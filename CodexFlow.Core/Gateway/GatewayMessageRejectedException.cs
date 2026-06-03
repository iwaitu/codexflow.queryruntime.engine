namespace CodexFlow.Core.Gateway;

/// <summary>
/// Thrown when the gateway rejects a message before it enters the per-session queue.
/// </summary>
public sealed class GatewayMessageRejectedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GatewayMessageRejectedException"/> class.
    /// </summary>
    public GatewayMessageRejectedException()
    {
        Reason = "gateway_message_rejected";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GatewayMessageRejectedException"/> class.
    /// </summary>
    /// <param name="message">User-facing rejection message.</param>
    public GatewayMessageRejectedException(string message)
        : base(message)
    {
        Reason = "gateway_message_rejected";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GatewayMessageRejectedException"/> class.
    /// </summary>
    /// <param name="message">User-facing rejection message.</param>
    /// <param name="innerException">Inner exception.</param>
    public GatewayMessageRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = "gateway_message_rejected";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GatewayMessageRejectedException"/> class.
    /// </summary>
    /// <param name="reason">Stable machine-readable rejection reason.</param>
    /// <param name="message">User-facing rejection message.</param>
    public GatewayMessageRejectedException(string reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    /// Stable machine-readable rejection reason.
    /// </summary>
    public string Reason { get; }
}
