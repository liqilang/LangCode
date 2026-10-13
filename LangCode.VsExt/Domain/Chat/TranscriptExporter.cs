using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LangCode.VsExt.Domain.Chat
{
    /// <summary>
    /// 把一次会话导出成 Markdown
    /// </summary>
    public static class TranscriptExporter
    {
        /// <summary>
        /// 导出为 Markdown（<paramref name="title"/> 为空时取首条用户消息）
        /// </summary>
        public static string ToMarkdown(string title, IEnumerable<ChatMessage> messages)
        {
            var list = (messages ?? Enumerable.Empty<ChatMessage>()).ToList();
            var builder = new StringBuilder();

            var heading = string.IsNullOrWhiteSpace(title) ? FirstUserText(list) : title.Trim();
            builder.Append("# ").Append(string.IsNullOrWhiteSpace(heading) ? "LangCode 会话" : heading).Append('\n');
            builder.Append('\n');
            builder.Append("> 导出自 LangCode · ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append('\n');
            builder.Append('\n');

            foreach (var message in list)
            {
                AppendMessage(builder, message);
            }

            return builder.ToString().TrimEnd() + "\n";
        }

        /// <summary>
        /// 只导出正文（不含导出说明头）—— 便于直接把一段对话贴到别处
        /// </summary>
        public static string ToPlainMarkdown(IEnumerable<ChatMessage> messages)
        {
            var builder = new StringBuilder();
            foreach (var message in (messages ?? Enumerable.Empty<ChatMessage>()).ToList())
            {
                AppendMessage(builder, message);
            }

            return builder.ToString().Trim();
        }

        private static void AppendMessage(StringBuilder builder, ChatMessage message)
        {
            if (message == null)
            {
                return;
            }

            builder.Append("## ").Append(message.IsUser ? "你" : "LangCode").Append('\n').Append('\n');

            if (message.IsUser)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                {
                    builder.Append(message.Text.Trim()).Append('\n').Append('\n');
                }

                if (message.Images.Count > 0)
                {
                    builder.Append("（附带 ").Append(message.Images.Count).Append(" 张图片）").Append('\n').Append('\n');
                }

                return;
            }

            foreach (var block in message.Blocks)
            {
                AppendBlock(builder, block);
            }

            if (message.Blocks.Count == 0 && !string.IsNullOrWhiteSpace(message.Text))
            {
                builder.Append(message.Text.Trim()).Append('\n').Append('\n');
            }
        }

        private static void AppendBlock(StringBuilder builder, ChatBlock block)
        {
            if (block == null)
            {
                return;
            }

            switch (block.Kind)
            {
                case ChatBlockKind.Text:
                    if (!string.IsNullOrWhiteSpace(block.Text))
                    {
                        builder.Append(block.Text.Trim()).Append('\n').Append('\n');
                    }

                    break;

                case ChatBlockKind.Thinking:
                    if (!string.IsNullOrWhiteSpace(block.Text))
                    {
                        builder.Append("<details><summary>推理过程</summary>").Append('\n').Append('\n');
                        builder.Append(block.Text.Trim()).Append('\n').Append('\n');
                        builder.Append("</details>").Append('\n').Append('\n');
                    }

                    break;

                case ChatBlockKind.CodeCard:
                    builder.Append("### ").Append(TitleOr(block, "代码")).Append('\n').Append('\n');
                    if (!string.IsNullOrWhiteSpace(block.Path))
                    {
                        builder.Append("`").Append(block.Path).Append("`").Append('\n').Append('\n');
                    }

                    AppendFence(builder, string.Empty, block.Text);
                    break;

                case ChatBlockKind.DiffCard:
                    builder.Append("### ").Append(TitleOr(block, "改动")).Append('\n').Append('\n');
                    if (!string.IsNullOrWhiteSpace(block.Path))
                    {
                        builder.Append("`").Append(block.Path).Append("`").Append('\n').Append('\n');
                    }

                    AppendDiff(builder, block);
                    break;

                case ChatBlockKind.Todo:
                    if (block.Todos.Count > 0)
                    {
                        builder.Append("### 任务清单").Append('\n').Append('\n');
                        foreach (var item in block.Todos)
                        {
                            var mark = item.IsCompleted ? "x" : item.IsInProgress ? "-" : " ";
                            builder.Append("- [").Append(mark).Append("] ").Append(item.Text).Append('\n');
                        }

                        builder.Append('\n');
                    }

                    break;

                case ChatBlockKind.Question:
                    builder.Append("### 提问").Append('\n').Append('\n');
                    builder.Append(block.Text).Append('\n').Append('\n');
                    if (block.IsAnswered)
                    {
                        builder.Append("回答：").Append(block.AnswerText).Append('\n').Append('\n');
                    }

                    break;

                case ChatBlockKind.SubTask:
                    builder.Append("### 子任务").Append('\n').Append('\n');
                    builder.Append(block.Text).Append('\n').Append('\n');
                    break;

                case ChatBlockKind.Note:
                case ChatBlockKind.Done:
                case ChatBlockKind.Approval:
                default:
                    if (!string.IsNullOrWhiteSpace(block.Text))
                    {
                        builder.Append(block.Text.Trim()).Append('\n').Append('\n');
                    }

                    break;
            }
        }

        private static void AppendDiff(StringBuilder builder, ChatBlock block)
        {
            builder.Append("```diff").Append('\n');
            foreach (var line in block.Lines)
            {
                var prefix = line.Kind == DiffLineKind.Add ? "+" : line.Kind == DiffLineKind.Delete ? "-" : " ";
                builder.Append(prefix).Append(line.Text).Append('\n');
            }

            builder.Append("```").Append('\n').Append('\n');
        }

        private static void AppendFence(StringBuilder builder, string language, string text)
        {
            builder.Append("```").Append(language ?? string.Empty).Append('\n');
            builder.Append((text ?? string.Empty).Trim()).Append('\n');
            builder.Append("```").Append('\n').Append('\n');
        }

        private static string TitleOr(ChatBlock block, string fallback)
        {
            return string.IsNullOrWhiteSpace(block.Title) ? fallback : block.Title;
        }

        private static string FirstUserText(IList<ChatMessage> messages)
        {
            var first = messages.FirstOrDefault(m => m.IsUser && !string.IsNullOrWhiteSpace(m.Text));
            return first?.Text ?? string.Empty;
        }
    }
}

