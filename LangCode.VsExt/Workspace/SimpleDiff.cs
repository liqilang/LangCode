using LangCode.VsExt.Domain.Chat;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LangCode.VsExt.Workspace
{
    /// <summary>
    /// 行级 Diff（LCS）用于把「将要写入的内容」渲染成设计稿里的 Applied Diff 卡
    /// </summary>
    public static class SimpleDiff
    {
        private const int MaxLines = 1500;

        public static IList<ChatBlockLine> Diff(string before, string after)
        {
            var oldLines = SplitLines(before);
            var newLines = SplitLines(after);

            if (oldLines.Length > MaxLines || newLines.Length > MaxLines)
            {
                var coarse = new List<ChatBlockLine>();
                foreach (var line in oldLines)
                {
                    coarse.Add(new ChatBlockLine(DiffLineKind.Delete, "− ", line));
                }

                foreach (var line in newLines)
                {
                    coarse.Add(new ChatBlockLine(DiffLineKind.Add, "+ ", line));
                }

                return coarse;
            }

            // 后缀 LCS 长度表：lcs[i, j] = oldLines[i..] 与 newLines[j..] 的最长公共子序列长度
            var lcs = new int[oldLines.Length + 1, newLines.Length + 1];
            for (var i = oldLines.Length - 1; i >= 0; i--)
            {
                for (var j = newLines.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            var result = new List<ChatBlockLine>();
            var x = 0;
            var y = 0;
            while (x < oldLines.Length && y < newLines.Length)
            {
                if (string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
                {
                    result.Add(new ChatBlockLine(DiffLineKind.Context, "  ", oldLines[x]));
                    x++;
                    y++;
                }
                else if (lcs[x + 1, y] >= lcs[x, y + 1])
                {
                    result.Add(new ChatBlockLine(DiffLineKind.Delete, "− ", oldLines[x]));
                    x++;
                }
                else
                {
                    result.Add(new ChatBlockLine(DiffLineKind.Add, "+ ", newLines[y]));
                    y++;
                }
            }

            while (x < oldLines.Length)
            {
                result.Add(new ChatBlockLine(DiffLineKind.Delete, "− ", oldLines[x++]));
            }

            while (y < newLines.Length)
            {
                result.Add(new ChatBlockLine(DiffLineKind.Add, "+ ", newLines[y++]));
            }

            return result;
        }

        /// <summary>
        /// 按行比较（忽略 CRLF/LF 差异）
        /// </summary>
        public static bool AreEqual(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
        }

        public static int CountAdded(IEnumerable<ChatBlockLine> lines)
        {
            return lines?.Count(l => l.Kind == DiffLineKind.Add) ?? 0;
        }

        public static int CountDeleted(IEnumerable<ChatBlockLine> lines)
        {
            return lines?.Count(l => l.Kind == DiffLineKind.Delete) ?? 0;
        }

        private static string Normalize(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string[] SplitLines(string text)
        {
            var normalized = Normalize(text);

            // 空文件是「零行」，不是「一行空行」——否则新建文件的 Diff 会凭空多出一条删除行
            return normalized.Length == 0 ? new string[0] : normalized.Split('\n');
        }
    }
}

