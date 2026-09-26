using System.Text.Json;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Models.Diagnostics;

/// <summary>
/// Allow-list projections of the three observation points into
/// <see cref="QreSemanticRequest"/>. No projection keeps text, arguments, schema
/// descriptions, URLs or plaintext names: names become ordinal, case-sensitive
/// package-local aliases and relations are computed in memory.
/// </summary>
internal static class QreSemanticProjector
{
    internal const string ToolAliasKind = "tool";
    internal const string ModelAliasKind = "model";

    private static readonly HashSet<string> ChatCompletionsFields = new(StringComparer.Ordinal)
    {
        "model", "messages", "tools", "tool_choice", "temperature", "max_tokens", "max_completion_tokens",
        "response_format", "stream", "stream_options", "format", "extra_body", "options", "top_p",
        "parallel_tool_calls"
    };

    private static readonly HashSet<string> ResponsesFields = new(StringComparer.Ordinal)
    {
        "model", "input", "tools", "tool_choice", "temperature", "max_output_tokens", "response_format", "text",
        "stream", "stream_options", "structured_outputs", "reasoning", "include_reasoning", "enable_reasoning",
        "stream_reasoning", "top_p", "parallel_tool_calls"
    };

    private static readonly HashSet<string> AnthropicFields = new(StringComparer.Ordinal)
    {
        "model", "max_tokens", "messages", "system", "tools", "tool_choice", "temperature", "stream", "thinking",
        "top_p"
    };

    public static QreSemanticRequest FromRuntime(RuntimeModelRequest request, QreOutboundDiagnostics diagnostics)
    {
        var declared = request.Tools.Select(static tool => tool.CanonicalName).ToArray();
        var messages = new List<QreSemanticMessage>(request.Messages.Count);
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        int linked = 0, unlinked = 0;
        foreach (var message in request.Messages)
        {
            var kinds = new List<string>(message.Items.Count);
            foreach (var item in message.Items)
            {
                switch (item)
                {
                    case RuntimeTextItem:
                        kinds.Add("text");
                        break;
                    case RuntimeReasoningItem:
                        kinds.Add("reasoning");
                        break;
                    case RuntimeToolCallItem call:
                        kinds.Add("tool_call");
                        callIds.Add(call.Call.InvocationId.Value);
                        break;
                    case RuntimeToolResultItem result:
                        kinds.Add("tool_result");
                        CountLink(callIds, result.Result.InvocationId.Value, ref linked, ref unlinked);
                        break;
                    case RuntimeArtifactItem:
                        kinds.Add("artifact");
                        break;
                    default:
                        kinds.Add("other");
                        break;
                }
            }
            messages.Add(new QreSemanticMessage { Role = Role(message.Role), ItemKinds = kinds });
        }

        var parameters = request.Parameters;
        var required = string.IsNullOrWhiteSpace(parameters.RequiredToolName) ? null : parameters.RequiredToolName;
        return new QreSemanticRequest
        {
            Protocol = "runtime",
            Messages = Messages(messages, linked, unlinked),
            Tools = Tools(declared, diagnostics),
            ToolChoice = required == null
                ? new QreSemanticToolChoice
                {
                    State = QreDiagnosticFieldStates.Absent,
                    Mode = "unspecified",
                    RequiredToolRelation = "not_applicable"
                }
                : new QreSemanticToolChoice
                {
                    State = QreDiagnosticFieldStates.Present,
                    Mode = "require_specific",
                    RequiredToolAlias = diagnostics.Alias(ToolAliasKind, required),
                    RequiredToolRelation = Relation(required, declared)
                },
            Temperature = Number(parameters.Temperature, "parameters.temperature"),
            MaxOutputTokens = Number(parameters.MaxOutputTokens, "parameters.maxOutputTokens"),
            // RequireJsonObject is a non-nullable bool: false and "unspecified" are equivalent.
            ResponseFormat = new QreSemanticValue
            {
                State = QreDiagnosticFieldStates.Present,
                Value = parameters.RequireJsonObject ? "json_object" : "none",
                SourcePath = "parameters.requireJsonObject"
            },
            Stream = Unobserved(),
            Model = string.IsNullOrWhiteSpace(parameters.Model)
                ? new QreSemanticModel { State = QreDiagnosticFieldStates.Absent, Source = "unknown" }
                : new QreSemanticModel
                {
                    State = QreDiagnosticFieldStates.Present,
                    Alias = diagnostics.Alias(ModelAliasKind, parameters.Model.Trim()),
                    Source = "explicit"
                },
            UncheckedFieldCount = 0
        };
    }

