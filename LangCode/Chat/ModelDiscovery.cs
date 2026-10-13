using LangCode.Agent;
using LangCode.Domain.Catalog;
using LangCode.Domain.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Chat
{
    /// <summary>
    /// 一次「从端点拉取模型列表」的结果
    /// </summary>
    public sealed class ModelDiscoveryResult
    {
        /// <summary>
        /// 拉到的模型 id（去重、排序）
        /// </summary>
        public IList<string> Ids { get; set; } = new List<string>();

        /// <summary>
        /// 失败原因（成功时为空）
        /// </summary>
        public string Error { get; set; } = string.Empty;

        public bool Ok => Ids != null && Ids.Count > 0;
    }

    /// <summary>
    /// 动态模型发现：向 OpenAI 兼容端点的 <c>/models</c> 或 Anthropic 的 <c>/v1/models</c>
    /// 拉取「当前 Key 真正可用」的模型 id，弥补内置目录（<see cref="ModelCatalog"/>）的滞后。
    /// </summary>
    public static class ModelDiscovery
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        private const string AnthropicVersion = "2023-06-01";

        /// <summary>
        /// 拉取当前设置对应端点的模型列表
        /// </summary>
        public static async Task<ModelDiscoveryResult> FetchAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            var result = new ModelDiscoveryResult();
            if (settings == null)
            {
                result.Error = "没有可用的设置";
                return result;
            }

            var provider = ProviderCatalog.Canonicalize(settings.Provider);
            var baseUrl = ProviderCatalog.ResolveBaseUrl(provider, settings.BaseUrl);

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                result.Error = "「" + provider + "」没有可用的端点地址，请先在设置里填写 Base URL";
                return result;
            }

            // Anthropic 官方端点：走原生 /v1/models + x-api-key；
            // 其它（含指向第三方网关的 Anthropic）统一按 OpenAI 兼容处理
            var anthropicNative =
                string.Equals(provider, ProviderCatalog.Anthropic, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(settings.BaseUrl) ||
                 baseUrl.IndexOf("anthropic.com", StringComparison.OrdinalIgnoreCase) >= 0);

            var url = anthropicNative ? AnthropicModelsUrl(baseUrl) : OpenAiModelsUrl(baseUrl);

            try
            {
                using (var http = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (anthropicNative)
                    {
                        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                        {
                            http.Headers.TryAddWithoutValidation("x-api-key", settings.ApiKey);
                        }

                        http.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                    }
                    else if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                    {
                        http.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.ApiKey);
                    }

                    using (var response = await Client.SendAsync(http, cancellationToken).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            result.Error = "HTTP " + (int)response.StatusCode + "： " + Clip(body, 200);
                            return result;
                        }

                        result.Ids = ParseModelIds(body);
                        if (result.Ids.Count == 0)
                        {
                            result.Error = "端点返回里没有解析到任何模型 id";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.GetBaseException().Message;
            }

            return result;
        }

        private static string OpenAiModelsUrl(string baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).TrimEnd('/');
            return trimmed + "/models";
        }

        private static string AnthropicModelsUrl(string baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).TrimEnd('/');
            return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? trimmed + "/models"
                : trimmed + "/v1/models";
        }

        /// <summary>
        /// 解析 <c>{"data":[{"id":"..."}]}</c>（OpenAI 与 Anthropic 的列表结构一致）
        /// </summary>
        private static IList<string> ParseModelIds(string json)
        {
            var ids = new List<string>();
            try
            {
                var root = LiteJson.ParseObject(json);
                var data = LiteJson.GetArray(root, "data");
                if (data == null)
                {
                    return ids;
                }

                foreach (var item in data)
                {
                    if (item is Dictionary<string, object> obj)
                    {
                        var id = LiteJson.GetString(obj, "id");
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            ids.Add(id.Trim());
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 解析失败：返回已取到的部分（可能为空）
            }

            return ids
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var flat = text.Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }

        // ==================== 缓存 / 单飞 ====================

        /// <summary>
        /// 同一端点的缓存有效期：太长会让新增模型看不到，太短则每次打开弹层都打网
        /// </summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        private static readonly object CacheGate = new object();

        private static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private sealed class CacheEntry
        {
            public ModelDiscoveryResult Result;
            public DateTime AtUtc;
        }

        /// <summary>
        /// 带缓存的拉取：同一端点 <see cref="CacheTtl"/> 内只真正请求一次；
        /// <paramref name="force"/> 为真则强制刷新。失败结果也会短暂缓存（避免每次打开都卡在失败上）。
        /// </summary>
        public static Task<ModelDiscoveryResult> FetchCachedAsync(
            AppSettings settings, bool force, CancellationToken cancellationToken)
        {
            if (settings == null)
            {
                return Task.FromResult(new ModelDiscoveryResult { Error = "没有可用的设置" });
            }

            // 整个「查缓存 → 发请求 → 回写缓存」都放到线程池上：
            // 调用方（UI 线程）只是 await，期间的任何解析 / 字典操作都不会占用界面线程
            return Task.Run(async () =>
            {
                var key = CacheKey(settings);

                lock (CacheGate)
                {
                    if (!force && Cache.TryGetValue(key, out var cached) &&
                        DateTime.UtcNow - cached.AtUtc < CacheTtl)
                    {
                        return cached.Result;
                    }
                }

                var result = await FetchAsync(settings, cancellationToken).ConfigureAwait(false);

                lock (CacheGate)
                {
                    Cache[key] = new CacheEntry { Result = result, AtUtc = DateTime.UtcNow };
                }

                return result;
            }, cancellationToken);
        }

        /// <summary>
        /// 缓存键：厂商 + 端点 + Key 的弱散列（换 Key / 换端点即视为不同来源）
        /// </summary>
        private static string CacheKey(AppSettings settings)
        {
            var provider = ProviderCatalog.Canonicalize(settings.Provider);
            var baseUrl = ProviderCatalog.ResolveBaseUrl(provider, settings.BaseUrl);
            var keyHash = (settings.ApiKey ?? string.Empty).GetHashCode().ToString("X");
            return provider + "|" + baseUrl + "|" + keyHash;
        }
    }
}
