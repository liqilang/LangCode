using LangCode.VsExt.Domain.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.VsExt.Mcp
{
    /// <summary>
    /// MCP 服务器管理：按设置里的服务器列表建立 / 复用 stdio 连接，并把调用结果交回 Agent
    /// </summary>
    public sealed class McpManager
    {
        private static readonly object Gate = new object();

        /// <summary>
        /// 服务器名 → 客户端（进程级共享）
        /// </summary>
        private static readonly Dictionary<string, McpClient> Clients = new Dictionary<string, McpClient>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 服务器工作目录（MCP 进程的工作目录）
        /// </summary>
        public string WorkingDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 把设置的服务器列表与已建连接对齐：停用 / 改配置的会被关掉
        /// </summary>
        public static void Sync(IEnumerable<McpServerSettings> servers)
        {
            var list = (servers ?? Enumerable.Empty<McpServerSettings>()).ToList();

            lock (Gate)
            {
                var stale = new List<string>();
                foreach (var pair in Clients)
                {
                    var config = list.FirstOrDefault(s => string.Equals(s.Name, pair.Key, StringComparison.OrdinalIgnoreCase));

                    // 服务器被删 / 停用：释放连接
                    if (config == null || !config.Enabled)
                    {
                        stale.Add(pair.Key);
                    }
                }

                foreach (var name in stale)
                {
                    Clients[name].Dispose();
                    Clients.Remove(name);
                }
            }
        }

        /// <summary>
        /// 所有已启用服务器的连接状态（设置页展示用）
        /// </summary>
        public static IList<(string Name, string Status, int ToolCount)> Snapshot(IEnumerable<McpServerSettings> servers)
        {
            var result = new List<(string, string, int)>();

            lock (Gate)
            {
                foreach (var server in servers ?? Enumerable.Empty<McpServerSettings>())
                {
                    Clients.TryGetValue(server.Name ?? string.Empty, out var client);
                    result.Add((server.Name ?? string.Empty, client?.Status ?? (server.Enabled ? "未连接" : "已停用"), client?.Tools.Count ?? 0));
                }
            }

            return result;
        }

        /// <summary>
        /// 调用某个服务器上的工具<paramref name="server"/> 为空时用第一个已启用的服务器
        /// 失败不抛，返回给模型看的说明文字
        /// </summary>
        public async Task<string> CallAsync(AppSettings settings, string server, string tool, string arguments, CancellationToken cancellationToken)
        {
            var servers = (settings?.McpServers ?? []).Where(s => s.Enabled).ToList();

            if (servers.Count == 0)
            {
                return "没有已启用的 MCP 服务器请在「设置 → MCP」里添加并启用";
            }

            var target = string.IsNullOrWhiteSpace(server)
                ? servers[0]
                : servers.FirstOrDefault(s => string.Equals(s.Name, server, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                return "找不到 MCP 服务器「" + server + "」已启用的有：" + string.Join(", ", servers.Select(s => s.Name));
            }

            if (string.IsNullOrWhiteSpace(tool))
            {
                var client = GetOrCreate(target);
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

                return client.IsConnected
                    ? "服务器「" + target.Name + "」可用工具：\n" +
                      string.Join("\n", client.Tools.Select(t => "- " + t.Name + (t.Description.Length == 0 ? string.Empty : "：" + t.Description)))
                    : "服务器「" + target.Name + "」不可用：" + client.Status;
            }

            var active = GetOrCreate(target);
            return await active.CallToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 给确认卡用的预览文字
        /// </summary>
        public static string Describe(McpServerSettings server, string tool, string arguments)
        {
            var builder = new StringBuilder();
            builder.Append("服务器：").Append(server?.Name ?? "(未指定)").Append('\n');
            builder.Append("工具：").Append(tool ?? "(列出全部工具)").Append('\n');
            builder.Append("参数：").Append(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            return builder.ToString();
        }

        private McpClient GetOrCreate(McpServerSettings server)
        {
            var name = server.Name ?? string.Empty;

            lock (Gate)
            {
                if (Clients.TryGetValue(name, out var existing))
                {
                    // 命令/参数被改过 → 重建连接，否则一直用的是旧配置
                    if (existing.Matches(server.Command, server.Arguments))
                    {
                        return existing;
                    }

                    existing.Dispose();
                    Clients.Remove(name);
                }

                var created = new McpClient(name, server.Command, server.Arguments, WorkingDirectory);
                Clients[name] = created;
                return created;
            }
        }

        /// <summary>
        /// 关闭全部连接（窗口关闭时调用）
        /// </summary>
        public static void DisposeAll()
        {
            lock (Gate)
            {
                foreach (var client in Clients.Values)
                {
                    client.Dispose();
                }

                Clients.Clear();
            }
        }
    }
}

