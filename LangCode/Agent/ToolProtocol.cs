using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LangCode.Agent
{
    /// <summary>
    /// 模型请求的一次工具调用
    /// </summary>
    public sealed class ToolAction
    {
        public ToolAction(string name, Dictionary<string, string> arguments)
        {
            Name = name;
            Arguments = arguments ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Name { get; }

        public Dictionary<string, string> Arguments { get; }

        /// <summary>
        /// 取参数
        /// </summary>
        public string Get(string key)
        {
            return Arguments.TryGetValue(key, out var value) ? value : string.Empty;
        }
    }

    /// <summary>
    /// 工具协议：模型用**围栏代码块 + 扁平 JSON** 表达动作
    /// </summary>
    public static class ToolProtocol
    {
        /// <summary>
        /// 只认**语言标注为 <c>action</c>** 的围栏
        /// </summary>
        private static readonly Regex FenceRegex = new Regex("```[ \\t]*[Aa][Cc][Tt][Ii][Oo][Nn][ \\t]*\\r?\\n(?<body>[\\s\\S]*?)```", RegexOptions.CultureInvariant);

        /// <summary>
        /// 从模型输出里解析出全部动作
        /// </summary>
        public static IList<ToolAction> ExtractActions(string assistantText)
        {
            var actions = new List<ToolAction>();
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return actions;
            }

            foreach (Match match in FenceRegex.Matches(assistantText))
            {
                var parsed = LiteJson.ParseFlatObject(match.Groups["body"].Value);
                if (parsed == null)
                {
                    continue;
                }

                if (!parsed.TryGetValue("tool", out var name) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                actions.Add(new ToolAction(name.Trim(), parsed));
            }

            return actions;
        }

        /// <summary>
        /// 去掉动作块后的可见文本 
        /// </summary>
        public static string StripActions(string assistantText)
        {
            if (string.IsNullOrEmpty(assistantText))
            {
                return string.Empty;
            }

            var stripped = FenceRegex.Replace(assistantText, string.Empty);

            // 折叠掉因移除动作块而产生的多余空行 
            return Regex.Replace(stripped, "\\n{3,}", "\n\n").Trim();
        }

        /// <summary>
        /// 把一次工具结果格式化成回灌给模型的文本
        /// </summary>
        public static string FormatForModel(string toolName, bool ok, string output)
        {
            var builder = new StringBuilder();
            builder.Append("工具 ").Append(toolName).Append(ok ? " 执行成功" : " 执行失败").Append('\n');
            builder.Append(output ?? string.Empty);
            return builder.ToString();
        }
    }
}
