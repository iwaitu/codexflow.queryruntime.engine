using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Governance;
using CodexFlow.Core.Services;

namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 委员会角色的 IChatClient 工厂。
/// 根据 CommitteeRoleBindings 配置为每个角色创建独立的 IChatClient。
/// </summary>
public interface ICommitteeRoleChatClientFactory
{
    /// <summary>
    /// 获取指定角色的 IChatClient。
    /// </summary>
    /// <param name="role">角色名：Analyst, Architect, ProjectManager。</param>
    IChatClient GetClientForRole(string role);
}

/// <summary>
/// 基于 IConfiguration 的角色 ChatClient 工厂。
/// 每个角色读取对应的 vllm 配置节，构造专用 IChatClient。
/// 若角色未配置独立节，回退到默认 IChatClient。
/// </summary>
public class CommitteeRoleChatClientFactory : ICommitteeRoleChatClientFactory
{
    private readonly IConfiguration _configuration;
    private readonly IChatClient _defaultClient;
    private readonly ILogger<CommitteeRoleChatClientFactory> _logger;
    private readonly Dictionary<string, string> _roleBindings;

    public CommitteeRoleChatClientFactory(
        IConfiguration configuration,
        IChatClient defaultClient,
        ILogger<CommitteeRoleChatClientFactory> logger)
    {
        _configuration = configuration;
        _defaultClient = defaultClient;
        _logger = logger;

        _roleBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bindings = _configuration.GetSection("Planning:CommitteeRoleBindings");
        foreach (var child in bindings.GetChildren())
        {
            if (!string.IsNullOrEmpty(child.Value))
            {
                _roleBindings[child.Key] = child.Value;
            }
        }
    }

    public IChatClient GetClientForRole(string role)
    {
        if (!_roleBindings.TryGetValue(role, out var sectionName))
        {
            _logger.LogDebug("角色 {Role} 未配置独立 vllm section，使用默认 IChatClient", role);
            return _defaultClient;
        }

        var section = _configuration.GetSection(sectionName);
        if (!section.Exists())
        {
            _logger.LogWarning("角色 {Role} 绑定的 section {Section} 不存在，使用默认 IChatClient", role, sectionName);
            return _defaultClient;
        }

        var apiUrl = section["ApiUrl"];
        var apiKey = section["ApiKey"];
        var model = section["Model"];

        if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model))
        {
            _logger.LogWarning("角色 {Role} 的 section {Section} 缺少必要配置，使用默认 IChatClient", role, sectionName);
            return _defaultClient;
        }

        var temperature = section.GetValue<float?>("Temperature");
        var topP = section.GetValue<float?>("TopP");

        // 根据 model 名称选择对应的 VllmChatClient 实现
        IChatClient rawClient = VllmChatClientFactory.Create(apiUrl, apiKey, model, section["ApiMode"]);

        _logger.LogDebug("为角色 {Role} 创建专用 IChatClient: Model={Model}, Temp={Temp}, TopP={TopP}",
            role, model, temperature, topP);

        return new RoleConfiguredChatClient(rawClient, temperature, topP, _logger, role);
    }
}

/// <summary>
/// 角色配置的 ChatClient 包装器，应用温度和 TopP。
/// 与 ConfiguredSamplingChatClient 功能一致，但放在 Core 项目中以避免对 Web 项目的依赖。
/// </summary>
[ApprovedNonStreamingLlm(
    "Facade wrapper for committee role-specific non-streaming chat calls.",
    Ticket = "non-streaming-llm-migration",
    ReviewBy = "2026-06-30",
    Scope = ApprovedNonStreamingLlmScope.Facade)]
internal sealed class RoleConfiguredChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly float? _temperature;
    private readonly float? _topP;
    private readonly ILogger _logger;
    private readonly string _role;

    public RoleConfiguredChatClient(IChatClient inner, float? temperature, float? topP, ILogger logger, string role)
    {
        _inner = inner;
        _temperature = temperature;
        _topP = topP;
        _logger = logger;
        _role = role;
    }

    public void Dispose() => _inner.Dispose();

    public TService? GetService<TService>(object? serviceKey = null) where TService : class
        => this is TService self ? self : _inner.GetService<TService>(serviceKey);

    object? IChatClient.GetService(Type serviceType, object? serviceKey)
        => serviceType.IsInstanceOfType(this) ? this : ((IChatClient)_inner).GetService(serviceType, serviceKey);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materializedMessages = messages.ToList();
        var effectiveOptions = ApplySampling(options);
        var source = $"{nameof(RoleConfiguredChatClient)}:{_role}";

        try
        {
            var response = await _inner.GetResponseAsync(materializedMessages, effectiveOptions, cancellationToken);
            var (reply, thinking) = ChatClientAudit.ExtractResponse(response);
            ChatClientAudit.LogInteraction(_logger, source, "sync", materializedMessages, effectiveOptions, reply, thinking);
            return response;
        }
        catch (Exception ex)
        {
            ChatClientAudit.LogInteraction(_logger, source, "sync", materializedMessages, effectiveOptions, string.Empty, string.Empty, ex);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materializedMessages = messages.ToList();
        var effectiveOptions = ApplySampling(options);
        var responseText = new System.Text.StringBuilder();
        var thinkingText = new System.Text.StringBuilder();
        var source = $"{nameof(RoleConfiguredChatClient)}:{_role}";

        await using var enumerator = _inner
            .GetStreamingResponseAsync(materializedMessages, effectiveOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    ChatClientAudit.LogInteraction(
                        _logger,
                        source,
                        "stream",
                        materializedMessages,
                        effectiveOptions,
                        responseText.ToString(),
                        thinkingText.ToString());
                    yield break;
                }

                update = enumerator.Current;
            }
            catch (Exception ex)
            {
                ChatClientAudit.LogInteraction(
                    _logger,
                    source,
                    "stream",
                    materializedMessages,
                    effectiveOptions,
                    responseText.ToString(),
                    thinkingText.ToString(),
                    ex);
                throw;
            }

            ChatClientAudit.AppendStreamingUpdate(update, responseText, thinkingText);
            yield return update;
        }
    }

    private ChatOptions ApplySampling(ChatOptions? options)
    {
        var effective = options ?? new ChatOptions();
        if (_temperature.HasValue) effective.Temperature = _temperature.Value;
        if (_topP.HasValue) effective.TopP = _topP.Value;
        return effective;
    }
}
