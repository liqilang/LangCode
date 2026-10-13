using LangCode.Agent;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Workspace
{
    /// <summary>
    /// 浏览器自动化 （通过 Chrome DevTools Protocol 远程调试接口控制 Edge / Chrome 浏览器）
    /// </summary>
    public sealed class BrowserAutomation
    {
        private static readonly HttpClient Http = CreateHttp();

        public BrowserAutomation(string root)
        {
            Root = (root ?? string.Empty).TrimEnd('\\', '/');
        }

        public string Root { get; }

        /// <summary>
        /// 远程调试端口
        /// </summary>
        public int Port { get; set; } = 9222;

        /// <summary>
        /// 无头模式
        /// </summary>
        public bool Headless { get; set; } = true;

        /// <summary>
        /// 浏览器可执行文件路径（留空则自动探测）
        /// </summary>
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>
        /// 单次动作超时（秒）
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// 助手自动探测到的本地开发地址（如 <c>http://localhost:5173</c>）
        /// </summary>
        public string DefaultUrl { get; set; } = string.Empty;

        /// <summary>
        /// 截图输出目录（工作区内）
        /// </summary>
        public string ScreenshotDirectory => Path.Combine(Root, ".langcode", "browser");

        /// <summary>
        /// 执行一次浏览器动作<paramref name="action"/> 取值：
        /// navigate / read / html / click / fill / evaluate / screenshot / console / wait
        /// </summary>
        public async Task<ToolResult> ExecuteAsync(
            string action,
            string url,
            string selector,
            string value,
            string script,
            CancellationToken cancellationToken)
        {
            var op = (action ?? string.Empty).Trim().ToLowerInvariant();
            if (op.Length == 0)
            {
                // 有地址（显式或自动探测到的）就默认导航，否则读当前页
                op = string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(DefaultUrl) ? "read" : "navigate";
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                linked.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, TimeoutSeconds)));
                var token = linked.Token;

                try
                {
                    if (op == "wait")
                    {
                        var ms = int.TryParse((value ?? "1000").Trim(), out var parsed) ? parsed : 1000;
                        await Task.Delay(Math.Max(0, Math.Min(60_000, ms)), token).ConfigureAwait(false);
                        return Ok("已等待 " + ms + " 毫秒");
                    }

                    var target = await EnsureTargetAsync(token).ConfigureAwait(false);
                    if (target == null)
                    {
                        return Fail("无法连接到浏览器调试端口 " + Port +
                                    "请确认已安装 Edge / Chrome，或在设置里指定浏览器路径");
                    }

                    using (var session = await CdpSession.ConnectAsync(target.WebSocketUrl, token).ConfigureAwait(false))
                    {
                        await session.SendAsync("Page.enable", null, token).ConfigureAwait(false);
                        await session.SendAsync("Runtime.enable", null, token).ConfigureAwait(false);

                        switch (op)
                        {
                            case "navigate":
                                return await NavigateAsync(session, url, token).ConfigureAwait(false);
                            case "read":
                            case "text":
                                return await ReadTextAsync(session, selector, token).ConfigureAwait(false);
                            case "html":
                                return await ReadHtmlAsync(session, selector, token).ConfigureAwait(false);
                            case "click":
                                return await ClickAsync(session, selector, token).ConfigureAwait(false);
                            case "fill":
                            case "type":
                                return await FillAsync(session, selector, value, token).ConfigureAwait(false);
                            case "evaluate":
                            case "eval":
                                return await EvaluateAsync(session, script, token).ConfigureAwait(false);
                            case "screenshot":
                                return await ScreenshotAsync(session, token).ConfigureAwait(false);
                            case "console":
                                return Ok(FormatConsole(session.Console));
                            default:
                                return Fail("未知的浏览器动作：" + action +
                                            "（支持 navigate / read / html / click / fill / evaluate / screenshot / console / wait）");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return Fail("浏览器动作超时（" + TimeoutSeconds + " 秒，动作 " + op + "）");
                }
                catch (Exception ex)
                {
                    return Fail("浏览器动作失败：" + ex.Message);
                }
            }
        }

        // ==================== 各动作实现 ====================

        private async Task<ToolResult> NavigateAsync(CdpSession session, string url, CancellationToken token)
        {
            var target = (url ?? string.Empty).Trim();
            if (target.Length == 0)
            {
                target = (DefaultUrl ?? string.Empty).Trim();
            }

            if (target.Length == 0)
            {
                return Fail("navigate 需要提供 url（助手也未探测到本地开发地址）");
            }

            var paramsJson = "{\"url\":" + LiteJson.Quote(target) + "}";
            var response = await session.SendAsync("Page.navigate", paramsJson, token).ConfigureAwait(false);
            var error = CdpSession.ExtractError(response);
            if (error != null)
            {
                return Fail("导航失败：" + error);
            }

            // 轮询 readyState，等页面真正可用（最多 ~10 秒），比固定 sleep 稳
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                var state = await session.EvaluateAsync("document.readyState", token).ConfigureAwait(false);
                if (string.Equals(state.Text, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                await Task.Delay(250, token).ConfigureAwait(false);
            }

            var title = await session.EvaluateAsync("document.title", token).ConfigureAwait(false);
            return Ok("已导航到：" + target + "\n标题：" + (title.Text ?? string.Empty) +
                      "\n如需正文请用 {\"action\":\"read\"}，如需 HTML 请用 {\"action\":\"html\"}");
        }

        private async Task<ToolResult> ReadTextAsync(CdpSession session, string selector, CancellationToken token)
        {
            var expression = string.IsNullOrWhiteSpace(selector)
                ? "document.documentElement.innerText"
                : "((document.querySelector(" + LiteJson.Quote(selector) + ")||{}).innerText||'__NOTFOUND__')";

            var result = await session.EvaluateAsync(expression, token).ConfigureAwait(false);
            if (!result.Ok)
            {
                return Fail(result.Text);
            }

            if (result.Text == "__NOTFOUND__")
            {
                return Fail("没有找到元素：" + selector);
            }

            return Ok("页面正文：\n" + Trim(result.Text, 12000));
        }

        private async Task<ToolResult> ReadHtmlAsync(CdpSession session, string selector, CancellationToken token)
        {
            var expression = string.IsNullOrWhiteSpace(selector)
                ? "document.documentElement.outerHTML"
                : "((document.querySelector(" + LiteJson.Quote(selector) + ")||{}).outerHTML||'__NOTFOUND__')";

            var result = await session.EvaluateAsync(expression, token).ConfigureAwait(false);
            if (!result.Ok)
            {
                return Fail(result.Text);
            }

            if (result.Text == "__NOTFOUND__")
            {
                return Fail("没有找到元素：" + selector);
            }

            return Ok("页面 HTML：\n" + Trim(result.Text, 12000));
        }

        private async Task<ToolResult> ClickAsync(CdpSession session, string selector, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return Fail("click 需要提供 selector（CSS 选择器）");
            }

            var expression =
                "(function(){var el=document.querySelector(" + LiteJson.Quote(selector) + ");" +
                "if(!el)return '__NOTFOUND__';el.scrollIntoView({block:'center'});el.click();return '__OK__';})()";

            var result = await session.EvaluateAsync(expression, token).ConfigureAwait(false);
            if (!result.Ok)
            {
                return Fail(result.Text);
            }

            if (result.Text == "__NOTFOUND__")
            {
                return Fail("没有找到要点击的元素：" + selector);
            }

            return Ok("已点击：" + selector);
        }

        private async Task<ToolResult> FillAsync(CdpSession session, string selector, string value, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return Fail("fill 需要提供 selector（CSS 选择器）");
            }

            var expression =
                "(function(){var el=document.querySelector(" + LiteJson.Quote(selector) + ");" +
                "if(!el)return '__NOTFOUND__';el.focus();el.value=" + LiteJson.Quote(value ?? string.Empty) + ";" +
                "el.dispatchEvent(new Event('input',{bubbles:true}));" +
                "el.dispatchEvent(new Event('change',{bubbles:true}));return '__OK__';})()";

            var result = await session.EvaluateAsync(expression, token).ConfigureAwait(false);
            if (!result.Ok)
            {
                return Fail(result.Text);
            }

            if (result.Text == "__NOTFOUND__")
            {
                return Fail("没有找到要填写的元素：" + selector);
            }

            return Ok("已填写 " + selector + " ← " + LiteJson.Quote(value ?? string.Empty));
        }

        private async Task<ToolResult> EvaluateAsync(CdpSession session, string script, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                return Fail("evaluate 需要提供 script（JS 表达式）");
            }

            var result = await session.EvaluateAsync(script, token).ConfigureAwait(false);
            return result.Ok
                ? Ok("脚本结果：\n" + Trim(result.Text, 8000))
                : Fail(result.Text);
        }

        private async Task<ToolResult> ScreenshotAsync(CdpSession session, CancellationToken token)
        {
            var response = await session
                .SendAsync("Page.captureScreenshot", "{\"format\":\"png\",\"captureBeyondViewport\":false}", token)
                .ConfigureAwait(false);

            var error = CdpSession.ExtractError(response);
            if (error != null)
            {
                return Fail("截图失败：" + error);
            }

            var root = LiteJson.ParseObject(response);
            var result = LiteJson.GetObject(root, "result");
            var base64 = LiteJson.GetString(result, "data");
            if (string.IsNullOrEmpty(base64))
            {
                return Fail("截图失败：没有拿到图片数据");
            }

            try
            {
                Directory.CreateDirectory(ScreenshotDirectory);
                var file = Path.Combine(ScreenshotDirectory, "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                File.WriteAllBytes(file, Convert.FromBase64String(base64));

                var relative = Root.Length > 0 && file.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(Root.Length).TrimStart('\\', '/').Replace('\\', '/')
                    : file;

                return Ok("已截图并保存到：" + relative + "\n（可用 read_file 之外的方式查看；模型可据此对照 UI 与设计稿）");
            }
            catch (Exception ex)
            {
                return Fail("截图保存失败：" + ex.Message);
            }
        }

        private static string FormatConsole(IList<string> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return "（本次会话没有捕获到 console 输出）";
            }

            var builder = new StringBuilder("console 输出（").Append(lines.Count).Append(" 条）：\n");
            foreach (var line in lines)
            {
                builder.Append("  ").Append(line).Append('\n');
            }

            return builder.ToString().TrimEnd();
        }

        // ==================== 连接 / 启动浏览器 ====================

        /// <summary>
        /// 确保有一个可用的页面 target；返回它的 WebSocket 调试地址
        /// </summary>
        private async Task<BrowserTarget> EnsureTargetAsync(CancellationToken token)
        {
            if (!await IsDebugPortAliveAsync(token).ConfigureAwait(false))
            {
                // 端口被别的程序占用（但不是调试端点）时自动换一个空闲端口，
                // 否则浏览器会因为端口冲突起不来 —— 用户什么都不用配
                if (IsPortInUse(Port))
                {
                    Port = FindFreePort();
                }

                if (!await TryLaunchBrowserAsync(token).ConfigureAwait(false))
                {
                    return null;
                }
            }

            var list = await GetJsonAsync("/json/list", token).ConfigureAwait(false);
            var targets = list as List<object>;
            BrowserTarget firstPage = null;
            if (targets != null)
            {
                foreach (var item in targets)
                {
                    if (!(item is Dictionary<string, object> target))
                    {
                        continue;
                    }

                    if (!string.Equals(LiteJson.GetString(target, "type"), "page", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var ws = LiteJson.GetString(target, "webSocketDebuggerUrl");
                    if (string.IsNullOrEmpty(ws))
                    {
                        continue;
                    }

                    var browserTarget = new BrowserTarget
                    {
                        WebSocketUrl = ws,
                        Url = LiteJson.GetString(target, "url") ?? string.Empty,
                    };

                    firstPage ??= browserTarget;

                    // 优先复用已经打开的 about:blank / 新标签，避免每次都新开一个
                    if (string.IsNullOrEmpty(browserTarget.Url) || browserTarget.Url == "about:blank")
                    {
                        return browserTarget;
                    }
                }
            }

            if (firstPage != null)
            {
                return firstPage;
            }

            // 没有任何页面：新建一个（新版 Chrome 要求 PUT）
            var created = await CreateNewTabAsync(token).ConfigureAwait(false);
            return created;
        }

        /// <summary>
        /// TCP 端口是否已被占用（占用 ≠ 是我们的调试端点）
        /// </summary>
        private static bool IsPortInUse(int port)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 让系统分配一个空闲端口（交给内核选，避免撞车）
        /// </summary>
        private static int FindFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private async Task<bool> IsDebugPortAliveAsync(CancellationToken token)
        {
            try
            {
                var version = await GetJsonAsync("/json/version", token).ConfigureAwait(false);
                return version is Dictionary<string, object>;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// GET 一个调试端点的 JSON 路径（/json/version、/json/list …）
        /// </summary>
        private async Task<object> GetJsonAsync(string path, CancellationToken token)
        {
            using (var response = await Http.GetAsync(Endpoint + path, token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return LiteJson.Parse(body);
            }
        }

        private async Task<BrowserTarget> CreateNewTabAsync(CancellationToken token)
        {
            foreach (var method in new[] { HttpMethod.Put, HttpMethod.Get })
            {
                try
                {
                    using (var request = new HttpRequestMessage(method, Endpoint + "/json/new?about:blank"))
                    using (var response = await Http.SendAsync(request, token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            continue;
                        }

                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var obj = LiteJson.ParseObject(body);
                        var ws = LiteJson.GetString(obj, "webSocketDebuggerUrl");
                        if (!string.IsNullOrEmpty(ws))
                        {
                            return new BrowserTarget { WebSocketUrl = ws, Url = "about:blank" };
                        }
                    }
                }
                catch (Exception)
                {
                    // 换下一种方法再试
                }
            }

            return null;
        }

        private async Task<bool> TryLaunchBrowserAsync(CancellationToken token)
        {
            var executable = ResolveExecutable();
            if (executable == null)
            {
                return false;
            }

            try
            {
                var userData = Path.Combine(Path.GetTempPath(), "LangCodeBrowser");
                Directory.CreateDirectory(userData);

                var args = new StringBuilder();
                args.Append("--remote-debugging-port=").Append(Port).Append(' ');
                args.Append("--user-data-dir=").Append(QuoteArg(userData)).Append(' ');
                args.Append("--no-first-run --no-default-browser-check ");
                if (Headless)
                {
                    args.Append("--headless=new --disable-gpu ");
                }

                args.Append("about:blank");

                var info = new ProcessStartInfo(executable, args.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                Process.Start(info);
            }
            catch (Exception)
            {
                return false;
            }

            // 轮询端口，等浏览器把调试服务起起来
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, TimeoutSeconds));
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (await IsDebugPortAliveAsync(token).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(300, token).ConfigureAwait(false);
            }

            return false;
        }

        private string ResolveExecutable()
        {
            if (!string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath))
            {
                return ExecutablePath;
            }

            var candidates = new[]
            {
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
            };

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string Combine(string root, string relative)
        {
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, relative);
        }

        private static string QuoteArg(string value)
        {
            return value.IndexOf(' ') >= 0 ? "\"" + value + "\"" : value;
        }

        private string Endpoint => "http://127.0.0.1:" + Port;

        private static HttpClient CreateHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LangCode/1.0");
            return client;
        }

        private static string Trim(string text, int max)
        {
            var value = text ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max) + "\n…（已截断）";
        }

        private static ToolResult Ok(string output)
        {
            return new ToolResult { Ok = true, Output = output ?? string.Empty };
        }

        private static ToolResult Fail(string message)
        {
            return new ToolResult { Ok = false, Output = "✗ " + message };
        }

        /// <summary>
        /// 一个可连接的页面目标
        /// </summary>
        private sealed class BrowserTarget
        {
            public string WebSocketUrl = string.Empty;
            public string Url = string.Empty;
        }
    }
}

