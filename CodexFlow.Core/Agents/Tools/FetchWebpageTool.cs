using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 抓取指定 URL 网页纯文本内容的工具，供模型在搜索结果之外按需深挖信息。
/// </summary>
public class FetchWebpageTool : ICodexTool
{
    private readonly ILogger<FetchWebpageTool> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public string Name => "fetch_webpage";
    public string Description => "获取指定网页 URL 的主体纯文本内容。适用于当搜索结果的摘要不足以解答问题时，深度阅读该页面的代码或详细说明。参数: url (需要抓取的 HTTP/HTTPS 链接)。Few-shot: fetch_webpage({\"url\":\"https://learn.microsoft.com/dotnet\"})。";
    public ToolCategory Category => ToolCategory.Read;
    public IReadOnlyList<int> AllowedStages { get; } = [1, 2, 3, 4, 5]; // Available across most stages

    public FetchWebpageTool(ILogger<FetchWebpageTool> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var url = arguments.GetValueOrDefault("url")?.ToString();
        if (string.IsNullOrWhiteSpace(url))
        {
            return CodexToolResult.Error("Missing required parameter: url");
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return CodexToolResult.Error("Invalid URL format. Must start with http:// or https://");
        }

        StructuredLog.Information(_logger, "fetch_webpage: Fetching content from '{Url}'", url);

        try
        {
            using var client = _httpClientFactory.CreateClient();
            // 伪装浏览器指纹防止被轻易拦截
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            client.Timeout = TimeSpan.FromSeconds(15);

            using var response = await client.GetAsync(new Uri(url, UriKind.Absolute), ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var cleanText = ExtractCleanText(html);

            // 截断超大文本以保护 token (约 15000 字符限制)
            const int MaxChars = 15000;
            if (cleanText.Length > MaxChars)
            {
                cleanText = string.Concat(cleanText.AsSpan(0, MaxChars), "\n\n... (部分内容已省略，超出最大长度限制) ...");
            }

            return CodexToolResult.Succeeded($"成功获取网页内容 (长度: {cleanText.Length} 字符):\n\n{cleanText}");
        }
        catch (HttpRequestException reqEx)
        {
            StructuredLog.Warning(_logger, reqEx, "fetch_webpage HTTP request failed for '{Url}'", url);
            return CodexToolResult.Error($"获取网页失败 (HTTP错误): {reqEx.Message}");
        }
        catch (TaskCanceledException)
        {
            return CodexToolResult.Error("获取网页超时 (超过15秒)。");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(_logger, ex, "fetch_webpage failed for '{Url}'", url);
            return CodexToolResult.Error($"获取网页内容失败: {ex.Message}");
        }
        catch (RegexMatchTimeoutException ex)
        {
            StructuredLog.Error(_logger, ex, "fetch_webpage failed for '{Url}'", url);
            return CodexToolResult.Error($"获取网页内容失败: {ex.Message}");
        }
    }

    private static string ExtractCleanText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // 基础正则清理
        string result = html;

        // 移除 head
        result = Regex.Replace(result, @"<head.*?>.*?</head>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // 移除 script
        result = Regex.Replace(result, @"<script.*?>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // 移除 style
        result = Regex.Replace(result, @"<style.*?>.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // 移除 svg 等无用区块
        result = Regex.Replace(result, @"<svg.*?>.*?</svg>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // 替换常见块级标签为换行，保证阅读排版
        result = Regex.Replace(result, @"<(p|div|br|hr|li|h[1-6]|table|tr).*?>", "\n", RegexOptions.IgnoreCase);
        // 移除其他所有 HTML 标签
        result = Regex.Replace(result, @"<.*?>", string.Empty, RegexOptions.Singleline);

        // 解析 HTML 实体
        result = System.Net.WebUtility.HtmlDecode(result);

        // 去除多余空行和空白字符 (将连续3个以上的换行换成1个，多余空格压缩)
        result = Regex.Replace(result, @"(\r\n|\n|\r){3,}", "\n\n");
        result = Regex.Replace(result, @"[ \t]{2,}", " ");

        return result.Trim();
    }
}

