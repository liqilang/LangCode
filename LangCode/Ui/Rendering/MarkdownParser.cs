using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LangCode.Ui.Rendering
{
    /// <summary>
    /// 块级元素类型
    /// </summary>
    internal enum MdBlockKind
    {
        /// <summary>
        /// 普通段落
        /// </summary>
        Paragraph,

        /// <summary>
        /// 标题（<see cref="MdBlock.Level"/> = 1..6）
        /// </summary>
        Heading,

        /// <summary>
        /// 围栏代码块
        /// </summary>
        Code,

        /// <summary>
        /// 表格（管道语法）
        /// </summary>
        Table,

        /// <summary>
        /// 列表（有序 / 无序 / 任务清单）
        /// </summary>
        List,

        /// <summary>
        /// 引用块
        /// </summary>
        Quote,

        /// <summary>
        /// 分隔线
        /// </summary>
        Rule,
    }

    /// <summary>
    /// 行内片段：一段同样式文字
    /// </summary>
    internal sealed class MdRun
    {
        public MdRun(string text, bool bold = false, bool italic = false, bool code = false, string url = null)
        {
            Text = text ?? string.Empty;
            Bold = bold;
            Italic = italic;
            Code = code;
            Url = url;
        }

        public string Text { get; }

        public bool Bold { get; }

        public bool Italic { get; }

        /// <summary>
        /// 行内代码（<c>`x`</c>）
        /// </summary>
        public bool Code { get; }

        /// <summary>
        /// 链接地址（非空表示这是一个链接）
        /// </summary>
        public string Url { get; }

        public bool IsLink => !string.IsNullOrEmpty(Url);
    }

    /// <summary>
    /// 列表项
    /// </summary>
    internal sealed class MdItem
    {
        public MdItem(string marker, string text, int indent = 0)
        {
            Marker = marker ?? "•";
            Text = text ?? string.Empty;
            Indent = indent;
        }

        /// <summary>
        /// 行首符号（<c>•</c> / <c>1.</c> / <c>☐</c> …）
        /// </summary>
        public string Marker { get; }

        public string Text { get; }

        /// <summary>
        /// 嵌套层级（0 为顶层），用于缩进
        /// </summary>
        public int Indent { get; }
    }

    /// <summary>
    /// 块级元素
    /// </summary>
    internal sealed class MdBlock
    {
        public MdBlockKind Kind { get; set; } = MdBlockKind.Paragraph;

        /// <summary>
        /// Heading 层级（1..6）
        /// </summary>
        public int Level { get; set; }

        /// <summary>
        /// 段落 / 标题 / 引用的原文（含行内标记，渲染时再解析）
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// 代码块的语言标注（```csharp 里的 csharp）
        /// </summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>
        /// 代码块正文
        /// </summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// 流式输出时围栏还没闭合（据此在末尾提示「仍在输出」）
        /// </summary>
        public bool Unclosed { get; set; }

        /// <summary>
        /// 列表项
        /// </summary>
        public List<MdItem> Items { get; } = [];

        /// <summary>
        /// 表格表头
        /// </summary>
        public List<string> Headers { get; } = [];

        /// <summary>
        /// 表格数据行
        /// </summary>
        public List<List<string>> Rows { get; } = [];

        /// <summary>
        /// 表格列数（表头与所有数据行里最宽的一行）
        /// </summary>
        public int ColumnCount
        {
            get
            {
                var count = Headers.Count;
                foreach (var row in Rows)
                {
                    count = Math.Max(count, row.Count);
                }

                return count;
            }
        }
    }

    /// <summary>
    /// 对话正文用的精简 Markdown 解析器
    /// </summary>
    internal static class MarkdownParser
    {
        public static List<MdBlock> Parse(string markdown)
        {
            var blocks = new List<MdBlock>();
            if (string.IsNullOrEmpty(markdown))
            {
                return blocks;
            }

            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var index = 0;

            while (index < lines.Length)
            {
                var line = lines[index];

                if (string.IsNullOrWhiteSpace(line))
                {
                    index++;
                    continue;
                }

                // ① 围栏代码块（``` 或 ~~~）
                if (TryReadFence(lines, index, out var code, out var language, out var unclosed, out var consumed))
                {
                    blocks.Add(new MdBlock
                    {
                        Kind = MdBlockKind.Code,
                        Language = language,
                        Code = code,
                        Unclosed = unclosed,
                    });

                    index += consumed;
                    continue;
                }

                // ② 标题
                if (TryReadHeading(line, out var level, out var heading))
                {
                    blocks.Add(new MdBlock { Kind = MdBlockKind.Heading, Level = level, Text = heading });
                    index++;
                    continue;
                }

                // ③ 分隔线
                if (IsRule(line))
                {
                    blocks.Add(new MdBlock { Kind = MdBlockKind.Rule });
                    index++;
                    continue;
                }

                // ④ 表格（必须在段落之前判断：表格行也含普通文字）
                if (TryReadTable(lines, index, out var table, out var tableConsumed))
                {
                    blocks.Add(table);
                    index += tableConsumed;
                    continue;
                }

                // ⑤ 引用
                if (IsQuote(line))
                {
                    var quote = new StringBuilder();
                    while (index < lines.Length && IsQuote(lines[index]))
                    {
                        if (quote.Length > 0)
                        {
                            quote.Append('\n');
                        }

                        quote.Append(Unquote(lines[index]));
                        index++;
                    }

                    blocks.Add(new MdBlock { Kind = MdBlockKind.Quote, Text = quote.ToString() });
                    continue;
                }

                // ⑥ 列表
                if (TryReadListItem(line, out _, out _, out _))
                {
                    var list = new MdBlock { Kind = MdBlockKind.List };
                    while (index < lines.Length && TryReadListItem(lines[index], out var marker, out var itemText, out var indent))
                    {
                        list.Items.Add(new MdItem(marker, itemText, indent));
                        index++;
                    }

                    blocks.Add(list);
                    continue;
                }

                // ⑦ 段落：吃到空行为止（单个换行保留，聊天里多行内容更易读）
                var paragraph = new StringBuilder();
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
                {
                    if (paragraph.Length > 0)
                    {
                        if (IsRule(lines[index]) ||
                            TryReadHeading(lines[index], out _, out _) ||
                            IsQuote(lines[index]) ||
                            TryReadListItem(lines[index], out _, out _, out _) ||
                            StartsFence(lines[index]) ||
                            // 注意判断的是「这一行 + 下一行能不能组成表格」
                            // 只判当前行是不是分隔行是不够的：表格的**首行**是表头，
                            // 于是整张表会被当成普通文字吸进上一段段落里
                            StartsTable(lines, index))
                        {
                            break;
                        }

                        paragraph.Append('\n');
                    }

                    paragraph.Append(lines[index].Trim());
                    index++;
                }

                // 兜底：上面的 break 可能一行都没吃到（理论上不会），避免死循环
                if (paragraph.Length == 0)
                {
                    paragraph.Append(lines[index].Trim());
                    index++;
                }

                blocks.Add(new MdBlock { Kind = MdBlockKind.Paragraph, Text = paragraph.ToString() });
            }

            return blocks;
        }

        /// <summary>
        /// 把一段文字里的行内标记解析成同样式片段
        /// </summary>
        public static List<MdRun> ParseInline(string text)
        {
            var runs = new List<MdRun>();
            if (string.IsNullOrEmpty(text))
            {
                return runs;
            }

            var plain = new StringBuilder();
            var i = 0;

            while (i < text.Length)
            {
                var c = text[i];

                // 行内代码 `x`
                if (c == '`')
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end > i + 1)
                    {
                        Flush(runs, plain);
                        runs.Add(new MdRun(text.Substring(i + 1, end - i - 1), code: true));
                        i = end + 1;
                        continue;
                    }
                }

                // 粗体 ** / __
                if (i + 1 < text.Length &&
                    ((c == '*' && text[i + 1] == '*') || (c == '_' && text[i + 1] == '_')))
                {
                    var token = text.Substring(i, 2);
                    var end = text.IndexOf(token, i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        Flush(runs, plain);
                        runs.Add(new MdRun(text.Substring(i + 2, end - i - 2), bold: true));
                        i = end + 2;
                        continue;
                    }
                }

                // 删除线 ~~x~~：去掉标记，保留文字
                if (i + 1 < text.Length && c == '~' && text[i + 1] == '~')
                {
                    var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        Flush(runs, plain);
                        runs.Add(new MdRun(text.Substring(i + 2, end - i - 2)));
                        i = end + 2;
                        continue;
                    }
                }

                // 链接 [文字](地址)
                if (c == '[')
                {
                    var close = text.IndexOf(']', i + 1);
                    if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                    {
                        var paren = text.IndexOf(')', close + 2);
                        if (paren > close)
                        {
                            Flush(runs, plain);
                            runs.Add(new MdRun(text.Substring(i + 1, close - i - 1), url: text.Substring(close + 2, paren - close - 2)));
                            i = paren + 1;
                            continue;
                        }
                    }
                }

                // 斜体 * / _（下划线不能在单词中间，避免把 snake_case 误判成斜体）
                if (c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1]))))
                {
                    var end = text.IndexOf(c, i + 1);
                    if (end > i + 1)
                    {
                        Flush(runs, plain);
                        runs.Add(new MdRun(text.Substring(i + 1, end - i - 1), italic: true));
                        i = end + 1;
                        continue;
                    }
                }

                plain.Append(c);
                i++;
            }

            Flush(runs, plain);
            return runs;
        }

        /// <summary>
        /// 去掉行内标记，只留可读文本（标题、历史摘要等纯文本场景用）
        /// </summary>
        public static string CleanInline(string text)
        {
            return string.Concat(ParseInline(text).Select(run => run.Text));
        }

        // ==================== 块级识别 ====================

        private static void Flush(List<MdRun> runs, StringBuilder buffer)
        {
            if (buffer.Length == 0)
            {
                return;
            }

            runs.Add(new MdRun(buffer.ToString()));
            buffer.Clear();
        }

        private static bool StartsFence(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
        }

        private static bool TryReadFence(
            string[] lines,
            int start,
            out string code,
            out string language,
            out bool unclosed,
            out int consumed)
        {
            code = string.Empty;
            language = string.Empty;
            unclosed = false;
            consumed = 0;

            var first = lines[start].TrimStart();
            if (!StartsFence(first))
            {
                return false;
            }

            var marker = first.Substring(0, 3);
            language = first.Substring(3).Trim();

            var body = new List<string>();
            var index = start + 1;
            var closed = false;

            while (index < lines.Length)
            {
                if (lines[index].TrimStart().StartsWith(marker, StringComparison.Ordinal))
                {
                    closed = true;
                    index++;
                    break;
                }

                body.Add(lines[index]);
                index++;
            }

            code = string.Join("\n", body).TrimEnd('\n');

            // 没闭合 = 流式输出还没结束，先把已有内容当代码渲染
            unclosed = !closed;
            consumed = index - start;
            return true;
        }

        private static bool TryReadHeading(string line, out int level, out string text)
        {
            level = 0;
            text = string.Empty;

            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '#')
            {
                return false;
            }

            var hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#')
            {
                hashes++;
            }

            if (hashes > 6)
            {
                return false;
            }

            // " #标签" 不是标题
            if (hashes < trimmed.Length && trimmed[hashes] != ' ' && trimmed[hashes] != '\t')
            {
                return false;
            }

            var body = trimmed.Substring(hashes).Trim().TrimEnd('#').Trim();
            if (body.Length == 0)
            {
                return false;
            }

            text = body;
            level = hashes;
            return true;
        }

        private static bool IsRule(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 3)
            {
                return false;
            }

            var marker = trimmed[0];
            if (marker != '-' && marker != '*' && marker != '_')
            {
                return false;
            }

            foreach (var c in trimmed)
            {
                if (c != marker && c != ' ')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsQuote(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.Length > 0 && trimmed[0] == '>';
        }

        private static string Unquote(string line)
        {
            var trimmed = line.TrimStart();
            var rest = trimmed.Length > 1 ? trimmed.Substring(1) : string.Empty;
            return rest.StartsWith(" ", StringComparison.Ordinal) ? rest.Substring(1) : rest;
        }

        private static bool TryReadListItem(string line, out string marker, out string text, out int indent)
        {
            marker = string.Empty;
            text = string.Empty;
            indent = 0;

            var leading = line.Length - line.TrimStart().Length;
            indent = Math.Min(3, leading / 2);
            var trimmed = line.TrimStart();
            if (trimmed.Length < 2)
            {
                return false;
            }

            var first = trimmed[0];
            var bullet = first == '-' || first == '*' || first == '+';
            var ordered = false;
            var digits = 0;

            if (bullet)
            {
                if (trimmed[1] != ' ')
                {
                    return false;
                }
            }
            else if (char.IsDigit(first))
            {
                while (digits < trimmed.Length && char.IsDigit(trimmed[digits]))
                {
                    digits++;
                }

                if (digits + 1 >= trimmed.Length ||
                    (trimmed[digits] != '.' && trimmed[digits] != ')') ||
                    trimmed[digits + 1] != ' ')
                {
                    return false;
                }

                ordered = true;
            }
            else
            {
                return false;
            }

            var offset = ordered ? digits + 2 : 2;
            var body = trimmed.Substring(offset).Trim();

            // 任务清单：- [ ] / - [x] / - [-]
            if (body.Length >= 3 && body[0] == '[' && body[2] == ']' && (body.Length == 3 || body[3] == ' '))
            {
                var state = char.ToLowerInvariant(body[1]);
                marker = state == 'x' ? "☑" : state == '-' ? "◐" : "☐";
                text = body.Substring(3).Trim();
                return true;
            }

            marker = ordered ? trimmed.Substring(0, digits) + "." : (bullet ? "•" : string.Empty);

            text = body;
            return true;
        }

        /// <summary>
        /// 表格：<c>| a | b |</c> 紧跟一行分隔 <c>|---|---|</c>
        /// 分隔行是硬性条件 —— 少了它就只是「含竖线的普通文字」，不能当表格吃掉
        /// </summary>
        private static bool TryReadTable(string[] lines, int start, out MdBlock table, out int consumed)
        {
            table = null;
            consumed = 0;

            if (start + 1 >= lines.Length || !LooksLikeTableRow(lines[start]) || !IsTableSeparator(lines[start + 1]))
            {
                return false;
            }

            table = new MdBlock { Kind = MdBlockKind.Table };
            table.Headers.AddRange(SplitRow(lines[start]));

            var index = start + 2;
            while (index < lines.Length && LooksLikeTableRow(lines[index]))
            {
                table.Rows.Add(SplitRow(lines[index]));
                index++;
            }

            consumed = index - start;
            return true;
        }

        /// <summary>
        /// 从 <paramref name="index"/> 起是不是一张表（表头行 + 紧跟分隔行）
        /// </summary>
        private static bool StartsTable(string[] lines, int index)
        {
            return index + 1 < lines.Length && LooksLikeTableRow(lines[index]) && IsTableSeparator(lines[index + 1]);
        }

        private static bool LooksLikeTableRow(string line)
        {
            return !string.IsNullOrWhiteSpace(line) && line.IndexOf('|') >= 0;
        }

        /// <summary>
        /// | --- | :--: | 这类分隔行
        /// </summary>
        private static bool IsTableSeparator(string line)
        {
            if (!LooksLikeTableRow(line))
            {
                return false;
            }

            var cells = SplitRow(line);
            if (cells.Count == 0)
            {
                return false;
            }

            foreach (var cell in cells)
            {
                var hasDash = false;
                foreach (var c in cell)
                {
                    if (c == '-')
                    {
                        hasDash = true;
                    }
                    else if (c != ':' && c != ' ')
                    {
                        return false;
                    }
                }

                if (!hasDash)
                {
                    return false;
                }
            }

            return true;
        }

        private static List<string> SplitRow(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1);
            }

            if (trimmed.EndsWith("|", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
        }
    }
}

