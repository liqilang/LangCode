using LangCode.Domain.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LangCode.Mcp
{
    /// <summary>
    /// 从其他工具的配置里导入 MCP 服务器
    /// </summary>
    public static class McpConfig
    {
        private static readonly Regex EntryRegex = new Regex(
            "\"(?<name>[^\"]+)\"\\s*:\\s*\\{\\s*\"command\"\\s*:\\s*\"(?<command>[^\"]*)\"" +
            "\\s*(?:,\\s*\"args\"\\s*:\\s*\\[(?<args>[^\\]]*)\\])?",
            RegexOptions.CultureInvariant);

        private static readonly Regex QuotedRegex = new Regex("\"([^\"]*)\"", RegexOptions.CultureInvariant);

        /// <summary>
        /// 解析出服务器列表；解析不出返回空列表（不抛）
        /// </summary>
        public static IList<McpServerSettings> Parse(string json)
        {
            var result = new List<McpServerSettings>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            foreach (Match match in EntryRegex.Matches(json))
            {
                var name = match.Groups["name"].Value;

                // 外层键不当作服务器
                if (string.Equals(name, "mcpServers", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "mcp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var arguments = string.Join(" ", QuotedRegex.Matches(match.Groups["args"].Value).Cast<Match>().Select(m => m.Groups[1].Value));

                result.Add(new McpServerSettings
                {
                    Name = name,
                    Command = match.Groups["command"].Value,
                    Arguments = arguments,
                    Enabled = true,
                    Status = "unknown",
                });
            }

            return result;
        }

        /// <summary>
        /// 合并导入的服务器：同名覆盖，避免导入多次后出现一堆重复项
        /// </summary>
        public static int Merge(IList<McpServerSettings> target, IEnumerable<McpServerSettings> imported)
        {
            if (target == null || imported == null)
            {
                return 0;
            }

            var count = 0;
            foreach (var server in imported)
            {
                // 倒着删：IList 没有 RemoveAll，正着删会漏项
                for (var i = target.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(target[i].Name, server.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        target.RemoveAt(i);
                    }
                }

                target.Add(server);
                count++;
            }

            return count;
        }
    }
}

