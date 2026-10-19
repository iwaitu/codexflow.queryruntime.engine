using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

/// <summary>
/// Deterministic Q6 repair request construction and strict response parsing.
/// The request restates only the exposed tool names and complete input schemas
/// (never descriptions or the whole catalog); the response is accepted only as
/// exactly one registered read-only tool call or a tool-free final JSON object.
/// </summary>
internal static class RuntimeToolCallRepair
{
    private static readonly JsonWriterOptions CompactWriter = new() { Indented = false };

    public static RuntimeModelRequest BuildRequest(
        RuntimeModelRequest original,
        IReadOnlyList<RuntimeToolDescriptor> exposedTools,
        RuntimeToolCallRepairPolicy policy)
    {
        var instruction = policy.Instruction.TrimEnd() + "\n" + SerializeToolDefinitions(exposedTools);
        var messages = original.Messages
            .Append(new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(instruction)]))
            .ToArray();
        return original with
        {
            Messages = Array.AsReadOnly(messages),
            Tools = Array.Empty<RuntimeToolDescriptor>(),
            Parameters = original.Parameters with
            {
                RequiredToolName = null,
                RequireJsonObject = false,
                ResponseFormat = RuntimeResponseFormat.JsonObject()
            }
        };
    }

    public static int Estimate(RuntimeModelRequest request)
        => request.Messages.Sum(RuntimeTokenEstimator.Estimate);

    public static RepairParseResult Parse(
        string text,
        IReadOnlyList<RuntimeToolDescriptor> exposedTools,
        RuntimeStepId stepId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text.Trim(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException)
        {
            return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeInvalidJson);
        }
        catch (ArgumentException)
        {
            return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeInvalidJson);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeInvalidShape);
            }
            var properties = root.EnumerateObject().ToArray();
            if (!properties.Any(static property => property.NameEquals("tool_calls")))
            {
                return RepairParseResult.FinalPayload(TextDigest(text));
            }
            if (properties.Length != 1)
            {
                // Mixed protocol and answer keys, or a duplicated tool_calls key.
                return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeInvalidShape);
            }
            var calls = properties[0].Value;
            if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0)
            {
                return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeInvalidShape);
            }
            if (calls.GetArrayLength() > 1)
            {
                return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeMultipleToolCalls);
            }
            var entry = calls[0];
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeInvalidShape);
            }
            var entryProperties = entry.EnumerateObject().ToArray();
            if (entryProperties.Length != 2 ||
                !entry.TryGetProperty("name", out var nameElement) ||
                !entry.TryGetProperty("arguments", out var arguments) ||
                nameElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameElement.GetString()) ||
                arguments.ValueKind != JsonValueKind.Object)
            {
                return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeInvalidShape);
            }
            var name = nameElement.GetString()!;
            // Exact match only: the repair can never guess a tool by case or prefix.
            var descriptor = exposedTools.FirstOrDefault(tool =>
                string.Equals(tool.CanonicalName, name, StringComparison.Ordinal));
            if (descriptor == null)
            {
                return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeUnknownTool);
            }
            if (descriptor.SideEffect != RuntimeToolSideEffect.ReadOnly)
            {
                return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeSideEffectNotReadOnly);
            }
            RuntimeNormalizedArguments normalized;
            try
            {
                normalized = RuntimeToolArgumentNormalizer.NormalizeAndValidate(descriptor.InputSchema, arguments);
            }
            catch (Exception ex) when (ex is RuntimeToolPreparationException or ArgumentException)
            {
                return RepairParseResult.Rejected(RuntimeToolCallRepairCodes.OutcomeInvalidArguments);
            }
            var call = new RuntimeToolCall(
                new RuntimeInvocationId($"{stepId.Value}:repair:0"),
                descriptor.CanonicalName,
                normalized.Value);
            return RepairParseResult.ToolCall(call, ArgumentsDigest(call.Arguments), TextDigest(text));
        }
    }

    public static string ArgumentsDigest(JsonElement arguments)
        => Sha256(arguments.GetRawText());

    public static string TextDigest(string text)
        => Sha256(text);

    private static string SerializeToolDefinitions(IReadOnlyList<RuntimeToolDescriptor> tools)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, CompactWriter))
        {
            writer.WriteStartArray();
            foreach (var tool in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.CanonicalName);
                writer.WritePropertyName("input_schema");
                tool.InputSchema.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed record RepairParseResult(
    string Outcome,
    RuntimeToolCall? Call,
    string? ArgumentsSha256,
    string? ResponseSha256)
{
    public bool Success => RuntimeToolCallRepairCodes.IsSuccess(Outcome);

    public static RepairParseResult Rejected(string outcome) => new(outcome, null, null, null);

    public static RepairParseResult FinalPayload(string responseSha256)
        => new(RuntimeToolCallRepairCodes.OutcomeFinalPayloadReturned, null, null, responseSha256);

    public static RepairParseResult ToolCall(RuntimeToolCall call, string argumentsSha256, string responseSha256)
        => new(RuntimeToolCallRepairCodes.OutcomeToolCallRecovered, call, argumentsSha256, responseSha256);
}
