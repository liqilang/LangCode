using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.VsExt.Mcp
{
    /// <summary>
    /// MCP 服务器上的一个工具
    /// </summary>
    public sealed class McpToolInfo
    {
        public McpToolInfo(string name, string description)
        {
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
        }

        public string Name { get; }

        public string Description { get; }
    }

    /// <summary>
    /// 一个 MCP 服务器的 stdio 客户端（JSON-RPC 2.0，一行一个 JSON 对象）
    /// </summary>
    public sealed class McpClient : IDisposable
    {
        private static readonly Regex NameRegex = new Regex("\"name\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        private static readonly Regex TextRegex = new Regex("\"text\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        private static readonly Regex MessageRegex = new Regex("\"message\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        private static readonly Regex IdRegex = new Regex("\"id\"\\s*:\\s*(?<v>\\d+)", RegexOptions.CultureInvariant);

        private readonly string _name;
        private readonly string _command;
        private readonly string _arguments;
        private readonly string _workingDirectory;
        private readonly object _gate = new object();
        private readonly Dictionary<int, TaskCompletionSource<string>> _pending =
            [];

        private Process _process;
        private StreamWriter _stdin;
        private int _nextId;

        public McpClient(string name, string command, string arguments, string workingDirectory)
        {
            _name = name ?? string.Empty;
            _command = command ?? string.Empty;
            _arguments = arguments ?? string.Empty;
            _workingDirectory = workingDirectory ?? string.Empty;
        }

        public string Name => _name;

        /// <summary>
        /// 已建连接的配置是否与给定命令 / 参数一致（不一致就要重建进程）
        /// </summary>
        public bool Matches(string command, string arguments)
        {
            return string.Equals(_command, command ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(_arguments, arguments ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>
        /// 连接状态说明（未连接 / 已连接 N 个工具 / 失败原因）
        /// </summary>
        public string Status { get; private set; } = "未连接";

        public bool IsConnected { get; private set; }

        /// <summary>
        /// 服务器提供的工具（连接成功后填充）
        /// </summary>
        public IList<McpToolInfo> Tools { get; } = [];

        /// <summary>
        /// 启动进程并完成握手（initialize + tools/list）失败不抛，状态写进 <see cref="Status"/>
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
        {
            if (IsConnected)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(_command))
            {
                Status = "未配置启动命令";
                return false;
            }

            try
            {
                Start();

                var parameters = "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{}," +
                                 "\"clientInfo\":{\"name\":\"LangCode\",\"version\":\"1.0\"}}";

                var init = await SendAsync("initialize", parameters, cancellationToken).ConfigureAwait(false);
                if (init == null)
                {
                    Status = "initialize 无响应（超时）";
                    return false;
                }

                // 握手完成的通知（不需要等回复）
                Notify("notifications/initialized", "{}");

                var list = await SendAsync("tools/list", "{}", cancellationToken).ConfigureAwait(false);
                Tools.Clear();

                if (!string.IsNullOrEmpty(list))
                {
                    // tools/list 的结果里每个工具都有 name/description；顺序扫描即可
                    // 用 name/description 的成对出现来切分
                    var names = NameRegex.Matches(list).Cast<Match>().Select(m => Unescape(m.Groups["v"].Value)).ToList();
                    var descriptions = DescriptionRegex.Matches(list).Cast<Match>().Select(m => Unescape(m.Groups["v"].Value)).ToList();

                    for (var i = 0; i < names.Count; i++)
                    {
                        Tools.Add(new McpToolInfo(names[i], i < descriptions.Count ? descriptions[i] : string.Empty));
                    }
                }

                IsConnected = true;
                Status = "已连接 · " + Tools.Count + " 个工具";
                return true;
            }
            catch (Exception ex)
            {
                Status = "连接失败：" + ex.Message;
                Dispose();
                return false;
            }
        }

        /// <summary>
        /// 调用服务器上的工具；<paramref name="argumentsJson"/> 必须是 JSON 对象文本
        /// </summary>
        public async Task<string> CallToolAsync(string tool, string argumentsJson, CancellationToken cancellationToken)
        {
            if (!IsConnected)
            {
                var ok = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    return "MCP 服务器「" + _name + "」不可用：" + Status;
                }
            }

            var arguments = string.IsNullOrWhiteSpace(argumentsJson) || !argumentsJson.TrimStart().StartsWith("{", StringComparison.Ordinal)
                ? "{}"
                : argumentsJson.Trim();

            var parameters = "{\"name\":\"" + Escape(tool ?? string.Empty) + "\",\"arguments\":" + arguments + "}";

            var response = await SendAsync("tools/call", parameters, cancellationToken).ConfigureAwait(false);
            if (response == null)
            {
                return "调用超时（" + _name + "." + tool + "）";
            }

            var error = MessageRegex.Match(response);
            if (response.IndexOf("\"error\"", StringComparison.Ordinal) >= 0 && error.Success)
            {
                return "MCP 返回错误：" + Unescape(error.Groups["v"].Value);
            }

            var texts = TextRegex.Matches(response).Cast<Match>()
                .Select(m => Unescape(m.Groups["v"].Value))
                .Where(t => t.Length > 0)
                .ToList();

            return texts.Count > 0 ? string.Join("\n", texts) : "（MCP 工具无文本返回）";
        }

        /// <summary>
        /// 子进程退出：立刻把状态改成「已断开」并放行所有等待中的请求
        /// </summary>
        private void OnProcessExited(object sender, EventArgs e)
        {
            IsConnected = false;

            var code = -1;
            try
            {
                code = _process?.ExitCode ?? -1;
            }
            catch (Exception)
            {
                // 拿不到退出码不影响后续处理
            }

            Status = "进程已退出（code " + code + "）";

            lock (_gate)
            {
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetResult(null);
                }

                _pending.Clear();
            }
        }

        public void Dispose()
        {
            IsConnected = false;

            try
            {
                _stdin?.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch (Exception)
            {
                // 忽略
            }

            try
            {
                _process?.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }

            _process = null;
            _stdin = null;

            lock (_gate)
            {
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetResult(null);
                }

                _pending.Clear();
            }
        }

        private static readonly Regex DescriptionRegex = new Regex(
            "\"description\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"",
            RegexOptions.CultureInvariant);

        private void Start()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // 用一个 cmd 包一层：MCP 服务器常写成 `npx -y @xxx/server` 这种带参数的形式
                Arguments = "/d /s /c \"" + _command + (_arguments.Length == 0 ? string.Empty : " " + _arguments) + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!string.IsNullOrWhiteSpace(_workingDirectory) && Directory.Exists(_workingDirectory))
            {
                startInfo.WorkingDirectory = _workingDirectory;
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;
            _process.Start();

            // net472 的 ProcessStartInfo 没有 StandardInputEncoding，
            // 这里直接在底层流上套一个 UTF-8 写入器：否则参数里的中文会被按 ANSI 编码送出去
            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            // 后台读循环：按 id 把响应派发给等待中的请求
            Task.Run(() => ReadLoop(_process.StandardOutput));

            // stderr 也必须读掉，否则管道满了会卡住子进程
            Task.Run(() => Drain(_process.StandardError));
        }

        private void ReadLoop(StreamReader reader)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var match = IdRegex.Match(line);
                    if (!match.Success || !int.TryParse(match.Groups["v"].Value, out var id))
                    {
                        continue;   // 通知 / 日志：忽略
                    }

                    TaskCompletionSource<string> pending;
                    lock (_gate)
                    {
                        if (!_pending.TryGetValue(id, out pending))
                        {
                            continue;
                        }

                        _pending.Remove(id);
                    }

                    pending.TrySetResult(line);
                }
            }
            catch (Exception)
            {
                // 进程结束：让所有等待者超时返回
            }
        }

        private static void Drain(StreamReader reader)
        {
            try
            {
                while (reader.ReadLine() != null)
                {
                    // 丢弃 stderr（服务器日志），但必须读完
                }
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        private async Task<string> SendAsync(string method, string parameters, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            var payload = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + Escape(method) + "\",\"params\":" + parameters + "}";

            TaskCompletionSource<string> pending;
            lock (_gate)
            {
                pending = new TaskCompletionSource<string>();
                _pending[id] = pending;
            }

            try
            {
                await _stdin.WriteLineAsync(payload).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _pending.Remove(id);
                }

                throw new InvalidOperationException("写入 MCP 进程失败：" + ex.Message, ex);
            }

            var timeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var completed = await Task.WhenAny(pending.Task, timeout).ConfigureAwait(false);
            if (completed != pending.Task)
            {
                lock (_gate)
                {
                    _pending.Remove(id);
                }

                return null;
            }

            return await pending.Task.ConfigureAwait(false);
        }

        private void Notify(string method, string parameters)
        {
            try
            {
                _stdin?.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"" + Escape(method) + "\",\"params\":" + parameters + "}");
                _stdin?.Flush();
            }
            catch (Exception)
            {
                // 通知失败不影响主流程
            }
        }

        private static string Escape(string value)
        {
            var builder = new StringBuilder();
            foreach (var c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: builder.Append(c); break;
                }
            }

            return builder.ToString();
        }

        private static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(value[i]);
                    continue;
                }

                i++;
                switch (value[i])
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'u':
                        if (i + 4 < value.Length &&
                            int.TryParse(value.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var code))
                        {
                            builder.Append((char)code);
                            i += 4;
                        }

                        break;
                    default: builder.Append(value[i]); break;
                }
            }

            return builder.ToString();
        }
    }
}

