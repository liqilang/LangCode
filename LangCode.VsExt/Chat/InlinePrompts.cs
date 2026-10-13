using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LangCode.VsExt.Chat
{
    /// <summary>
    /// 编辑器内联能力 
    /// </summary>
    public static class InlinePrompts
    {
        /// <summary>
        /// 前后文各自保留的最大字符数（超长文件的保护阀）
        /// </summary>
        public const int MaxContextChars = 4000;

        /// <summary>
        /// 单次补全最多生成的字符数（防止一次补出整文件）
        /// </summary>
        public const int MaxCompletionChars = 2000;

        internal static readonly Regex FenceRegex = new Regex("```[^\\n]*\\r?\\n(?<body>[\\s\\S]*?)```", RegexOptions.CultureInvariant);

        /// <summary>
        /// 内联补全的系统提示词：只输出要插入的代码，不要解释、不要围栏
        /// </summary>
        public static string BuildCompletionSystemPrompt()
        {
            var builder = new StringBuilder();
            builder.AppendLine("你是编辑器里的代码补全引擎用户的光标处有一段上文（prefix）与下文（suffix），");
            builder.AppendLine("你必须补出「光标位置应当插入的内容」，让整段代码自然接续");
            builder.AppendLine();
            builder.AppendLine("硬性要求：");
            builder.AppendLine("- 只输出要插入的原始代码字符，不要任何解释、注释、Markdown 围栏或引号包裹");
            builder.AppendLine("- 补全要短：能一两行完成就一两行，不要补出完整文件或大段无关代码");
            builder.AppendLine("- 缩进必须与光标处一致，且与下文自然衔接（注意下文可能从中间开始，别重复它已有的内容）");
            builder.AppendLine("- 上下文不足以判断时，输出空字符串（宁可不补，也不要瞎补）");
            return builder.ToString();
        }

        /// <summary>
        /// 内联补全的用户提示词：把前后文（截断后）拼成一条消息
        /// </summary>
        public static string BuildCompletionUserPrompt(string language, string filePath, string prefix, string suffix)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                builder.Append("文件：").Append(filePath).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(language))
            {
                builder.Append("语言：").Append(language).Append('\n');
            }

            builder.Append('\n');
            builder.Append("<prefix>\n").Append(Tail(prefix, MaxContextChars)).Append("\n</prefix>\n");
            builder.Append("<suffix>\n").Append(Head(suffix, MaxContextChars)).Append("\n</suffix>\n");
            builder.Append('\n');
            builder.Append("请只输出光标处应插入的代码");
            return builder.ToString();
        }

        /// <summary>
        /// 选区内联编辑的系统提示词：返回用来替换选区的新代码
        /// </summary>
        public static string BuildInlineEditSystemPrompt()
        {
            var builder = new StringBuilder();
            builder.AppendLine("你是编辑器里的原地改写引擎用户选中了一段代码并给出修改要求，");
            builder.AppendLine("你要返回「替换这段选中代码」的新代码");
            builder.AppendLine();
            builder.AppendLine("硬性要求：");
            builder.AppendLine("- 只输出替换后的代码本身，不要解释、不要 Markdown 围栏");
            builder.AppendLine("- 在满足要求的前提下尽量保持原代码的风格、缩进与结构，改动最小化");
            builder.AppendLine("- 若要求本身不可行，原样返回选中的代码");
            return builder.ToString();
        }

        /// <summary>
        /// 选区内联编辑的用户提示词
        /// </summary>
        public static string BuildInlineEditUserPrompt(string language, string filePath, string selection, string instruction, string before, string after)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                builder.Append("文件：").Append(filePath).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(language))
            {
                builder.Append("语言：").Append(language).Append('\n');
            }

            builder.Append('\n');
            builder.Append("修改要求：").Append((instruction ?? string.Empty).Trim()).Append('\n').Append('\n');

            if (!string.IsNullOrWhiteSpace(before) || !string.IsNullOrWhiteSpace(after))
            {
                builder.Append("<context>\n");
                if (!string.IsNullOrWhiteSpace(before))
                {
                    builder.Append("上文：\n").Append(Tail(before, MaxContextChars / 2)).Append('\n');
                }

                if (!string.IsNullOrWhiteSpace(after))
                {
                    builder.Append("下文：\n").Append(Head(after, MaxContextChars / 2)).Append('\n');
                }

                builder.Append("</context>\n\n");
            }

            builder.Append("<selection>\n").Append(selection ?? string.Empty).Append("\n</selection>\n");
            builder.Append('\n');
            builder.Append("请只输出替换 <selection> 的新代码");
            return builder.ToString();
        }

        /// <summary>
        /// 清洗补全结果：去掉 Markdown 围栏、常见的客套前缀与多余空行，得到可直接插入的文本
        /// </summary>
        public static string SanitizeCompletion(string raw)
        {
            var text = UnwrapFence(raw);
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // 去掉模型偶尔加的「Here is / 以下是补全内容：」之类前缀
            text = StripLeadingPreamble(text);

            // 去掉首尾多余的空行，但保留内部换行与原缩进
            text = text.Trim('\r', '\n');

            // 纯空白等于「没有补全」：直接给空串，别让编辑器插入一段空白字符
            if (text.Trim().Length == 0)
            {
                return string.Empty;
            }

            if (text.Length > MaxCompletionChars)
            {
                text = text.Substring(0, MaxCompletionChars);
            }

            return text;
        }

        /// <summary>
        /// 从内联编辑结果里取出代码：有围栏就取围栏内，否则原样（去首尾空白行）
        /// </summary>
        public static string ExtractCode(string raw)
        {
            var text = UnwrapFence(raw);
            return (text ?? string.Empty).Trim('\r', '\n');
        }

        /// <summary>
        /// 若整段被一个 Markdown 围栏包住，就取下围栏内的内容
        /// </summary>
        private static string UnwrapFence(string raw)
        {
            var text = raw ?? string.Empty;
            var match = FenceRegex.Match(text);
            if (match.Success)
            {
                // 围栏外几乎没别的内容时，认为是「整段被包住」
                var outside = FenceRegex.Replace(text, string.Empty).Trim();
                if (outside.Length <= 20)
                {
                    return match.Groups["body"].Value;
                }
            }

            return text;
        }

        private static string StripLeadingPreamble(string text)
        {
            var trimmed = text.TrimStart();
            var lower = trimmed.ToLowerInvariant();

            foreach (var marker in new[] { "here is", "here's", "sure,", "sure:", "以下是", "补全如下", "结果如下" })
            {
                if (lower.StartsWith(marker, StringComparison.Ordinal))
                {
                    var newline = trimmed.IndexOf('\n');
                    return newline >= 0 ? trimmed.Substring(newline + 1) : string.Empty;
                }
            }

            return text;
        }

        private static string Head(string text, int maxChars)
        {
            var value = text ?? string.Empty;
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "\n…（已截断）";
        }

        private static string Tail(string text, int maxChars)
        {
            var value = text ?? string.Empty;
            return value.Length <= maxChars ? value : "（已截断）…\n" + value.Substring(value.Length - maxChars);
        }
    }

    /// <summary>
    /// 一次内联补全请求（VS 侧从编辑器取前缀 / 后缀后填进来）
    /// </summary>
    public sealed class InlineCompletionRequest
    {
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// 语言 id / 名称（如 csharp、typescript）
        /// </summary>
        public string Language { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// 光标之前的文本
        /// </summary>
        public string Prefix { get; set; } = string.Empty;

        /// <summary>
        /// 光标之后的文本
        /// </summary>
        public string Suffix { get; set; } = string.Empty;

        /// <summary>
        /// 相关符号提示（可选，当前文件里出现过的标识符）
        /// </summary>
        public IList<string> Symbols { get; set; } = [];
    }

    /// <summary>
    /// 一次选区内联编辑请求
    /// </summary>
    public sealed class InlineEditRequest
    {
        public string Model { get; set; } = string.Empty;

        public string Language { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// 被选中的代码
        /// </summary>
        public string Selection { get; set; } = string.Empty;

        /// <summary>
        /// 用户的自然语言修改要求
        /// </summary>
        public string Instruction { get; set; } = string.Empty;

        /// <summary>
        /// 选区上文（可选）
        /// </summary>
        public string Before { get; set; } = string.Empty;

        /// <summary>
        /// 选区下文（可选）
        /// </summary>
        public string After { get; set; } = string.Empty;
    }
}

