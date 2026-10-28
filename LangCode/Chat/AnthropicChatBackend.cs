using LangCode.Agent;
using LangCode.Domain.Chat;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Chat
{
    /// <summary>
    /// Anthropic 原生 Messages API 后端（<c>POST {base}/v1/messages</c>，SSE 流式）。
    ///
    /// 与「OpenAI 兼容」的区别（这也是之前只能用兼容网关绕行的原因）：
    /// <list type="bullet">
    /// <item>鉴权走 <c>x-api-key</c> + <c>anthropic-version</c>，不是 <c>Authorization: Bearer</c>；</item>
    /// <item>系统提示词是顶层 <c>system</c> 字段，而不是 <c>messages</c> 里的一条 system 消息；</item>
    /// <item>工具是 <c>input_schema</c> 结构，工具调用走 <c>tool_use</c> / <c>tool_result</c> 内容块；</item>
    /// <item>提示缓存用内容块上的 <c>cache_control</c> 断点，用量字段是
    ///       <c>input_tokens</c> / <c>cache_read_input_tokens</c> / <c>output_tokens</c>。</item>
    /// </list>
    ///
    /// 出站仍统一成「```action 文本块」交给 <see cref="Agent.AgentRunner"/>，
    /// 因此上层工具循环无需为它单独分支。
    /// </summary>
    public sealed class AnthropicChatBackend : IChatBackend
    {
        private static readonly HttpClient Client = CreateClient();

        private const string AnthropicVersion = "2023-06-01";

        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;

        public AnthropicChatBackend(string baseUrl, string apiKey, string model)
        {
            _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            _apiKey = apiKey ?? string.Empty;
            _model = model ?? string.Empty;
        }

        public string DisplayName => "Anthropic 原生（" + _baseUrl + "）";

        public async Task SendAsync(ChatRequest request, Action<string> onDelta, CancellationToken cancellationToken)
        {
            var payload = BuildPayload(request);

            // 调试：与 OpenAI 兼容后端保持一致的请求摘要
            ChatDiagnostics.Section("模型请求（Anthropic 原生）");
            ChatDiagnostics.Log("endpoint=" + MessagesUrl()
                + " model=" + _model
                + " thinkingBudget=" + request.ThinkingBudgetTokens
                + " tools=" + (string.IsNullOrWhiteSpace(request.AnthropicToolsJson) ? "no" : "yes")
                + " onReasoningCallback=" + (request.OnReasoning == null ? "null" : "set"));
            ChatDiagnostics.Log("payload=" + payload);

            using (var http = new HttpRequestMessage(HttpMethod.Post, MessagesUrl()))
            {
                http.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                if (_apiKey.Length > 0)
                {
                    http.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                }

                http.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

                using (var response = await Client
                    .SendAsync(http, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new InvalidOperationException("HTTP " + (int)response.StatusCode + " —— " + Trim(body, 500));
                    }

                    await ReadStreamAsync(response, request, onDelta, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 拼接 Messages 端点：用户填的 BaseURL 可能已经带 <c>/v1</c>，避免拼成 <c>/v1/v1/messages</c>
        /// </summary>
        private string MessagesUrl()
        {
            if (_baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                return _baseUrl + "/messages";
            }

            return _baseUrl + "/v1/messages";
        }

        // ==================== 请求体 ====================

        private string BuildPayload(ChatRequest request)
        {
            var builder = new StringBuilder();
            builder.Append("{\"model\":").Append(JsonString(_model));
            builder.Append(",\"max_tokens\":").Append(request.MaxTokens <= 0 ? 4096 : request.MaxTokens);
            builder.Append(",\"stream\":true");

            var thinking = request.ThinkingBudgetTokens > 0 && request.ThinkingBudgetTokens < (request.MaxTokens <= 0 ? 4096 : request.MaxTokens);

            // 系统提示词：开了提示缓存就写成带 cache_control 的内容数组（长前缀命中后按折扣价计费）
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                builder.Append(",\"system\":");
                if (request.PromptCaching)
                {
                    builder.Append("[{\"type\":\"text\",\"text\":").Append(JsonString(request.SystemPrompt)).Append(",\"cache_control\":{\"type\":\"ephemeral\"}}]");
                }
                else
                {
                    builder.Append(JsonString(request.SystemPrompt));
                }
            }

            builder.Append(",\"messages\":");
            AppendMessages(builder, request.Turns);

            // 扩展思考开启时，Anthropic 要求 temperature / top_p 保持默认值，不能同时传
            if (!thinking)
            {
                builder.Append(",\"temperature\":").Append(request.Temperature.ToString("0.##", CultureInfo.InvariantCulture));
                builder.Append(",\"top_p\":").Append(request.TopP.ToString("0.##", CultureInfo.InvariantCulture));
            }

            if (thinking)
            {
                builder.Append(",\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":").Append(request.ThinkingBudgetTokens).Append('}');
            }

            if (!string.IsNullOrWhiteSpace(request.AnthropicToolsJson))
            {
                builder.Append(",\"tools\":").Append(request.AnthropicToolsJson);
                builder.Append(",\"tool_choice\":{\"type\":\"auto\"}");
            }

            return builder.Append('}').ToString();
        }

        /// <summary>
        /// 一条待发送的消息（连续同角色的消息会被合并）。
        /// Anthropic 要求 user / assistant 严格交替，而本插件会追加「自动上下文 / 追加指令」
        /// 等额外 user 消息，必须在这里合并掉。
        /// </summary>
        private sealed class MessagePart
        {
            public MessagePart(string role)
            {
                Role = role;
            }

            public string Role { get; }
            public string Text { get; set; } = string.Empty;
            public List<ChatImage> Images { get; } = new List<ChatImage>();
        }

        private static void AppendMessages(StringBuilder builder, IList<ChatTurn> turns)
        {
            var parts = new List<MessagePart>();

            foreach (var turn in turns ?? [])
            {
                var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                if (string.Equals(turn.Role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    // 系统提示词已放到顶层 system 字段；历史里若混入 system 一律跳过
                    continue;
                }

                var text = turn.Content ?? string.Empty;
                if (parts.Count > 0 && string.Equals(parts[parts.Count - 1].Role, role, StringComparison.Ordinal))
                {
                    var last = parts[parts.Count - 1];
                    last.Text = last.Text.Length == 0 ? text : last.Text + "\n\n" + text;
                    if (role == "user" && turn.Images != null)
                    {
                        last.Images.AddRange(turn.Images);
                    }

                    continue;
                }

                var part = new MessagePart(role) { Text = text };
                if (role == "user" && turn.Images != null)
                {
                    part.Images.AddRange(turn.Images);
                }

                parts.Add(part);
            }

            builder.Append('[');
            var first = true;
            foreach (var part in parts)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;

                builder.Append("{\"role\":").Append(JsonString(part.Role)).Append(",\"content\":");

                if (part.Images.Count == 0)
                {
                    builder.Append(JsonString(part.Text)).Append('}');
                    continue;
                }

                builder.Append("[{\"type\":\"text\",\"text\":").Append(JsonString(part.Text)).Append('}');
                foreach (var image in part.Images)
                {
                    builder.Append(",{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":")
                           .Append(JsonString(image.MimeType))
                           .Append(",\"data\":").Append(JsonString(image.Base64)).Append("}}");
                }

                builder.Append("]}");
            }

            builder.Append(']');
        }

        // ==================== 流式解析 ====================

        private static async Task ReadStreamAsync(HttpResponseMessage response, ChatRequest request, Action<string> onDelta, CancellationToken cancellationToken)
        {
            // 工具调用：按内容块下标归并 input_json 分片
            var toolBlocks = new Dictionary<int, ToolAccumulator>();
            double inputTokens = 0;
            double outputTokens = 0;
            double cachedTokens = 0;

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
                    if (data.Length == 0)
                    {
                        continue;
                    }

                    Dictionary<string, object> evt;
                    try
                    {
                        evt = LiteJson.ParseObject(data);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    var type = LiteJson.GetString(evt, "type") ?? string.Empty;
                    switch (type)
                    {
                        case "message_start":
                            {
                                var message = LiteJson.GetObject(evt, "message");
                                var usage = LiteJson.GetObject(message, "usage");
                                inputTokens = GetNumber(usage, "input_tokens");
                                cachedTokens = GetNumber(usage, "cache_read_input_tokens") + GetNumber(usage, "cache_creation_input_tokens");
                                break;
                            }

                        case "content_block_start":
                            {
                                var block = LiteJson.GetObject(evt, "content_block");
                                if (string.Equals(LiteJson.GetString(block, "type"), "tool_use", StringComparison.Ordinal))
                                {
                                    toolBlocks[GetInt(evt, "index")] = new ToolAccumulator
                                    {
                                        Name = LiteJson.GetString(block, "name") ?? string.Empty,
                                    };
                                }

                                break;
                            }

                        case "content_block_delta":
                            {
                                var delta = LiteJson.GetObject(evt, "delta");
                                var deltaType = LiteJson.GetString(delta, "type") ?? string.Empty;

                                if (string.Equals(deltaType, "text_delta", StringComparison.Ordinal))
                                {
                                    var text = LiteJson.GetString(delta, "text");
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        onDelta(text);
                                    }
                                }
                                else if (string.Equals(deltaType, "thinking_delta", StringComparison.Ordinal))
                                {
                                    var thinking = LiteJson.GetString(delta, "thinking");
                                    if (!string.IsNullOrEmpty(thinking))
                                    {
                                        request.OnReasoning?.Invoke(thinking);
                                    }
                                }
                                else if (string.Equals(deltaType, "input_json_delta", StringComparison.Ordinal))
                                {
                                    var index = GetInt(evt, "index");
                                    if (toolBlocks.TryGetValue(index, out var accumulator))
                                    {
                                        accumulator.Json.Append(LiteJson.GetString(delta, "partial_json") ?? string.Empty);
                                    }
                                }

                                break;
                            }

                        case "message_delta":
                            {
                                var usage = LiteJson.GetObject(evt, "usage");
                                var output = GetNumber(usage, "output_tokens");
                                if (output > 0)
                                {
                                    outputTokens = output;
                                }

                                break;
                            }

                        case "error":
                            {
                                var error = LiteJson.GetObject(evt, "error");
                                throw new InvalidOperationException("Anthropic 返回错误：" + (LiteJson.GetString(error, "message") ?? "未知错误"));
                            }

                        case "message_stop":
                            // 只结束读取循环；工具块翻译与 usage 上报在循环之后，
                            // 这里不能 return，否则 Anthropic 原生工具永远不会执行。
                            goto endStream;
                    }
                }
            }

        endStream:
            // 流结束后：把 tool_use 内容块翻译成 ```action 文本块（复用文本动作协议）
            if (toolBlocks.Count > 0)
            {
                var indexes = new List<int>(toolBlocks.Keys);
                indexes.Sort();
                foreach (var index in indexes)
                {
                    onDelta(BuildActionBlock(toolBlocks[index]));
                }
            }

            if (request.OnUsage != null && (inputTokens > 0 || outputTokens > 0))
            {
                request.OnUsage(new ChatUsage
                {
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CachedInputTokens = cachedTokens,
                });
            }
        }

        private sealed class ToolAccumulator
        {
            public string Name = string.Empty;
            public StringBuilder Json = new StringBuilder();
        }

        /// <summary>
        /// 把一次 tool_use 还原成动作块：把 <c>"tool":"名字"</c> 注入到入参 JSON 的开头，
        /// 这样既保留了原始入参（不去解析、不会丢字段），又符合 ```action 协议
        /// </summary>
        private static string BuildActionBlock(ToolAccumulator accumulator)
        {
            var input = accumulator.Json.Length == 0 ? "{}" : accumulator.Json.ToString().Trim();
            string arguments;

            if (input.StartsWith("{", StringComparison.Ordinal) && input.Length > 1)
            {
                arguments = "{\"tool\":\"" + accumulator.Name + "\"," + input.Substring(1);
            }
            else
            {
                arguments = "{\"tool\":\"" + accumulator.Name + "\"}";
            }

            return "```action\n" + arguments + "\n```\n";
        }

        // ==================== 辅助 ====================

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/event-stream");
            return client;
        }

        private static double GetNumber(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, out var value) || value == null)
            {
                return 0;
            }

            return value is double d
                ? d
                : double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;
        }

        private static int GetInt(Dictionary<string, object> obj, string key)
        {
            return (int)GetNumber(obj, key);
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
}
