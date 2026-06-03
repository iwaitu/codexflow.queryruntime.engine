using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Agents;

public class DefaultCodexValidator : ICodexValidator
{
    private sealed record LogEntry(DateTime TimestampUtc, string Line);
    private sealed record TaskExecutionWindow(DateTime StartUtc, DateTime EndUtc);
    private sealed record TaskExecutionEvidence(
        IReadOnlyList<string> ToolLines,
        IReadOnlyList<string> ServiceLines,
        IReadOnlyList<string> WarningLines,
        bool HasReadOnlyEvidence,
        bool HasWriteEvidence,
        bool HasSuccessfulBuildEvidence,
        bool HasSuccessfulTestEvidence,
        bool HasRunCommandContractError,
        bool HasValidatorEmptyResponse)
    {
        public bool HasConcreteVerificationEvidence => HasSuccessfulBuildEvidence || HasSuccessfulTestEvidence;
    }

    private readonly IChatClient _chatClient;
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<DefaultCodexValidator> _logger;
    private readonly ILLMExecutor? _llmExecutor;

    public DefaultCodexValidator(
        IChatClient chatClient,
        IToolRegistry toolRegistry,
        ILogger<DefaultCodexValidator> logger,
        ILLMExecutor? llmExecutor = null)
    {
        _chatClient = chatClient;
        _toolRegistry = toolRegistry;
        _logger = logger;
        _llmExecutor = llmExecutor;
    }

    public async Task<ValidationResult> ValidateAsync(CodexSession session, CodexTask task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(task);

        CodexTaskClassifier.NormalizeTask(task);

        StructuredLog.Information(_logger, "Validating task: {TaskId} - {Title}", task.Id, task.Title);

        var systemPrompt = $$"""
你是一个严格的交付验证专家。你的任务是核实“当前步骤（当前任务）”是否已真正按照要求完成。
你必须基于已有的执行日志和项目现状进行评估。
你只对当前任务做验收，不对整个计划做全量审计。

[项目背景]
{{session.ProjectSummary}}

[当前任务]
ID: {{task.Id}}
标题: {{task.Title}}
描述: {{task.Description}}
预期产出: {{string.Join(", ", task.Outputs)}}

[任务结果备注]
{{task.ResultNotes}}

验证规则：
1. 只检查“当前任务”目标是否完全达成；不要要求同时完成后续任务（即便你知道后续任务还未完成）。
2. 检查当前任务产出物（代码、文件、配置）是否符合预期。
3. 如果发现不属于当前任务范围的问题（例如明显属于后续步骤的目标，或项目既有存量问题），将其记录到 Issues 并以 `[Deferred]` 前缀标注，但**不得仅因这些问题判定失败**。
4. 只有以下情况才能判定 Success=false：当前任务目标未达成、当前任务引入回归/破坏、或当前任务要求的验证命令未被实际执行成功。
5. 如果原任务明确要求执行测试（如 dotnet test 等验证命令），你需要核实执行日志中是否有**实际执行此命令和相应成功的输出**，如果没有，必须将此验证标为失败 (Success: false)。
6. 你的输出必须是单个 JSON 对象。
7. **绝对禁止**在 JSON 之外输出任何思考过程、问候语或解释性文本。直接输出 JSON。
8. 对于代码类任务，如果执行日志中已包含构建/测试结果，可直接基于日志判断；仅在日志不完整时调用 `analyze_code`。
9. 如果你无法确定当前任务是否完成，且重试了相关查看工具依旧无法确认（或者缺少关键的成功执行日志），必须返回 Success=false 并在 Issues 中注明存疑点。绝对禁止因信息不足而返回 Success=true。

效率准则：
- 不要遍历项目目录。只检查执行日志中提及的文件。
- `ivilson_ls` 最多调用 1 次，`ivilson_read` 最多调用 2 次。
- 直接输出 JSON，不要说"让我验证..."。

JSON 模型示例：
{
  "Success": true,
  "Summary": "代码已编写且通过静态分析和测试执行，符合预期。",
  "Issues": []
}
""";

        var availableCodexTools = _toolRegistry.GetAvailableTools(session).ToList();
        systemPrompt = ToolCatalogPromptComposer.AppendRuntimeToolGuidance(systemPrompt, availableCodexTools);

        var messages = new List<ChatMessage>
        {
            // [Fix] Merge system prompt into first user message to avoid VLLM strict system-role content validation errors (Invalid type for 'messages.[0].content')
            new ChatMessage(ChatRole.User, systemPrompt + $"\n\n请验证任务 {task.Id}。"),
            new ChatMessage(ChatRole.Assistant, """
{
  "Success": true,
  "Summary": "代码结构完整，静态分析通过，符合 Clean Architecture 规范。",
  "Issues": []
}
"""),
            new ChatMessage(ChatRole.User, "请开始验证该任务并返回 JSON 结果。如果需要调用工具，请直接调用，不要说“让我调用...”。")
        };

        // 获取可用工具（特别是分析工具）
        var availableTools = availableCodexTools
            .Select(t => AIFunctionFactory.Create(async (Dictionary<string, object?> args, CancellationToken ct2) =>
                await t.ExecuteAsync(ToolArgumentNormalizer.NormalizeCopy(args), ct2).ConfigureAwait(false),
                name: t.Name,
                description: t.Description))
            .ToList();

        var options = new ChatOptions
        {
            Tools = availableTools.Cast<AITool>().ToList(),
            Temperature = 0.0f,
            ResponseFormat = ChatResponseFormat.Json,
            MaxOutputTokens = 4000
        };

        ChatResponse? response = null;
        int transportFailureCount = 0;
        Exception? lastTransportException = null;
        var sawProtocolTransportError = false;

        // 处理验证过程中的工具调用（循环最多 3 次）
        int guard = 0;
        while (guard++ < 5) // Increased guard to allow for retries
        {
            try
            {
                // Streaming LLM call with thinking chain collection
                var valTextSb = new System.Text.StringBuilder();
                var valThinkingSb = new System.Text.StringBuilder();
                var valContents = new List<Microsoft.Extensions.AI.AIContent>();
                if (_llmExecutor != null)
                {
                    await foreach (var update in _llmExecutor.StreamAsync(
                        new LLMExecutionRequest(messages, options, MemoryInjectionScenario.Validation), ct).ConfigureAwait(false))
                        CollectValidatorStreamingContent(update, valTextSb, valThinkingSb, valContents);
                }
                else
                {
                    await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
                        CollectValidatorStreamingContent(update, valTextSb, valThinkingSb, valContents);
                }
                // [Fix] Detect tool calls from collected contents and set FinishReason,
                // otherwise the downstream tool-loop check (FinishReason == ToolCalls)
                // will always break out even when the model streamed FunctionCallContent.
                var hasToolCalls = valContents.Any(c => c is Microsoft.Extensions.AI.FunctionCallContent);
                var finishReason = hasToolCalls ? Microsoft.Extensions.AI.ChatFinishReason.ToolCalls : Microsoft.Extensions.AI.ChatFinishReason.Stop;
                response = new ChatResponse([new ChatMessage(ChatRole.Assistant, valContents)])
                {
                    FinishReason = finishReason
                };
                if (valThinkingSb.Length > 0)
                    _logger.LogInformation("Validator thinking: {Thinking}", valThinkingSb.ToString().Length > 2000 ? valThinkingSb.ToString().Substring(0, 2000) : valThinkingSb.ToString());
                transportFailureCount = 0;
                lastTransportException = null;
            }
            catch (HttpRequestException ex)
            {
                transportFailureCount++;
                lastTransportException = ex;
                if (IsProviderProtocolError(ex))
                {
                    sawProtocolTransportError = true;
                }

                StructuredLog.Warning(_logger, ex, "Validator: GetResponseAsync failed. Attempting retry with error feedback.");
                messages.Add(new ChatMessage(ChatRole.User, $"你的上一个验证动作（可能是工具调用）导致了系统解析错误：{ex.Message}。请重试，并确保 arguments 是合法 JSON 对象。若系统自动生成了 args/input_params 容器，服务端会自动展开。"));
                continue;
            }
            catch (TaskCanceledException ex)
            {
                transportFailureCount++;
                lastTransportException = ex;
                var isTransportTimeout = !ct.IsCancellationRequested;
                if (isTransportTimeout && transportFailureCount >= 2)
                {
                    StructuredLog.Warning(_logger, ex, "Validator: repeated transport timeout detected. Stop retrying and fallback to deterministic validator.");
                    response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "")])
                    {
                        FinishReason = ChatFinishReason.Stop
                    };
                    break;
                }

