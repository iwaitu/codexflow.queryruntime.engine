namespace CodexFlow.QueryRuntime.Engine.V2;

/// <summary>A provider's opaque reasoning cannot fit the configured item limit intact.</summary>
public sealed class RuntimeOpaqueReasoningLimitException : InvalidOperationException
{
    public const string ErrorCode = "opaque_reasoning_item_too_large";

    public RuntimeOpaqueReasoningLimitException(int estimatedTokens, int maxItemTokens)
        : base($"{ErrorCode}: opaque reasoning exceeds the item limit ({estimatedTokens} > {maxItemTokens}); no history was appended.")
    {
        EstimatedTokens = estimatedTokens;
        MaxItemTokens = maxItemTokens;
    }

    public string Code => ErrorCode;
    public int EstimatedTokens { get; }
    public int MaxItemTokens { get; }
}
