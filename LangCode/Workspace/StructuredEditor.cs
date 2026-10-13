using System;
using System.Collections.Generic;
using System.Linq;

namespace LangCode.Workspace
{
    /// <summary>
    /// 一个符号在源码里的字符区间（<c>[Start,End)</c>）
    /// </summary>
    public sealed class BlockLocation
    {
        /// <summary>
        /// 符号声明的起始偏移（含）
        /// </summary>
        public int Start;

        /// <summary>
        /// 符号块的结束偏移（不含）
        /// </summary>
        public int End;

        /// <summary>
        /// 起始行号（1 起）
        /// </summary>
        public int StartLine;

        /// <summary>
        /// 结束行号（1 起）
        /// </summary>
        public int EndLine;

        public string Symbol = string.Empty;
        public string Kind = string.Empty;

        /// <summary>
        /// 是否定位到了大括号块（否则是缩进块 / 分号结尾）
        /// </summary>
        public bool HasBraces;

        /// <summary>
        /// 大括号内部区间的字符索引（不含大括号本身）；无大括号时为 -1
        /// </summary>
        public int BodyStart = -1;
        public int BodyEnd = -1;
    }

    /// <summary>
    /// 语言感知的结构化编辑：按「符号名」定位代码块，再对整块 / 仅块内 / 块前后做增删改
    /// </summary>
    public static class StructuredEditor
    {
        public const string ModeReplace = "replace";        // 整块替换（含签名）
        public const string ModeReplaceBody = "replace_body"; // 仅替换块内内容
        public const string ModeInsertBefore = "insert_before";
        public const string ModeInsertAfter = "insert_after";
        public const string ModeDelete = "delete";