    public static QreSemanticRequest FromAdapter(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        string? descriptorDefaultModel,
        QreOutboundDiagnostics diagnostics)
    {
        var functions = options.Tools?.OfType<AIFunction>().Select(static tool => tool.Name).ToArray() ?? [];
        var nonFunctionTools = (options.Tools?.Count ?? 0) - functions.Length;
        var projected = new List<QreSemanticMessage>(messages.Count);
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        int linked = 0, unlinked = 0;
        foreach (var message in messages)
        {
            var kinds = new List<string>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent:
                        kinds.Add("reasoning");
                        break;
                    case TextContent:
                        kinds.Add("text");
                        break;
                    case FunctionCallContent call:
                        kinds.Add("tool_call");
                        callIds.Add(call.CallId);
                        break;
                    case FunctionResultContent result:
                        kinds.Add("tool_result");
                        CountLink(callIds, result.CallId, ref linked, ref unlinked);
                        break;
                    case DataContent or UriContent:
                        kinds.Add("image");
                        break;
                    default:
                        kinds.Add("other");
                        break;
                }
            }
            projected.Add(new QreSemanticMessage { Role = Role(message.Role), ItemKinds = kinds });
        }

        QreSemanticModel model;
        if (!string.IsNullOrWhiteSpace(options.ModelId))
        {
            model = new QreSemanticModel
            {
                State = QreDiagnosticFieldStates.Present,
                Alias = diagnostics.Alias(ModelAliasKind, options.ModelId.Trim()),
                Source = "explicit"
            };
        }
        else if (!string.IsNullOrWhiteSpace(descriptorDefaultModel))
        {
            model = new QreSemanticModel
            {
                State = QreDiagnosticFieldStates.Absent,
                Source = "descriptor_default",
                EffectiveAlias = diagnostics.Alias(ModelAliasKind, descriptorDefaultModel.Trim())
            };
        }
        else
        {
            model = new QreSemanticModel { State = QreDiagnosticFieldStates.Absent, Source = "unknown" };
        }

        return new QreSemanticRequest
        {
            Protocol = "meai",
            Messages = Messages(projected, linked, unlinked),
            Tools = Tools(functions, diagnostics),
            ToolChoice = AdapterToolChoice(options.ToolMode, functions, diagnostics),
            Temperature = options.Temperature is { } temperature
                ? new QreSemanticNumber
                {
                    State = QreDiagnosticFieldStates.Present,
                    Value = temperature,
                    SourcePath = "ChatOptions.Temperature"
                }
                : Absent("ChatOptions.Temperature"),
            MaxOutputTokens = Number(options.MaxOutputTokens, "ChatOptions.MaxOutputTokens"),
            ResponseFormat = options.ResponseFormat switch
            {
                null => new QreSemanticValue
                {
                    State = QreDiagnosticFieldStates.Absent,
                    Value = "none",
                    SourcePath = "ChatOptions.ResponseFormat"
                },
                ChatResponseFormatJson { Schema: not null } => Value("json_schema", "ChatOptions.ResponseFormat"),
                ChatResponseFormatJson => Value("json_object", "ChatOptions.ResponseFormat"),
                ChatResponseFormatText => Value("text", "ChatOptions.ResponseFormat"),
                _ => Value("other", "ChatOptions.ResponseFormat")
            },
            Stream = Value("true", "GetStreamingResponseAsync"),
            Model = model,
            // AdditionalProperties and RawRepresentationFactory are never serialized
            // or stringified; only their presence is counted.
            UncheckedFieldCount = (options.AdditionalProperties?.Count ?? 0) +
                                  (options.RawRepresentationFactory == null ? 0 : 1) +
                                  Math.Max(0, nonFunctionTools),
            Notes = nonFunctionTools > 0 ? ["non_function_tools_present"] : []
        };
    }

    /// <summary>
    /// Projects a captured, complete UTF-8 JSON request body. Returns
    /// <c>unsupported</c> for shapes no registered rule understands.
    /// </summary>
    public static (QreSemanticRequest? Request, string Status, string? Reason) FromHttpJson(
        ReadOnlySpan<byte> utf8Json,
        string apiMode,
        int maxJsonDepth,
        QreOutboundDiagnostics diagnostics)
    {
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = maxJsonDepth });
            if (!JsonDocument.TryParseValue(ref reader, out var parsed))
            {
                return (null, QreDiagnosticCaptureStatus.Failed, "request_json_invalid");
            }
            document = parsed;
        }
        catch (JsonException)
        {
            return (null, QreDiagnosticCaptureStatus.Failed, "request_json_invalid_or_too_deep");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, QreDiagnosticCaptureStatus.Unsupported, "request_json_not_object");
            }
            return apiMode switch
            {
                QreApiModeNames.ChatCompletions => (ChatCompletions(root, diagnostics), QreDiagnosticCaptureStatus.Complete, null),
                QreApiModeNames.Responses => (Responses(root, diagnostics), QreDiagnosticCaptureStatus.Complete, null),
                QreApiModeNames.AnthropicMessages => (Anthropic(root, diagnostics), QreDiagnosticCaptureStatus.Complete, null),
                _ => (null, QreDiagnosticCaptureStatus.Unsupported, "api_mode_unknown")
            };
        }
    }

    private static QreSemanticRequest ChatCompletions(JsonElement root, QreOutboundDiagnostics diagnostics)
    {
        var declared = ToolNames(root, static tool =>
            tool.TryGetProperty("function", out var function) ? function : tool);
        var messages = new List<QreSemanticMessage>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        int linked = 0, unlinked = 0;
        var messagesState = QreDiagnosticFieldStates.Absent;
        if (root.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            messagesState = QreDiagnosticFieldStates.Present;
            foreach (var message in list.EnumerateArray())
            {
                var role = StringProperty(message, "role");
                var kinds = new List<string>();
                if (message.TryGetProperty("reasoning_content", out var reasoning) &&
                    reasoning.ValueKind == JsonValueKind.String)
                {
                    kinds.Add("reasoning");
                }
                AddContentKinds(message, kinds, "text", "image_url");
                if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        kinds.Add("tool_call");
                        if (StringProperty(call, "id") is { } id)
                        {
                            callIds.Add(id);
                        }
                    }
                }
                if (role == "tool")
                {
                    kinds.RemoveAll(static kind => kind == "text");
                    kinds.Add("tool_result");
                    CountLink(callIds, StringProperty(message, "tool_call_id"), ref linked, ref unlinked);
                }
                messages.Add(new QreSemanticMessage { Role = NormalizeRole(role), ItemKinds = kinds });
            }
        }

        var toolChoice = root.TryGetProperty("tool_choice", out var choice)
            ? choice.ValueKind switch
            {
                JsonValueKind.String => StringChoice(choice.GetString()),
                JsonValueKind.Object when choice.TryGetProperty("function", out var function) &&
                                          StringProperty(function, "name") is { } name =>
                    SpecificChoice(name, declared, diagnostics),
                _ => UnknownChoice()
            }
            : AbsentChoice();

        var temperature = NumberProperty(root, "temperature");
        if (temperature == null && root.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
        {
            var nested = NumberProperty(options, "temperature");
            temperature = nested == null ? null : nested with { SourcePath = "options.temperature" };
        }
        temperature ??= Absent("temperature");

        var maxTokens = NumberProperty(root, "max_completion_tokens") ?? NumberProperty(root, "max_tokens");
        return new QreSemanticRequest
        {
            Protocol = QreApiModeNames.ChatCompletions,
            Messages = Messages(messages, linked, unlinked, messagesState),
            Tools = Tools(declared, diagnostics, root.TryGetProperty("tools", out _)),
            ToolChoice = toolChoice,
            Temperature = temperature,
            MaxOutputTokens = maxTokens ?? Absent("max_tokens"),
            ResponseFormat = ResponseFormat(root, "response_format", alternatives: ["format", "extra_body.structured_outputs"]),
            Stream = BoolProperty(root, "stream"),
            Model = HttpModel(root, diagnostics),
            UncheckedFieldCount = CountUnchecked(root, ChatCompletionsFields)
        };
    }

    private static QreSemanticRequest Responses(JsonElement root, QreOutboundDiagnostics diagnostics)
    {
        var declared = ToolNames(root, static tool => tool);
        var messages = new List<QreSemanticMessage>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        int linked = 0, unlinked = 0;
        var messagesState = QreDiagnosticFieldStates.Absent;
        if (root.TryGetProperty("input", out var input))
        {
            messagesState = QreDiagnosticFieldStates.Present;
            if (input.ValueKind == JsonValueKind.String)
            {
                messages.Add(new QreSemanticMessage { Role = "user", ItemKinds = ["text"] });
            }
            else if (input.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in input.EnumerateArray())
                {
                    switch (StringProperty(item, "type"))
                    {
                        case "function_call":
                            if (StringProperty(item, "call_id") is { } callId)
                            {
                                callIds.Add(callId);
                            }
                            messages.Add(new QreSemanticMessage { Role = "assistant", ItemKinds = ["tool_call"] });
                            break;
                        case "function_call_output":
                            CountLink(callIds, StringProperty(item, "call_id"), ref linked, ref unlinked);
                            messages.Add(new QreSemanticMessage { Role = "tool", ItemKinds = ["tool_result"] });
                            break;
                        case "reasoning":
                            messages.Add(new QreSemanticMessage { Role = "assistant", ItemKinds = ["reasoning"] });
                            break;
                        default:
                            var kinds = new List<string>();
                            AddContentKinds(item, kinds, "input_text", "input_image");
                            messages.Add(new QreSemanticMessage
                            {
                                Role = NormalizeRole(StringProperty(item, "role")),
                                ItemKinds = kinds
                            });
                            break;
                    }
                }
            }
        }

        var toolChoice = root.TryGetProperty("tool_choice", out var choice)
            ? choice.ValueKind switch
            {
                JsonValueKind.String => StringChoice(choice.GetString()),
                JsonValueKind.Object when StringProperty(choice, "name") is { } name =>
                    SpecificChoice(name, declared, diagnostics),
                _ => UnknownChoice()
            }
            : AbsentChoice();

        var format = ResponseFormat(root, "response_format", alternatives: ["structured_outputs"]);
        if (format.State == QreDiagnosticFieldStates.Absent &&
            root.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.Object &&
            text.TryGetProperty("format", out var textFormat))
        {
            format = ResponseFormat(text, "format", []) with { SourcePath = "text.format" };
        }

        return new QreSemanticRequest
        {
            Protocol = QreApiModeNames.Responses,
            Messages = Messages(messages, linked, unlinked, messagesState),
            Tools = Tools(declared, diagnostics, root.TryGetProperty("tools", out _)),
            ToolChoice = toolChoice,
            Temperature = NumberProperty(root, "temperature") ?? Absent("temperature"),
            MaxOutputTokens = NumberProperty(root, "max_output_tokens") ?? Absent("max_output_tokens"),
            ResponseFormat = format,
            Stream = BoolProperty(root, "stream"),
            Model = HttpModel(root, diagnostics),
            UncheckedFieldCount = CountUnchecked(root, ResponsesFields)
        };
    }

    private static QreSemanticRequest Anthropic(JsonElement root, QreOutboundDiagnostics diagnostics)
    {
        var declared = ToolNames(root, static tool => tool);
        var messages = new List<QreSemanticMessage>();
        var notes = new List<string>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        int linked = 0, unlinked = 0;
        if (root.TryGetProperty("system", out var system))
        {
            // Anthropic hoists system content to a top-level field; the normalized
            // form restores it as a leading system message.
            var parts = system.ValueKind == JsonValueKind.Array ? system.GetArrayLength() : 1;
            messages.Add(new QreSemanticMessage
            {
                Role = "system",
                ItemKinds = Enumerable.Repeat("text", Math.Max(1, parts)).ToArray()
            });
            notes.Add("system_hoisted_to_top_level");
        }

        var messagesState = QreDiagnosticFieldStates.Absent;
        if (root.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            messagesState = QreDiagnosticFieldStates.Present;
            foreach (var message in list.EnumerateArray())
            {
                var role = NormalizeRole(StringProperty(message, "role"));
                var kinds = new List<string>();
                if (message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        kinds.Add("text");
                    }
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            switch (StringProperty(block, "type"))
                            {
                                case "text":
                                    kinds.Add("text");
                                    break;
                                case "thinking":
                                case "redacted_thinking":
                                    kinds.Add("reasoning");
                                    break;
                                case "tool_use":
                                    kinds.Add("tool_call");
                                    if (StringProperty(block, "id") is { } id)
                                    {
                                        callIds.Add(id);
                                    }
                                    break;
                                case "tool_result":
                                    kinds.Add("tool_result");
                                    CountLink(callIds, StringProperty(block, "tool_use_id"), ref linked, ref unlinked);
                                    break;
                                case "image":
                                    kinds.Add("image");
                                    break;
                                default:
                                    kinds.Add("other");
                                    break;
                            }
                        }
                    }
                }
                if (kinds.Count > 0 && kinds.All(static kind => kind == "tool_result"))
                {
                    // Anthropic carries tool results in user-role messages.
                    role = "tool";
                    if (!notes.Contains("tool_result_in_user_role"))
                    {
                        notes.Add("tool_result_in_user_role");
                    }
                }
                messages.Add(new QreSemanticMessage { Role = role, ItemKinds = kinds });
            }
        }

        var toolChoice = AbsentChoice();
        if (root.TryGetProperty("tool_choice", out var choice) && choice.ValueKind == JsonValueKind.Object)
        {
            toolChoice = StringProperty(choice, "type") switch
            {
                "auto" => StringChoice("auto"),
                "any" => StringChoice("required"),
                "none" => StringChoice("none"),
                "tool" when StringProperty(choice, "name") is { } name => SpecificChoice(name, declared, diagnostics),
                _ => UnknownChoice()
            };
        }

        return new QreSemanticRequest
        {
            Protocol = QreApiModeNames.AnthropicMessages,
            Messages = Messages(messages, linked, unlinked, messagesState),
            Tools = Tools(declared, diagnostics, root.TryGetProperty("tools", out _)),
            ToolChoice = toolChoice,
            Temperature = NumberProperty(root, "temperature") ?? Absent("temperature"),
            MaxOutputTokens = NumberProperty(root, "max_tokens") ?? Absent("max_tokens"),
            // The Messages API has no JSON response-format field.
            ResponseFormat = new QreSemanticValue
            {
                State = QreDiagnosticFieldStates.Absent,
                Value = "none",
                SourcePath = "unsupported_by_protocol"
            },
            Stream = BoolProperty(root, "stream"),
            Model = HttpModel(root, diagnostics),
            UncheckedFieldCount = CountUnchecked(root, AnthropicFields),
            Notes = notes
        };
    }

    private static string[] ToolNames(JsonElement root, Func<JsonElement, JsonElement> nameHolder)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var names = new List<string>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind == JsonValueKind.Object && StringProperty(nameHolder(tool), "name") is { } name)
            {
                names.Add(name);
            }
        }
        return names.ToArray();
    }

    private static void AddContentKinds(JsonElement message, List<string> kinds, string textType, string imageType)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return;
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            kinds.Add("text");
            return;
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var part in content.EnumerateArray())
        {
            var type = part.ValueKind == JsonValueKind.Object ? StringProperty(part, "type") : null;
            if (type == textType || type == "text" || type == "output_text" || part.ValueKind == JsonValueKind.String)
            {
                kinds.Add("text");
            }
            else if (type == imageType || type == "image_url" || type == "image")
            {
                kinds.Add("image");
            }
            else
            {
                kinds.Add("other");
            }
        }
    }

    private static QreSemanticValue ResponseFormat(JsonElement root, string property, string[] alternatives)
    {
        if (root.TryGetProperty(property, out var format))
        {
            return format.ValueKind switch
            {
                JsonValueKind.Null => new QreSemanticValue { State = QreDiagnosticFieldStates.Present, SourcePath = property },
                JsonValueKind.String or JsonValueKind.Object => FormatCategory(format, property),
                _ => new QreSemanticValue { State = QreDiagnosticFieldStates.Invalid, SourcePath = property }
            };
        }
        foreach (var path in alternatives)
        {
            if (TryGetPath(root, path, out var alternative))
            {
                return new QreSemanticValue
                {
                    State = QreDiagnosticFieldStates.Present,
                    Value = alternative.ValueKind == JsonValueKind.Null ? null : "alternative_json_only",
                    SourcePath = path
                };
            }
        }
        return new QreSemanticValue { State = QreDiagnosticFieldStates.Absent, Value = "none", SourcePath = property };
    }

    private static QreSemanticValue FormatCategory(JsonElement format, string path)
    {
        var type = format.ValueKind switch
        {
            JsonValueKind.Object => StringProperty(format, "type"),
            JsonValueKind.String => format.GetString(),
            _ => null
        };
        return Value(type switch
        {
            "json_object" or "json" => "json_object",
            "json_schema" => "json_schema",
            "text" => "text",
            _ => "other"
        }, path);
    }

    private static bool TryGetPath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }
        return true;
    }

    private static QreSemanticModel HttpModel(JsonElement root, QreOutboundDiagnostics diagnostics)
        => StringProperty(root, "model") is { Length: > 0 } model
            ? new QreSemanticModel
            {
                State = QreDiagnosticFieldStates.Present,
                Alias = diagnostics.Alias(ModelAliasKind, model.Trim()),
                Source = "explicit"
            }
            : new QreSemanticModel { State = QreDiagnosticFieldStates.Absent, Source = "unknown" };

    private static QreSemanticToolChoice AdapterToolChoice(
        ChatToolMode? mode,
        IReadOnlyList<string> declared,
        QreOutboundDiagnostics diagnostics)
        => mode switch
        {
            null => AbsentChoice(),
            NoneChatToolMode => StringChoice("none"),
            AutoChatToolMode => StringChoice("auto"),
            RequiredChatToolMode { RequiredFunctionName: { Length: > 0 } name } => SpecificChoice(name, declared, diagnostics),
            RequiredChatToolMode => StringChoice("required"),
            _ => UnknownChoice()
        };

    private static QreSemanticToolChoice StringChoice(string? value)
        => new()
        {
            State = QreDiagnosticFieldStates.Present,
            Mode = value switch
            {
                "none" => "none",
                "auto" => "auto",
                "required" => "required",
                _ => "other"
            },
            RequiredToolRelation = "not_applicable"
        };

    private static QreSemanticToolChoice SpecificChoice(
        string name,
        IReadOnlyList<string> declared,
        QreOutboundDiagnostics diagnostics)
        => new()
        {
            State = QreDiagnosticFieldStates.Present,
            Mode = "require_specific",
            RequiredToolAlias = diagnostics.Alias(ToolAliasKind, name),
            RequiredToolRelation = Relation(name, declared)
        };

    private static QreSemanticToolChoice AbsentChoice()
        => new()
        {
            State = QreDiagnosticFieldStates.Absent,
            Mode = "unspecified",
            RequiredToolRelation = "not_applicable"
        };

    private static QreSemanticToolChoice UnknownChoice()
        => new()
        {
            State = QreDiagnosticFieldStates.Present,
            Mode = "other",
            RequiredToolRelation = "not_applicable"
        };

    /// <summary>Compares plaintext in memory only; the output is a safe relation marker.</summary>
    private static string Relation(string required, IReadOnlyList<string> declared)
    {
        if (declared.Any(name => string.Equals(name, required, StringComparison.Ordinal)))
        {
            return "declared_exact";
        }
        return declared.Any(name => string.Equals(name, required, StringComparison.OrdinalIgnoreCase))
            ? "case_only_mismatch"
            : "not_declared";
    }

    private static QreSemanticTools Tools(
        IReadOnlyList<string> names,
        QreOutboundDiagnostics diagnostics,
        bool fieldPresent = true)
        => new()
        {
            State = fieldPresent || names.Count > 0 ? QreDiagnosticFieldStates.Present : QreDiagnosticFieldStates.Absent,
            Count = names.Count,
            Aliases = names.Select(name => diagnostics.Alias(ToolAliasKind, name)).ToArray()
        };

    private static QreSemanticMessages Messages(
        IReadOnlyList<QreSemanticMessage> items,
        int linked,
        int unlinked,
        string state = QreDiagnosticFieldStates.Present)
        => new()
        {
            State = state,
            Count = items.Count,
            Items = items,
            LinkedToolResults = linked,
            UnlinkedToolResults = unlinked
        };

    private static void CountLink(HashSet<string> callIds, string? callId, ref int linked, ref int unlinked)
    {
        if (callId != null && callIds.Contains(callId))
        {
            linked++;
        }
        else
        {
            unlinked++;
        }
    }

    private static int CountUnchecked(JsonElement root, HashSet<string> registered)
    {
        var count = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (!registered.Contains(property.Name))
            {
                count++;
            }
        }
        return count;
    }

    private static QreSemanticNumber Number(double? value, string path)
        => value is { } number
            ? new QreSemanticNumber { State = QreDiagnosticFieldStates.Present, Value = number, SourcePath = path }
            : Absent(path);

    private static QreSemanticNumber? NumberProperty(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Null)
            return new QreSemanticNumber { State = QreDiagnosticFieldStates.Present, SourcePath = property };
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            return Number(number, property);
        return new QreSemanticNumber { State = QreDiagnosticFieldStates.Invalid, SourcePath = property };
    }

    private static QreSemanticNumber Absent(string path)
        => new() { State = QreDiagnosticFieldStates.Absent, SourcePath = path };

    private static QreSemanticValue BoolProperty(JsonElement root, string property)
        => !root.TryGetProperty(property, out var value)
            ? new QreSemanticValue { State = QreDiagnosticFieldStates.Absent, SourcePath = property }
            : value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => Value(value.GetBoolean() ? "true" : "false", property),
                JsonValueKind.Null => new QreSemanticValue { State = QreDiagnosticFieldStates.Present, SourcePath = property },
                _ => new QreSemanticValue { State = QreDiagnosticFieldStates.Invalid, SourcePath = property }
            };

    private static QreSemanticValue Value(string value, string path)
        => new() { State = QreDiagnosticFieldStates.Present, Value = value, SourcePath = path };

    private static QreSemanticValue Unobserved()
        => new() { State = QreDiagnosticFieldStates.Unobserved };

    private static string? StringProperty(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Role(RuntimeMessageRole role) => role switch
    {
        RuntimeMessageRole.System => "system",
        RuntimeMessageRole.User => "user",
        RuntimeMessageRole.Assistant => "assistant",
        RuntimeMessageRole.Tool => "tool",
        _ => "other"
    };

    private static string Role(ChatRole role)
        => role == ChatRole.System ? "system"
            : role == ChatRole.User ? "user"
            : role == ChatRole.Assistant ? "assistant"
            : role == ChatRole.Tool ? "tool"
            : "other";

    private static string NormalizeRole(string? role) => role switch
    {
        "system" or "developer" => "system",
        "user" => "user",
        "assistant" => "assistant",
        "tool" => "tool",
        _ => "other"
    };
}
