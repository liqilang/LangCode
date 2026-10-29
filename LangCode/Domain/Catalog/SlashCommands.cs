using System;
using System.Collections.Generic;

namespace LangCode.Domain.Catalog
{
    /// <summary>
    /// 输入框里以 <c>/</c> 唤起的一条命令
    /// </summary>
    public sealed class SlashCommand
    {
        public SlashCommand(string key, string title, string description, string insertText, bool isAction = false)
        {
            Key = key ?? string.Empty;
            Title = title ?? string.Empty;
            Description = description ?? string.Empty;
            InsertText = insertText ?? string.Empty;
            IsAction = isAction;
        }

        /// <summary>
        /// 命令名（不含前导 <c>/</c>）
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// 列表里显示的名字
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// 一句话说明（悬停提示）
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// 选中后插入输入框的提示词（动作型命令为空）
        /// </summary>
        public string InsertText { get; }

        /// <summary>
        /// 是否直接执行某个动作（例如 <c>/clear</c>），而不是往输入框里插文本
        /// </summary>
        public bool IsAction { get; }

        /// <summary>
        /// 列表里显示的 <c>/plan</c> 形式
        /// </summary>
        public string Display => "/" + Key;
    }

    /// <summary>
    /// 斜杠命令表
    /// </summary>
    public static class SlashCommands
    {
        /// <summary>
        /// 全部命令（界面按此顺序展示）
        /// </summary>
        public static IList<SlashCommand> All { get; } =
        [
            new SlashCommand(
                "plan",
                "先出计划",
                "只做调研并给出分步计划，不修改任何文件",
                "先不要改代码：把现状调研清楚，然后用 update_todo_list 给出分步计划，并说明关键假设、风险与取舍"),

            new SlashCommand(
                "review",
                "审查改动",
                "审查未提交的改动，按严重程度列出问题",
                "审查当前工作区里未提交的改动：先调用 git_status 和 git_diff（需要看暂存区时用 git_diff staged=true），必要时用 git_log / git_show 了解上下文；指出潜在 bug、边界条件与改进点，并按严重程度排序。不要用 run_command 手写 git diff"),

            new SlashCommand(
                "commit",
                "生成并执行提交",
                "按 Conventional Commits 生成提交信息，确认后暂存并提交",
                "检查当前未提交的改动：先调用 git_status 和 git_diff 复核，按 Conventional Commits 生成一条清晰的提交信息，并列出本次将纳入提交的文件；我确认后，依次调用 git_stage 和 git_commit 完成暂存与提交（不要用 run_command 执行 git commit，也不要 push）"),

            new SlashCommand(
                "test",
                "补单元测试",
                "为最近改动的代码补测试",
                "为最近改动的代码补上单元测试先说明用哪个测试框架、放在哪个文件，再动手实现"),

            new SlashCommand(
                "fix",
                "定位并修复",
                "先说清根因，再给最小改动",
                "定位并修复当前的问题：先说清楚根因，再给出最小范围的改动"),

            new SlashCommand(
                "explain",
                "解释代码",
                "解释一段代码的作用与陷阱",
                "解释下面这段代码的作用、关键流程与潜在陷阱：\n（在这里粘贴代码，或用 @文件 提及）"),

            new SlashCommand(
                "clear",
                "清空会话",
                "清空当前会话的消息（不删除历史记录）",
                string.Empty,
                isAction: true),
        ];

        /// <summary>
        /// 按前缀过滤（<paramref name="query"/> 不含前导 <c>/</c>；为空则返回全部）
        /// </summary>
        public static IList<SlashCommand> Match(string query)
        {
            var text = (query ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return All;
            }

            var result = new List<SlashCommand>();
            foreach (var command in All)
            {
                if (command.Key.StartsWith(text, StringComparison.OrdinalIgnoreCase) ||
                    command.Display.StartsWith("/" + text, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(command);
                }
            }

            return result;
        }

        /// <summary>
        /// 按命令名取一条；找不到返回 null
        /// </summary>
        public static SlashCommand Find(string key)
        {
            foreach (var command in All)
            {
                if (string.Equals(command.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return command;
                }
            }

            return null;
        }
    }
}

