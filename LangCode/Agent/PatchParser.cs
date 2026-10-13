using System;
using System.Collections.Generic;
using System.Text;

namespace LangCode.Agent
{
    /// <summary>
    /// 补丁里的一节（一个文件的一处改动）
    /// </summary>
    public sealed class PatchSection
    {
        /// <summary>
        /// 相对工作区的文件路径
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// 要替换掉的原文
        /// </summary>
        public string OldText { get; set; } = string.Empty;

        /// <summary>
        /// 替换成的新文本
        /// </summary>
        public string NewText { get; set; } = string.Empty;

        /// <summary>
        /// 是否是「新建文件」节
        /// </summary>
        public bool IsNewFile { get; set; }
    }

    /// <summary>
    /// 多文件补丁的解析器 
    /// </summary>
    public static class PatchParser
    {
        private const string SearchMarker = "<<<<<<<";
        private const string DividerMarker = "=======";
        private const string ReplaceMarker = ">>>>>>>";

        /// <summary>
        /// 解析补丁串 
        /// </summary>
        public static IList<PatchSection> Parse(string text, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "补丁内容为空";
                return null;
            }

            var sections = new List<PatchSection>();
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            PatchSection current = null;

            // 0 = 等文件头，1 = 读原文，2 = 读新文
            var state = 0;

            var body = new StringBuilder();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith(SearchMarker, StringComparison.Ordinal))
                {
                    if (current == null)
                    {
                        error = "补丁缺少文件头（应以 *** Update File: 路径 或 *** Add File: 路径 开始）";
                        return null;
                    }

                    state = 1;
                    body.Clear();
                    continue;
                }

                if (state == 1 && trimmed.StartsWith(DividerMarker, StringComparison.Ordinal))
                {
                    current.OldText = TrimTrailingNewline(body);
                    body.Clear();
                    state = 2;
                    continue;
                }

                if (state == 2 && trimmed.StartsWith(ReplaceMarker, StringComparison.Ordinal))
                {
                    current.NewText = TrimTrailingNewline(body);
                    sections.Add(current);
                    current = null;
                    state = 0;
                    body.Clear();
                    continue;
                }

                if (state == 0)
                {
                    // 文件头：*** Update File: path / *** Add File: path / --- path
                    var path = ReadFileHeader(trimmed, out var isNew);
                    if (path != null)
                    {
                        current = new PatchSection { Path = path, IsNewFile = isNew };
                    }

                    continue;
                }

                body.Append(line).Append('\n');
            }

            if (state != 0 || current != null)
            {
                error = "补丁没有正常收尾（缺 >>>>>>> REPLACE，或少了 ======= 分隔符）";
                return null;
            }

            if (sections.Count == 0)
            {
                error = "补丁里没有任何可应用的节";
                return null;
            }

            foreach (var section in sections)
            {
                if (string.IsNullOrWhiteSpace(section.Path))
                {
                    error = "有一节补丁没写文件路径";
                    return null;
                }

                if (!section.IsNewFile && string.IsNullOrEmpty(section.OldText))
                {
                    error = section.Path + "：修改已有文件时原文不能为空";
                    return null;
                }
            }

            return sections;
        }

        /// <summary>
        /// 读文件头；不是文件头返回 null
        /// </summary> 
        private static string ReadFileHeader(string line, out bool isNewFile)
        {
            isNewFile = false;

            string rest = null;
            if (line.StartsWith("*** Update File:", StringComparison.OrdinalIgnoreCase))
            {
                rest = line.Substring("*** Update File:".Length);
            }
            else if (line.StartsWith("*** Add File:", StringComparison.OrdinalIgnoreCase))
            {
                rest = line.Substring("*** Add File:".Length);
                isNewFile = true;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                rest = line.Substring(4);
            }

            if (rest == null)
            {
                return null;
            }

            var path = rest.Trim().Trim('"');
            return path.Length == 0 ? null : path;
        }

        private static string TrimTrailingNewline(StringBuilder builder)
        {
            var text = builder.ToString();
            return text.EndsWith("\n", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text;
        }
    }
}