                StructuredLog.Warning(_logger, ex, "Validator: GetResponseAsync failed. Attempting retry with error feedback.");
                messages.Add(new ChatMessage(ChatRole.User, $"你的上一个验证动作（可能是工具调用）导致了系统解析错误：{ex.Message}。请重试，并确保 arguments 是合法 JSON 对象。若系统自动生成了 args/input_params 容器，服务端会自动展开。"));
                continue;
            }
            catch (InvalidOperationException ex)
            {
                transportFailureCount++;
                lastTransportException = ex;
                if (IsProviderProtocolError(ex))
                {
                    sawProtocolTransportError = true;
                }

                StructuredLog.Warning(_logger, ex, "Validator: GetResponseAsync failed. Attempting retry with error feedback.");
                messages.Add(new ChatMessage(ChatRole.User, $"你的上一个验证动作（可能是工具调用）导致了系统解析错误：{ex.Message}。请重试，并确保 arguments 是合法 JSON 对象。若系统自动生成了 args/input_params 容器，服务端会自动展开。"));
                continue;
            }

            if (response.Messages.Count == 0) break;

            if (response.FinishReason != ChatFinishReason.ToolCalls)
            {
                break; // Final response reached (JSON result)
            }

            var lastMsg = response.Messages.LastOrDefault();
            if (lastMsg == null || lastMsg.Contents == null) break;

            var calls = lastMsg.Contents.OfType<FunctionCallContent>().ToList();
            if (calls == null || calls.Count == 0) break;

            messages.Add(new ChatMessage(ChatRole.Assistant, lastMsg.Contents));

            foreach (var fc in calls)
            {
                var toolName = fc.Name?.Trim();
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(fc.CallId, "Error: Empty tool name. Please call a valid tool.")]));
                    continue;
                }

                var tool = _toolRegistry.GetAvailableTools(session)
                    .FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));

                var args = fc.Arguments != null ? new Dictionary<string, object?>(fc.Arguments) : new Dictionary<string, object?>();
                ToolArgumentNormalizer.NormalizeInPlace(args);

                args["session_id"] = session.Id;
                args["workspace_path"] = session.WorkspacePath;
                // project_root is an internal trusted field and must not be overridden by model args.
                args["project_root"] = ToolPathResolver.ResolveProjectRoot(session.WorkspacePath, null, session.ProjectUrl, session.Metadata);

                var toolResultObj = tool != null
                    ? await tool.ExecuteAsync(args, ct)
