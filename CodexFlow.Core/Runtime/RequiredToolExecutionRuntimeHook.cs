namespace CodexFlow.Core.Runtime;

/// <summary>
/// Built-in stop hook that prevents a query from ending before its required tool contract is met.
/// </summary>
public sealed class RequiredToolExecutionRuntimeHook : IRuntimeHook
{
    public ValueTask<AfterModelResponseHookResult> OnAfterModelResponseAsync(
        AfterModelResponseContext context,
        CancellationToken ct = default)
        => ValueTask.FromResult(AfterModelResponseHookResult.None);

    public ValueTask<BeforeStopHookResult> OnBeforeStopAsync(
        BeforeStopContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var contract = context.Request.RequiredToolContract;
        if (contract == null || !context.Request.EnableTools)
        {
            return ValueTask.FromResult(BeforeStopHookResult.None);
        }

        if (contract.IsSatisfiedBy(context.ExecutedToolNames, context.SuccessfulToolNames))
        {
            return ValueTask.FromResult(BeforeStopHookResult.None);
        }

        var availableToolName = contract.ResolveRequiredToolName(
            context.Request.AvailableToolsProvider?.Invoke() ?? context.Request.AvailableTools);
        var feedback = string.IsNullOrWhiteSpace(contract.Feedback)
            ? BuildDefaultFeedback(contract, availableToolName)
            : contract.Feedback.Trim();

        return ValueTask.FromResult(new BeforeStopHookResult
        {
            Continue = true,
            Message = feedback,
            Reason = $"required tool contract '{contract.Name}' was not satisfied",
            AllowToolCallsOnNextRound = true,
            RequiredToolNameForNextRound = availableToolName,
            MaxContinuationAttempts = contract.MaxContinuationAttempts,
            ExhaustionDetailCode = QueryTerminalDetailCodes.RequiredToolContractViolation,
            ExhaustionMessage = string.IsNullOrWhiteSpace(contract.ExhaustionMessage)
                ? $"Required tool contract '{contract.Name}' was not satisfied after {Math.Max(1, contract.MaxContinuationAttempts)} continuation attempt(s)."
                : contract.ExhaustionMessage.Trim()
        });
    }

    private static string BuildDefaultFeedback(
        RequiredToolExecutionContract contract,
        string? requiredToolName)
    {
        var required = string.IsNullOrWhiteSpace(requiredToolName)
            ? "one of the required tools"
            : $"`{requiredToolName}`";

        return
            $"Required tool contract `{contract.Name}` is not satisfied. " +
            $"Before stopping, you must call {required}. " +
            $"Acceptable tools: {contract.FormatToolList()}. " +
            "Do not summarize or explain first; issue the tool call now.";
    }
}
