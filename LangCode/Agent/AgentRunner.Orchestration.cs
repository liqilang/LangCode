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
    /// AgentRunner 分部：长任务编排类工具（待办 / 提问 / 子任务 / 跳转 / 重命名 / 多文件补丁）
    /// </summary>
    public sealed partial class AgentRunner
    {
        /// <summary>
        /// 执行长任务编排类工具（不需要审批，直接执行）
        /// </summary>
        private async Task<ToolResult> ExecuteOrchestrationAsync(ToolAction action, AgentCallbacks callbacks, CancellationToken cancellationToken, bool suppressStreaming)
        {
            switch (action.Name)
            {
                case "update_todo_list":
                    return UpdateTodoList(action, callbacks);

                case "apply_patch":
                    return await ApplyPatchAsync(action, callbacks, cancellationToken).ConfigureAwait(false);

                case "goto_definition":
                    {
                        var path = action.Get("path");
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            return new ToolResult { Ok = false, Output = "path 不能为空" };
                        }

                        var line = ParseInt(action.Get("line")) ?? 0;
                        callbacks.GoToDefinition?.Invoke(path, line);

                        return new ToolResult
                        {
                            Ok = true,
                            Output = "已在 Visual Studio 中打开 " + path + (line > 0 ? "（第 " + line + " 行）" : string.Empty) +
                                     " 并请求「转到定义」若没有跳转，请改用 list_symbols / search_text 自行定位",
                        };
                    }

                case "ask_followup_question":
                    return await AskFollowupAsync(action, callbacks, cancellationToken).ConfigureAwait(false);

                case "new_task":
                    return await RunSubTaskAsync(action, callbacks, cancellationToken, suppressStreaming).ConfigureAwait(false);

                case "rename_symbol":
                    return await ApplyRenameAsync(action, callbacks, cancellationToken).ConfigureAwait(false);

                default:
                    return new ToolResult { Ok = false, Output = "✗ 未知工具：" + action.Name };
            }
        }

        /// <summary>
        /// 多文件补丁：解析补丁串，对每一节按「准备 → 审批 → 落盘」走一遍，
        /// 最后把各节结果汇总成一条工具结果回灌给模型
        /// </summary>
        private async Task<ToolResult> ApplyPatchAsync(ToolAction action, AgentCallbacks callbacks, CancellationToken cancellationToken)
        {
            var raw = action.Get("patches");
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = action.Get("patch");
            }

            var sections = PatchParser.Parse(raw, out var error);
            if (sections == null)
            {
                return new ToolResult { Ok = false, Output = "补丁解析失败：" + error };
            }

            var summary = new StringBuilder();
            var applied = 0;

            foreach (var section in sections)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var prepared = section.IsNewFile
                    ? _workspace.PrepareWrite(section.Path, section.NewText)
                    : _workspace.PrepareReplace(section.Path, section.OldText, section.NewText);

                if (!prepared.Ok || prepared.Pending == null)
                {
                    summary.Append("✗ ").Append(section.Path).Append("：").Append(prepared.Output).Append('\n');
                    continue;
                }

                var pending = prepared.Pending;

                // 与单个写工具同一套判定：覆盖算「编辑」、新建算「新增」
                var auto = ApprovalCatalog.IsAutoApproved(_settings, pending.IsNewFile ? ApprovalCatalog.Create : ApprovalCatalog.Edit);

                var approved = auto || callbacks.ApproveAsync == null || await callbacks.ApproveAsync(pending).ConfigureAwait(false);

                if (!approved)
                {
                    summary.Append("✗ ").Append(section.Path).Append("：用户拒绝了这一节改动\n");
                    continue;
                }

                var blocked = CheckFileDirty(pending, callbacks);
                if (blocked != null)
                {
                    summary.Append("✗ ").Append(section.Path).Append("：").Append(blocked.Output).Append('\n');
                    continue;
                }

                var result = _workspace.Apply(pending);
                if (result.Ok)
                {
                    applied++;
                    _editApplied = true;
                    callbacks.OnEditApplied?.Invoke(pending);
                    _index.Invalidate();
                }

                summary.Append(result.Ok ? "✓ " : "✗ ").Append(section.Path).Append("：").Append(result.Output).Append('\n');
            }

            return new ToolResult
            {
                Ok = applied > 0,
                Output = "多文件补丁：应用了 " + applied + "/" + sections.Count + " 节\n" + summary,
            };
        }

        /// <summary>
        /// 整体替换任务清单，并把进度推给界面
        /// </summary>
        private ToolResult UpdateTodoList(ToolAction action, AgentCallbacks callbacks)
        {
            var raw = action.Get("todos");
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = action.Get("items");
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = action.Get("list");
            }

            var items = TodoListParser.Parse(raw);
            if (items.Count == 0)
            {
                return new ToolResult
                {
                    Ok = false,
                    Output = "清单是空的请每行一项、以 [-]/[ ]/[x] 开头，至少列出一项任务",
                };
            }

            // 每次都是整体替换：界面永远只显示「当前这一份清单」
            _todos = [.. items];

            var progress = TodoListParser.Summarize(_todos);
            callbacks.OnTodoUpdate?.Invoke(_todos, TodoListParser.Describe(progress));

            return new ToolResult
            {
                Ok = true,
                Output = "任务清单已更新（" + TodoListParser.Describe(progress) + "）：\n" + TodoListParser.Render(_todos),
            };
        }

        /// <summary>
        /// 把用户的问题交给界面，等一个回答再继续
        /// </summary>
        private async Task<ToolResult> AskFollowupAsync(ToolAction action, AgentCallbacks callbacks, CancellationToken cancellationToken)
        {
            var question = action.Get("question");
            if (string.IsNullOrWhiteSpace(question))
            {
                question = action.Get("text");
            }

            if (string.IsNullOrWhiteSpace(question))
            {
                return new ToolResult { Ok = false, Output = "question 为空：请写清楚要问用户什么" };
            }

            if (callbacks.AskQuestionAsync == null)
            {
                return new ToolResult
                {
                    Ok = false,
                    Output = "当前环境无法向用户提问请按最合理的默认方案继续，并在答复里说明你的假设",
                };
            }

            var ask = new AgentQuestion
            {
                Question = question,
                Options = TodoListParser.SplitOptions(action.Get("options")),
            };

            var answer = await callbacks.AskQuestionAsync(ask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(answer))
            {
                return new ToolResult
                {
                    Ok = false,
                    Output = "用户没有回答（跳过）请按最合理的默认方案继续，并在答复里说明你的假设",
                };
            }

            return new ToolResult { Ok = true, Output = "用户的回答：" + answer };
        }

        /// <summary>
        /// 拆一个子任务出去独立跑一轮，把它最后的自然语言结论带回来
        /// </summary>
        private async Task<ToolResult> RunSubTaskAsync(ToolAction action, AgentCallbacks callbacks, CancellationToken cancellationToken, bool suppressStreaming)
        {
            var task = action.Get("task");
            if (string.IsNullOrWhiteSpace(task))
            {
                task = action.Get("description");
            }

            if (string.IsNullOrWhiteSpace(task))
            {
                return new ToolResult { Ok = false, Output = "task 为空：请描述这个子任务要做什么" };
            }

            // 只允许拆一层：再往里拆会让「谁在等谁」变得看不懂，而且很容易空转
            if (SubTaskDepth >= 1)
            {
                return new ToolResult { Ok = false, Output = "子任务不能再嵌套子任务请直接在当前任务里完成这件事" };
            }

            var subTask = new AgentSubTask { Task = task, Context = action.Get("context") };
            callbacks.OnSubTask?.Invoke(subTask, true);

            var brief = new StringBuilder();
            brief.Append("这是一个父任务拆出来的子任务，请独立完成它\n");
            brief.Append("子任务：").Append(task).Append('\n');
            if (!string.IsNullOrWhiteSpace(subTask.Context))
            {
                brief.Append("补充上下文：").Append(subTask.Context).Append('\n');
            }

            brief.Append("做完之后用自然语言给出简明结论（做了什么、改了哪些文件、结果如何），不要再输出 action 块");

            var subTurns = new List<ChatTurn> { new ChatTurn("user", brief.ToString()) };
            var conclusion = new StringBuilder();

            var subCallbacks = new AgentCallbacks
            {
                // 并行子任务时不转发流式分片：两个模型同时流式会把正文 / 推理串成乱码
                OnDelta = suppressStreaming ? null : callbacks.OnDelta,
                OnTurnStart = suppressStreaming ? null : callbacks.OnTurnStart,
                OnReasoning = suppressStreaming ? null : callbacks.OnReasoning,
                OnToolResult = callbacks.OnToolResult,
                ApproveAsync = callbacks.ApproveAsync,
                ApproveActionAsync = callbacks.ApproveActionAsync,
                OnEditApplied = callbacks.OnEditApplied,
                OnStepLimit = callbacks.OnStepLimit,
                AskQuestionAsync = callbacks.AskQuestionAsync,
                OnTodoUpdate = callbacks.OnTodoUpdate,
                IsFileDirty = callbacks.IsFileDirty,

                // 子任务不再往下拆（上面已按深度挡住），所以不给它 RunSubTaskAsync

                // 「不带动作的那一轮」才是子任务的最终结论：中间那些带动作的轮次也会转发给界面显示，
                // 但只有终局答复会被当作结论带回父任务
                OnTurnComplete = (rawText, hasActions) =>
                {
                    if (!hasActions && !string.IsNullOrWhiteSpace(rawText))
                    {
                        conclusion.Clear();
                        conclusion.Append(ToolProtocol.StripActions(rawText));
                    }

                    callbacks.OnTurnComplete?.Invoke(rawText, hasActions);
                },
            };

            var subRunner = new AgentRunner(_backend, _workspace, _settings)
            {
                MaxSteps = MaxSteps,
                SubTaskDepth = SubTaskDepth + 1,
                ConversationId = ConversationId,
            };

            await subRunner.RunAsync(subTurns, subCallbacks, cancellationToken).ConfigureAwait(false);

            var result = conclusion.ToString().Trim();
            if (result.Length == 0)
            {
                result = "（子任务结束了，但没有给出文字结论）";
            }

            subTask.Result = result;
            callbacks.OnSubTask?.Invoke(subTask, false);

            return new ToolResult { Ok = true, Output = "子任务完成，结论如下：\n" + result };
        }

        /// <summary>
        /// 跨文件重命名（Roslyn 语法级）：逐文件「准备 → 审批 → 落盘」，最后汇总。
        /// 「找到哪些文件、改哪些位置、改成什么」在这里一次算清，避免模型逐文件调用 replace_in_file
        /// </summary>
        private async Task<ToolResult> ApplyRenameAsync(ToolAction action, AgentCallbacks callbacks, CancellationToken cancellationToken)
        {
            var symbol = (action.Get("symbol") ?? string.Empty).Trim();
            var newName = (action.Get("new_name") ?? action.Get("to")) ?? string.Empty;

            var pendings = _workspace.PrepareRename(symbol, newName, action.Get("glob"), 40, out var error);
            if (pendings == null)
            {
                return new ToolResult { Ok = false, Output = "重命名失败：" + (error ?? "未知错误") };
            }

            if (pendings.Count == 0)
            {
                return new ToolResult
                {
                    Ok = true,
                    Output = "没有找到可改名的引用：" + symbol + "（可能该符号不在工作区的 C# 文件里，或只出现在字符串 / 注释中）",
                };
            }

            var summary = new StringBuilder();
            var applied = 0;
            var auto = ApprovalCatalog.IsAutoApproved(_settings, ApprovalCatalog.Edit);

            foreach (var pending in pendings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var approved = auto || callbacks.ApproveAsync == null || await callbacks.ApproveAsync(pending).ConfigureAwait(false);

                if (!approved)
                {
                    summary.Append("✗ ").Append(pending.RelativePath).Append("：用户拒绝了这一处改名\n");
                    continue;
                }

                var blocked = CheckFileDirty(pending, callbacks);
                if (blocked != null)
                {
                    summary.Append("✗ ").Append(pending.RelativePath).Append("：").Append(blocked.Output).Append('\n');
                    continue;
                }

                var result = _workspace.Apply(pending);
                if (result.Ok)
                {
                    applied++;
                    _editApplied = true;
                    callbacks.OnEditApplied?.Invoke(pending);
                    _index.Invalidate();
                }

                summary.Append(result.Ok ? "✓ " : "✗ ").Append(pending.RelativePath).Append("：").Append(result.Output).Append('\n');
            }

            return new ToolResult
            {
                Ok = applied > 0,
                Output = "重命名 " + symbol + " → " + newName.Trim() + "：改动了 " + applied + "/" + pendings.Count + " 个文件\n" + summary,
            };
        }
    }
}
