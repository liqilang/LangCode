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
        /// 动作块的**开头** 
        /// </summary>
        private static readonly Regex ActionOpenRegex = new Regex("```[ \\t]*[Aa][Cc][Tt][Ii][Oo][Nn][ \\t]*\\r?\\n", RegexOptions.CultureInvariant);

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
        /// 流式显示用 
        /// </summary>
        public static string StripActionsForDisplay(string assistantText)
        {
            if (string.IsNullOrEmpty(assistantText))
            {
                return string.Empty;
            }

            // 先去掉成对的完整动作块
            var stripped = FenceRegex.Replace(assistantText, string.Empty);

            // 剩下的第一个 ```action 一定没有闭合（成对的已被上面替换掉）：
            // 从它开始整段切掉；连同紧邻的换行一起吞掉，避免正文底部留下一串空行
            var open = ActionOpenRegex.Match(stripped);
            if (open.Success)
            {
                var cut = open.Index;
                while (cut > 0 && (stripped[cut - 1] == '\n' || stripped[cut - 1] == '\r'))
                {
                    cut--;
                }

                stripped = stripped.Substring(0, cut);
            }

            return stripped.TrimEnd();
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
