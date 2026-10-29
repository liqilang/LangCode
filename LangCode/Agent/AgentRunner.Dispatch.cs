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
            // 工作区策略 / 敏感路径 / 命令前缀 / 网络守卫：命中即拒绝，不进入审批
            var policyBlock = _permission.BlockReason(action);
            if (policyBlock != null)
            {
                return new ToolResult
                {
                    Ok = false,
                    Output = "✗ 安全策略拒绝：" + policyBlock,
                };
            }

            // 规划模式：只放行只读工具，改动类直接挡回并说明原因
            // 这样做而不是「提前不问模型」：模型必须知道为什么被拒，才不会反复重试同一个动作
            if (_settings != null && _settings.PlanMode && ToolCatalog.IsDestructive(action.Name))
            {
                return new ToolResult
                {
                    Ok = false,
                    Output = "当前处于「规划模式」：不允许修改文件、执行命令或调用外部工具" +
                             "请只用 list_files / read_file / search_text / git_status / git_diff 等只读工具调研，并用 update_todo_list 给出分步计划",
                };
            }

            // 按「工作区策略 → 会话授权 → 审批模式 → 类别开关」统一判定
            var auto = _permission.IsAutoApproved(action);

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

                // 供审批界面做「本次会话始终允许此工具」的授权
                pending.ToolName = action.Name;

                // 逐条改动重新判定：新建 / 覆盖 / 移动目标 / 敏感文件 / 路径策略都可能不同
                var effectiveAuto = _permission.IsAutoApprovedEdit(pending);

                if (effectiveAuto)
                {
                    return Apply(pending, callbacks);
                }

                if (callbacks.ApproveAsync == null)
                {
                    // 需要审批却没有审批通道：拒绝，不能静默落盘
                    return new ToolResult
                    {
                        Ok = false,
                        Output = "这次改动需要用户确认，但当前没有审批回调（已拒绝）：" + pending.RelativePath,
                    };
                }

                var approved = await callbacks.ApproveAsync(pending).ConfigureAwait(false);
                return approved ? Apply(pending, callbacks) : new ToolResult
                {
                    Ok = false,
                    Output = "用户拒绝了这次修改（文件未改动）：" + pending.RelativePath,
                };
            }

            if (!auto)
            {
                if (callbacks.ApproveActionAsync == null)
                {
                    return new ToolResult
                    {
                        Ok = false,
                        Output = "这次操作需要用户确认，但当前没有审批回调（已拒绝）：" + action.Name,
                    };
                }

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
                case "git_stage":
                    {
                        var all = ParseBool(action.Get("all"));
                        var paths = (action.Get("paths") ?? string.Empty).Trim();
                        return all
                            ? "将要暂存当前工作区的全部改动（git add -A）"
                            : "将要暂存：\n" + (paths.Length == 0 ? "（未指定路径）" : paths);
                    }

                case "git_commit":
                    {
                        var message = (action.Get("message") ?? string.Empty).Trim();
                        var addAll = ParseBool(action.Get("add_all"));
                        return "将要提交" + (addAll ? "（先暂存全部改动）" : "（只提交已暂存内容）") + "：\n" +
                               (message.Length == 0 ? "（提交信息为空）" : message);
                    }

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

                case "web_search":
                    return "将要联网搜索：\n" + (action.Get("query") ?? string.Empty) +
                           "\n提供商：" + (_settings?.WebSearchProvider ?? "Bing");

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

                case "read_multiple_files":
                    return _workspace.ReadMultipleFiles(action.Get("paths"), ParseInt(action.Get("max_chars_each")));

                case "list_dir":
                    return _workspace.ListDirectory(action.Get("path"), ParseInt(action.Get("depth")));

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

                case "search_docs":
                    {
                        var limit = ParseInt(action.Get("limit")) ?? (_settings?.DocSearchResults ?? 8);
                        return _workspace.SearchDocuments(action.Get("query"), action.Get("glob"), limit);
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

                case "delete_file":
                    return _workspace.PrepareDelete(action.Get("path"));

                case "move_file":
                    return _workspace.PrepareMove(action.Get("from"), action.Get("to"));

                case "git_status":
                    return GitWorkspace.Status(_workspace?.Root);

                case "git_diff":
                    return GitWorkspace.Diff(
                        _workspace?.Root,
                        ParseBool(action.Get("staged")),
                        action.Get("path"),
                        ParseBool(action.Get("stat")));

                case "git_log":
                    return GitWorkspace.Log(
                        _workspace?.Root,
                        ParseInt(action.Get("limit")) ?? 20,
                        action.Get("path"));

                case "git_show":
                    return GitWorkspace.Show(
                        _workspace?.Root,
                        action.Get("rev"),
                        ParseBool(action.Get("stat")));

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
                "path", "from", "to", "paths", "command", "url", "pattern", "query",
                "symbol", "type", "action", "server", "tool", "glob",
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
