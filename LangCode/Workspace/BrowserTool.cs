using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Workspace
{
    /// <summary>
    /// 浏览器类别的工具实现：把 URL 交给系统默认浏览器打开，并按需抓取页面正文回给模型
    /// </summary>
    public sealed class BrowserTool
    {
        private static readonly HttpClient Client = CreateClient();

        private static readonly Regex ScriptStyleRegex = new Regex("<(script|style|noscript|svg)[\\s\\S]*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 整块去掉 <c><head></c>：标题已经单独抽出来放在最前面了，
        /// 不整块去掉的话标题文字会在正文里**重复出现一次**
        /// </summary>
        private static readonly Regex HeadRegex = new Regex("<head[\\s\\S]*?</head>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TagRegex = new Regex("<[^>]+>", RegexOptions.CultureInvariant);
        private static readonly Regex WhitespaceRegex = new Regex("[ \\t\\u00A0]+", RegexOptions.CultureInvariant);
        private static readonly Regex BlankLinesRegex = new Regex("\\n{3,}", RegexOptions.CultureInvariant);
        private static readonly Regex TitleRegex = new Regex("<title[^>]*>(?<title>[\\s\\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public BrowserTool(string root)
        {
            Root = (root ?? string.Empty).TrimEnd('\\', '/');
        }

        /// <summary>
        /// 工作区根目录（用于把相对路径转成本地文件链接）
        /// </summary>
        public string Root { get; }

        /// <summary>
        /// 抓取正文的长度上限
        /// </summary>
        public int MaxChars { get; set; } = 12000;

        /// <summary>
        /// 抓取超时（秒）
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// 把目标解析成「要打开的地址」；无法识别返回 null
        /// </summary>
        public string ResolveTarget(string url)
        {
            var text = (url ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            if (Uri.TryCreate(text, UriKind.Absolute, out var absolute))
            {
                var scheme = absolute.Scheme;
                if (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return absolute.AbsoluteUri;
                }
            }

            // 相对路径 → 工作区内的文件（越界一律不认）
            if (string.IsNullOrWhiteSpace(Root) || !Directory.Exists(Root))
            {
                return null;
            }

            try
            {
                var full = Path.GetFullPath(Path.Combine(Root, text.Replace('/', '\\')));
                var inside = full.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
                return inside && File.Exists(full) ? new Uri(full).AbsoluteUri : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 打开（可选抓取）
        /// </summary>
        public async Task<ToolResult> OpenAsync(string url, bool fetch, CancellationToken cancellationToken)
        {
            var target = ResolveTarget(url);
            if (target == null)
            {
                return new ToolResult
                {
                    Ok = false,
                    Output = "无法识别的地址：" + url + "\n只支持 http / https，或工作区内的相对文件路径",
                };
            }

            var report = new StringBuilder();

            // ① 交给系统默认浏览器（打不开也只记一笔，不影响后面的抓取）
            var error = OpenExternal(target);
            report.Append(error == null ? "已在默认浏览器中打开：" + target : "打开浏览器失败（" + error + "）：" + target);

            if (!fetch)
            {
                return new ToolResult { Ok = true, Output = report.ToString() };
            }

            if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                report.Append("\n\n（本地文件不做抓取：模型无法通过 file:// 读取内容，需要内容请用 read_file）");
                return new ToolResult { Ok = true, Output = report.ToString() };
            }

            // ② 抓正文给模型
            try
            {
                using (var response = await Client.GetAsync(target, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        report.Append("\n\n抓取失败：HTTP ").Append((int)response.StatusCode);
                        return new ToolResult { Ok = false, Output = report.ToString() };
                    }

                    var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var text = ToPlainText(html);

                    report.Append("\n\n—— 页面正文（").Append(text.Length).Append(" 字符）——\n").Append(text);
                    return new ToolResult { Ok = true, Output = report.ToString() };
                }
            }
            catch (Exception ex)
            {
                report.Append("\n\n抓取失败：").Append(ex.Message);
                return new ToolResult { Ok = false, Output = report.ToString() };
            }
        }

        /// <summary>
        /// 只抓取网页正文、<b>不</b>打开系统浏览器（供 <c>@web</c> 芯片 / 内部调用）
        /// 非 http(s) 地址、请求失败或正文为空时返回 <c>null</c>
        /// </summary>
        public async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
        {
            var target = ResolveTarget(url);
            if (target == null || !target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                using (var response = await Client.GetAsync(target, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var text = ToPlainText(html);
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 把地址交给系统默认浏览器（ShellExecute）；成功返回 null，失败返回原因
        /// </summary>
        public static string OpenExternal(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "地址为空";
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// 把 HTML 压成纯文本：去脚本样式 → 去标签 → 解常见实体 → 压空白
        /// 不做完整 HTML 解析：这一版的目的是「让模型读到内容」，不是还原排版
        /// </summary>
        public string ToPlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var title = TitleRegex.Match(html).Groups["title"].Value;

            var text = HeadRegex.Replace(html, " ");
            text = ScriptStyleRegex.Replace(text, " ");
            text = TagRegex.Replace(text, "\n");
            text = DecodeEntities(text);

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                var trimmed = WhitespaceRegex.Replace(line, " ").Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                builder.Append(trimmed).Append('\n');
            }

            var body = BlankLinesRegex.Replace(builder.ToString(), "\n\n").Trim();

            if (!string.IsNullOrWhiteSpace(title))
            {
                body = "# " + DecodeEntities(title).Trim() + "\n\n" + body;
            }

            return body.Length <= MaxChars ? body : body.Substring(0, MaxChars) + "\n…（正文过长，已截断到 " + MaxChars + " 字符）";
        }

        private static string DecodeEntities(string text)
        {
            // HTML 实体统一用「amp + 名字」拼出来：直接写实体字面量会被上层工具当成 XML 实体解码掉，
            // 源码里就变成了一个孤零零的引号（编译不过）
            const string amp = "&";

            return text
                .Replace(amp + "nbsp;", " ")
                .Replace(amp + "lt;", "<")
                .Replace(amp + "gt;", ">")
                .Replace(amp + "quot;", "\"")
                .Replace(amp + "#39;", "'")
                .Replace(amp + "apos;", "'")
                .Replace(amp + "mdash;", "—")
                .Replace(amp + "ndash;", "–")
                .Replace(amp + "hellip;", "…")
                .Replace(amp + "amp;", amp);   // 放最后：避免二次解码
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; LangCode/1.0)");
            return client;
        }
    }
}

