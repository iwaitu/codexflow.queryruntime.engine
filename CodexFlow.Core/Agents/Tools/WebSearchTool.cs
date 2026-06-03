using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 使用内置 HttpClient 执行 Web 搜索的工具 (免 API Key 版)。
/// </summary>
public class WebSearchTool : ICodexTool
{
    private readonly ILogger<WebSearchTool> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public string Name => "web_search";
    public string Description => "执行互联网搜索，查找技术文档、最佳实践或漏洞修复方案。参数: query (搜索关键词)。如果是针对特定技术（如 C#），建议在关键词中指明，或直接包含 site:learn.microsoft.com 等限定符。Few-shot: web_search({\"query\":\"ASP.NET Core file upload security best practices\"})。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages { get; } = [1, 2, 3, 4, 5]; // Available across most stages

    public WebSearchTool(ILogger<WebSearchTool> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var query = arguments.GetValueOrDefault("query")?.ToString();
        if (string.IsNullOrWhiteSpace(query))
        {
            return CodexToolResult.Error("Missing required parameter: query");
        }

        StructuredLog.Information(_logger, "web_search: Executing search for '{Query}'", query);

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");

            // 使用 DuckDuckGo Lite 作为免 key 的数据源
            var url = new Uri("https://lite.duckduckgo.com/lite/", UriKind.Absolute);
            using var requestContent = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", query)]);

            using var response = await client.PostAsync(url, requestContent, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var results = ParseDuckDuckGoLite(html);

            if (results.Count == 0)
            {
                return CodexToolResult.Succeeded("未找到相关结果。请尝试更换搜索词或放宽 site 限制。");
            }

            var output = string.Join("\n\n", results.Take(5).Select((r, i) => $"### {i + 1}. {r.Title}\n**URL**: {r.Url}\n**Snippet**: {r.Snippet}"));
            return CodexToolResult.Succeeded($"找到以下搜索结果：\n\n{output}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(_logger, ex, "web_search failed for query '{Query}'", query);
            return CodexToolResult.Error($"搜索失败: {ex.Message}");
        }
        catch (RegexMatchTimeoutException ex)
        {
            StructuredLog.Error(_logger, ex, "web_search failed for query '{Query}'", query);
            return CodexToolResult.Error($"搜索失败: {ex.Message}");
        }
    }

    private static List<SearchResult> ParseDuckDuckGoLite(string html)
    {
        var results = new List<SearchResult>();

        // DuckDuckGo Lite HTML 结构极其简单，可以通过正则快速提取
        // 提取标题和URL: <a class="result-url" href="...">...</a> (有时包裹在 td 中)
        // 提取摘要: <td class="result-snippet">...</td>

        var rowRegex = new Regex(@"<tr.*?>(.*?)</tr>", RegexOptions.Singleline);
        var titleUrlRegex = new Regex(@"<a class=""result-url"" href=""([^""]+)"".*?>(.*?)</a>", RegexOptions.Singleline);
        var snippetRegex = new Regex(@"<td class=""result-snippet"".*?>(.*?)</td>", RegexOptions.Singleline);

        SearchResult? currentResult = null;

        foreach (Match rowMatch in rowRegex.Matches(html))
        {
            var rowHtml = rowMatch.Groups[1].Value;

            var tuMatch = titleUrlRegex.Match(rowHtml);
            if (tuMatch.Success)
            {
                currentResult = new SearchResult
                {
                    Url = tuMatch.Groups[1].Value,
                    Title = CleanHtml(tuMatch.Groups[2].Value)
                };
            }
            else if (currentResult != null)
            {
                var sMatch = snippetRegex.Match(rowHtml);
                if (sMatch.Success)
                {
                    currentResult.Snippet = CleanHtml(sMatch.Groups[1].Value);
                    results.Add(currentResult);
                    currentResult = null; // 重置以匹配下一组
                }
            }
        }

        return results;
    }

    private static string CleanHtml(string input)
    {
        input = Regex.Replace(input, "<.*?>", string.Empty); // 移除标签
        input = input.Replace("&#x27;", "'", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
        input = input.Replace("\n", " ", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        return Regex.Replace(input, @"\s{2,}", " "); // 将多个空格替换为一个
    }

    private sealed class SearchResult
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
    }
}

