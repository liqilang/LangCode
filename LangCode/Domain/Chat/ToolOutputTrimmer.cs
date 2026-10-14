namespace LangCode.Domain.Chat
{
    /// <summary>
    /// 工具结果「回灌预算」：把过长的工具输出压到可接受的长度
    /// </summary>
    public static class ToolOutputTrimmer
    {
        /// <summary>回灌给模型的单条工具结果字符上限（超出即掐掉中段）。</summary>
        public const int DefaultMaxChars = 12000;

        /// <summary>
        /// 压缩一条工具结果 
        /// </remarks>
        public static string Shrink(string output, int maxChars = DefaultMaxChars)
        {
            var text = output ?? string.Empty;
            if (text.Length <= maxChars)
            {
                return text;
            }

            var head = maxChars * 7 / 10;
            var tail = maxChars - head;
            var omitted = text.Length - head - tail;

            return text.Substring(0, head)
                + "\n…（中间省略 " + omitted + " 字符；需要原文请缩小范围重新读取，例如带 start_line / end_line 或更精确的搜索）…\n"
                + text.Substring(text.Length - tail);
        }
    }
}
