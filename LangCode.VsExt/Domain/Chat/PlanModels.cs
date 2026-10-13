using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LangCode.VsExt.Domain.Chat
{
    /// <summary>
    /// 任务清单里一项的状态 
    /// </summary>
    public enum TodoStatus
    {
        Pending,
        InProgress,
        Completed,
    }

    /// <summary>
    /// 长任务编排里的一条待办
    /// </summary>
    public sealed class TodoItem : ObservableObject
    {
        private string _text = string.Empty;
        private TodoStatus _status;

        public TodoItem(string text, TodoStatus status)
        {
            _text = (text ?? string.Empty).Trim();
            _status = status;
        }

        public string Text
        {
            get { return _text; }
            set { Set(ref _text, value ?? string.Empty); }
        }

        public TodoStatus Status
        {
            get { return _status; }
            set
            {
                if (!Set(ref _status, value))
                {
                    return;
                }

                Raise(nameof(IsCompleted));
                Raise(nameof(IsInProgress));
                Raise(nameof(IsPending));
                Raise(nameof(Marker));
                Raise(nameof(StatusLabel));
            }
        }

        public bool IsCompleted => Status == TodoStatus.Completed;

        public bool IsInProgress => Status == TodoStatus.InProgress;

        public bool IsPending => Status == TodoStatus.Pending;

        /// <summary>
        /// 状态符号（界面每行行首那一格）
        /// </summary>
        public string Marker
        {
            get
            {
                switch (Status)
                {
                    case TodoStatus.Completed: return "✓";
                    case TodoStatus.InProgress: return "◐";
                    default: return "○";
                }
            }
        }

        public string StatusLabel
        {
            get
            {
                switch (Status)
                {
                    case TodoStatus.Completed: return "已完成";
                    case TodoStatus.InProgress: return "进行中";
                    default: return "待办";
                }
            }
        }
    }

    /// <summary>
    /// 一次清单更新的汇总（进度条 / 统计文案用）
    /// </summary>
    public struct TodoProgress
    {
        public int Total;

        public int Completed;

        public int InProgress;

        public int Pending => Total - Completed - InProgress;

        public int Percent => Total <= 0 ? 0 : (int)Math.Round(Completed * 100.0 / Total);
    }

    /// <summary>
    /// 任务清单的解析与格式化
    /// </summary>
    public static class TodoListParser
    {
        private static readonly Regex LineRegex = new Regex(
            "^\\s*(?:(?:[-*+])\\s+|(?:\\d+[.)])\\s*)?\\[(?<mark>[^\\]]*)\\]\\s*(?<text>.+?)\\s*$",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// 把模型给的清单文本解析成条目（空行忽略）
        /// </summary>
        public static IList<TodoItem> Parse(string raw)
        {
            var items = new List<TodoItem>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return items;
            }

            foreach (var line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var match = LineRegex.Match(line);
                if (match.Success && match.Groups["text"].Success)
                {
                    items.Add(new TodoItem(match.Groups["text"].Value, ParseStatus(match.Groups["mark"].Value)));
                    continue;
                }

                // 容错：不是复选框写法就当「待办」整行收下
                var text = line.Trim().TrimStart('-', '*', '+').Trim();
                if (text.Length > 0)
                {
                    items.Add(new TodoItem(text, TodoStatus.Pending));
                }
            }

            return items;
        }

        /// <summary>
        /// 统计进度
        /// </summary>
        public static TodoProgress Summarize(IEnumerable<TodoItem> items)
        {
            var progress = new TodoProgress();
            if (items == null)
            {
                return progress;
            }

            foreach (var item in items)
            {
                progress.Total++;
                if (item.IsCompleted)
                {
                    progress.Completed++;
                }
                else if (item.IsInProgress)
                {
                    progress.InProgress++;
                }
            }

            return progress;
        }

        /// <summary>
        /// 一行进度文案（「2/5 已完成 · 40%」）
        /// </summary>
        public static string Describe(TodoProgress progress)
        {
            if (progress.Total <= 0)
            {
                return string.Empty;
            }

            var text = progress.Completed + "/" + progress.Total + " 已完成 · " + progress.Percent + "%";
            if (progress.InProgress > 0)
            {
                text += " · " + progress.InProgress + " 项进行中";
            }

            return text;
        }

        /// <summary>
        /// 回灌给模型的清单原文（保持 <c>[ ]</c> 写法，模型看得懂）
        /// </summary>
        public static string Render(IEnumerable<TodoItem> items)
        {
            var builder = new System.Text.StringBuilder();
            if (items == null)
            {
                return string.Empty;
            }

            foreach (var item in items)
            {
                string mark;
                switch (item.Status)
                {
                    case TodoStatus.Completed: mark = "[x]"; break;
                    case TodoStatus.InProgress: mark = "[-]"; break;
                    default: mark = "[ ]"; break;
                }

                builder.Append(mark).Append(' ').Append(item.Text).Append('\n');
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 按 <c>|</c> / 换行 / 分号切分备选答案（去掉序号与空项）
        /// </summary>
        public static IList<string> SplitOptions(string raw)
        {
            var options = new List<string>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return options;
            }

            foreach (var part in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split(new[] { '|', '\n', ';' }))
            {
                var text = part.Trim().TrimStart('-', '*', '+').Trim();

                // 「1. 方案A」这种序号写法也收下（去序号）
                var match = Regex.Match(text, "^\\d+[.)]\\s*(?<text>.+)$");
                if (match.Success)
                {
                    text = match.Groups["text"].Value.Trim();
                }

                if (text.Length > 0 && !options.Contains(text))
                {
                    options.Add(text);
                }
            }

            return options;
        }

        private static TodoStatus ParseStatus(string mark)
        {
            var value = (mark ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return TodoStatus.Pending;
            }

            switch (value[0])
            {
                case 'x':
                case 'X':
                case '\u2713':      // ✓
                    return TodoStatus.Completed;

                case '-':
                case '+':
                case '>':
                case '\u2192':      // →
                case '\u25d0':      // ◐
                    return TodoStatus.InProgress;

                default:
                    return TodoStatus.Pending;
            }
        }
    }

    /// <summary>
    /// 模型主动向用户提的一个问题 
    /// </summary>
    public sealed class AgentQuestion
    {
        public string Question { get; set; } = string.Empty;

        /// <summary>
        /// 备选答案（点击即回答；为空表示只能自由输入）
        /// </summary>
        public IList<string> Options { get; set; } = [];
    }

    /// <summary>
    /// 要拆出去独立完成的一个子任务 
    /// </summary>
    public sealed class AgentSubTask
    {
        public string Task { get; set; } = string.Empty;

        public string Context { get; set; } = string.Empty;

        /// <summary>
        /// 完成后的结论（由子任务循环回填）
        /// </summary>
        public string Result { get; set; } = string.Empty;
    }
}

