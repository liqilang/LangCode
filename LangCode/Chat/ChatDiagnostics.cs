using System;
using System.Text;
using System.Text.RegularExpressions;

namespace LangCode.Chat
{
    /// <summary>
    /// 聊天链路调试日志：把「本轮关键上下文 + 模型请求 + 原始流式返回 + 推理/正文提取结果」
    /// 记在内存里，由设置页「数据」里的「复制调试信息」一键复制后发回，用于定位
    /// 「OpenAI Compatible 自定义模型看不到推理过程」这类问题。
    ///
    /// 设计取舍：
    /// <list type="bullet">
    /// <item><b>常开</b>：用户不必先去某个开关里打开；出了问题时日志已经在了。</item>
    /// <item><b>有上限</b>：缓冲最多 ~30 万字符，超出丢弃最旧的部分，长时间运行也不会把内存吃爆。</item>
    /// <item><b>单条截断</b>：一条日志最多 4000 字符，避免一次超长返回把整个缓冲冲掉。</item>
    /// </list>
    /// </summary>
    public static class ChatDiagnostics
    {
        private const int MaxChars = 300_000;

        private const int MaxLineChars = 4_000;

        private static readonly object Gate = new object();

        private static readonly StringBuilder Buffer = new StringBuilder();

        /// <summary>
        /// 是否记录（默认开启；关掉可做到完全零开销）
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// 开一段新小节（便于阅读：请求 / 流式 / UI 各成一段）
        /// </summary>
        public static void Section(string title)
        {
            if (!Enabled)
            {
                return;
            }

            Append("\n===== " + title + " ===== " + DateTime.Now.ToString("HH:mm:ss") + "\n");
        }

        public static void Log(string line)
        {
            if (!Enabled)
            {
                return;
            }

            Append("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + Clip(line) + "\n");
        }

        /// <summary>
        /// 把当前缓冲整体导出为可粘贴的文本
        /// </summary>
        public static string Dump()
        {
            lock (Gate)
            {
                var builder = new StringBuilder();
                builder.Append("LangCode 调试信息\n");
                builder.Append("生成时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
                builder.Append("缓冲字符数：").Append(Buffer.Length).Append('\n');
                builder.Append("----------------------------------------\n");
                builder.Append(Sanitize(Buffer.ToString()));
                return builder.ToString();
            }
        }

        /// <summary>
        /// 导出前对常见密钥格式做脱敏 
        /// </summary>
        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var value = Regex.Replace(
                text,
                @"(?i)(authorization[""'\s:=]+bearer\s+)[^\s""',}]+",
                "$1***");
            value = Regex.Replace(
                value,
                @"(?i)((?:x-api-key|api[_-]?key|access[_-]?token)[""'\s:=]+)[^\s""',}]{8,}",
                "$1***");
            value = Regex.Replace(
                value,
                @"(?i)\bsk-[A-Za-z0-9_\-]{8,}",
                "sk-***");
            return value;
        }

        public static void Clear()
        {
            lock (Gate)
            {
                Buffer.Clear();
            }
        }

        private static void Append(string text)
        {
            lock (Gate)
            {
                if (Buffer.Length + text.Length > MaxChars)
                {
                    // 丢掉最旧的一半，保留最近的信息（近因更相关）
                    var drop = Math.Min(Buffer.Length, Math.Max(MaxChars / 2, 1));
                    Buffer.Remove(0, drop);
                }

                Buffer.Append(text);
            }
        }

        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // 压成单行：SSE 原始行本来就没有换行，日志里再换行反而难读
            var flat = text.Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= MaxLineChars ? flat : flat.Substring(0, MaxLineChars) + "…(截断)";
        }
    }
}
