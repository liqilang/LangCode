using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LangCode.Persistence
{
    /// <summary>
    /// 审计日志里的一条记录（工具调用：名字 + 参数摘要 + 结果）
    /// </summary>
    public sealed class AuditEntry
    {
        public DateTime AtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 工具名（read_file / run_command …）
        /// </summary>
        public string Tool { get; set; } = string.Empty;

        /// <summary>
        /// 参数摘要（已截断，避免把整段文件内容写进日志）
        /// </summary>
        public string Arguments { get; set; } = string.Empty;

        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Ok { get; set; }

        /// <summary>
        /// 结果摘要（已截断）
        /// </summary>
        public string Result { get; set; } = string.Empty;

        /// <summary>
        /// 对话 id（可为空）
        /// </summary>
        public string ConversationId { get; set; } = string.Empty;
    }

    /// <summary>
    /// 调用审计日志（追加写入 <c>%APPDATA%\LangCode\vsext\audit.log</c>）
    /// </summary>
    public static class AuditLog
    {
        private static readonly object Gate = new object();

        /// <summary>
        /// 单条字段的字符上限（参数 / 结果各截断到这里）
        /// </summary>
        private const int MaxFieldChars = 800;

        /// <summary>
        /// 文件大小上限（字节）；超过即轮转一次
        /// </summary>
        private const long MaxBytes = 2 * 1024 * 1024;

        public static string FilePath => Path.Combine(SettingsStore.Directory, "audit.log");

        /// <summary>
        /// 写入一条审计记录（失败静默，不阻断 Agent）
        /// </summary>
        public static void Append(AuditEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(SettingsStore.Directory);
                    RotateIfNeeded();

                    File.AppendAllText(FilePath, ToLine(entry), new UTF8Encoding(false));
                }
                catch (Exception)
                {
                    // 审计失败不阻断对话
                }
            }
        }

        /// <summary>
        /// 便捷重载：直接给工具名 / 参数 / 结果
        /// </summary>
        public static void Append(string tool, string arguments, bool ok, string result, string conversationId = null)
        {
            Append(new AuditEntry
            {
                Tool = tool ?? string.Empty,
                Arguments = arguments ?? string.Empty,
                Ok = ok,
                Result = result ?? string.Empty,
                ConversationId = conversationId ?? string.Empty,
            });
        }

        /// <summary>
        /// 读取最近的若干条（倒序；文件不存在返回空表）
        /// </summary>
        public static IList<AuditEntry> ReadRecent(int count)
        {
            var result = new List<AuditEntry>();
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(FilePath))
                    {
                        return result;
                    }

                    var lines = File.ReadAllLines(FilePath);
                    for (var i = lines.Length - 1; i >= 0 && result.Count < count; i--)
                    {
                        var entry = FromLine(lines[i]);
                        if (entry != null)
                        {
                            result.Add(entry);
                        }
                    }
                }
                catch (Exception)
                {
                    // 读失败返回已读到的部分
                }
            }

            return result;
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists || info.Length < MaxBytes)
                {
                    return;
                }

                var backup = FilePath + ".1";
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }

                File.Move(FilePath, backup);
            }
            catch (Exception)
            {
                // 轮转失败就算了，最多文件继续变大
            }
        }

        private static string ToLine(AuditEntry entry)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            AppendField(builder, "atUtc", entry.AtUtc.ToString("o"));
            builder.Append(',');
            AppendField(builder, "tool", entry.Tool);
            builder.Append(',');
            AppendField(builder, "args", Truncate(entry.Arguments));
            builder.Append(",\"ok\":").Append(entry.Ok ? "true" : "false").Append(',');
            AppendField(builder, "result", Truncate(entry.Result));
            builder.Append(',');
            AppendField(builder, "conversation", entry.ConversationId);
            builder.Append('}');
            return builder.Append('\n').ToString();
        }

        private static void AppendField(StringBuilder builder, string name, string value)
        {
            builder.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= MaxFieldChars
                ? value
                : value.Substring(0, MaxFieldChars) + "…";
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// 极简解析（只认自己写出的转义；够用于「最近记录」展示）
        /// </summary>
        private static AuditEntry FromLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
            {
                return null;
            }

            return new AuditEntry
            {
                AtUtc = ParseDate(Find(line, "atUtc")),
                Tool = Find(line, "tool"),
                Arguments = Find(line, "args"),
                Ok = line.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0,
                Result = Find(line, "result"),
                ConversationId = Find(line, "conversation"),
            };
        }

        private static DateTime ParseDate(string value)
        {
            return DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : DateTime.UtcNow;
        }

        private static string Find(string line, string key)
        {
            var marker = "\"" + key + "\":\"";
            var start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }

            start += marker.Length;
            var builder = new StringBuilder();
            for (var i = start; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '\\' && i + 1 < line.Length)
                {
                    var next = line[++i];
                    builder.Append(next == 'n' ? '\n' : next == 'r' ? '\r' : next == 't' ? '\t' : next);
                    continue;
                }

                if (c == '"')
                {
                    break;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }
    }
}