        /// <summary>
        /// 列出文件里匹配 / 包含 <paramref name="symbol"/> 的候选符号（用于提示歧义）
        /// </summary>
        public static IList<SymbolItem> Candidates(string text, string symbol)
        {
            var symbols = SymbolScanner.Scan(text, 1000);
            var needle = (symbol ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                return symbols;
            }

            return symbols
                .Where(s => s.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        /// <summary>
        /// 定位一个符号的代码块成功返回 true；失败给出原因（含候选清单）
        /// </summary>
        public static bool TryLocate(string text, string symbol, out BlockLocation location, out string error)
        {
            location = null;
            error = string.Empty;

            if (string.IsNullOrEmpty(text))
            {
                error = "文件内容为空";
                return false;
            }

            var needle = (symbol ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                error = "symbol 不能为空";
                return false;
            }

            var symbols = SymbolScanner.Scan(text, 1000);

            // 先精确匹配（区分大小写），再退化到忽略大小写
            var matches = symbols.Where(s => string.Equals(s.Name, needle, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0)
            {
                matches = symbols.Where(s => string.Equals(s.Name, needle, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (matches.Count == 0)
            {
                var fuzzy = Candidates(text, needle);
                error = fuzzy.Count == 0
                    ? "文件里没有找到符号：" + needle
                    : "文件里没有名为 " + needle + " 的符号相近的有：" +
                      string.Join("、", fuzzy.Take(10).Select(s => s.Kind + " " + s.Name));
                return false;
            }

            if (matches.Count > 1)
            {
                error = "符号 " + needle + " 在文件里出现了 " + matches.Count + " 次（行 " +
                        string.Join("、", matches.Select(s => s.Line)) + "）" +
                        "请用同名重载较少的那种写法，或改用 replace_in_file 指定上下文";
                return false;
            }

            var target = matches[0];
            return TryLocateFrom(text, target, out location, out error);
        }

        private static bool TryLocateFrom(string text, SymbolItem target, out BlockLocation location, out string error)
        {
            location = null;
            error = string.Empty;

            var lineStarts = LineStarts(text);
            if (target.Line < 1 || target.Line > lineStarts.Count)
            {
                error = "符号行号超出文件范围";
                return false;
            }

            var start = lineStarts[target.Line - 1];
            var result = new BlockLocation
            {
                Start = start,
                StartLine = target.Line,
                Symbol = target.Name,
                Kind = target.Kind,
            };

            // 从符号声明处向后扫描，跳过字符串 / 字符 / 注释，找出块的真身：
            // 大括号块、分号结尾，或缩进块
            var scan = ScanForBlock(text, start);
            if (scan.BraceOpen >= 0)
            {
                var close = MatchBrace(text, scan.BraceOpen);
                if (close < 0)
                {
                    error = "大括号没有配对（文件可能不完整）";
                    return false;
                }

                result.HasBraces = true;
                result.End = close + 1;
                result.BodyStart = scan.BraceOpen + 1;
                result.BodyEnd = close;
            }
            else if (scan.Semicolon >= 0)
            {
                result.End = scan.Semicolon + 1;
            }
            else
            {
                // 缩进块（Python 等）：以声明行的缩进为基准，吸收所有更深缩进 / 空行
                result.End = IndentBlockEnd(text, lineStarts, target.Line);
            }

            result.EndLine = LineOf(lineStarts, Math.Max(result.Start, result.End - 1));
            location = result;
            return true;
        }

        /// <summary>
        /// 对定位到的块应用一次编辑，返回新的完整文件文本
        /// </summary>
        public static bool TryApply(string text, string symbol, string mode, string newText, out string result, out string error)
        {
            result = null;

            if (!TryLocate(text, symbol, out var location, out error))
            {
                return false;
            }

            var body = NormalizeNewlines(newText ?? string.Empty, UsesCrLf(text));
            var action = (mode ?? string.Empty).Trim().ToLowerInvariant();
            if (action.Length == 0)
            {
                action = ModeReplace;
            }

            switch (action)
            {
                case ModeReplace:
                    result = text.Substring(0, location.Start) + body + text.Substring(location.End);
                    return true;

                case ModeReplaceBody:
                    if (!location.HasBraces || location.BodyStart < 0)
                    {
                        error = "该符号不是大括号块，无法只替换块内内容；请改用 replace 或 replace_in_file";
                        return false;
                    }

                    result = text.Substring(0, location.BodyStart) + body + text.Substring(location.BodyEnd);
                    return true;

                case ModeInsertBefore:
                    result = text.Substring(0, location.Start) + body + Newline(text) + text.Substring(location.Start);
                    return true;

                case ModeInsertAfter:
                    result = text.Substring(0, location.End) + Newline(text) + body + text.Substring(location.End);
                    return true;

                case ModeDelete:
                    {
                        var end = location.End;
                        // 连带吃掉紧随其后的一个换行，避免留下空行
                        if (end < text.Length && text[end] == '\r')
                        {
                            end++;
                        }

                        if (end < text.Length && text[end] == '\n')
                        {
                            end++;
                        }

                        result = text.Substring(0, location.Start) + text.Substring(end);
                        return true;
                    }

                default:
                    error = "未知的编辑模式：" + mode +
                            "（支持 replace / replace_body / insert_before / insert_after / delete）";
                    return false;
            }
        }

        // ==================== 扫描辅助 ====================

        private struct BlockScan
        {
            public int BraceOpen;
            public int Semicolon;
        }

        /// <summary>
        /// 从 <paramref name="start"/> 起扫描，找块的开始标志：
        /// 返回第一个大括号位置，或（没大括号时）第一个分号位置字符串 / 注释里的符号不算
        /// </summary>
        private static BlockScan ScanForBlock(string text, int start)
        {
            var scan = new BlockScan { BraceOpen = -1, Semicolon = -1 };
            var parenDepth = 0;
            var i = start;

            while (i < text.Length)
            {
                var c = text[i];

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    i = SkipLineComment(text, i);
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i = SkipBlockComment(text, i);
                    continue;
                }

                if (c == '"' || c == '\'' || c == '`')
                {
                    i = SkipQuoted(text, i, c);
                    continue;
                }

                if (c == '(' || c == '[')
                {
                    parenDepth++;
                    i++;
                    continue;
                }

                if (c == ')' || c == ']')
                {
                    parenDepth = Math.Max(0, parenDepth - 1);
                    i++;
                    continue;
                }

                if (parenDepth == 0)
                {
                    if (c == '{')
                    {
                        scan.BraceOpen = i;
                        return scan;
                    }

                    if (c == ';')
                    {
                        scan.Semicolon = i;
                        return scan;
                    }

                    // 缩进语言：声明行结束后还没遇到块标志，就交给缩进判定
                    if (c == '\n' && scan.BraceOpen < 0 && scan.Semicolon < 0)
                    {
                        var next = NextNonSpace(text, i + 1);
                        // 下一行若不再是签名续行（不是以 : . , ) { => 开头的公共续行风格），直接收尾
                        if (next >= 0 && text[next] != ':' && text[next] != '.' && text[next] != ',' &&
                            text[next] != ')' && text[next] != '{')
                        {
                            // 多行返回类型 / 泛型续行可能以其它字符开头；这里给两行容错
                            var secondNewline = text.IndexOf('\n', i + 1);
                            if (secondNewline >= 0)
                            {
                                return scan;
                            }
                        }
                    }
                }

                i++;
            }

            return scan;
        }

        private static int NextNonSpace(string text, int index)
        {
            var i = index;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n'))
            {
                i++;
            }

            return i < text.Length ? i : -1;
        }

        /// <summary>
        /// 从开括号起做配对，返回对应的闭括号位置；不配对返回 -1
        /// </summary>
        private static int MatchBrace(string text, int openIndex)
        {
            var depth = 0;
            var i = openIndex;

            while (i < text.Length)
            {
                var c = text[i];

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    i = SkipLineComment(text, i);
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i = SkipBlockComment(text, i);
                    continue;
                }

                if (c == '"' || c == '\'' || c == '`')
                {
                    i = SkipQuoted(text, i, c);
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }

                i++;
            }

            return -1;
        }

        private static int SkipLineComment(string text, int index)
        {
            var i = index;
            while (i < text.Length && text[i] != '\n')
            {
                i++;
            }

            return i;
        }

        private static int SkipBlockComment(string text, int index)
        {
            var i = index + 2;
            while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
            {
                i++;
            }

            return i + 2;
        }

        /// <summary>
        /// 跳过一段字符串（含转义）；处理 C# 逐字字符串 <c>@"..."</c> 里的 <c>""</c>
        /// </summary>
        private static int SkipQuoted(string text, int index, char quote)
        {
            var verbatim = quote == '"' && index > 0 && (text[index - 1] == '@' || text[index - 1] == '$');
            var i = index + 1;

            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\\' && !verbatim)
                {
                    i += 2;
                    continue;
                }

                if (c == quote)
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == quote)
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                if (!verbatim && (c == '\n' || c == '\r'))
                {
                    // 未闭合的单行字符串：避免整段吞掉后面的代码
                    return i;
                }

                i++;
            }

            return i;
        }

        /// <summary>
        /// 缩进块（Python 等）：返回块结束偏移（不含）
        /// </summary>
        private static int IndentBlockEnd(string text, IList<int> lineStarts, int declLine)
        {
            var declIndent = IndentOf(text, lineStarts, declLine);
            var last = declLine;
            var line = declLine + 1;

            while (line <= lineStarts.Count)
            {
                var start = lineStarts[line - 1];
                var end = line < lineStarts.Count ? lineStarts[line] : text.Length;
                var content = text.Substring(start, end - start).TrimEnd('\r', '\n');

                if (content.Trim().Length == 0)
                {
                    // 空行先记账，若后面还有更深缩进就一起吸收
                    line++;
                    continue;
                }

                if (IndentOf(text, lineStarts, line) <= declIndent)
                {
                    break;
                }

                last = line;
                line++;
            }

            // 结束位置 = 最后一行的行尾（含该行换行）
            return last < lineStarts.Count ? lineStarts[last] : text.Length;
        }

        private static int IndentOf(string text, IList<int> lineStarts, int line)
        {
            if (line < 1 || line > lineStarts.Count)
            {
                return 0;
            }

            var start = lineStarts[line - 1];
            var i = start;
            var indent = 0;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            {
                // 制表符按 4 个空格折算
                indent += text[i] == '\t' ? 4 : 1;
                i++;
            }

            return indent;
        }

        private static IList<int> LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    starts.Add(i + 1);
                }
            }

            return starts;
        }

        private static int LineOf(IList<int> lineStarts, int offset)
        {
            // lineStarts 升序：二分找最后一个 <= offset 的行
            var lo = 0;
            var hi = lineStarts.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (lineStarts[mid] <= offset)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return lo + 1;
        }

        private static bool UsesCrLf(string text)
        {
            return text != null && text.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
        }

        private static string Newline(string text)
        {
            return UsesCrLf(text) ? "\r\n" : "\n";
        }

        private static string NormalizeNewlines(string value, bool crlf)
        {
            var body = value.Replace("\r\n", "\n").Replace('\r', '\n');
            return crlf ? body.Replace("\n", "\r\n") : body;
        }
    }
}

