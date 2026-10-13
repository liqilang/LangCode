using LangCode.Agent;
using LangCode.Domain.Catalog;
using LangCode.Domain.Chat;
using LangCode.Domain.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Chat
{
    /// <summary>
    /// 一轮对话（发给模型的一条消息）
    /// </summary>
    public sealed class ChatTurn
    {
        public ChatTurn(string role, string content, IList<ChatImage> images = null)
        {
            Role = role;
            Content = content;
            Images = images ?? [];
        }

        /// <summary>
        /// system / user / assistant
        /// </summary>
        public string Role { get; }

        public string Content { get; }

        /// <summary>
        /// 随消息附带的图片（仅 user 消息会被渲染成多模态 content 数组）
        /// </summary>
        public IList<ChatImage> Images { get; }
    }

    /// <summary>
    /// 一次补全请求
    /// </summary>
    public sealed class ChatRequest
    {
        public string Model { get; set; } = string.Empty;

        public string SystemPrompt { get; set; } = string.Empty;

        public IList<ChatTurn> Turns { get; set; } = [];

        public double Temperature { get; set; } = 0.2;

        public double TopP { get; set; } = 1.0;

        public int MaxTokens { get; set; } = 4096;

        /// <summary>
        /// 原生 function calling 的 <c>tools</c> JSON（OpenAI 兼容格式）；
        /// 空表示不用原生工具调用（走文本动作协议）
        /// </summary>
        public string ToolsJson { get; set; } = string.Empty;

        /// <summary>
        /// 原生 function calling 的 <c>tools</c> JSON（Anthropic 原生格式）；
        /// 仅 Anthropic 原生后端使用，两套格式由 <see cref="ToolSchema"/> 同步生成
        /// </summary>
        public string AnthropicToolsJson { get; set; } = string.Empty;

        /// <summary>
        /// 用户自定义的「附加请求参数」JSON 对象（OpenAI 兼容网关专用）。
        /// 形如 <c>{"chat_template_kwargs":{"enable_thinking":true}}</c>：内容会被原样拼进请求体根对象，
        /// 用于补上 OpenAI 协议之外的厂商私有开关（例如 vLLM / Qwen3 的 enable_thinking）。
        /// </summary>
        public string ExtraBodyJson { get; set; } = string.Empty;

        /// <summary>
        /// 推理过程增量 
        /// </summary>
        public Action<string> OnReasoning { get; set; }

        /// <summary>
        /// 是否启用提示缓存（prompt caching）以降低成本 
        /// </summary>
        public bool PromptCaching { get; set; }

        /// <summary>
        /// 推理强度（"" = 不带该字段；minimal / low / medium / high） 
        /// </summary>
        public string ReasoningEffort { get; set; } = string.Empty;

        /// <summary>
        /// 回答详细度（"" = 不带该字段；low / medium / high）GPT-5 系会带上 <c>verbosity</c>
        /// </summary>
        public string Verbosity { get; set; } = string.Empty;

        /// <summary>
        /// 思考预算（token；0 = 不带该字段）支持的端点会注入 <c>thinking.budget_tokens</c>
        /// </summary>
        public int ThinkingBudgetTokens { get; set; }

        /// <summary>
        /// 服务端回报用量（含缓存命中）时的回调；端点不回报则不触发
        /// </summary>
        public Action<ChatUsage> OnUsage { get; set; }
    }

    /// <summary>
    /// 一次调用由服务端回报的用量（含提示缓存命中）
    /// </summary>
    public sealed class ChatUsage
    {
        /// <summary>
        /// 输入 token（含缓存命中部分）
        /// </summary>
        public double InputTokens { get; set; }

        public double OutputTokens { get; set; }

        /// <summary>
        /// 命中提示缓存的输入 token（这部分按折扣价计费）
        /// </summary>
        public double CachedInputTokens { get; set; }
    }

    /// <summary>
    /// 可插拔的聊天后端
    /// </summary>
    public interface IChatBackend
    {
        /// <summary>
        /// 显示名（用于状态提示）
        /// </summary>
        string DisplayName { get; }

        Task SendAsync(ChatRequest request, Action<string> onDelta, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 本地回声：未配置 API Key 时使用，保证界面开箱可用（结果不来自任何模型）
    /// </summary>
    public sealed class EchoChatBackend : IChatBackend
    {
        public string DisplayName => "本地回声（未配置 API Key）";

        public async Task SendAsync(ChatRequest request, Action<string> onDelta, CancellationToken cancellationToken)
        {
            var last = request?.Turns?.LastOrDefault(t => t.Role == "user")?.Content ?? string.Empty;

            // 顺带演示「推理过程」块：真实推理模型走各家自己的推理字段，这里给一段占位说明，
            // 这样不配 API Key 也能确认推理块的渲染、折叠与中文编码都正常
            var reasoning = "本地回声模式没有真实推理过程；这一段用于验证「推理过程」块的渲染与折叠"
                + "接上真实模型（如 DeepSeek / OpenRouter 上的推理模型）后，这里显示的就是模型的思考内容";

            foreach (var chunk in Chunk(reasoning, 6))
            {
                cancellationToken.ThrowIfCancellationRequested();
                request?.OnReasoning?.Invoke(chunk);
                await Task.Delay(12, cancellationToken).ConfigureAwait(false);
            }

            var reply =
                "（本地回声模式：尚未配置 API Key，以下内容由本地生成，不来自任何模型）" + Environment.NewLine + Environment.NewLine +
                "你刚才说：" + last + Environment.NewLine + Environment.NewLine +
                "到「设置 → API」填入 API Key（并把 Base URL 指向 OpenAI 兼容端点）后，这里就会变成真实模型的流式回复";

            foreach (var chunk in Chunk(reply, 4))
            {
                cancellationToken.ThrowIfCancellationRequested();
                onDelta(chunk);
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }

        private static IEnumerable<string> Chunk(string text, int size)
        {
            for (var i = 0; i < text.Length; i += size)
            {
                yield return text.Substring(i, Math.Min(size, text.Length - i));
            }
        }
    }

    /// <summary>
    /// 固定提示（用于「该厂商协议尚未接入」这类情况，行为上等同一次回复）
    /// </summary>
    public sealed class NoticeChatBackend : IChatBackend
    {
        private readonly string _notice;

        public NoticeChatBackend(string notice)
        {
            _notice = notice;
        }

        public string DisplayName => "提示";

        public async Task SendAsync(ChatRequest request, Action<string> onDelta, CancellationToken cancellationToken)
        {
            foreach (var chunk in _notice.Split('\n'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                onDelta(chunk);
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// OpenAI 兼容的流式后端（<c>POST {baseUrl}/chat/completions</c>，SSE）
    /// 适用于 OpenAI / OpenRouter / DeepSeek / 通义 / Ollama / LM Studio 等兼容端点
    /// </summary>
    public sealed class OpenAiCompatibleChatBackend : IChatBackend
    {
        private static readonly HttpClient Client = CreateClient();

        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;

        /// <summary>
        /// 是否指向 OpenRouter（决定要不要附带它私有的推理开启参数）
        /// </summary>
        private readonly bool _isOpenRouter;

        /// <summary>
        /// 是否按 Anthropic 系处理（厂商为 Anthropic 或模型名含 claude）：注入 cache_control
        /// </summary>
        private readonly bool _isAnthropic;

        /// <summary>
        /// 是否允许用 <c>stream_options.include_usage</c> 换取用量回报
        /// 端点若因此报 400 会自动降级为 false（拿不到用量回报总比报错强）
        /// </summary>
        private bool _includeUsage = true;

        /// <summary>
        /// 遇到可重试的错误（429 / 5xx / 网络抖动）时是否自动重试（对应设置里的「自动重试」）
        /// </summary>
        private readonly bool _autoRetry;

        /// <summary>
        /// 单次请求的最大重试次数（在还没吐出任何正文时才会重试）
        /// </summary>
        private const int MaxRetries = 3;

        public OpenAiCompatibleChatBackend(string baseUrl, string apiKey, string model, string provider = null, bool autoRetry = true)
        {
            _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
            _apiKey = apiKey ?? string.Empty;
            _model = model ?? string.Empty;
            _autoRetry = autoRetry;

            // OpenRouter 的 <c>reasoning</c> 参数是它私有的：直连厂商或其它兼容网关时，
            // 附上未知字段可能直接 400，所以这里按「提供商名 或 Base URL 域名」双重判断，只对它生效
            _isOpenRouter = ProviderCatalog.IsOpenRouter(provider) || _baseUrl.IndexOf("openrouter.ai", StringComparison.OrdinalIgnoreCase) >= 0;

            _isAnthropic = string.Equals(ProviderCatalog.Canonicalize(provider), ProviderCatalog.Anthropic, StringComparison.OrdinalIgnoreCase) || _model.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public string DisplayName => "OpenAI 兼容（" + _baseUrl + "）";

        public async Task SendAsync(ChatRequest request, Action<string> onDelta, CancellationToken cancellationToken)
        {
            // 只有非 Anthropic 系（自动缓存 + 用量回报）才需要 stream_options；Anthropic 系走 cache_control
            var includeUsage = request != null && request.PromptCaching && !_isAnthropic && _includeUsage;
            var attempt = 0;

            while (true)
            {
                // 已经吐过正文就不再重试：重试会把同一段内容再发一遍（界面上会串字）
                var started = false;
                Action<string> sink = delta =>
                {
                    started = true;
                    onDelta(delta);
                };

                try
                {
                    await SendOnceAsync(request, sink, cancellationToken, includeUsage).ConfigureAwait(false);
                    return;
                }
                catch (UnsupportedStreamOptionsException)
                {
                    // 端点不认识 stream_options：关掉它重试一次（不计入退避重试）
                    ChatDiagnostics.Log("端点不支持 stream_options，去掉该参数后重试");
                    _includeUsage = false;
                    includeUsage = false;
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested || !_autoRetry || started ||
                        attempt >= MaxRetries || !IsTransient(ex))
                    {
                        ChatDiagnostics.Log("请求失败（不再重试）：" + ex.GetType().Name + ": " + ex.Message);
                        throw;
                    }

                    attempt++;
                    ChatDiagnostics.Log("请求失败，第 " + attempt + " 次重试（" + ex.GetType().Name + ": " + ex.Message + "）");
                    await Task.Delay(BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 指数退避（0.5s / 1s / 2s + 抖动），避免多路请求同时重试把端点再次打满
        /// </summary>
        private static TimeSpan BackoffDelay(int attempt)
        {
            var baseMs = 500 * Math.Pow(2, attempt - 1);
            var jitter = new Random().Next(0, 200);
            return TimeSpan.FromMilliseconds(baseMs + jitter);
        }

        /// <summary>
        /// 该异常是否属于「重试可能有用」的瞬时错误（限流 / 服务端错误 / 网络抖动）
        /// </summary>
        private static bool IsTransient(Exception ex)
        {
            if (ex is HttpRequestException || ex is System.IO.IOException)
            {
                return true;
            }

            var message = ex.Message ?? string.Empty;
            return message.IndexOf("HTTP 429", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("HTTP 500", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("HTTP 502", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("HTTP 503", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("HTTP 504", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 端点因不支持 <c>stream_options</c> 而拒绝（用于自动降级重试）
        /// </summary>
        private sealed class UnsupportedStreamOptionsException : Exception
        {
            public UnsupportedStreamOptionsException(string message) : base(message)
            {
            }
        }

        private async Task SendOnceAsync(
            ChatRequest request,
            Action<string> onDelta,
            CancellationToken cancellationToken,
            bool includeUsage)
        {
            var payload = BuildPayload(request, includeUsage);

            // 调试：请求摘要（排查「看不到推理」时先看这一行 —— 推理参数是否真的随请求带上了）
            ChatDiagnostics.Section("模型请求");
            ChatDiagnostics.Log("endpoint=" + _baseUrl + "/chat/completions"
                + " model=" + _model
                + " openrouter=" + _isOpenRouter
                + " anthropicCompat=" + _isAnthropic
                + " includeUsage=" + includeUsage
                + " reasoningEffort=" + (request.ReasoningEffort ?? string.Empty)
                + " verbosity=" + (request.Verbosity ?? string.Empty)
                + " thinkingBudget=" + request.ThinkingBudgetTokens
                + " tools=" + (string.IsNullOrWhiteSpace(request.ToolsJson) ? "no" : "yes")
                + " onReasoningCallback=" + (request.OnReasoning == null ? "null" : "set"));
            ChatDiagnostics.Log("payload=" + payload);

            using (var http = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions"))
            {
                http.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                if (_apiKey.Length > 0)
                {
                    http.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                }

                using (var response = await Client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        // 该端点不认识 stream_options：交给 SendAsync 关掉它重试一次
                        if (includeUsage && (int)response.StatusCode == 400 &&
                            (body.IndexOf("stream_options", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             body.IndexOf("include_usage", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            throw new UnsupportedStreamOptionsException("HTTP 400 —— " + Trim(body, 400));
                        }

                        throw new InvalidOperationException("HTTP " + (int)response.StatusCode + " —— " + Trim(body, 400));
                    }

                    ChatDiagnostics.Log("HTTP " + (int)response.StatusCode + " " + response.StatusCode);

                    // 原生 function calling：把流式到达的 tool_calls 分片按 index 归并起来，
                    // 等整条流结束后再翻译成动作块（分片可能把 JSON 转义序列一刀两断，不能逐片解析）
                    var toolCalls = new Dictionary<int, ToolCallTranslator.CallAccumulator>();

                    // 调试：原始流最多记这么多行（推理一般出现在正文之前，前若干行就足以看清字段名）
                    var diagLines = 0;
                    const int DiagMaxLines = 400;

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (!line.StartsWith("data:", StringComparison.Ordinal))
                            {
                                continue;
                            }

                            var data = line.Substring(5).Trim();
                            if (data.Length == 0 || data == "[DONE]")
                            {
                                continue;
                            }

                            // 推理过程单独回调，避免和正文混在一起（字段名各家不同，由提取器统一兼容）
                            var reasoning = ExtractDeltaReasoning(data);
                            var delta = ExtractDeltaContent(data);

                            // 调试：逐行记录「提取到了什么 + 原始行」，用于确认推理字段名是否被识别
                            if (diagLines < DiagMaxLines)
                            {
                                diagLines++;
                                ChatDiagnostics.Log("#" + diagLines
                                    + " reasoning=" + (string.IsNullOrEmpty(reasoning) ? "-" : reasoning.Length + "ch")
                                    + " content=" + (string.IsNullOrEmpty(delta) ? "-" : delta.Length + "ch")
                                    + " raw=" + data);
                            }

                            if (!string.IsNullOrEmpty(reasoning))
                            {
                                request.OnReasoning?.Invoke(reasoning);
                            }

                            if (!string.IsNullOrEmpty(delta))
                            {
                                onDelta(delta);
                            }

                            ToolCallTranslator.Accumulate(data, toolCalls);

                            // 用量回报（含缓存命中）：OpenAI/DeepSeek 会在末片带 usage
                            ReportUsage(data, request);
                        }
                    }

                    if (diagLines >= DiagMaxLines)
                    {
                        ChatDiagnostics.Log("（原始流行数超过 " + DiagMaxLines + "，后续行已省略）");
                    }

                    ChatDiagnostics.Log("流结束：记录原始行 " + diagLines + " 条，工具调用块 " + toolCalls.Count + " 个");

                    var actionBlocks = ToolCallTranslator.ToActionBlocks(toolCalls);
                    if (actionBlocks.Length > 0)
                    {
                        // 交给 AgentRunner 的文本协议去解析 —— 原生与文本两条路径共用同一套执行逻辑
                        onDelta(actionBlocks);
                    }
                }
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/event-stream");
            return client;
        }

        private string BuildPayload(ChatRequest request, bool includeUsage)
        {
            var builder = new StringBuilder();
            builder.Append("{\"model\":").Append(JsonString(_model));
            builder.Append(",\"stream\":true");
            builder.Append(",\"temperature\":").Append(request.Temperature.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(",\"top_p\":").Append(request.TopP.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(",\"max_tokens\":").Append(request.MaxTokens);

            // ---- 模型高级选项（均为「用户显式设置才带上」，默认完全不影响兼容性）----
            if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
            {
                builder.Append(",\"reasoning_effort\":").Append(JsonString(request.ReasoningEffort.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(request.Verbosity))
            {
                builder.Append(",\"verbosity\":").Append(JsonString(request.Verbosity.Trim()));
            }

            if (request.ThinkingBudgetTokens > 0)
            {
                builder.Append(",\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":").Append(request.ThinkingBudgetTokens).Append('}');
            }

            // 请求服务端在流末回报 usage（提示缓存的命中量就藏在这里）不支持该字段的端点会被自动降级
            if (includeUsage)
            {
                builder.Append(",\"stream_options\":{\"include_usage\":true}");
            }

            // 仅在用户显式开启时附带 tools：部分兼容网关对它的支持不完整
            if (!string.IsNullOrWhiteSpace(request.ToolsJson))
            {
                builder.Append(",\"tools\":").Append(request.ToolsJson);
                builder.Append(",\"tool_choice\":\"auto\"");
            }

            // OpenRouter 需要显式开启推理，才会在流里回传推理增量（不同模型默认值不一）
            // 该参数是 OpenRouter 私有的，所以只在它这里加；其它端点加了会因未知字段报错
            if (_isOpenRouter)
            {
                builder.Append(",\"reasoning\":{\"enabled\":true}");
            }

            // 用户附加的私有参数（OpenAI 协议之外）：把用户 JSON 的「内层」原样拼进根对象。
            // 例如 vLLM 上跑 Qwen3 要开思考，就在设置里填 {"chat_template_kwargs":{"enable_thinking":true}}
            if (!string.IsNullOrWhiteSpace(request.ExtraBodyJson))
            {
                var extra = request.ExtraBodyJson.Trim();
                if (extra.Length > 2 && extra.StartsWith("{", StringComparison.Ordinal) && extra.EndsWith("}", StringComparison.Ordinal))
                {
                    builder.Append(',').Append(extra.Substring(1, extra.Length - 2).Trim());
                    ChatDiagnostics.Log("已附加自定义请求参数：" + extra);
                }
                else
                {
                    ChatDiagnostics.Log("自定义请求参数不是合法的 JSON 对象，已忽略：" + extra);
                }
            }

            builder.Append(",\"messages\":[");

            var first = true;
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                // Anthropic 系：把系统提示词写成带 cache_control 的内容数组，显式打一个缓存断点
                // （系统提示词 / 工具清单在一轮任务里每步都重复发送，是最值得缓存的前缀）
                if (request.PromptCaching && _isAnthropic)
                {
                    AppendSystemWithCache(builder, ref first, request.SystemPrompt);
                }
                else
                {
                    AppendMessage(builder, ref first, new ChatTurn("system", request.SystemPrompt));
                }
            }

            foreach (var turn in request.Turns ?? [])
            {
                AppendMessage(builder, ref first, turn);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        /// <summary>
        /// 写一条消息带图且是 user 角色时输出多模态 <c>content</c> 数组：
        /// <c>[{"type":"text","text":...},{"type":"image_url","image_url":{"url":"data:..."}}]</c>；
        /// 其余情况仍是纯字符串 content（兼容不支持多模态的端点）
        /// </summary>
        private static void AppendMessage(StringBuilder builder, ref bool first, ChatTurn turn)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;

            var images = turn.Images;
            var multimodal = images != null && images.Count > 0 && string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase);

            builder.Append("{\"role\":").Append(JsonString(turn.Role)).Append(",\"content\":");

            if (!multimodal)
            {
                builder.Append(JsonString(turn.Content ?? string.Empty)).Append('}');
                return;
            }

            builder.Append("[{\"type\":\"text\",\"text\":").Append(JsonString(turn.Content ?? string.Empty)).Append('}');
            foreach (var image in images)
            {
                builder.Append(",{\"type\":\"image_url\",\"image_url\":{\"url\":").Append(JsonString(image.DataUrl)).Append("}}");
            }

            builder.Append("]}");
        }

        /// <summary>
        /// 把系统消息写成带 <c>cache_control</c> 的内容数组（Anthropic 兼容端点用）
        /// </summary>
        private static void AppendSystemWithCache(StringBuilder builder, ref bool first, string systemPrompt)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;

            builder.Append("{\"role\":\"system\",\"content\":[{\"type\":\"text\",\"text\":").Append(JsonString(systemPrompt)).Append(",\"cache_control\":{\"type\":\"ephemeral\"}}]}");
        }

        /// <summary>
        /// 从流片里解析 <c>usage</c>（若存在）并回调；取不到数字就不回调
        /// </summary>
        private static void ReportUsage(string data, ChatRequest request)
        {
            if (request?.OnUsage == null || string.IsNullOrEmpty(data) || data.IndexOf("\"usage\"", StringComparison.Ordinal) < 0)
            {
                return;
            }

            var input = FirstNumber(data, "\"prompt_tokens\"", "\"input_tokens\"");
            var output = FirstNumber(data, "\"completion_tokens\"", "\"output_tokens\"");
            if (input <= 0 && output <= 0)
            {
                return;
            }

            // 缓存命中量各家字段名不同：OpenAI 用 prompt_tokens_details.cached_tokens，
            // DeepSeek 用 prompt_cache_hit_tokens，Anthropic 系用 cache_read_input_tokens
            var cached = FirstNumber(data, "\"cached_tokens\"", "\"prompt_cache_hit_tokens\"", "\"cache_read_input_tokens\"");

            // 调试：把服务端自报的 reasoning_tokens 也记下来 —— 它是「模型到底有没有思考」的铁证
            ChatDiagnostics.Log("usage: prompt=" + input + " completion=" + output
                + " cached=" + cached
                + " reasoning=" + FirstNumber(data, "\"reasoning_tokens\""));

            request.OnUsage(new ChatUsage
            {
                InputTokens = input,
                OutputTokens = output,
                CachedInputTokens = cached,
            });
        }

        /// <summary>
        /// 在一段 JSON 文本里按字段名取第一个数字（取不到返回 0）
        /// </summary>
        private static double FirstNumber(string json, params string[] fields)
        {
            foreach (var field in fields)
            {
                var index = json.IndexOf(field, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                var colon = json.IndexOf(':', index + field.Length);
                if (colon < 0)
                {
                    continue;
                }

                var cursor = colon + 1;
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor]))
                {
                    cursor++;
                }

                var start = cursor;
                while (cursor < json.Length && (char.IsDigit(json[cursor]) || json[cursor] == '.' || json[cursor] == '-'))
                {
                    cursor++;
                }

                if (cursor > start && double.TryParse(json.Substring(start, cursor - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            return 0;
        }

        /// <summary>
        /// 从 <c>{"choices":[{"delta":{"content":"…"}}]}</c> 里取出增量文本
        /// </summary>
        internal static string ExtractDeltaContent(string json)
        {
            return ExtractDeltaField(json, "\"content\"");
        }

        /// <summary>
        /// 从流式 <c>delta</c> 里取出推理增量 
        /// </summary>
        internal static string ExtractDeltaReasoning(string json)
        {
            // 1) DeepSeek 系（也是目前最普遍的一种）
            var value = ExtractDeltaField(json, "\"reasoning_content\"");
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            // 2) OpenRouter / 部分 vLLM字段名 "reasoning" 后必须是引号，故不会命中 "reasoning_content"
            value = ExtractDeltaField(json, "\"reasoning\"");
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            // 3) 少数 Anthropic 兼容网关
            value = ExtractDeltaField(json, "\"thinking\"");
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            // 3.5) 部分网关用 reasoning_text
            value = ExtractDeltaField(json, "\"reasoning_text\"");
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            // 4) OpenRouter 对 Claude 等扩展思考模型返回结构化数组 "reasoning_details"，
            //    可读文本在数组元素的 "text" 字段先确认数组存在，再逐段取 text，避免误抓同名键
            if (json != null && json.IndexOf("\"reasoning_details\"", StringComparison.Ordinal) >= 0)
            {
                return ExtractReasoningDetailsText(json);
            }

            return null;
        }

        /// <summary>
        /// 把 <c>reasoning_details</c> 数组里所有 <c>"text"</c> 字段按顺序拼起来 
        /// </summary>
        private static string ExtractReasoningDetailsText(string json)
        {
            var builder = new StringBuilder();
            var searchFrom = 0;

            while (searchFrom < json.Length)
            {
                var fieldIndex = json.IndexOf("\"text\"", searchFrom, StringComparison.Ordinal);
                if (fieldIndex < 0)
                {
                    break;
                }

                var colon = json.IndexOf(':', fieldIndex + "\"text\"".Length);
                if (colon < 0)
                {
                    break;
                }

                var cursor = colon + 1;
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor]))
                {
                    cursor++;
                }

                // 值必须是字符串：null / 数字 / 对象一律跳过，绝不顺势读到下一个字段
                if (cursor < json.Length && json[cursor] == '"')
                {
                    builder.Append(ReadJsonString(json, cursor));
                }

                searchFrom = colon + 1;
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        /// <summary>
        /// 从 <c>delta</c> 对象里取某个字符串字段 
        /// </summary>
        private static string ExtractDeltaField(string json, string field)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var deltaIndex = json.IndexOf("\"delta\"", StringComparison.Ordinal);
            var from = deltaIndex >= 0 ? deltaIndex : 0;

            var fieldIndex = json.IndexOf(field, from, StringComparison.Ordinal);
            if (fieldIndex < 0)
            {
                return null;
            }

            var colon = json.IndexOf(':', fieldIndex + field.Length);
            if (colon < 0)
            {
                return null;
            }

            var cursor = colon + 1;
            while (cursor < json.Length && char.IsWhiteSpace(json[cursor]))
            {
                cursor++;
            }

            // 值不是字符串（null / 数字 / 对象）→ 本次没有增量，绝不能顺势读到下一个字段
            return cursor < json.Length && json[cursor] == '"' ? ReadJsonString(json, cursor) : null;
        }

        private static string ReadJsonString(string text, int startQuote)
        {
            var builder = new StringBuilder();
            for (var i = startQuote + 1; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"')
                {
                    break;
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                i++;
                if (i >= text.Length)
                {
                    break;
                }

                switch (text[i])
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'u':
                        if (i + 4 < text.Length &&
                            int.TryParse(text.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            builder.Append((char)code);
                            i += 4;
                        }

                        break;
                    default:
                        builder.Append(text[i]);
                        break;
                }
            }

            return builder.ToString();
        }

        private static string JsonString(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var flat = text.Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }
    }

    /// <summary>
    /// 按当前设置挑选后端
    /// </summary>
    public static class ChatBackendFactory
    {
        public static IChatBackend Create(AppSettings settings)
        {
            if (settings == null)
            {
                return new EchoChatBackend();
            }

            // 旧设置里可能存着历史别名（如「Ollama (本地)」）：先归一化到当前规范名
            var provider = ProviderCatalog.Canonicalize(settings.Provider);

            // 本地推理（Ollama / LM Studio）/ 自建网关 / 订阅通道不需要 API Key，开箱即可用
            // 每家厂商是否需要 Key 由 ProviderCatalog 声明，避免这里散落字符串判断
            if (ProviderCatalog.NeedsApiKey(provider) && string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return new EchoChatBackend();
            }

            // 没有内置地址、又必须手填的厂商（OpenAI Compatible / Anthropic / Vertex / OAuth 订阅等）：
            // 没填 Base URL 就无从发起请求，给出厂商自己的说明
            if (ProviderCatalog.NeedsManualBaseUrl(provider) && string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                var note = ProviderCatalog.NoteOf(provider);
                if (string.IsNullOrWhiteSpace(note))
                {
                    note = "该厂商需要手动指定一个 OpenAI 兼容端点地址";
                }

                return new NoticeChatBackend(
                    "「" + provider + "」需要先填写端点地址\n\n" + note +
                    "\n\n请到「设置 → API → Base URL」填写网关地址（通常以 /v1 结尾），" +
                    "再把「模型」填成该端点支持的 id，然后重试");
            }

            var baseUrl = ResolveBaseUrl(provider, settings.BaseUrl);

            // Anthropic：默认（或指向官方端点）走原生 Messages API；
            // 若用户显式填了第三方兼容网关，则仍走 OpenAI 兼容（既有配置继续可用）
            if (string.Equals(provider, ProviderCatalog.Anthropic, StringComparison.OrdinalIgnoreCase))
            {
                var official = string.IsNullOrWhiteSpace(settings.BaseUrl) ||
                               baseUrl.IndexOf("anthropic.com", StringComparison.OrdinalIgnoreCase) >= 0;
                if (official)
                {
                    return new AnthropicChatBackend(baseUrl, settings.ApiKey, settings.Model);
                }
            }

            return new OpenAiCompatibleChatBackend(baseUrl, settings.ApiKey, settings.Model, provider, settings.AutoRetry);
        }

        /// <summary>
        /// 把「提供商」映射到默认 Base URL（显式配置优先）
        /// </summary>
        public static string ResolveBaseUrl(string provider, string configured)
        {
            return ProviderCatalog.ResolveBaseUrl(provider, configured);
        }
    }
}

