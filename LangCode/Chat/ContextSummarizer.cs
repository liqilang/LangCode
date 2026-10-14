using LangCode.Domain.Settings;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Chat
{
    /// <summary>
    /// 总结式上下文压缩：把「即将被折叠的较早对话」交给模型压成一段结构化摘要，而不是直接丢弃。 
    /// </summary>  
    public static class ContextSummarizer
    {
        /// <summary>
        /// 少于这个字符量时不值得为它单独发起一次模型调用（交给调用方退回固定说明）
        /// </summary>
        private const int MinCharsToSummarize = 1200;

        /// <summary>
        /// 摘要提示词（固定不变，便于服务端提示缓存命中）
        /// </summary>
        public const string SystemPrompt =
            "你是一个上下文压缩器。用户会给你一段即将被折叠的对话记录（其中可能包含用户需求、助手回复与工具执行结果）。\n" +
            "请把它压缩成一份**信息密度高、无冗余**的中文摘要，供后续继续执行任务时参考。必须保留：\n" +
            "1) 用户的原始目标与关键要求；\n" +
            "2) 已经确定的方案 / 决策与关键假设；\n" +
            "3) 已经改动过的文件与其改动要点（逐文件列出相对路径）；\n" +
            "4) 当前进度与尚未完成的事项；\n" +
            "5) 仍需注意的报错 / 风险 / 待确认问题。\n" +
            "丢弃无关寒暄、重复内容与已被覆盖的中间过程。直接输出摘要正文，不要复述本指令，也不要调用任何工具。";

        /// <summary>
        /// 把一组对话轮次总结成一段摘要 
        /// </summary> 
        public static async Task<string> SummarizeAsync(IChatBackend backend, AppSettings settings, IList<ChatTurn> dropped, CancellationToken cancellationToken)
        {
            if (backend == null || dropped == null || dropped.Count == 0)
            {
                return null;
            }

            var transcript = BuildTranscript(dropped);
            if (transcript.Length < MinCharsToSummarize)
            {
                // 太短：压缩收益可能还不如这次调用本身的开销
                return null;
            }

            var request = new ChatRequest
            {
                Model = settings?.Model ?? string.Empty,
                SystemPrompt = SystemPrompt,
                Turns = [new ChatTurn("user", "以下是需要压缩的对话记录：\n\n" + transcript)],
                Temperature = 0.2,
                TopP = 1.0,
                MaxTokens = 1024,

                // 压缩调用本身不带工具；是否打缓存断点沿用用户设置
                PromptCaching = settings?.PromptCaching ?? false,
            };

            var buffer = new StringBuilder();
            await backend.SendAsync(request, delta => buffer.Append(delta), cancellationToken).ConfigureAwait(false);

            var text = buffer.ToString().Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>
        /// 把轮次拼成一段纯文本记录；单条过长时先各自收敛，避免把压缩调用本身撑爆。
        /// </summary>
        private static string BuildTranscript(IList<ChatTurn> turns)
        {
            var builder = new StringBuilder();
            foreach (var turn in turns)
            {
                if (turn == null || string.IsNullOrWhiteSpace(turn.Content))
                {
                    continue;
                }

                var role = string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase) ? "用户" : "助手";
                builder.Append('【').Append(role).Append("】\n");

                var content = turn.Content;
                if (content.Length > 6000)
                {
                    content = content.Substring(0, 6000) + "\n…（该条过长，已截断）";
                }

                builder.Append(content).Append("\n\n");
            }

            return builder.ToString();
        }
    }
}