.ConfigureAwait(false)
                    : CodexToolResult.Error($"Tool {toolName} not found.");

                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(fc.CallId, toolResultObj.Output)]));
            }
        }

        var responseText = ExtractValidationResponseText(response);

        if (response == null || responseText == null)
        {
            var reason = sawProtocolTransportError
                ? "验证模型通信协议异常（tool_call/function.name 非法）"
                : "验证模型未返回有效结论或请求超时";
            StructuredLog.Error(_logger, "Validator: {Reason}. Attempting deterministic fallback.", reason);
            var fallback = TryDeterministicValidation(session, task);
            if (fallback != null)
            {
                // [Bug-03 Fix] Mark fallback result so Orchestrator can distinguish from a real LLM pass.
                return AttachChecklistEvaluation(session, task, MarkFallbackResult(fallback, reason, task.Id));
            }

            var issues = new List<string> { "[System] Validator LLM response was null/empty." };
            if (!string.IsNullOrWhiteSpace(lastTransportException?.Message))
            {
                issues.Add(lastTransportException!.Message);
            }
            return AttachChecklistEvaluation(session, task, new ValidationResult(false, $"{reason}，且规则兜底无法确认任务结果。", issues, IsInfrastructureError: true));
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            StructuredLog.Warning(_logger, "Validator returned empty final content. Attempting deterministic fallback.");
            var deterministic = TryDeterministicValidation(session, task);
            if (deterministic != null)
            {
                return AttachChecklistEvaluation(session, task, ApplyQualityGateWarnings(MarkFallbackResult(deterministic, "empty final validator content", task.Id), task));
            }

            return AttachChecklistEvaluation(session, task, new ValidationResult(
                false,
                "验证模型未输出最终 JSON，且规则兜底无法确认任务结果。",
                new List<string> { "Validator result content is empty." },
                IsInfrastructureError: true));
        }

        var json = responseText.Trim();
        try
        {
            var parseResult = TryParseValidationResult(json);
            if (parseResult.Result != null)
            {
                return AttachChecklistEvaluation(session, task, ApplyQualityGateWarnings(parseResult.Result, task));
            }

            if (parseResult.DetectedToolCallXml)
            {
                StructuredLog.Warning(_logger, "Validator: Detected tool-call XML instead of JSON. Attempting deterministic fallback.");
                var fallback = TryDeterministicValidation(session, task);
                if (fallback != null)
                {
                    // [Bug-03 Fix] Mark as fallback before applying quality gate warnings.
                    return AttachChecklistEvaluation(session, task, ApplyQualityGateWarnings(MarkFallbackResult(fallback, "tool-call XML response", task.Id), task));
                }

                return AttachChecklistEvaluation(session, task, new ValidationResult(false, "验证阶段模型返回了工具调用而不是 JSON 结果，且规则兜底无法确认任务结果。", new List<string> { "[System] Validator returned tool-call XML instead of JSON." }, IsInfrastructureError: true));
            }

            throw new InvalidOperationException(parseResult.ErrorMessage ?? "Validation result parsing failed.");
        }
        catch (JsonException ex)
        {
            StructuredLog.Warning(_logger, ex, "Failed to parse Validator JSON. Raw: {Raw}", responseText);

            // 启发式逻辑（收紧版）：仅识别明确的失败信号；解析异常默认 fail-closed
            var rawText = NormalizeFailureSignalText(response.Text);

            // 明确失败信号才判失败；信息不充分时按 fail-closed 拦截，因为不能允许带病代码合入主干
            var hasExplicitFailureSignal = rawText.Contains("\"SUCCESS\":FALSE", StringComparison.Ordinal) ||
                                           rawText.Contains("验证失败", StringComparison.Ordinal) ||
                                           rawText.Contains("AUDITFAILED", StringComparison.Ordinal) ||
                                           rawText.Contains("SECURITYFAILED", StringComparison.Ordinal);

            if (hasExplicitFailureSignal)
            {
                return AttachChecklistEvaluation(session, task, new ValidationResult(false, "验证异常（检测到失败信号）", new List<string> { ex.Message, "Raw Response: " + (responseText.Length > 300 ? responseText[..300] + "..." : responseText) }));
            }

            var deterministic = TryDeterministicValidation(session, task);
            if (deterministic != null)
            {
                // [Bug-03 Fix] Mark as fallback.
                return AttachChecklistEvaluation(session, task, ApplyQualityGateWarnings(MarkFallbackResult(deterministic, "JSON parse exception (JsonException)", task.Id), task));
            }

            var rawSnippet = responseText.Length > 500
                ? responseText[..500] + "...[truncated]"
                : responseText;
            StructuredLog.Error(_logger, ex,
                "Validator JSON parse failed. DeterministicFallback=unavailable. RawResponse={Raw}", rawSnippet);
            return AttachChecklistEvaluation(session, task, new ValidationResult(false, "验证器输出解析异常，且规则兜底无法确认任务结果。",
                new List<string>
                {
                    $"ParseException: {ex.Message}",
                    $"RawResponseSnippet: {rawSnippet}"
                }, IsInfrastructureError: true));
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Warning(_logger, ex, "Failed to parse Validator JSON. Raw: {Raw}", responseText);

            // 启发式逻辑（收紧版）：仅识别明确的失败信号；解析异常默认 fail-closed
            var rawText = NormalizeFailureSignalText(response.Text);

            // 明确失败信号才判失败；信息不充分时按 fail-closed 拦截，因为不能允许带病代码合入主干
            var hasExplicitFailureSignal = rawText.Contains("\"SUCCESS\":FALSE", StringComparison.Ordinal) ||
                                           rawText.Contains("验证失败", StringComparison.Ordinal) ||
                                           rawText.Contains("AUDITFAILED", StringComparison.Ordinal) ||
                                           rawText.Contains("SECURITYFAILED", StringComparison.Ordinal);

            if (hasExplicitFailureSignal)
            {
                return AttachChecklistEvaluation(session, task, new ValidationResult(false, "验证异常（检测到失败信号）", new List<string> { ex.Message, "Raw Response: " + (responseText.Length > 300 ? responseText[..300] + "..." : responseText) }));
            }

            var deterministic = TryDeterministicValidation(session, task);
            if (deterministic != null)
            {
                // [Bug-03 Fix] Mark as fallback.
                return AttachChecklistEvaluation(session, task, ApplyQualityGateWarnings(MarkFallbackResult(deterministic, "JSON parse exception (InvalidOperationException)", task.Id), task));
            }

            var rawSnippet = responseText.Length > 500
                ? responseText[..500] + "...[truncated]"
                : responseText;
            StructuredLog.Error(_logger, ex,
                "Validator JSON parse failed. DeterministicFallback=unavailable. RawResponse={Raw}", rawSnippet);
            return AttachChecklistEvaluation(session, task, new ValidationResult(false, "验证器输出解析异常，且规则兜底无法确认任务结果。",
                new List<string>
                {
                    $"ParseException: {ex.Message}",
                    $"RawResponseSnippet: {rawSnippet}"
                }, IsInfrastructureError: true));
        }
    }

    private ValidationResult? TryDeterministicValidation(CodexSession session, CodexTask task)
    {
        try
        {
            var projectRoot = ToolPathResolver.ResolveProjectRoot(session.WorkspacePath, null, session.ProjectUrl, session.Metadata);
            var taskText = $"{task.Title}\n{task.Description}";
            var evidence = CollectTaskExecutionEvidence(task);
            var checksAttempted = 0;
            var issues = new List<string>();

            // BUG-002 fix: Evaluate structured artifact assertions FIRST, before keyword checks.
            // If the task declares RequiredArtifacts/ForbiddenStates and any assertion fails,
            // this is a hard failure — no fallback can override it.
            var allAssertions = task.RequiredArtifacts.Concat(task.ForbiddenStates).ToList();
            var assertionIssues = EvaluateArtifactAssertions(projectRoot, allAssertions);
            if (assertionIssues.Count > 0)
            {
                return AttachChecklistEvaluation(task, projectRoot, new ValidationResult(false,
                    $"[ASSERTION_FAILED] 任务契约断言未通过: {string.Join("；", assertionIssues.Take(5))}",
                    assertionIssues));
            }
            // If assertions exist and all passed, count this as a successful check
            if (allAssertions.Count > 0)
            {
                checksAttempted++;
            }

            if (CodexTaskClassifier.IsAnalysisTask(task))
            {
                checksAttempted++;
                if (evidence.HasWriteEvidence)
                {
                    issues.Add("analysis 任务出现了写入/执行类工具调用，不符合只读任务约束。");
                }
                else if (!evidence.HasReadOnlyEvidence)
                {
                    issues.Add("缺少当前 analysis 任务的只读执行证据。");
                }
            }

            if (taskText.Contains("ulid", StringComparison.OrdinalIgnoreCase))
            {
                checksAttempted++;
                var appFilePath = FindFileByName(projectRoot, "AppFile.cs");
                if (string.IsNullOrWhiteSpace(appFilePath) || !File.Exists(appFilePath))
                {
                    issues.Add("未找到 AppFile.cs，无法验证 Ulid 依赖是否移除。");
                }
                else
                {
                    var content = File.ReadAllText(appFilePath);
                    if (content.Contains("Ulid.NewUlid()", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add("AppFile.cs 仍包含 Ulid.NewUlid()。");
                    }
                }
            }

            if (taskText.Contains("工厂", StringComparison.OrdinalIgnoreCase) &&
                taskText.Contains("fileservice", StringComparison.OrdinalIgnoreCase))
            {
                checksAttempted++;
                var fileServicePath = FindFileByName(projectRoot, "FileService.cs");
                if (string.IsNullOrWhiteSpace(fileServicePath) || !File.Exists(fileServicePath))
                {
                    issues.Add("未找到 FileService.cs，无法验证实体创建方式。");
                }
                else
                {
                    var content = File.ReadAllText(fileServicePath);
                    if (content.Contains("new AppFile", StringComparison.Ordinal))
                    {
                        issues.Add("FileService.cs 仍直接 new AppFile，未使用工厂方法。");
                    }
                }
            }

            var requireBuildEvidence = taskText.Contains("dotnet build", StringComparison.OrdinalIgnoreCase) ||
                                       taskText.Contains("构建", StringComparison.OrdinalIgnoreCase) ||
                                       taskText.Contains("编译", StringComparison.OrdinalIgnoreCase);
            if (requireBuildEvidence)
            {
                checksAttempted++;
                if (!evidence.HasSuccessfulBuildEvidence)
                {
                    issues.Add("缺少当前任务对应的 dotnet build 成功证据。");
                }
            }

            var requireTestEvidence = taskText.Contains("dotnet test", StringComparison.OrdinalIgnoreCase) ||
                                      taskText.Contains("run_tests", StringComparison.OrdinalIgnoreCase) ||
                                      taskText.Contains("测试", StringComparison.OrdinalIgnoreCase);
            if (requireTestEvidence)
            {
                checksAttempted++;
                if (!evidence.HasSuccessfulTestEvidence)
                {
                    issues.Add("缺少当前任务对应的测试成功证据。");
                }
            }

            if (CodexTaskClassifier.IsCodeExecutionTask(task))
            {
                checksAttempted++;
                if (!evidence.HasWriteEvidence)
                {
                    issues.Add("缺少当前 code 任务的实际代码变更证据。");
                }

                if (!requireBuildEvidence && !requireTestEvidence && !evidence.HasConcreteVerificationEvidence)
                {
                    issues.Add("缺少当前 code 任务的成功构建或测试证据。");
                }
            }

            if (checksAttempted == 0)
            {
                if (TryDetectInfrastructureFailure(task.Id, evidence, out var infraResultWithoutChecks))
                {
                    return infraResultWithoutChecks == null
                        ? null
                        : AttachChecklistEvaluation(task, projectRoot, infraResultWithoutChecks);
                }

                // BUG-002 fix: Fail-closed when no deterministic checks matched AND no artifact
                // assertions were declared. Without any verification basis, the validator cannot
                // confirm task completion — returning null (which upstream treats as "pass") is unsafe.
                if (task.RequiredArtifacts.Count == 0 && task.ForbiddenStates.Count == 0)
                {
                    return AttachChecklistEvaluation(task, projectRoot, new ValidationResult(false,
                        "[VALIDATION_CONFIG_INSUFFICIENT] 无可执行的确定性检查，且任务未声明结构化验收契约（RequiredArtifacts/ForbiddenStates）。验证器无法确认任务完成。",
                        new List<string>
                        {
                            "任务描述未匹配任何确定性检查规则",
                            "任务未声明 RequiredArtifacts 或 ForbiddenStates",
                            "fail-closed: 不允许在无任何验证条件时默认通过"
                        }));
                }

                // Artifact assertions were present and all passed (failures handled above),
                // so reaching here means keyword checks were 0 but assertions were OK.
                // This is a valid pass — the assertions provided the verification basis.
                return null;
            }

            if (issues.Count == 0)
            {
                if (evidence.WarningLines.Count > 0)
                {
                    return AttachChecklistEvaluation(task, projectRoot, new ValidationResult(
                        true,
                        $"规则兜底验证通过，但检测到 {evidence.WarningLines.Count} 条 warning。",
                        new List<string>(),
                        HasQualityWarnings: true,
                        WarningDetails: evidence.WarningLines));
                }

                return AttachChecklistEvaluation(task, projectRoot, new ValidationResult(true, "规则兜底验证通过：任务关键目标与命令证据满足。", new List<string>()));
            }

            if (ShouldSurfaceInfrastructureFailure(issues, evidence) &&
                TryDetectInfrastructureFailure(task.Id, evidence, out var infraResult))
            {
                return infraResult == null
                    ? null
                    : AttachChecklistEvaluation(task, projectRoot, infraResult);
            }

            // Bug-03: include structured issue list so the model can diagnose the exact failure reason.
            var issuesSummary = issues.Count > 0
                ? string.Join("；", issues.Select((s, i) => $"[{i + 1}] {s}"))
                : "无具体诊断信息";
            return AttachChecklistEvaluation(task, projectRoot, new ValidationResult(false,
                $"[FALLBACK_VALIDATION_FAILED] 规则兜底验证未通过。失败原因：{issuesSummary}",
                issues));
        }
        catch (IOException ex)
        {
            StructuredLog.Warning(_logger, ex, "Deterministic validator fallback failed.");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Warning(_logger, ex, "Deterministic validator fallback failed.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Warning(_logger, ex, "Deterministic validator fallback failed.");
            return null;
        }
    }

    private static bool TryDetectInfrastructureFailure(string taskId, TaskExecutionEvidence evidence, out ValidationResult? result)
    {
        result = null;

        if (evidence.HasRunCommandContractError)
        {
            result = new ValidationResult(
                false,
                "检测到 run_command 参数契约错误，验证命令未真正执行。",
                new List<string>
                {
                    "run_command 返回了参数格式错误：Expected a string array like [\"dotnet\", \"build\"].",
                    "当前任务的构建/测试证据无效，因为命令没有实际运行成功。"
                },
                IsInfrastructureError: true);
            return true;
        }

        if (evidence.HasValidatorEmptyResponse && !evidence.HasConcreteVerificationEvidence)
        {
            result = new ValidationResult(
                false,
                "验证模型返回空响应，无法完成结构化验收。",
                new List<string> { "Validator result content is empty." },
                IsInfrastructureError: true);
            return true;
        }

        return false;
    }

    private static string? FindFileByName(string workspacePath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return null;
        }

        return Directory.EnumerateFiles(workspacePath, fileName, SearchOption.AllDirectories)
            .FirstOrDefault(p => !p.Contains($"{Path.DirectorySeparatorChar}shadows{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// BUG-002 fix: Evaluate structured artifact assertions against the filesystem.
    /// This is a deterministic, keyword-independent check that replaces the fragile
    /// keyword-matching approach in TryDeterministicValidation.
    /// </summary>
    internal static List<string> EvaluateArtifactAssertions(string projectRoot, IReadOnlyList<ArtifactAssertion> assertions)
    {
        var issues = new List<string>();
        foreach (var assertion in assertions)
        {
            var evaluation = EvaluateArtifactAssertion(projectRoot, assertion);
            if (!evaluation.Passed)
            {
                issues.Add(evaluation.Message);
            }
        }
        return issues;
    }

    internal static ChecklistEvaluation? BuildChecklistEvaluation(CodexTask task, string projectRoot, IReadOnlyList<string>? validationIssues = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.ChecklistItems.Count == 0)
        {
            return null;
        }

        var issues = validationIssues ?? [];
        var assertionEvaluations = task.RequiredArtifacts
            .Concat(task.ForbiddenStates)
            .Select(assertion => EvaluateArtifactAssertion(projectRoot, assertion))
            .ToDictionary(evaluation => evaluation.AssertionKey, StringComparer.OrdinalIgnoreCase);

        var evidenceByItem = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var relatedAssertionsMatched = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var completed = new List<ChecklistItemEvaluation>();
        var failed = new List<ChecklistItemEvaluation>();
        var pending = new List<ChecklistItemEvaluation>();

        var buildRelatedIssues = FindIssues(issues, IsBuildRelatedIssue);
        var testRelatedIssues = FindIssues(issues, IsTestRelatedIssue);
        var buildSucceeded = task.ExecutionEvidence?.HasSuccessfulBuildEvidence == true;
        var testSucceeded = task.ExecutionEvidence?.HasSuccessfulTestEvidence == true;

        foreach (var item in task.ChecklistItems)
        {
            var itemEvidence = new List<string>();
            var matchedAssertions = new List<string>();
            var status = TaskChecklistItemStatus.Pending;

            if (item.RelatedAssertions.Count > 0)
            {
                var allRelatedMatched = true;
                var hasAssertionFailure = false;

                foreach (var relatedKey in item.RelatedAssertions)
                {
                    if (!assertionEvaluations.TryGetValue(relatedKey, out var assertionEvaluation))
                    {
                        allRelatedMatched = false;
                        continue;
                    }

                    if (assertionEvaluation.Passed)
                    {
                        matchedAssertions.Add(relatedKey);
                        continue;
                    }

                    hasAssertionFailure = true;
                    itemEvidence.Add(assertionEvaluation.Message);
                }

                if (hasAssertionFailure)
                {
                    status = TaskChecklistItemStatus.Failed;
                }
                else if (allRelatedMatched && matchedAssertions.Count == item.RelatedAssertions.Count)
                {
                    status = TaskChecklistItemStatus.Completed;
                    itemEvidence.Add($"已满足 {matchedAssertions.Count} 条关联断言。");
                }
            }

            if (IndicatesBuildChecklistItem(item.Text))
            {
                if (buildSucceeded)
                {
                    status = TaskChecklistItemStatus.Completed;
                    itemEvidence.Add("检测到成功的构建证据。");
                }
                else if (buildRelatedIssues.Length > 0)
                {
                    status = TaskChecklistItemStatus.Failed;
                    itemEvidence.AddRange(buildRelatedIssues);
                }
            }

            if (IndicatesTestChecklistItem(item.Text))
            {
                if (testSucceeded)
                {
                    status = TaskChecklistItemStatus.Completed;
                    itemEvidence.Add("检测到成功的测试证据。");
                }
                else if (!buildSucceeded && buildRelatedIssues.Length > 0)
                {
                    status = TaskChecklistItemStatus.Pending;
                    itemEvidence.Add("构建尚未通过，测试步骤仍待执行。");
                }
                else if (testRelatedIssues.Length > 0)
                {
                    status = TaskChecklistItemStatus.Failed;
                    itemEvidence.AddRange(testRelatedIssues);
                }
            }

            var evaluation = new ChecklistItemEvaluation
            {
                ItemId = item.Id,
                Text = item.Text,
                Status = status,
                Evidence = itemEvidence.Distinct(StringComparer.Ordinal).ToArray(),
                RelatedAssertionsMatched = matchedAssertions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };

            evidenceByItem[item.Id] = evaluation.Evidence;
            relatedAssertionsMatched[item.Id] = evaluation.RelatedAssertionsMatched;

            switch (status)
            {
                case TaskChecklistItemStatus.Completed:
                    completed.Add(evaluation);
                    break;
                case TaskChecklistItemStatus.Failed:
                    failed.Add(evaluation);
                    break;
                default:
                    pending.Add(evaluation);
                    break;
            }
        }

        return new ChecklistEvaluation
        {
            CompletedItems = completed.ToArray(),
            FailedItems = failed.ToArray(),
            PendingItems = pending.ToArray(),
            EvidenceByItem = evidenceByItem,
            RelatedAssertionsMatched = relatedAssertionsMatched
        };
    }

    private ValidationResult AttachChecklistEvaluation(CodexSession session, CodexTask task, ValidationResult result)
    {
        var projectRoot = ToolPathResolver.ResolveProjectRoot(session.WorkspacePath, null, session.ProjectUrl, session.Metadata);
        return AttachChecklistEvaluation(task, projectRoot, result);
    }

    internal static ValidationResult AttachChecklistEvaluation(CodexTask task, string projectRoot, ValidationResult result)
    {
        var checklistEvaluation = BuildChecklistEvaluation(task, projectRoot, result.Issues);
        return checklistEvaluation == null
            ? result
            : result with { ChecklistEvaluation = checklistEvaluation };
    }

    private static AssertionEvaluation EvaluateArtifactAssertion(string projectRoot, ArtifactAssertion assertion)
    {
        var fullPath = Path.IsPathRooted(assertion.Path)
            ? assertion.Path
            : Path.Combine(projectRoot, assertion.Path);
        var assertionKey = BuildAssertionKey(assertion);

        switch (assertion.Type.ToLowerInvariant())
        {
            case "file_exists":
                return File.Exists(fullPath)
                    ? new AssertionEvaluation(assertionKey, true, $"断言通过: 文件存在 {assertion.Path}")
                    : new AssertionEvaluation(assertionKey, false, $"断言失败: 文件应存在但未找到: {assertion.Path}");
            case "file_not_exists":
                return File.Exists(fullPath)
                    ? new AssertionEvaluation(assertionKey, false, $"断言失败: 文件应不存在但实际存在: {assertion.Path}")
                    : new AssertionEvaluation(assertionKey, true, $"断言通过: 文件不存在 {assertion.Path}");
            case "file_contains":
                if (!File.Exists(fullPath))
                {
                    return new AssertionEvaluation(assertionKey, false, $"断言失败: 无法检查 file_contains，文件不存在: {assertion.Path}");
                }

                var content = File.ReadAllText(fullPath);
                return assertion.Text != null && content.Contains(assertion.Text, StringComparison.OrdinalIgnoreCase)
                    ? new AssertionEvaluation(assertionKey, true, $"断言通过: 文件 {assertion.Path} 包含 \"{assertion.Text}\"")
                    : new AssertionEvaluation(assertionKey, false, $"断言失败: 文件 {assertion.Path} 应包含 \"{assertion.Text}\" 但未找到");
            case "file_not_contains":
                if (!File.Exists(fullPath))
                {
                    return new AssertionEvaluation(assertionKey, true, $"断言通过: 文件不存在，因此满足不包含约束: {assertion.Path}");
                }

                var contentForExclusion = File.ReadAllText(fullPath);
                return assertion.Text != null && contentForExclusion.Contains(assertion.Text, StringComparison.OrdinalIgnoreCase)
                    ? new AssertionEvaluation(assertionKey, false, $"断言失败: 文件 {assertion.Path} 不应包含 \"{assertion.Text}\" 但实际包含")
                    : new AssertionEvaluation(assertionKey, true, $"断言通过: 文件 {assertion.Path} 不包含 \"{assertion.Text}\"");
            default:
                return new AssertionEvaluation(assertionKey, false, $"断言失败: 未知断言类型 \"{assertion.Type}\"，路径: {assertion.Path}");
        }
    }

    private static string[] FindIssues(IReadOnlyList<string> issues, Func<string, bool> predicate)
        => issues.Where(predicate).Distinct(StringComparer.Ordinal).ToArray();

    private static bool IndicatesBuildChecklistItem(string text)
        => text.Contains("dotnet build", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("构建", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("编译", StringComparison.OrdinalIgnoreCase);

    private static bool IndicatesTestChecklistItem(string text)
        => text.Contains("dotnet test", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("run_tests", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("测试", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildRelatedIssue(string issue)
        => issue.Contains("dotnet build", StringComparison.OrdinalIgnoreCase) ||
           issue.Contains("构建", StringComparison.OrdinalIgnoreCase) ||
           issue.Contains("编译", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestRelatedIssue(string issue)
        => issue.Contains("dotnet test", StringComparison.OrdinalIgnoreCase) ||
           issue.Contains("run_tests", StringComparison.OrdinalIgnoreCase) ||
           issue.Contains("测试", StringComparison.OrdinalIgnoreCase);

    private static string BuildAssertionKey(ArtifactAssertion assertion)
        => $"{assertion.Type.Trim().ToLowerInvariant()}|{CanonicalizePath(assertion.Path)}|{assertion.Text?.Trim() ?? string.Empty}";

    private static string CanonicalizePath(string? path)
        => (path ?? string.Empty).Replace('\\', '/').Trim().Trim('`');

    private sealed record AssertionEvaluation(string AssertionKey, bool Passed, string Message);

    private static TaskExecutionEvidence CollectTaskExecutionEvidence(CodexTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var taskId = task.Id;
        var toolEntries = ReadToolCallLogEntries();
        var serviceEntries = ReadServiceLogEntries();
        var window = FindTaskExecutionWindow(taskId, serviceEntries);

            var toolLines = window != null
                ? SliceLogLines(toolEntries, window.StartUtc, window.EndUtc)
                : GetFallbackTaskScopedLines(taskId, toolEntries.Select(x => x.Line).ToList());
            var fallbackToolLines = GetFallbackTaskScopedLines(taskId, toolEntries.Select(x => x.Line).ToList());

            var serviceLines = window != null
                ? SliceLogLines(serviceEntries, window.StartUtc, window.EndUtc.AddSeconds(90))
                : GetFallbackTaskScopedLines(taskId, serviceEntries.Select(x => x.Line).ToList());
            var fallbackServiceLines = GetFallbackTaskScopedLines(taskId, serviceEntries.Select(x => x.Line).ToList());

            toolLines = MergeTaskScopedFallbackLines(toolLines, fallbackToolLines);
            serviceLines = MergeTaskScopedFallbackLines(serviceLines, fallbackServiceLines);

        var readSignals = new[]
        {
            "tool=analyze_project", "tool=analyze_code", "tool=read_file", "tool=ivilson_read", "tool=ivilson_ls",
            "tool=search_file_index", "tool=search_code", "tool=search_in_files", "tool=fetch_webpage", "tool=web_search"
        };
        var writeSignals = new[]
        {
            "tool=write_file", "tool=ivilson_smart_patch", "tool=apply_patch", "tool=create_directory",
            "tool=delete_file", "Created file:", "Updated file:", "Modified file:",
            "文件已updated", "文件已创建", "patch applied"
        };

        var structuredEvidence = task.ExecutionEvidence;
        var hasRead = toolLines.Any(line => readSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)));
        var hasWrite = toolLines.Any(line => writeSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase))) ||
                       structuredEvidence is { WriteToolCalls: > 0 } ||
                       structuredEvidence is { ChangedFiles.Count: > 0 } ||
                       structuredEvidence is { CreatedFiles.Count: > 0 } ||
                       structuredEvidence is { DeletedFiles.Count: > 0 };
        var hasBuildSuccess = toolLines.Any(ContainsSuccessfulBuildSignal) ||
                              structuredEvidence?.HasSuccessfulBuildEvidence == true;
        var hasTestSuccess = toolLines.Any(ContainsSuccessfulTestSignal) ||
                             structuredEvidence?.HasSuccessfulTestEvidence == true;
        var warningLines = toolLines
            .Concat(serviceLines)
            .Where(IsQualityWarningLine)
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
        var hasRunCommandContractError = toolLines.Any(line =>
            line.Contains("Missing or invalid command parameter. Expected a string array", StringComparison.OrdinalIgnoreCase));
        var hasValidatorEmptyResponse = serviceLines.Any(line =>
            line.Contains("Result content is empty.", StringComparison.OrdinalIgnoreCase));

        return new TaskExecutionEvidence(
            toolLines,
            serviceLines,
            warningLines,
            hasRead,
            hasWrite,
            hasBuildSuccess,
            hasTestSuccess,
            hasRunCommandContractError,
            hasValidatorEmptyResponse);
    }

    /// <summary>
    /// [Bug-03 Fix] Marks a deterministic-fallback result as IsFallback=true and emits a prominent
    /// warning log so the audit trail clearly distinguishes fallback-pass from a real LLM verdict.
    /// Failure results are returned unchanged (no need to distinguish; fail is fail).
    /// </summary>
    private ValidationResult MarkFallbackResult(ValidationResult result, string triggerReason, string taskId)
    {
        if (!result.Success)
        {
            return result;
        }

        StructuredLog.Warning(_logger,
            "[Validator-Fallback] ⚠️ Task {TaskId} passed via DETERMINISTIC FALLBACK — NOT a real LLM verdict. " +
            "Trigger: {Reason}. Summary: {Summary}. " +
            "This will be recorded as IsFallback=true in the audit trail.",
            taskId, triggerReason, result.Summary);

        return result with { IsFallback = true };
    }

    private static ValidationResult ApplyQualityGateWarnings(ValidationResult result, CodexTask task)
    {
        if (!result.Success || result.HasQualityWarnings)
        {
            return result;
        }

        var evidence = CollectTaskExecutionEvidence(task);
        if (evidence.WarningLines.Count == 0)
        {
            return result;
        }

        var summary = string.IsNullOrWhiteSpace(result.Summary)
            ? $"验证通过，但检测到 {evidence.WarningLines.Count} 条 warning。"
            : $"{result.Summary} 质量门禁提示：检测到 {evidence.WarningLines.Count} 条 warning。";
        return result with
        {
            Summary = summary,
            HasQualityWarnings = true,
            WarningDetails = evidence.WarningLines
        };
    }

    private static TaskExecutionWindow? FindTaskExecutionWindow(string taskId, IReadOnlyList<LogEntry> serviceEntries)
    {
        if (serviceEntries.Count == 0 || string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        var validateEntry = serviceEntries.LastOrDefault(entry =>
            entry.Line.Contains($"Validating task: {taskId}", StringComparison.OrdinalIgnoreCase));
        if (validateEntry == null)
        {
            validateEntry = serviceEntries.LastOrDefault(entry =>
                entry.Line.Contains($"Atomic execution started for Task: {taskId}", StringComparison.OrdinalIgnoreCase));
            if (validateEntry == null)
            {
                return null;
            }
        }

        var startEntry = serviceEntries.LastOrDefault(entry =>
            entry.TimestampUtc <= validateEntry.TimestampUtc &&
            entry.Line.Contains($"Atomic execution started for Task: {taskId}", StringComparison.OrdinalIgnoreCase));

        var start = startEntry?.TimestampUtc ?? validateEntry.TimestampUtc.AddMinutes(-20);
        var end = validateEntry.TimestampUtc.AddSeconds(5);
        if (end < start)
        {
            end = start.AddMinutes(20);
        }

        return new TaskExecutionWindow(start, end);
    }

    private static bool ShouldSurfaceInfrastructureFailure(IEnumerable<string> issues, TaskExecutionEvidence evidence)
    {
        var issueList = issues.Where(issue => !string.IsNullOrWhiteSpace(issue)).ToList();
        if (issueList.Count == 0)
        {
            return false;
        }

        if (evidence.HasRunCommandContractError)
        {
            return true;
        }

        if (!evidence.HasValidatorEmptyResponse)
        {
            return false;
        }

        return issueList.All(IsEvidenceGapIssue) && !evidence.HasConcreteVerificationEvidence;
    }

    private static bool IsEvidenceGapIssue(string issue)
    {
        return issue.Contains("缺少", StringComparison.OrdinalIgnoreCase) ||
               issue.Contains("只读执行证据", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSuccessfulBuildSignal(string line)
    {
        return line.Contains("tool=run_command", StringComparison.OrdinalIgnoreCase) &&
               line.Contains("failed=False", StringComparison.OrdinalIgnoreCase) &&
               line.Contains("dotnet build", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSuccessfulTestSignal(string line)
    {
        if (line.Contains("tool=run_tests", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("failed=False", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("\"Success\":true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return line.Contains("tool=run_command", StringComparison.OrdinalIgnoreCase) &&
               line.Contains("failed=False", StringComparison.OrdinalIgnoreCase) &&
               line.Contains("dotnet test", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQualityWarningLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line.Contains("质量门禁", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Project memory drift detected", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Validator:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.Contains(": warning ", StringComparison.OrdinalIgnoreCase) ||
               line.Contains(" warning CS", StringComparison.OrdinalIgnoreCase) ||
               line.Contains(" warning NU", StringComparison.OrdinalIgnoreCase) ||
               line.Contains(" warning CA", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("warning CS", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("warning NU", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("warning CA", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SliceLogLines(IEnumerable<LogEntry> entries, DateTime startUtc, DateTime endUtc)
    {
        return entries
            .Where(entry => entry.TimestampUtc >= startUtc && entry.TimestampUtc <= endUtc)
            .Select(entry => entry.Line)
            .ToList();
    }

    private static List<string> GetFallbackTaskScopedLines(string taskId, List<string> lines)
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(taskId))
        {
            return new List<string>();
        }

        var taskHint = $"{Path.DirectorySeparatorChar}{taskId}{Path.DirectorySeparatorChar}";
        var indexes = new SortedSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(taskId, StringComparison.OrdinalIgnoreCase) ||
                lines[i].Contains(taskHint, StringComparison.OrdinalIgnoreCase) ||
                lines[i].Contains($"taskId={taskId}", StringComparison.OrdinalIgnoreCase) ||
                lines[i].Contains($"task={taskId}", StringComparison.OrdinalIgnoreCase) ||
                lines[i].Contains($"\"TaskId\":\"{taskId}\"", StringComparison.OrdinalIgnoreCase) ||
                lines[i].Contains($"\"taskId\":\"{taskId}\"", StringComparison.OrdinalIgnoreCase))
            {
                for (var j = Math.Max(0, i - 80); j <= Math.Min(lines.Count - 1, i + 120); j++)
                {
                    indexes.Add(j);
                }
            }
        }

        return indexes.Select(index => lines[index]).ToList();
    }

    private static List<string> MergeTaskScopedFallbackLines(List<string> slicedLines, List<string> fallbackLines)
    {
        if (fallbackLines.Count == 0)
        {
            return slicedLines;
        }

        if (slicedLines.Count == 0)
        {
            return fallbackLines;
        }

        var merged = new List<string>(slicedLines.Count + fallbackLines.Count);
        merged.AddRange(slicedLines);
        foreach (var line in fallbackLines)
        {
            if (!merged.Contains(line, StringComparer.Ordinal))
            {
                merged.Add(line);
            }
        }

        return merged;
    }

    private static List<LogEntry> ReadToolCallLogEntries()
    {
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        var candidates = new[]
        {
            Path.Combine(logsDir, "toolscall.log"),
            Path.Combine(logsDir, "toolscall.log.bak")
        };

        return ReadLogEntries(candidates);
    }

    private static List<LogEntry> ReadServiceLogEntries()
    {
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logsDir))
        {
            return new List<LogEntry>();
        }

        var candidates = Directory.EnumerateFiles(logsDir, "service-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(2)
            .ToList();

        return ReadLogEntries(candidates);
    }

    private static List<LogEntry> ReadLogEntries(IEnumerable<string> paths)
    {
        var results = new List<LogEntry>();
        foreach (var path in paths)
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (TryParseLogTimestamp(line, out var timestampUtc))
                    {
                        results.Add(new LogEntry(timestampUtc, line));
                    }
                }
            }
            catch (IOException)
            {
                // ignore per-file read failures in fallback mode
            }
            catch (UnauthorizedAccessException)
            {
                // ignore per-file read failures in fallback mode
            }
        }

        return results;
    }

    private static bool TryParseLogTimestamp(string line, out DateTime timestampUtc)
    {
        timestampUtc = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var sepIndex = line.IndexOf('|', StringComparison.Ordinal);
        var tsText = sepIndex > 0 ? line[..sepIndex] : line;
        if (!DateTime.TryParse(tsText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return false;
        }

        timestampUtc = parsed.ToUniversalTime();
        return true;
    }

    private static bool IsProviderProtocolError(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("empty function.name", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("tool_calls", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Malformed tool-call protocol", StringComparison.OrdinalIgnoreCase);
    }

    private static (ValidationResult? Result, bool DetectedToolCallXml, string? ErrorMessage) TryParseValidationResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, false, "Result content is empty.");
        }

        if (raw.Contains("<invoke", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("<minimax:tool_call>", StringComparison.OrdinalIgnoreCase))
        {
            return (null, true, null);
        }

        var candidate = NormalizeJsonText(raw);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var parsed = TryDeserializeValidationResult(candidate);
            if (parsed != null)
            {
                return (parsed, false, null);
            }
        }

        // 尝试从混合文本中抽取 JSON 对象（优先包含 Success 字段的对象）
        foreach (var obj in ExtractJsonObjects(raw))
        {
            var normalized = NormalizeJsonText(obj);
            var parsed = TryDeserializeValidationResult(normalized);
            if (parsed != null)
            {
                return (parsed, false, null);
            }
        }

        // 最后一层：正则兜底提取核心字段，避免轻微格式错误导致整轮失败
        var regexResult = TryParseByRegex(raw);
        if (regexResult != null)
        {
            return (regexResult, false, null);
        }

        return (null, false, "Validation output format error: Output must be a valid JSON object with Success/Summary/Issues.");
    }

    private static string NormalizeJsonText(string text)
    {
        var json = (text ?? string.Empty)
            .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.Ordinal)
            .Trim();

        // 标准化常见中文标点，减少解析噪音
        json = json.Replace("：", ":", StringComparison.Ordinal)
                   .Replace("，", ",", StringComparison.Ordinal)
                   .Replace("“", "\"", StringComparison.Ordinal)
                   .Replace("”", "\"", StringComparison.Ordinal);

        // 截取首尾 JSON 边界
        var firstBrace = json.IndexOf('{', StringComparison.Ordinal);
        var lastBrace = json.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            json = json.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        // 去除尾逗号
        json = Regex.Replace(json, @",\s*([}\]])", "$1");
        return json.Trim();
    }

    private static ValidationResult? TryDeserializeValidationResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.Contains("\"Success\"", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var token = JToken.Parse(json);
            if (token is not JObject obj)
            {
                return null;
            }

            var successToken = obj.GetValue("Success", StringComparison.OrdinalIgnoreCase);
            var summaryToken = obj.GetValue("Summary", StringComparison.OrdinalIgnoreCase);
            var issuesToken = obj.GetValue("Issues", StringComparison.OrdinalIgnoreCase);
            if (successToken == null)
            {
                return null;
            }

            var success = successToken.Type switch
            {
                JTokenType.Boolean => successToken.Value<bool>(),
                JTokenType.String when bool.TryParse(successToken.Value<string>(), out var b) => b,
                _ => false
            };

            var summary = summaryToken?.Type == JTokenType.String
                ? (summaryToken.Value<string>() ?? string.Empty)
                : (summaryToken?.ToString() ?? string.Empty);

            var issues = new List<string>();
            if (issuesToken is JArray arr)
            {
                foreach (var item in arr)
                {
                    var s = item?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        issues.Add(s);
                    }
                }
            }
            else if (issuesToken != null)
            {
                var s = issuesToken.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    issues.Add(s);
                }
            }

            return new ValidationResult(success, string.IsNullOrWhiteSpace(summary) ? "验证结果缺少 Summary 字段。" : summary, issues);
        }
        catch (JsonReaderException)
        {
            return null;
        }
        catch (JsonSerializationException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ExtractJsonObjects(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        var depth = 0;
        var start = -1;
        var inString = false;
        var escape = false;

        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (ch == '\\')
            {
                escape = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (ch == '{')
            {
                if (depth == 0)
                {
                    start = i;
                }
                depth++;
                continue;
            }

            if (ch == '}')
            {
                if (depth == 0)
                {
                    continue;
                }

                depth--;
                if (depth == 0 && start >= 0)
                {
                    var candidate = raw.Substring(start, i - start + 1);
                    if (candidate.Contains("\"Success\"", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return candidate;
                    }
                    start = -1;
                }
            }
        }
    }

    private static ValidationResult? TryParseByRegex(string raw)
    {
        var successMatch = Regex.Match(raw, "\"Success\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
        if (!successMatch.Success)
        {
            return null;
        }

        var success = bool.TryParse(successMatch.Groups[1].Value, out var b) && b;
        var summaryMatch = Regex.Match(raw, "\"Summary\"\\s*:\\s*\"([\\s\\S]*?)\"", RegexOptions.IgnoreCase);
        var summary = summaryMatch.Success ? summaryMatch.Groups[1].Value : "验证结果格式异常（已使用兜底解析）。";
        summary = summary.Replace("\\\"", "\"", StringComparison.Ordinal).Trim();

        var issues = new List<string>();
        var issuesMatch = Regex.Match(raw, "\"Issues\"\\s*:\\s*\\[([\\s\\S]*?)\\]", RegexOptions.IgnoreCase);
        if (issuesMatch.Success)
        {
            var issueItemMatches = Regex.Matches(issuesMatch.Groups[1].Value, "\"([\\s\\S]*?)\"");
            foreach (Match m in issueItemMatches)
            {
                var issue = m.Groups[1].Value.Replace("\\\"", "\"", StringComparison.Ordinal).Trim();
                if (!string.IsNullOrWhiteSpace(issue))
                {
                    issues.Add(issue);
                }
            }
        }

        return new ValidationResult(success, summary, issues);
    }

    private static string NormalizeFailureSignalText(string? text) =>
        (text ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static string? ExtractValidationResponseText(ChatResponse? response)
    {
        if (response == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            return response.Text;
        }

        var builder = new StringBuilder();
        foreach (var message in response.Messages)
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                builder.Append(message.Text);
            }

            foreach (var content in message.Contents ?? [])
            {
                if (content is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                {
                    builder.Append(tc.Text);
                }
            }
        }

        return builder.Length == 0 ? string.Empty : builder.ToString();
    }

    private static void CollectValidatorStreamingContent(Microsoft.Extensions.AI.ChatResponseUpdate update, System.Text.StringBuilder textSb, System.Text.StringBuilder thinkingSb, List<Microsoft.Extensions.AI.AIContent> allContents)
    {
        var isThinking = update is Microsoft.Extensions.AI.ReasoningChatResponseUpdate ru && ru.Thinking;
        foreach (var part in update.Contents ?? Array.Empty<Microsoft.Extensions.AI.AIContent>())
        {
            if (part is Microsoft.Extensions.AI.TextContent tc && !string.IsNullOrEmpty(tc.Text))
            {
                if (isThinking) thinkingSb.Append(tc.Text);
                else { textSb.Append(tc.Text); allContents.Add(part); }
            }
            else
            {
                allContents.Add(part);
            }
        }
        // Fallback: providers that send text via update.Text instead of Contents.
        // Also covers ReasoningChatResponseUpdate on providers that set update.Text
        // directly while marking Thinking=true on the update.
        if (string.IsNullOrEmpty(update.Text) == false && (update.Contents == null || update.Contents.All(c => c is not Microsoft.Extensions.AI.TextContent)))
        {
            if (isThinking) thinkingSb.Append(update.Text);
            else { textSb.Append(update.Text); allContents.Add(new Microsoft.Extensions.AI.TextContent(update.Text)); }
        }
    }


}
