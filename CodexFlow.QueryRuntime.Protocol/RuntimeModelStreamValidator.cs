using System.Text.Json;

namespace CodexFlow.QueryRuntime.Protocol;

/// <summary>
/// Provider-neutral fail-closed validation for one model stream. It validates
/// protocol shape and ordering without owning Agent Loop state.
/// </summary>
public sealed class RuntimeModelStreamValidator
{
    private bool _completed;

    public int EventCount { get; private set; }

    public RuntimeModelStopReason? StopReason { get; private set; }

    public void Apply(RuntimeModelStreamEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        if (_completed)
        {
            ThrowProtocol("model_event_after_completion", "The model emitted an event after completion.");
        }

        switch (runtimeEvent)
        {
            case RuntimeTextDeltaEvent text when text.Text == null:
                ThrowProtocol("null_text_delta", "A text delta cannot be null.");
                break;
            case RuntimeReasoningDeltaEvent reasoning when reasoning.Text == null:
                ThrowProtocol("null_reasoning_delta", "A reasoning delta cannot be null.");
                break;
            case RuntimeToolCallEvent toolCall:
                ValidateToolCall(toolCall.Call);
                break;
            case RuntimeUsageEvent usage:
                ValidateUsage(usage.Usage);
                break;
            case RuntimeWarningEvent warning when
                string.IsNullOrWhiteSpace(warning.Warning.Code) ||
                string.IsNullOrWhiteSpace(warning.Warning.Message):
                ThrowProtocol("invalid_model_warning", "A model warning requires a code and message.");
                break;
            case RuntimeModelCompletedEvent completed:
                _completed = true;
                StopReason = completed.StopReason;
                break;
        }

        EventCount++;
    }

    public void Complete()
    {
        if (!_completed)
        {
            ThrowProtocol("missing_model_completion", "The model stream ended without a completion event.");
        }
    }

    private static void ValidateToolCall(RuntimeToolCall call)
    {
        if (string.IsNullOrWhiteSpace(call.InvocationId.Value) || string.IsNullOrWhiteSpace(call.Name))
        {
            ThrowProtocol("invalid_tool_call_identity", "A tool call requires an invocation ID and name.");
        }
        if (call.Arguments.ValueKind != JsonValueKind.Object)
        {
            ThrowProtocol("malformed_tool_arguments", "Tool call arguments must be a JSON object.");
        }
    }

    private static void ValidateUsage(RuntimeUsage usage)
    {
        if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.TotalTokens < 0 ||
            usage.Additional?.Values.Any(static value => value < 0) == true)
        {
            ThrowProtocol("negative_model_usage", "Model usage values cannot be negative.");
        }
        if (usage.InputTokens.HasValue && usage.OutputTokens.HasValue && usage.TotalTokens.HasValue &&
            usage.TotalTokens.Value < usage.InputTokens.Value + usage.OutputTokens.Value)
        {
            ThrowProtocol("inconsistent_model_usage", "Total model tokens cannot be smaller than input plus output tokens.");
        }
    }

    private static void ThrowProtocol(string code, string message)
        => throw new RuntimeModelStreamValidationException(new RuntimeError(
            RuntimeErrorCategory.ProviderProtocol,
            code,
            message));
}

public sealed class RuntimeModelStreamValidationException(RuntimeError error) : Exception(error.Message)
{
    public RuntimeError Error { get; } = error;
}
