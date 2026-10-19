using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LangCode.Agent
{
    /// <summary>
    /// 模型请求的一次工具调用
    /// </summary>
    public sealed class ToolAction
    {
        public ToolAction(string name, Dictionary<string, string> arguments)
        {
            Name = name;
            Arguments = arguments ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Name { get; }

        public Dictionary<string, string> Arguments { get; }

        /// <summary>
        /// 取参数
        /// </summary>
        public string Get(string key)
        {
            return Arguments.TryGetValue(key, out var value) ? value : string.Empty;
        }
    }

    /// <summary>
    /// 工具协议：模型用**围栏代码块 + 扁平 JSON** 表达动作
    /// </summary>
    public static class ToolProtocol
    {
        private const string Fence = "```";

        private const int FenceLength = 3;

        /// <summary>
        /// 动作块的**开头**：```action 之后**不要求换行**。
        /// </summary>
        /// <remarks>
        /// 模型经常把 JSON 和结尾围栏写在同一行（<c>```action {"tool":"read_file"}```</c>），
        /// 因此这里只匹配到语言标注为止，正文从标注后开始。
        /// 标注后面必须不是标识符字符，避免把 <c>```actionable</c> 之类的普通代码块当成动作。
        /// </remarks>
        private static readonly Regex ActionOpenRegex = new Regex(
            "```[ \\t]*[Aa][Cc][Tt][Ii][Oo][Nn](?![A-Za-z0-9_])[ \\t]*",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// 文本里识别出的一段动作块（可能只有范围，JSON 未必解析成功）
        /// </summary>
        public sealed class ActionBlock
        {
            public ActionBlock(int start, int bodyStart, int bodyEnd, int end, Dictionary<string, string> arguments)
            {
                Start = start;
                BodyStart = bodyStart;
                BodyEnd = bodyEnd;
                End = end;
                Arguments = arguments;
            }

            /// <summary>开头围栏（三个反引号）的起始下标</summary>
            public int Start { get; }

            /// <summary>正文起始下标（语言标注之后）</summary>
            public int BodyStart { get; }

            /// <summary>正文结束下标（结束围栏起始处）；没有结束围栏时为 -1</summary>
            public int BodyEnd { get; }

            /// <summary>整块结束下标（结束围栏之后）；没有结束围栏时指向下一段动作或文本末尾</summary>
            public int End { get; }

            /// <summary>解析成功的参数；解析失败为 null</summary>
            public Dictionary<string, string> Arguments { get; }

            /// <summary>是否有结束围栏</summary>
            public bool IsClosed => BodyEnd >= 0;
        }

        /// <summary>
        /// 扫描文本里的全部动作块
        /// </summary>
        /// <remarks>
        /// 结束围栏既可能**紧跟 JSON**（同一行），也可能**单独占一行**；
        /// 而动作正文的 JSON 字符串里又可能出现 Markdown 的三反引号。
        /// 因此不能用正则一把梭：这里逐个把候选三反引号当作结束围栏，
        /// 尝试解析它前面的正文，**第一个能解析出 tool 的候选**才算结束；
        /// 全都解析不出来时，才退回到「第一处看起来像结束围栏」的位置（让上层能提示格式错误）。
        /// </remarks>
        public static IList<ActionBlock> FindActionBlocks(string text)
        {
            var blocks = new List<ActionBlock>();
            if (string.IsNullOrEmpty(text))
            {
                return blocks;
            }

            var cursor = 0;
            while (cursor < text.Length)
            {
                var open = ActionOpenRegex.Match(text, cursor);
                if (!open.Success)
                {
                    break;
                }

                var start = open.Index;
                var bodyStart = start + open.Length;
                var nextCursor = bodyStart;
                var closedAt = -1;
                var fallbackAt = -1;
                Dictionary<string, string> parsed = null;

                while (true)
                {
                    var candidate = text.IndexOf(Fence, nextCursor, StringComparison.Ordinal);
                    if (candidate < 0)
                    {
                        nextCursor = text.Length;
                        break;
                    }

                    // 候选位置本身就是下一个动作块的开头：说明当前块没写结束围栏
                    if (candidate > bodyStart && IsActionOpenAt(text, candidate))
                    {
                        nextCursor = candidate;
                        break;
                    }

                    var candidateParsed = TryParseActionBody(text, bodyStart, candidate);
                    if (candidateParsed != null)
                    {
                        parsed = candidateParsed;
                        closedAt = candidate;
                        nextCursor = candidate + FenceLength;
                        break;
                    }

                    // 解析不出来时先记下最像结束围栏的位置，最后兜底使用
                    if (fallbackAt < 0 && LooksLikeClosingFence(text, candidate))
                    {
                        fallbackAt = candidate;
                    }

                    nextCursor = candidate + FenceLength;
                }

                if (closedAt < 0 && fallbackAt >= 0)
                {
                    closedAt = fallbackAt;
                    nextCursor = fallbackAt + FenceLength;
                }

                if (closedAt >= 0)
                {
                    blocks.Add(new ActionBlock(start, bodyStart, closedAt, closedAt + FenceLength, parsed));
                }
                else
                {
                    // 未闭合：End 指向下一段动作的开头（没有下一段时就是文本末尾）
                    blocks.Add(new ActionBlock(start, bodyStart, -1, Math.Max(nextCursor, bodyStart), null));
                }

                if (nextCursor <= cursor)
                {
                    break; // 保险：任何情况下都不允许死循环
                }

                cursor = nextCursor;
            }

            return blocks;
        }

        /// <summary>
        /// 文本里是否出现过 action 围栏（用于区分「模型没发动作」与「动作解析失败」）
        /// </summary>
        public static bool ContainsActionFence(string text)
        {
            return !string.IsNullOrEmpty(text) && ActionOpenRegex.IsMatch(text);
        }

        /// <summary>
        /// 从模型输出里解析出全部动作
        /// </summary>
        public static IList<ToolAction> ExtractActions(string assistantText)
        {
            var actions = new List<ToolAction>();
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return actions;
            }

            foreach (var block in FindActionBlocks(assistantText))
            {
                var parsed = block.Arguments;
                if (parsed == null)
                {
                    continue;
                }

                if (!parsed.TryGetValue("tool", out var name) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                actions.Add(new ToolAction(name.Trim(), parsed));
            }

            return actions;
        }

        /// <summary>
        /// 流式显示用 
        /// </summary>
        public static string StripActionsForDisplay(string assistantText)
        {
            if (string.IsNullOrEmpty(assistantText))
            {
                return string.Empty;
            }

            var blocks = FindActionBlocks(assistantText);
            if (blocks.Count == 0)
            {
                return assistantText.TrimEnd();
            }

            var builder = new StringBuilder(assistantText.Length);
            var cursor = 0;
            foreach (var block in blocks)
            {
                if (block.Start < cursor)
                {
                    continue;
                }

                builder.Append(assistantText, cursor, block.Start - cursor);
                cursor = Math.Min(block.End, assistantText.Length);

                // 还没写完的动作块：后面全是半截 JSON，整段不显示
                if (!block.IsClosed && cursor >= assistantText.Length)
                {
                    break;
                }

                // 吞掉动作块后面的换行，避免正文底部留下一串空行
                while (cursor < assistantText.Length &&
                       (assistantText[cursor] == '\n' || assistantText[cursor] == '\r'))
                {
                    cursor++;
                }
            }

            if (cursor < assistantText.Length)
            {
                builder.Append(assistantText, cursor, assistantText.Length - cursor);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 去掉动作块后的可见文本
        /// </summary>
        public static string StripActions(string assistantText)
        {
            if (string.IsNullOrEmpty(assistantText))
            {
                return string.Empty;
            }

            var blocks = FindActionBlocks(assistantText);
            if (blocks.Count == 0)
            {
                return assistantText;
            }

            var builder = new StringBuilder(assistantText.Length);
            var cursor = 0;
            foreach (var block in blocks)
            {
                if (block.Start < cursor)
                {
                    continue;
                }

                builder.Append(assistantText, cursor, block.Start - cursor);
                cursor = Math.Min(block.End, assistantText.Length);

                if (!block.IsClosed && cursor >= assistantText.Length)
                {
                    break;
                }

                while (cursor < assistantText.Length &&
                       (assistantText[cursor] == '\n' || assistantText[cursor] == '\r'))
                {
                    cursor++;
                }
            }

            if (cursor < assistantText.Length)
            {
                builder.Append(assistantText, cursor, assistantText.Length - cursor);
            }

            // 折叠掉因移除动作块而产生的多余空行 
            return Regex.Replace(builder.ToString(), "\\n{3,}", "\n\n").Trim();
        }

        /// <summary>
        /// 把一次工具结果格式化成回灌给模型的文本
        /// </summary>
        public static string FormatForModel(string toolName, bool ok, string output)
        {
            var builder = new StringBuilder();
            builder.Append("工具 ").Append(toolName).Append(ok ? " 执行成功" : " 执行失败").Append('\n');
            builder.Append(output ?? string.Empty);
            return builder.ToString();
        }

        // ==================== 扫描辅助 ====================

        private static bool IsActionOpenAt(string text, int index)
        {
            var match = ActionOpenRegex.Match(text, index);
            return match.Success && match.Index == index;
        }

        /// <summary>
        /// 解析 [bodyStart, bodyEnd) 之间的 JSON；必须带非空 tool 字段
        /// </summary>
        private static Dictionary<string, string> TryParseActionBody(string text, int bodyStart, int bodyEnd)
        {
            if (bodyEnd <= bodyStart)
            {
                return null;
            }

            var parsed = LiteJson.ParseFlatObject(text.Substring(bodyStart, bodyEnd - bodyStart));
            if (parsed == null)
            {
                return null;
            }

            return parsed.TryGetValue("tool", out var name) && !string.IsNullOrWhiteSpace(name) ? parsed : null;
        }

        /// <summary>
        /// 这个三反引号看上去是不是结束围栏：独占一行，或者紧跟 JSON 的收尾括号
        /// </summary>
        private static bool LooksLikeClosingFence(string text, int index)
        {
            var lineStart = index;
            while (lineStart > 0 && (text[lineStart - 1] == ' ' || text[lineStart - 1] == '\t'))
            {
                lineStart--;
            }

            var atLineStart = lineStart == 0 || text[lineStart - 1] == '\n' || text[lineStart - 1] == '\r';

            var lineEnd = index + FenceLength;
            while (lineEnd < text.Length && (text[lineEnd] == ' ' || text[lineEnd] == '\t'))
            {
                lineEnd++;
            }

            var atLineEnd = lineEnd >= text.Length || text[lineEnd] == '\n' || text[lineEnd] == '\r';

            if (atLineStart && atLineEnd)
            {
                return true;
            }

            var previous = index - 1;
            while (previous >= 0 && (text[previous] == ' ' || text[previous] == '\t'))
            {
                previous--;
            }

            return previous >= 0 && (text[previous] == '}' || text[previous] == ']');
        }
    }
}
