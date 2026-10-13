using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Chat
{
    /// <summary>
    /// 编辑器内联能力的引擎 
    /// </summary>
    public sealed class InlineEngine
    {
        private readonly IChatBackend _backend;

        public InlineEngine(IChatBackend backend)
        {
            _backend = backend;
        }

        /// <summary>
        /// 请求一次幽灵文本补全；返回可直接插入光标处的文本（无可用补全时为空串）
        /// </summary>
        public async Task<string> CompleteAsync(InlineCompletionRequest request, CancellationToken cancellationToken)
        {
            if (_backend == null || request == null)
            {
                return string.Empty;
            }

            var prompt = InlinePrompts.BuildCompletionUserPrompt(request.Language, request.FilePath, request.Prefix, request.Suffix);

            var symbols = FormatSymbols(request.Symbols);
            if (symbols.Length > 0)
            {
                prompt += "\n\n当前文件里出现过的相关符号（供参考，不必全部用到）：" + symbols;
            }

            var chat = new ChatRequest
            {
                Model = request.Model ?? string.Empty,
                SystemPrompt = InlinePrompts.BuildCompletionSystemPrompt(),
                Turns = [new ChatTurn("user", prompt)],

                // 补全要「稳」不要「花」：低温度、小输出上限
                Temperature = 0.1,
                TopP = 1.0,
                MaxTokens = 512,
            };

            var buffer = new StringBuilder();
            await _backend.SendAsync(chat, delta => buffer.Append(delta), cancellationToken).ConfigureAwait(false);

            return InlinePrompts.SanitizeCompletion(buffer.ToString());
        }

        /// <summary>
        /// 请求一次选区内联编辑；返回用来替换选区的新代码
        /// </summary>
        public async Task<string> EditAsync(InlineEditRequest request, CancellationToken cancellationToken)
        {
            if (_backend == null || request == null || string.IsNullOrWhiteSpace(request.Instruction))
            {
                return request?.Selection ?? string.Empty;
            }

            var prompt = InlinePrompts.BuildInlineEditUserPrompt(
                request.Language, request.FilePath, request.Selection,
                request.Instruction, request.Before, request.After);

            var chat = new ChatRequest
            {
                Model = request.Model ?? string.Empty,
                SystemPrompt = InlinePrompts.BuildInlineEditSystemPrompt(),
                Turns = [new ChatTurn("user", prompt)],
                Temperature = 0.2,
                TopP = 1.0,
                MaxTokens = 2048,
            };

            var buffer = new StringBuilder();
            await _backend.SendAsync(chat, delta => buffer.Append(delta), cancellationToken).ConfigureAwait(false);

            var code = InlinePrompts.ExtractCode(buffer.ToString());
            return code.Length == 0 ? request.Selection : code;
        }

        /// <summary>
        /// 把符号列表拼成一行提示（去重、限制数量与长度）
        /// </summary>
        private static string FormatSymbols(IList<string> symbols)
        {
            if (symbols == null || symbols.Count == 0)
            {
                return string.Empty;
            }

            var distinct = symbols
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(40)
                .ToList();

            var text = string.Join(", ", distinct);
            return text.Length <= 600 ? text : text.Substring(0, 600);
        }
    }
}

