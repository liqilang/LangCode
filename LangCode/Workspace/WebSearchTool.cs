using LangCode.Agent;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Workspace
{
    /// <summary>
    /// 一条联网搜索结果
    /// </summary>
    public sealed class WebSearchResult
    {
        public string Title { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string Snippet { get; set; } = string.Empty;
    }

    /// <summary>
    /// 联网搜索工具：把关键词交给搜索提供商，返回标题 / URL / 摘要 
    /// </summary>
    public sealed class WebSearchTool
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0 Safari/537.36 LangCode";

        private static readonly HttpClient Client = CreateClient();

        private static readonly Regex TagRegex = new Regex("<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex SpaceRegex = new Regex("\\s+", RegexOptions.Compiled);

        /// <summary>
        /// 搜索提供商：Bing / DuckDuckGo / Brave / Tavily / Serper
        /// </summary>
        public string Provider { get; set; } = "Bing";

        /// <summary>
        /// Brave / Tavily / Serper 的 API Key
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// 自定义搜索端点（留空使用内置地址）
        /// </summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>
        /// 默认返回条数
        /// </summary>
        public int MaxResults { get; set; } = 5;

        /// <summary>
        /// 单次搜索超时（秒）
        /// </summary>
        public int TimeoutSeconds { get; set; } = 20;

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,

                // 只在当前 handler 上启用 TLS 1.2，不修改 Visual Studio 进程的全局网络设置。
                // .NET Framework 4.7.2 在部分系统上默认仍会协商旧协议，Bing 会直接重置连接。
                SslProtocols = SslProtocols.Tls12,
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(40),
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            return client;
        }

        /// <summary>
        /// 执行一次联网搜索
        /// </summary>
        public async Task<ToolResult> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            var text = (query ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return Fail("搜索关键词不能为空");
            }

            var provider = NormalizeProvider(Provider);
            var count = Math.Max(1, Math.Min(15, limit > 0 ? limit : (MaxResults > 0 ? MaxResults : 5)));

            // 每次搜索单独计时：超时只影响这一次，不拖住整个任务
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                linked.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, TimeoutSeconds)));
                var token = linked.Token;

                try
                {
                    List<WebSearchResult> results;

                    switch (provider)
                    {
                        case "duckduckgo":
                            results = await SearchDuckDuckGoWithFallbackAsync(text, count, token).ConfigureAwait(false);
                            break;

                        case "brave":
                            results = await SearchBraveAsync(text, count, token).ConfigureAwait(false);
                            break;

                        case "tavily":
                            results = await SearchTavilyAsync(text, count, token).ConfigureAwait(false);
                            break;

                        case "serper":
                            results = await SearchSerperAsync(text, count, token).ConfigureAwait(false);
                            break;

                        default:
                            results = await SearchBingRssAsync(text, count, token).ConfigureAwait(false);
                            provider = "bing";
                            break;
                    }

                    if (results == null || results.Count == 0)
                    {
                        return Fail("没有搜到结果（" + DisplayName(provider) + "）请换一个更具体的关键词，" + "或在「设置 → 能力 → 网络搜索」里配置其它提供商");
                    }

                    return Ok(FormatResults(provider, text, results));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Fail("联网搜索超时（" + TimeoutSeconds + " 秒，提供商 " + DisplayName(provider) + "）");
                }
                catch (OperationCanceledException)
                {
                    // 用户取消了整个任务：交给 Agent 统一处理
                    throw;
                }
                catch (Exception ex)
                {
                    return Fail("联网搜索失败（" + DisplayName(provider) + "）：" + ex.GetBaseException().Message);
                }
            }
        }

        // ==================== 各提供商实现 ====================

        /// <summary>
        /// DuckDuckGo 无需 Key；无结果或页面结构变化时自动回退到 Bing RSS
        /// </summary>
        private async Task<List<WebSearchResult>> SearchDuckDuckGoWithFallbackAsync(string query, int count, CancellationToken token)
        {
            Exception firstError = null;

            try
            {
                var html = await GetAsync("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), null, token).ConfigureAwait(false);

                var results = ParseDuckDuckGo(html, count);
                if (results.Count > 0)
                {
                    return results;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstError = ex;
            }

            // Lite 版页面通常更轻、更稳定；仍然失败再走 Bing RSS
            try
            {
                var lite = await GetAsync("https://lite.duckduckgo.com/lite/?q=" + Uri.EscapeDataString(query), null, token).ConfigureAwait(false);

                var results = ParseDuckDuckGoLite(lite, count);
                if (results.Count > 0)
                {
                    return results;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }

            try
            {
                var results = await SearchBingRssAsync(query, count, token).ConfigureAwait(false);
                if (results.Count > 0)
                {
                    return results;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }

            if (firstError != null)
            {
                throw new InvalidOperationException("DuckDuckGo 与 Bing 回退均失败：" + firstError.GetBaseException().Message);
            }

            return [];
        }

        /// <summary>
        /// Bing 公开 RSS：无需 Key，返回结构与网页搜索结果接近
        /// </summary>
        private async Task<List<WebSearchResult>> SearchBingRssAsync(string query, int count, CancellationToken token)
        {
            // 注意：Bing RSS 对 count 参数比较敏感，带上它有时会返回空 channel；
            // 统一不带 count，取回结果后再由 ParseBingRss 截断到需要的条数。
            var url = string.IsNullOrWhiteSpace(Endpoint) ? "https://www.bing.com/search?format=rss&q=" + Uri.EscapeDataString(query) : Endpoint.Trim();

            var xml = await GetAsync(url, null, token).ConfigureAwait(false);
            return ParseBingRss(xml, count);
        }

        private async Task<List<WebSearchResult>> SearchBraveAsync(string query, int count, CancellationToken token)
        {
            var key = (ApiKey ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                throw new InvalidOperationException("Brave 需要 API Key：请到「设置 → 能力 → 网络搜索」填写");
            }

            var url = string.IsNullOrWhiteSpace(Endpoint) ? "https://api.search.brave.com/res/v1/web/search?count=" + count + "&safesearch=moderate&q=" + Uri.EscapeDataString(query) : Endpoint.Trim();

            var headers = new Dictionary<string, string>
            {
                ["X-Subscription-Token"] = key,
                ["Accept"] = "application/json",
            };

            var json = await GetAsync(url, headers, token).ConfigureAwait(false);
            var root = LiteJson.ParseObject(json);
            var web = LiteJson.GetObject(root, "web");
            var array = LiteJson.GetArray(web, "results");

            return ReadJsonResults(array, "title", "url", "description", count);
        }

        private async Task<List<WebSearchResult>> SearchTavilyAsync(string query, int count, CancellationToken token)
        {
            var key = (ApiKey ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                throw new InvalidOperationException("Tavily 需要 API Key：请到「设置 → 能力 → 网络搜索」填写");
            }

            var url = string.IsNullOrWhiteSpace(Endpoint) ? "https://api.tavily.com/search" : Endpoint.Trim();
            var body = "{\"api_key\":" + LiteJson.Quote(key) +
                       ",\"query\":" + LiteJson.Quote(query) +
                       ",\"max_results\":" + count +
                       ",\"search_depth\":\"basic\",\"include_answer\":false}";

            var json = await PostJsonAsync(url, body, null, token).ConfigureAwait(false);
            var root = LiteJson.ParseObject(json);
            var array = LiteJson.GetArray(root, "results");

            return ReadJsonResults(array, "title", "url", "content", count);
        }

        private async Task<List<WebSearchResult>> SearchSerperAsync(string query, int count, CancellationToken token)
        {
            var key = (ApiKey ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                throw new InvalidOperationException("Serper 需要 API Key：请到「设置 → 能力 → 网络搜索」填写");
            }

            var url = string.IsNullOrWhiteSpace(Endpoint) ? "https://google.serper.dev/search" : Endpoint.Trim();
            var body = "{\"q\":" + LiteJson.Quote(query) + ",\"num\":" + count + "}";

            var headers = new Dictionary<string, string>
            {
                ["X-API-KEY"] = key,
            };

            var json = await PostJsonAsync(url, body, headers, token).ConfigureAwait(false);
            var root = LiteJson.ParseObject(json);
            var array = LiteJson.GetArray(root, "organic");

            return ReadJsonResults(array, "title", "link", "snippet", count);
        }

        // ==================== 解析 ====================

        private static List<WebSearchResult> ParseDuckDuckGo(string html, int count)
        {
            var results = new List<WebSearchResult>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return results;
            }

            var snippetRegex = new Regex(
                "<(?:a|div)\\b(?=[^>]*result__snippet)[^>]*>(?<snippet>.*?)</(?:a|div)>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var snippets = new List<string>();
            foreach (Match match in snippetRegex.Matches(html))
            {
                snippets.Add(CleanHtml(match.Groups["snippet"].Value));
            }

            var linkRegex = new Regex(
                "<a\\b(?=[^>]*result__a)[^>]*href=\"(?<url>[^\"]+)\"[^>]*>(?<title>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var index = 0;
            foreach (Match match in linkRegex.Matches(html))
            {
                var url = DecodeDuckDuckGoUrl(WebUtility.HtmlDecode(match.Groups["url"].Value));
                if (string.IsNullOrWhiteSpace(url) || IsAdUrl(url))
                {
                    continue;
                }

                results.Add(new WebSearchResult
                {
                    Title = CleanHtml(match.Groups["title"].Value),
                    Url = url,
                    Snippet = index < snippets.Count ? snippets[index] : string.Empty,
                });

                index++;
                if (results.Count >= count)
                {
                    break;
                }
            }

            return results;
        }

        private static List<WebSearchResult> ParseDuckDuckGoLite(string html, int count)
        {
            var results = new List<WebSearchResult>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return results;
            }

            var linkRegex = new Regex(
                "<a\\b(?=[^>]*result-link)[^>]*href=\"(?<url>[^\"]+)\"[^>]*>(?<title>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var snippetRegex = new Regex(
                "<td\\b(?=[^>]*result-snippet)[^>]*>(?<snippet>.*?)</td>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var snippets = new List<string>();
            foreach (Match match in snippetRegex.Matches(html))
            {
                snippets.Add(CleanHtml(match.Groups["snippet"].Value));
            }

            var index = 0;
            foreach (Match match in linkRegex.Matches(html))
            {
                var url = DecodeDuckDuckGoUrl(WebUtility.HtmlDecode(match.Groups["url"].Value));
                if (string.IsNullOrWhiteSpace(url) || IsAdUrl(url))
                {
                    continue;
                }

                results.Add(new WebSearchResult
                {
                    Title = CleanHtml(match.Groups["title"].Value),
                    Url = url,
                    Snippet = index < snippets.Count ? snippets[index] : string.Empty,
                });

                index++;
                if (results.Count >= count)
                {
                    break;
                }
            }

            return results;
        }

        private static List<WebSearchResult> ParseBingRss(string xml, int count)
        {
            var results = new List<WebSearchResult>();
            if (string.IsNullOrWhiteSpace(xml))
            {
                return results;
            }

            var itemRegex = new Regex("<item>(?<item>.*?)</item>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var titleRegex = new Regex("<title>(?<value>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var linkRegex = new Regex("<link>(?<value>.*?)</link>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var descRegex = new Regex("<description>(?<value>.*?)</description>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match item in itemRegex.Matches(xml))
            {
                var body = item.Groups["item"].Value;
                var title = MatchValue(titleRegex, body);
                var link = MatchValue(linkRegex, body);
                var description = MatchValue(descRegex, body);

                if (string.IsNullOrWhiteSpace(link))
                {
                    continue;
                }

                results.Add(new WebSearchResult
                {
                    Title = CleanHtml(title),
                    Url = WebUtility.HtmlDecode(link).Trim(),
                    Snippet = CleanHtml(description),
                });

                if (results.Count >= count)
                {
                    break;
                }
            }

            return results;
        }

        private static List<WebSearchResult> ReadJsonResults(
            List<object> array,
            string titleKey,
            string urlKey,
            string snippetKey,
            int count)
        {
            var results = new List<WebSearchResult>();
            if (array == null)
            {
                return results;
            }

            foreach (var item in array)
            {
                if (!(item is Dictionary<string, object> obj))
                {
                    continue;
                }

                var url = LiteJson.GetString(obj, urlKey) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                results.Add(new WebSearchResult
                {
                    Title = CleanHtml(LiteJson.GetString(obj, titleKey) ?? string.Empty),
                    Url = url.Trim(),
                    Snippet = CleanHtml(LiteJson.GetString(obj, snippetKey) ?? string.Empty),
                });

                if (results.Count >= count)
                {
                    break;
                }
            }

            return results;
        }

        // ==================== HTTP ====================

        private static async Task<string> GetAsync(
            string url,
            IDictionary<string, string> headers,
            CancellationToken token)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
                if (headers != null)
                {
                    foreach (var pair in headers)
                    {
                        request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    }
                }

                using (var response = await Client.SendAsync(request, token).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            "HTTP " + (int)response.StatusCode + " —— " + Clip(body, 300));
                    }

                    return body;
                }
            }
        }

        private static async Task<string> PostJsonAsync(
            string url,
            string body,
            IDictionary<string, string> headers,
            CancellationToken token)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (headers != null)
                {
                    foreach (var pair in headers)
                    {
                        request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    }
                }

                using (var response = await Client.SendAsync(request, token).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            "HTTP " + (int)response.StatusCode + " —— " + Clip(text, 300));
                    }

                    return text;
                }
            }
        }

        // ==================== 格式化 / 小工具 ====================

        private static string FormatResults(string provider, string query, IList<WebSearchResult> results)
        {
            var builder = new StringBuilder();
            builder.Append("联网搜索（").Append(DisplayName(provider)).Append("）：「")
                   .Append(query.Trim()).Append("」共 ").Append(results.Count).Append(" 条：\n\n");

            var index = 0;
            foreach (var result in results)
            {
                builder.Append(++index).Append(". ").Append(string.IsNullOrWhiteSpace(result.Title) ? "(无标题)" : result.Title).Append('\n');
                builder.Append("   ").Append(result.Url).Append('\n');
                if (!string.IsNullOrWhiteSpace(result.Snippet))
                {
                    builder.Append("   ").Append(Clip(result.Snippet, 600)).Append('\n');
                }

                builder.Append('\n');
            }

            builder.Append("提示：搜索结果来自公开网页，可能与项目实际版本不一致；");
            builder.Append("需要正文时可用 open_url 打开链接，关键结论请结合项目代码核对。");
            return builder.ToString().TrimEnd();
        }

        private static string NormalizeProvider(string provider)
        {
            var text = (provider ?? string.Empty).Trim().ToLowerInvariant();
            return text switch
            {
                "duckduckgo" or "ddg" => "duckduckgo",
                "brave" => "brave",
                "tavily" => "tavily",
                "serper" => "serper",
                _ => "bing",
            };
        }

        private static string DisplayName(string provider)
        {
            return provider switch
            {
                "duckduckgo" => "DuckDuckGo",
                "brave" => "Brave",
                "tavily" => "Tavily",
                "serper" => "Serper",
                _ => "Bing",
            };
        }

        private static string DecodeDuckDuckGoUrl(string url)
        {
            var text = (url ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return text;
            }

            var match = Regex.Match(text, "[?&]uddg=(?<u>[^&]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                try
                {
                    text = Uri.UnescapeDataString(match.Groups["u"].Value);
                }
                catch (Exception)
                {
                    // 解码失败时保留原始链接
                }
            }

            if (text.StartsWith("//", StringComparison.Ordinal))
            {
                text = "https:" + text;
            }
            else if (text.StartsWith("/", StringComparison.Ordinal))
            {
                text = "https://duckduckgo.com" + text;
            }

            return text;
        }

        private static bool IsAdUrl(string url)
        {
            return url.IndexOf("duckduckgo.com/y.js", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("bing.com/aclick", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string MatchValue(Regex regex, string text)
        {
            var match = regex.Match(text ?? string.Empty);
            return match.Success ? match.Groups["value"].Value : string.Empty;
        }

        private static string CleanHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var text = TagRegex.Replace(html, " ");
            text = WebUtility.HtmlDecode(text);
            text = SpaceRegex.Replace(text, " ");
            return text.Trim();
        }

        private static string Clip(string text, int max)
        {
            var value = (text ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }

        private static ToolResult Ok(string output)
        {
            return new ToolResult { Ok = true, Output = output };
        }

        private static ToolResult Fail(string output)
        {
            return new ToolResult { Ok = false, Output = output };
        }
    }
}
