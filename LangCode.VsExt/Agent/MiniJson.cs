using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LangCode.VsExt.Agent
{
    /// <summary>
    /// 极简 JSON 解析 
    /// </summary>
    public static class MiniJson
    {
        /// <summary>
        /// 解析扁平对象；失败返回 null
        /// </summary>
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

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }

        /// <summary>
        /// 从 <paramref name="index"/> 处的引号开始读一个 JSON 字符串；失败返回 null
        /// </summary>
        private static string ReadString(string text, ref int index)
        {
            if (index >= text.Length || text[index] != '"')
            {
                return null;
            }

            var builder = new StringBuilder();
            index++;
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
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'u':
                            if (index + 4 < text.Length &&
                                int.TryParse(text.Substring(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
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

            return null;
        }
    }
}

