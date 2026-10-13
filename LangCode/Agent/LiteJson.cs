using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LangCode.Agent
{
    /// <summary>
    /// 轻量「嵌套」JSON 解析 / 序列化 
    /// </summary>
    public static class LiteJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var index = 0;
            var value = ParseValue(json, ref index);
            return value;
        }

        /// <summary>
        /// 解析并按 <c>Dictionary</c> 返回；不是对象则返回 null
        /// </summary>
        public static Dictionary<string, object> ParseObject(string json)
        {
            return Parse(json) as Dictionary<string, object>;
        }

        /// <summary>
        /// 解析「扁平」对象：所有值统一转成字符串（工具动作参数用）。
        /// 值本身是字符串时按其转义还原；否则保留原始文本（<c>null</c> 归一成空串）。
        /// 失败返回 <c>null</c>。
        /// </summary>
        /// <remarks>
        /// 与旧的 <c>MiniJson.ParseFlatObject</c> 语义一致；此处合并进 <see cref="LiteJson"/>，
        /// 让全项目只保留一套 JSON 解析器（解析逻辑与 <see cref="Parse"/> 共用读取原语）。
        /// </remarks>
        public static Dictionary<string, string> ParseFlatObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            SkipWhitespace(json, ref index);

            if (index >= json.Length || json[index] != '{')
            {
                return null;
            }

            index++;
            while (true)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                {
                    return null;
                }

                if (json[index] == '}')
                {
                    return result;
                }

                if (json[index] != '"')
                {
                    return null;
                }

                var key = ReadString(json, ref index);
                if (key == null)
                {
                    return null;
                }

                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':')
                {
                    return null;
                }

                index++;
                SkipWhitespace(json, ref index);

                string value;
                if (index < json.Length && json[index] == '"')
                {
                    value = ReadString(json, ref index);
                    if (value == null)
                    {
                        return null;
                    }
                }
                else
                {
                    var start = index;
                    while (index < json.Length && json[index] != ',' && json[index] != '}')
                    {
                        index++;
                    }

                    value = json.Substring(start, index - start).Trim();
                    if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                    {
                        value = string.Empty;
                    }
                }

                result[key] = value;

                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    continue;
                }

                if (index < json.Length && json[index] == '}')
                {
                    return result;
                }

                return null;
            }
        }

        /// <summary>
        /// 取对象里的字符串字段（不存在 / 非字符串返回 null）
        /// </summary>
        public static string GetString(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 取对象里的对象字段
        /// </summary>
        public static Dictionary<string, object> GetObject(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, out var value))
            {
                return null;
            }

            return value as Dictionary<string, object>;
        }

        /// <summary>
        /// 取对象里的数组字段
        /// </summary>
        public static List<object> GetArray(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, out var value))
            {
                return null;
            }

            return value as List<object>;
        }

        public static string Stringify(object value)
        {
            var builder = new StringBuilder();
            Write(builder, value);
            return builder.ToString();
        }

        /// <summary>
        /// 把一个字符串安全地写成 JSON 字符串字面量（含引号）
        /// </summary>
        public static string Quote(string text)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in text ?? string.Empty)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
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

        private static void Write(StringBuilder builder, object value)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    return;

                case string text:
                    builder.Append(Quote(text));
                    return;

                case bool flag:
                    builder.Append(flag ? "true" : "false");
                    return;

                case double number:
                    builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
                    return;

                case Dictionary<string, object> map:
                    builder.Append('{');
                    var first = true;
                    foreach (var pair in map)
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        first = false;
                        builder.Append(Quote(pair.Key)).Append(':');
                        Write(builder, pair.Value);
                    }

                    builder.Append('}');
                    return;

                case List<object> list:
                    builder.Append('[');
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }

                        Write(builder, list[i]);
                    }

                    builder.Append(']');
                    return;

                default:
                    builder.Append(Quote(Convert.ToString(value, CultureInfo.InvariantCulture)));
                    return;
            }
        }

        // ==================== 解析 ====================

        private static object ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length)
            {
                return null;
            }

            var c = text[index];
            switch (c)
            {
                case '{':
                    return ParseObjectBody(text, ref index);
                case '[':
                    return ParseArray(text, ref index);
                case '"':
                    return ReadString(text, ref index);
                case 't':
                    index += 4;
                    return true;
                case 'f':
                    index += 5;
                    return false;
                case 'n':
                    index += 4;
                    return null;
                default:
                    return ReadNumber(text, ref index);
            }
        }

        private static Dictionary<string, object> ParseObjectBody(string text, ref int index)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            index++; // '{'
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == '}')
            {
                index++;
                return result;
            }

            while (index < text.Length)
            {
                SkipWhitespace(text, ref index);
                var key = ReadString(text, ref index);
                SkipWhitespace(text, ref index);

                if (index < text.Length && text[index] == ':')
                {
                    index++;
                }

                result[key ?? string.Empty] = ParseValue(text, ref index);

                SkipWhitespace(text, ref index);
                if (index < text.Length && text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (index < text.Length && text[index] == '}')
                {
                    index++;
                }

                break;
            }

            return result;
        }

        private static List<object> ParseArray(string text, ref int index)
        {
            var result = new List<object>();
            index++; // '['
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == ']')
            {
                index++;
                return result;
            }

            while (index < text.Length)
            {
                result.Add(ParseValue(text, ref index));
                SkipWhitespace(text, ref index);

                if (index < text.Length && text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (index < text.Length && text[index] == ']')
                {
                    index++;
                }

                break;
            }

            return result;
        }

        private static string ReadString(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length || text[index] != '"')
            {
                return null;
            }

            index++;
            var builder = new StringBuilder();

            while (index < text.Length)
            {
                var c = text[index];
                if (c == '"')
                {
                    index++;
                    return builder.ToString();
                }

                if (c == '\\' && index + 1 < text.Length)
                {
                    index++;
                    switch (text[index])
                    {
                        case 'n': builder.Append('\n'); break;
                        case 't': builder.Append('\t'); break;
                        case 'r': builder.Append('\r'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case '/': builder.Append('/'); break;
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case 'u':
                            if (index + 4 < text.Length && int.TryParse(text.Substring(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            {
                                builder.Append((char)code);
                                index += 4;
                            }

                            break;
                        default:
                            builder.Append(text[index]);
                            break;
                    }

                    index++;
                    continue;
                }

                builder.Append(c);
                index++;
            }

            return builder.ToString();
        }

        private static object ReadNumber(string text, ref int index)
        {
            var start = index;
            while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '-' || text[index] == '+' || text[index] == '.' || text[index] == 'e' || text[index] == 'E'))
            {
                index++;
            }

            var slice = text.Substring(start, index - start);
            return double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : slice;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }
    }
}

