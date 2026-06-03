using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Services;
using CodexFlow.Core.Telemetry;
using CodexFlow.QueryRuntime.Experimental;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;

internal sealed class RealQueryRuntimeTestHost : IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly DefaultLLMExecutor _llmExecutor;

    private RealQueryRuntimeTestHost(
        RealVllmAgentSettings settings,
        IChatClient chatClient,
        DefaultLLMExecutor llmExecutor,
        QueryRuntimeEngine engine)
    {
        Settings = settings;
        _chatClient = chatClient;
        _llmExecutor = llmExecutor;
        Engine = engine;
    }

    public RealVllmAgentSettings Settings { get; }

    public QueryRuntimeEngine Engine { get; }

    public async Task<ChatResponse> GetResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        return await _chatClient.GetResponseAsync(messages, options, ct);
    }

    public IAsyncEnumerable<ChatResponseUpdate> StreamResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        return _chatClient.GetStreamingResponseAsync(messages, options, ct);
    }

    public IExperimentalModelClient CreateExperimentalModelClient(bool enableRequiredToolChoiceInjection = true)
        => enableRequiredToolChoiceInjection
            ? new ChatClientExperimentalModelClient(_chatClient)
            : new ChatClientExperimentalModelClient(CreatePlainChatClient(Settings));

    public QueryRuntimeEngine CreateEngine(IContextWindowManager? contextWindowManager = null)
    {
        return new QueryRuntimeEngine(
            _llmExecutor,
            contextWindowManager,
            new DefaultToolExecutionCoordinator(NullLogger<DefaultToolExecutionCoordinator>.Instance),
            new DefaultQueryRecoveryPolicy(NullLogger<DefaultQueryRecoveryPolicy>.Instance),
            telemetry: null,
            logger: NullLogger<QueryRuntimeEngine>.Instance,
            runtimeHookDispatcher: CreateRuntimeHookDispatcher());
    }

    public static bool TryCreate(out RealQueryRuntimeTestHost? host, out string reason)
    {
        host = null;

        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "Set RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true to enable live QueryRuntime integration tests.";
            return false;
        }

        var configPath = RepositoryPathHelper.FindRepositoryFile(Path.Combine("CodexFlow", "appsettings.json"));
        if (configPath is null)
        {
            reason = "Could not locate CodexFlow/appsettings.json from the current test process.";
            return false;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false, reloadOnChange: false)
            .Build();

        var settings = configuration.GetSection("VllmAgent").Get<RealVllmAgentSettings>();
        if (settings is null ||
            string.IsNullOrWhiteSpace(settings.ApiUrl) ||
            string.IsNullOrWhiteSpace(settings.ApiKey) ||
            string.IsNullOrWhiteSpace(settings.Model))
        {
            reason = "VllmAgent section is missing ApiUrl, ApiKey, or Model in CodexFlow/appsettings.json.";
            return false;
        }

        var chatClient = CreateChatClient(settings);
        var llmExecutor = new DefaultLLMExecutor(
            chatClient,
            new NoOpMemoryContextAssembler(),
            NullLogger<DefaultLLMExecutor>.Instance);

        var engine = new QueryRuntimeEngine(
            llmExecutor,
            contextWindowManager: null,
            toolCoordinator: new DefaultToolExecutionCoordinator(NullLogger<DefaultToolExecutionCoordinator>.Instance),
            recoveryPolicy: new DefaultQueryRecoveryPolicy(NullLogger<DefaultQueryRecoveryPolicy>.Instance),
            telemetry: null,
            logger: NullLogger<QueryRuntimeEngine>.Instance,
            runtimeHookDispatcher: CreateRuntimeHookDispatcher());

        host = new RealQueryRuntimeTestHost(settings, chatClient, llmExecutor, engine);
        reason = string.Empty;
        return true;
    }

    public static bool IsSoakEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("RUN_QUERY_RUNTIME_REAL_SOAK_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    public static int GetSoakIterations()
    {
        var raw = Environment.GetEnvironmentVariable("QUERY_RUNTIME_REAL_SOAK_ITERATIONS");
        return int.TryParse(raw, out var parsed) && parsed > 0
            ? parsed
            : 5;
    }

    public ChatOptions CreateChatOptions(IReadOnlyList<AIFunction>? tools = null, int maxOutputTokens = 256)
    {
        return new ChatOptions
        {
            Temperature = 0,
            TopP = Settings.TopP,
            MaxOutputTokens = maxOutputTokens,
            Tools = tools?.Cast<AITool>().ToList()
        };
    }

    public void Dispose()
    {
        _chatClient.Dispose();
    }

    private static IChatClient CreateChatClient(RealVllmAgentSettings settings)
    {
        var httpClient = new HttpClient(new LiveToolChoiceInjectionHandler(new HttpClientHandler()));
        var inner = VllmChatClientFactory.Create(settings.ApiUrl, settings.ApiKey, settings.Model, settings.ApiMode, httpClient);
        return new ToolChoiceAwareChatClient(inner);
    }

    private static IChatClient CreatePlainChatClient(RealVllmAgentSettings settings)
        => VllmChatClientFactory.Create(settings.ApiUrl, settings.ApiKey, settings.Model, settings.ApiMode);

    private static RuntimeHookDispatcher CreateRuntimeHookDispatcher()
        => new(
            [new RequiredToolExecutionRuntimeHook()],
            NullLogger<RuntimeHookDispatcher>.Instance);

    internal sealed class RealVllmAgentSettings
    {
        public string ApiUrl { get; init; } = string.Empty;

        public string ApiKey { get; init; } = string.Empty;

        public string Model { get; init; } = string.Empty;

        public string? ApiMode { get; init; }

        public int? MaxTokensLength { get; init; }

        public float? Temperature { get; init; }

        public float? TopP { get; init; }
    }

    private sealed class NoOpMemoryContextAssembler : IMemoryContextAssembler
    {
        public Task<MemoryContextEnvelope> AssembleAsync(MemoryContextRequest request, CancellationToken ct = default)
            => Task.FromResult(MemoryContextEnvelope.Empty);
    }

    private sealed class ToolChoiceAwareChatClient(IChatClient inner) : IChatClient
    {
        public void Dispose() => inner.Dispose();

        public TService? GetService<TService>(object? serviceKey = null) where TService : class
            => inner.GetService<TService>(serviceKey);

        object? IChatClient.GetService(Type serviceType, object? serviceKey)
            => ((IChatClient)inner).GetService(serviceType, serviceKey);

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            using var scope = LiveToolWireOverrideScope.Push(ResolveRequiredToolName(options));
            return await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var scope = LiveToolWireOverrideScope.Push(ResolveRequiredToolName(options));
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return update;
            }
        }

        private static string? ResolveRequiredToolName(ChatOptions? options)
        {
            if (options == null)
            {
                return null;
            }

            var toolMode = options.GetType().GetProperty("ToolMode")?.GetValue(options);
            if (toolMode != null)
            {
                foreach (var propertyName in new[] { "RequiredFunctionName", "RequiredToolName", "FunctionName", "ToolName", "Name" })
                {
                    var value = toolMode.GetType().GetProperty(propertyName)?.GetValue(toolMode) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return options.Tools?.Count == 1 ? options.Tools[0].Name : null;
        }
    }

    private sealed class LiveToolChoiceInjectionHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requiredToolName = LiveToolWireOverrideScope.Current;
            if (request.Content == null || string.IsNullOrWhiteSpace(requiredToolName))
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var effectiveBody = ApplyToolChoice(body, requiredToolName);
            if (!string.Equals(effectiveBody, body, StringComparison.Ordinal))
            {
                request.Content = CloneStringContent(request.Content.Headers, effectiveBody);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private static string ApplyToolChoice(string body, string requiredToolName)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return body;
            }

            try
            {
                var root = JObject.Parse(body);
                if (root["tools"] is not JArray tools || tools.Count == 0)
                {
                    return body;
                }

                foreach (var tool in tools.OfType<JObject>())
                {
                    if (!string.Equals(GetToolName(tool), requiredToolName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    root["tools"] = new JArray(tool.DeepClone());
                    root["tool_choice"] = BuildToolChoice(tool, requiredToolName);
                    return root.ToString(Formatting.None);
                }
            }
            catch (JsonException)
            {
                return body;
            }

            return body;
        }

        private static JToken BuildToolChoice(JObject matchedTool, string requiredToolName)
        {
            if (matchedTool["function"] is JObject)
            {
                return new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = requiredToolName
                    }
                };
            }

            return new JObject
            {
                ["type"] = "tool",
                ["name"] = requiredToolName
            };
        }

        private static string? GetToolName(JObject tool)
        {
            if (tool["name"]?.Type == JTokenType.String)
            {
                return tool["name"]!.Value<string>();
            }

            return tool["function"] is JObject function && function["name"]?.Type == JTokenType.String
                ? function["name"]!.Value<string>()
                : null;
        }

        private static HttpContent CloneStringContent(HttpContentHeaders headers, string body)
        {
            var mediaType = headers.ContentType?.MediaType ?? "application/json";
            var charset = headers.ContentType?.CharSet;
            Encoding encoding;
            try
            {
                encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }

            var content = new StringContent(body, encoding, mediaType);
            foreach (var header in headers)
            {
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return content;
        }
    }

    private sealed class LiveToolWireOverrideScope : IDisposable
    {
        private static readonly AsyncLocal<string?> CurrentOverride = new();
        private readonly string? _previous;

        private LiveToolWireOverrideScope(string? current)
        {
            _previous = CurrentOverride.Value;
            CurrentOverride.Value = current;
        }

        public static string? Current => CurrentOverride.Value;

        public static IDisposable Push(string? requiredToolName)
            => new LiveToolWireOverrideScope(requiredToolName);

        public void Dispose()
        {
            CurrentOverride.Value = _previous;
        }
    }
}
