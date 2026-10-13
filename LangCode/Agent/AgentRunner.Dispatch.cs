using LangCode.Chat;
using LangCode.Domain.Catalog;
using LangCode.Domain.Chat;
using LangCode.Domain.Settings;
using LangCode.Mcp;
using LangCode.Persistence;
using LangCode.VsIntegration;
using LangCode.Workspace;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Agent
{
    /// <summary>
    /// AgentRunner 分部：动作分派（规划模式拦截 → 编排 / 外部 / 本地只读写）与本地工具执行、记账
    /// </summary>
    public sealed partial class AgentRunner
    {
        /// <summary>
        /// 执行一个动作（规划模式拦截 → 编排 / 外部 / 只读写工具分派），返回结果。
        /// 「是不是破坏性 / 走哪条分派」全部来自 <see cref="ToolCatalog"/>，不再各写一个 switch。
        /// </summary>
        private async Task<ToolResult> ExecuteActionAsync(ToolAction action, AgentCallbacks callbacks, CancellationToken cancellationToken, bool suppressStreaming)
        {
            // 规划模式：只放行只读工具，改动类直接挡回并说明原因
            // 这样做而不是「提前不问模型」：模型必须知道为什么被拒，才不会反复重试同一个动作
            if (_settings != null && _settings.PlanMode && ToolCatalog.IsDestructive(action.Name))
            {
                return new ToolResult
                {
                    Ok = false,
                    Output = "当前处于「规划模式」：不允许修改文件、执行命令或调用外部工具" +
                             "请只用 list_files / read_file / search_text 调研，并用 update_todo_list 给出分步计划",
                };
            }

            // 按类别决定要不要问用户（设置页「批准」分页 / 输入框的 Auto 弹层）
            var auto = ApprovalCatalog.IsAutoApprovedForTool(_settings, action.Name);

            if (ToolCatalog.DispatchOf(action.Name) == ToolDispatch.Orchestration)
            {
                // 长任务编排（待办 / 提问 / 子任务）：不碰磁盘，因此不走「先问再做」的确认卡
                return await ExecuteOrchestrationAsync(action, callbacks, cancellationToken, suppressStreaming)
                    .ConfigureAwait(false);
            }

            if (ToolCatalog.DispatchOf(action.Name) == ToolDispatch.External)
            {
                // 有副作用的工具（命令 / 浏览器 / MCP）：先问、再执行
                return await ExecuteExternalAsync(action, auto, callbacks, cancellationToken).ConfigureAwait(false);
            }

            var result = Execute(action);

            if (result.Pending != null)
            {
                var pending = result.Pending;

                // 「新建文件」与「覆盖已有文件」是两种风险，不该被同一个开关捎带放行：
                // write_file 一律按类别 create 归类，于是「放开了新增」就等于「放开了覆盖」
                // 到这一步才知道文件是否存在（PrepareWrite 已经查过），所以在这里改成按真实情况判定
                var effectiveAuto = auto;
                if (action.Name == "write_file")
                {
                    effectiveAuto = ApprovalCatalog.IsAutoApproved(_settings, pending.IsNewFile ? ApprovalCatalog.Create : ApprovalCatalog.Edit);
                }

                if (effectiveAuto || callbacks.ApproveAsync == null)
                {
                    // 已放开，或没有审批回调：直接落盘
                    return Apply(pending, callbacks);
                }

                var approved = await callbacks.ApproveAsync(pending).ConfigureAwait(false);
                return approved ? Apply(pending, callbacks) : new ToolResult
                {
                    Ok = false,
                    Output = "用户拒绝了这次修改（文件未改动）：" + pending.RelativePath,
                };
            }

            if (!auto && callbacks.ApproveActionAsync != null)
            {
                // 只读类操作：默认放开；用户在设置里关掉后才逐次确认
                var allowed = await callbacks.ApproveActionAsync(action, result).ConfigureAwait(false);
                if (!allowed)
                {
                    return new ToolResult
                    {
                        Ok = false,
                        Output = "用户拒绝了这次操作：" + action.Name,
                    };
                }
            }

            return result;
        }

        /// <summary>
        /// 本次模型输出里包含几个 <c>new_task</c>（用于判断是否值得并行）
        /// </summary>
        private static int CountSubTasks(IList<ToolAction> actions)
        {
            var count = 0;
            foreach (var action in actions)
            {
                if (action.Name == "new_task")
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 给确认卡看的一行预览（先问再做，用户看到的就是这段）
        /// </summary>
        private string BuildPreview(ToolAction action)
        {
            switch (action.Name)
            {
                case "run_command":
                    return "将要执行命令：\n" + _commands.Describe(action.Get("command"), action.Get("working_directory"));

                case "run_tests":
                    var previewCommand = ResolveTestCommand();
                    return previewCommand == null
                        ? "将要运行测试（但无法自动识别测试命令）"
                        : "将要运行测试：\n" + _commands.Describe(ApplyTestFilter(previewCommand, action.Get("filter")), string.Empty);

                case "open_url":
                    return "将要打开：" + action.Get("url") +
                           (string.Equals(action.Get("fetch"), "true", StringComparison.OrdinalIgnoreCase)
                               ? "（并抓取正文）"
                               : string.Empty);

                case "browser_action":
                    return "浏览器自动化动作：" + action.Get("action") +
                           (string.IsNullOrWhiteSpace(action.Get("url")) ? string.Empty : "\n地址：" + action.Get("url")) +
                           (string.IsNullOrWhiteSpace(action.Get("selector")) ? string.Empty : "\n元素：" + action.Get("selector"));

                case "mcp_call":
                    var server = (_settings.McpServers ?? [])
                        .FirstOrDefault(s => string.Equals(s.Name, action.Get("server"), StringComparison.OrdinalIgnoreCase));

                    return "将要调用 MCP 工具：\n" + McpManager.Describe(server, action.Get("tool"), action.Get("arguments"));

                case "debug_inspect":
                case "debug_control":
                    return "调试器操作：" + action.Get("action") +
                           (string.IsNullOrWhiteSpace(action.Get("path")) ? string.Empty : "\n文件：" + action.Get("path")) +
                           (string.IsNullOrWhiteSpace(action.Get("condition")) ? string.Empty : "\n条件：" + action.Get("condition")) +
                           (string.IsNullOrWhiteSpace(action.Get("expression")) ? string.Empty : "\n表达式：" + action.Get("expression"));

                default:
                    return action.Name;
            }
        }

        private ToolResult Execute(ToolAction action)
        {
            switch (action.Name)
            {
                case "list_files":
                    {
                        var files = _workspace.ListFiles(action.Get("glob"), 6);
                        return new ToolResult
                        {
                            Ok = true,
                            Output = files.Count == 0 ? "（没有匹配的文件）" : string.Join("\n", files),
                        };
                    }

                case "read_file":
                    return _workspace.ReadFile(action.Get("path"), ParseInt(action.Get("start_line")), ParseInt(action.Get("end_line")));

                case "search_text":
                    return _workspace.SearchText(action.Get("pattern"), action.Get("glob"));

                case "list_symbols":
                    return _workspace.ListSymbols(action.Get("path"));

                case "find_references":
                    // Roslyn 可用时按标识符 token 精确匹配（自动跳过字符串 / 注释）；否则整词正则回退
                    return _workspace.FindReferences(
                        action.Get("symbol"),
                        action.Get("glob"),
                        ParseInt(action.Get("limit")) ?? 200);

                case "semantic_search":
                    {
                        if (_settings != null && !_settings.SemanticIndex)
                        {
                            return new ToolResult { Ok = false, Output = "语义索引已在设置里关闭" };
                        }

                        var limit = ParseInt(action.Get("limit")) ?? (_settings?.SemanticSearchResults ?? 8);
                        return _index.Search(action.Get("query"), limit);
                    }

                case "get_type_hierarchy":
                    {
                        // Roslyn 可用：走语法级实现（不依赖语义索引开关）
                        if (_workspace.SupportsRoslyn)
                        {
                            return _workspace.FindTypeHierarchy(action.Get("type"));
                        }

                        if (_settings != null && !_settings.SemanticIndex)
                        {
                            return new ToolResult { Ok = false, Output = "语义索引已在设置里关闭（Roslyn 也不可用）" };
                        }

                        return _index.TypeHierarchy(action.Get("type"));
                    }

                case "find_implementations":
                    {
                        if (_workspace.SupportsRoslyn)
                        {
                            return _workspace.FindImplementations(action.Get("type"));
                        }

                        if (_settings != null && !_settings.SemanticIndex)
                        {
                            return new ToolResult { Ok = false, Output = "语义索引已在设置里关闭（Roslyn 也不可用）" };
                        }

                        return _index.Implementations(action.Get("type"));
                    }

                case "edit_symbol":
                    {
                        if (_settings != null && !_settings.StructuredEditing)
                        {
                            return new ToolResult { Ok = false, Output = "结构化编辑已在设置里关闭" };
                        }

                        var structured = _workspace.PrepareStructuredEdit(
                            action.Get("path"),
                            action.Get("symbol"),
                            action.Get("mode"),
                            action.Get("new_text") ?? action.Get("content"));

                        // 结构化编辑也动了文件：落盘后语义索引需重建
                        if (structured.Ok && structured.Pending == null)
                        {
                            _index.Invalidate();
                        }

                        return structured;
                    }

                case "write_file":
                    return _workspace.PrepareWrite(action.Get("path"), action.Get("content"));

                case "replace_in_file":
                    return _workspace.PrepareReplace(
                        action.Get("path"),
                        action.Get("old_text"),
                        action.Get("new_text"),
                        ParseBool(action.Get("replace_all")));

                default:
                    return new ToolResult { Ok = false, Output = "✗ 未知工具：" + action.Name };
            }
        }

        /// <summary>
        /// 把一次「模型调用」的 token（本地估算）与费用记账
        /// </summary>
        private void RecordUsage(string systemPrompt, IList<ChatTurn> history, string raw)
        {
            if (_settings == null)
            {
                return;
            }

            try
            {
                double input = TokenEstimator.EstimateMessage(systemPrompt);
                foreach (var turn in history)
                {
                    input += TokenEstimator.EstimateMessage(turn.Content);
                }

                var output = TokenEstimator.EstimateMessage(raw);
                UsageStore.Record(_settings.Model, input, output, ConversationId);
            }
            catch (Exception)
            {
                // 记账失败不影响对话
            }
        }

        /// <summary>
        /// 审计日志：开启时记录一次工具调用的参数摘要与结果
        /// </summary>
        private void AuditToolCall(ToolAction action, ToolResult result)
        {
            if (_settings == null || !_settings.AuditLogEnabled)
            {
                return;
            }

            AuditLog.Append(action.Name, SummarizeArguments(action), result.Ok, result.Output, ConversationId);
        }

        /// <summary>
        /// 参数摘要：只保留最有辨识度的几个字段
        /// </summary>
        private static string SummarizeArguments(ToolAction action)
        {
            var parts = new List<string>();
            foreach (var key in new[]
            {
                "path", "command", "url", "pattern", "query", "symbol", "type",
                "action", "server", "tool", "glob",
            })
            {
                var value = action.Get(key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(key + "=" + value);
                }
            }

            var text = string.Join(", ", parts);
            return text.Length <= 400 ? text : text.Substring(0, 400) + "…";
        }
    }
}
