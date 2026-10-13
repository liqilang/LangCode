using LangCode.Agent;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Workspace
{
    /// <summary>
    /// 一次 <c>Runtime.evaluate</c> 的结果
    /// </summary>
    internal struct EvalResult
    {
        public bool Ok;
        public string Text;
    }

    /// <summary>
    /// 极简 Chrome DevTools Protocol 客户端 
    /// </summary>
    internal sealed class CdpSession : IDisposable
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly object _gate = new object();
        private readonly Dictionary<int, TaskCompletionSource<string>> _pending = [];
        private readonly List<string> _console = [];
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private int _nextId;
        private Task _pump;

        private CdpSession()
        {
        }

        /// <summary>
        /// 连接到一个页面 target 的 WebSocket 调试地址
        /// </summary>
        public static async Task<CdpSession> ConnectAsync(string webSocketUrl, CancellationToken token)
        {
            var session = new CdpSession();
            session._socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            // 某些浏览器在握手时会校验 Origin：带一个无关紧要的值以避免被拒
            try
            {
                session._socket.Options.SetRequestHeader("Origin", "http://localhost");
            }
            catch (Exception)
            {
                // 不支持设置（例如被禁用）时忽略
            }

            await session._socket.ConnectAsync(new Uri(webSocketUrl), token).ConfigureAwait(false);
            session._pump = Task.Run(() => session.PumpAsync());

            return session;
        }

        /// <summary>
        /// 本次会话捕获到的 console 输出
        /// </summary>
        public IList<string> Console
        {
            get
            {
                lock (_gate)
                {
                    return [.. _console];
                }
            }
        }

        /// <summary>
        /// 发送一个 CDP 命令，等它对应的响应，返回响应的原始 JSON
        /// </summary> 
        public async Task<string> SendAsync(string method, string paramsJson, CancellationToken token)
        {
            var id = Interlocked.Increment(ref _nextId);
            var payload = new StringBuilder().Append("{\"id\":").Append(id).Append(",\"method\":").Append(LiteJson.Quote(method));

            if (!string.IsNullOrEmpty(paramsJson))
            {
                payload.Append(",\"params\":").Append(paramsJson);
            }

            payload.Append('}');

            var tcs = new TaskCompletionSource<string>();
            lock (_gate)
            {
                _pending[id] = tcs;
            }

            using (token.Register(() => tcs.TrySetCanceled()))
            {
                var bytes = Encoding.UTF8.GetBytes(payload.ToString());
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);

                return await tcs.Task.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 执行一段 JS 表达式并取回结果
        /// </summary> 
        public async Task<EvalResult> EvaluateAsync(string expression, CancellationToken token)
        {
            var paramsJson = "{\"expression\":" + LiteJson.Quote(expression ?? string.Empty) + ",\"returnByValue\":true,\"awaitPromise\":true,\"userGesture\":true}";

            var response = await SendAsync("Runtime.evaluate", paramsJson, token).ConfigureAwait(false);

            var error = ExtractError(response);
            if (error != null)
            {
                return new EvalResult { Ok = false, Text = "执行脚本失败：" + error };
            }

            var root = LiteJson.ParseObject(response);
            var result = LiteJson.GetObject(root, "result");

            var exception = LiteJson.GetObject(result, "exceptionDetails");
            if (exception != null)
            {
                var inner = LiteJson.GetObject(exception, "exception");
                var exceptionDescription = inner != null ? LiteJson.GetString(inner, "description") : null;
                var text = LiteJson.GetString(exception, "text");
                return new EvalResult { Ok = false, Text = "JS 运行时报错：" + (exceptionDescription ?? text ?? "未知错误") };
            }

            var remote = LiteJson.GetObject(result, "result");
            if (remote == null)
            {
                return new EvalResult { Ok = true, Text = string.Empty };
            }

            // 优先 value（returnByValue）；没有则退到 description（对象 / 函数的文本表示）
            if (remote.TryGetValue("value", out var value) && value != null)
            {
                switch (value)
                {
                    case string text:
                        return new EvalResult { Ok = true, Text = text };
                    case bool flag:
                        return new EvalResult { Ok = true, Text = flag ? "true" : "false" };
                    case double number:
                        return new EvalResult { Ok = true, Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    default:
                        return new EvalResult { Ok = true, Text = LiteJson.Stringify(value) };
                }
            }

            var description = LiteJson.GetString(remote, "description");
            return new EvalResult { Ok = true, Text = description ?? string.Empty };
        }

        /// <summary>
        /// 从响应里取 <c>error.message</c>；没有错误返回 null
        /// </summary> 
        public static string ExtractError(string response)
        {
            var root = LiteJson.ParseObject(response);
            var error = LiteJson.GetObject(root, "error");
            return error == null ? null : (LiteJson.GetString(error, "message") ?? "未知错误");
        }

        // ==================== 接收循环 ====================

        private async Task PumpAsync()
        {
            var buffer = new byte[64 * 1024];
            var builder = new StringBuilder();

            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    builder.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    Dispatch(builder.ToString());
                }
            }
            catch (Exception)
            {
                // 连接关闭 / 取消：结束循环，下面把未决请求全部释放
            }
            finally
            {
                FailPending(new InvalidOperationException("浏览器连接已关闭"));
            }
        }

        private void Dispatch(string json)
        {
            var obj = LiteJson.ParseObject(json);
            if (obj == null)
            {
                return;
            }

            // 有 id = 命令响应
            if (obj.TryGetValue("id", out var idValue) && idValue is double number)
            {
                var id = (int)number;
                TaskCompletionSource<string> tcs;
                lock (_gate)
                {
                    if (!_pending.TryGetValue(id, out tcs))
                    {
                        return;
                    }

                    _pending.Remove(id);
                }

                tcs.TrySetResult(json);
                return;
            }

            // 无 id = 事件
            var method = LiteJson.GetString(obj, "method");
            if (string.Equals(method, "Runtime.consoleAPICalled", StringComparison.Ordinal))
            {
                AppendConsole(obj);
            }
        }

        private void AppendConsole(Dictionary<string, object> eventObj)
        {
            var root = LiteJson.GetObject(eventObj, "params");
            var type = LiteJson.GetString(root, "type") ?? "log";
            var args = LiteJson.GetArray(root, "args");
            if (args == null)
            {
                return;
            }

            var builder = new StringBuilder("[").Append(type).Append("] ");
            var first = true;
            foreach (var arg in args)
            {
                if (!first)
                {
                    builder.Append(' ');
                }

                first = false;
                var remote = arg as Dictionary<string, object>;
                if (remote == null)
                {
                    builder.Append(LiteJson.Stringify(arg));
                    continue;
                }

                var value = LiteJson.GetString(remote, "value");
                builder.Append(value ?? LiteJson.GetString(remote, "description") ?? LiteJson.Stringify(remote));
            }

            lock (_gate)
            {
                // 只留最近 200 条，避免长会话里无限增长
                _console.Add(builder.ToString());
                if (_console.Count > 200)
                {
                    _console.RemoveRange(0, _console.Count - 200);
                }
            }
        }

        private void FailPending(Exception error)
        {
            List<TaskCompletionSource<string>> pending;
            lock (_gate)
            {
                pending = [.. _pending.Values];
                _pending.Clear();
            }

            foreach (var tcs in pending)
            {
                tcs.TrySetException(error);
            }
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
            }
            catch (Exception)
            {
                // 忽略
            }

            FailPending(new ObjectDisposedException(nameof(CdpSession)));

            try
            {
                _socket.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }

            try
            {
                _cts.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }
}

