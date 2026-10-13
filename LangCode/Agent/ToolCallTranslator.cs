using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LangCode.Agent
{
    /// <summary>
    /// 原生 function calling 与文本动作协议之间的桥 
    /// </summary>
    public static class ToolCallTranslator
    {
        /// <summary>
        /// 一个正在累积的调用（流式分片按 index 归并）
        /// </summary>
        public sealed class CallAccumulator
        {
            public string Id = string.Empty;

            public string Name = string.Empty;

            /// <summary>
            /// arguments 的原始 JSON 片段（保留转义，最后一次性反转义）
            /// </summary>
            public readonly StringBuilder Arguments = new StringBuilder();
        }

        private static readonly Regex IndexRegex = new Regex("\"index\"\\s*:\\s*(?<v>\\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex IdRegex = new Regex("\"id\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        private static readonly Regex NameRegex = new Regex("\"name\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        private static readonly Regex ArgumentsRegex = new Regex("\"arguments\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);

        /// <summary>
        /// 从一段 SSE delta 里累积 tool_calls；返回是否读到了东西
        /// </summary>
        public static bool Accumulate(string deltaJson, IDictionary<int, CallAccumulator> calls)
        {
            if (string.IsNullOrEmpty(deltaJson) || calls == null ||
                deltaJson.IndexOf("\"tool_calls\"", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            // 一个 delta 里可能带多个调用（并行工具调用）：按 "index" 的位置切段
            var indices = IndexRegex.Matches(deltaJson);
            var found = false;

            for (var i = 0; i < indices.Count; i++)
            {
                var start = indices[i].Index;
                var end = i + 1 < indices.Count ? indices[i + 1].Index : deltaJson.Length;
                var segment = deltaJson.Substring(start, end - start);

                if (!int.TryParse(indices[i].Groups["v"].Value, out var index))
                {
                    continue;
                }

                if (!calls.TryGetValue(index, out var call))
                {
                    call = new CallAccumulator();
                    calls[index] = call;
                }

                var id = IdRegex.Match(segment);
                if (id.Success && call.Id.Length == 0)
                {
                    call.Id = id.Groups["v"].Value;
                }

                var name = NameRegex.Match(segment);
                if (name.Success)
                {
                    // name 一般不跨分片；真跨了直接追加也比重置好
                    call.Name += name.Groups["v"].Value;
                }

                var arguments = ArgumentsRegex.Match(segment);
                if (arguments.Success)
                {
                    // 分片原样拼接：逐片反转义会把跨片的转义序列拆坏
                    call.Arguments.Append(arguments.Groups["v"].Value);
                }

                found = true;
            }

            return found;
        }

        /// <summary>
        /// 把累积到的调用翻译成 ```action 代码块文本（与文本协议格式一致）
        /// </summary>
        public static string ToActionBlocks(IDictionary<int, CallAccumulator> calls)
        {
            if (calls == null || calls.Count == 0)
            {
                return string.Empty;
            }

            // 按 index 排序，保证多次调用的顺序稳定
            var keys = new List<int>(calls.Keys);
            keys.Sort();

            var builder = new StringBuilder();
            foreach (var key in keys)
            {
                var call = calls[key];
                var name = (call.Name ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                builder.Append("```action\n");
                builder.Append(BuildActionJson(name, call.Arguments.ToString()));
                builder.Append("\n```\n\n");
            }

            return builder.ToString().TrimEnd() + (builder.Length > 0 ? "\n" : string.Empty);
        }

        /// <summary>
        /// 把 <c>{"path":"a"}</c> 形态的参数拼进动作 JSON
        /// </summary>
        private static string BuildActionJson(string toolName, string argumentsJson)
        {
            var builder = new StringBuilder("{\"tool\":\"").Append(Escape(toolName)).Append('"');

            var parsed = MiniJson.ParseFlatObject(Unescape(argumentsJson));
            if (parsed != null)
            {
                foreach (var pair in parsed)
                {
                    if (string.Equals(pair.Key, "tool", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    builder.Append(',').Append('"').Append(Escape(pair.Key)).Append("\":\"").Append(Escape(pair.Value)).Append('"');
                }
            }
            else if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                // 解析不出就当整串参数交给工具（至少不丢信息）
                builder.Append(",\"arguments\":\"").Append(Escape(Unescape(argumentsJson))).Append('"');
            }

            return builder.Append('}').ToString();
        }

        /// <summary>
        /// 去掉 JSON 字符串里的转义（<c>\n</c> / <c>\"</c> / <c>\uXXXX</c> 等）
        /// </summary>
        public static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0)
            {
                return text ?? string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c != '\\' || i + 1 >= text.Length)
                {
                    builder.Append(c);
                    continue;
                }

                i++;
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
                        if (i + 4 < text.Length && int.TryParse(text.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code))
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

        /// <summary>
        /// 把一段文本转义成 JSON 字符串内容
        /// </summary>
        public static string Escape(string text)
        {
            var builder = new StringBuilder();
            foreach (var c in text ?? string.Empty)
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
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.ToString();
        }
    }
}

