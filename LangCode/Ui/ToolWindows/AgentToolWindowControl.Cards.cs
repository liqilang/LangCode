using LangCode.Agent;
using LangCode.Domain.Chat;
using LangCode.Workspace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：工具卡片 / 待办 / 提问 / 子任务类定义与字段见 AgentToolWindowControl.xaml.cs
    /// </summary>
    public partial class AgentToolWindowControl
    {
        // ============================================================
        // 卡片动作（作用于被点击按钮所在的内容块）
        // ============================================================
        private static ChatBlock BlockOf(object sender)
        {
            return (sender as FrameworkElement)?.DataContext as ChatBlock;
        }

        /// <summary>
        /// 点「思考中」这一行：展开 / 收起这一步的思考
        /// </summary>
        private void ThinkingHeader_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatBlock block)
            {
                block.IsBodyVisible = !block.IsBodyVisible;
                e.Handled = true;
            }
        }

        /// <summary>
        /// 在当前助手的卡片流末尾追加一条提示
        /// </summary>
        private void AddNote(string text)
        {
            var assistant = _session.Messages.LastOrDefault(m => !m.IsUser);
            if (assistant == null)
            {
                return;
            }

            assistant.Blocks.Add(new ChatBlock { Kind = ChatBlockKind.Note, Text = text });
            ScrollToEnd();
        }

        /// <summary>
        /// 把一次工具结果渲染成卡片（写入类 / 已出过确认卡的动作跳过）
        /// </summary>
        private void AddToolCard(ChatMessage message, ToolAction action, ToolResult result)
        {
            // 卡片会触发「跟随打开」等 DTE 调用，只能在 UI 线程执行
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (message == null || action == null)
            {
                return;
            }

            // 改动类工具不再出通用的「工具结果」卡：成功时差异卡（审批卡 / 落盘后补的
            // 「已应用」卡，见 RecordAppliedEditCard）已经记下了这次改动，重复出卡等于同一件事显示两遍。
            // 但**失败**（例如 old_text 不唯一）确实需要一条痕迹 —— 除非刚才那张审批卡
            // 已经写明结论（已拒绝），那就没必要再补一条失败提示
            if (IsEditTool(action.Name) &&
                (result.Ok || HasDecidedDiffCard(message, EditPathOf(action))))
            {
                return;
            }

            // 长任务编排三件套都有自己的专用卡片（进度卡 / 提问卡 / 子任务卡），
            // 不再额外出一张通用的「工具结果」卡，否则同一件事会显示两遍
            if (action.Name == "update_todo_list" ||
                action.Name == "ask_followup_question" ||
                action.Name == "new_task")
            {
                return;
            }

            // 命令：流式输出已经写在「命令输出」卡里了，这里只把最终输出与退出码补上，
            // 不再重复出一张卡
            if (action.Name == "run_command" && _liveCommandCard != null)
            {
                FlushCommandOutput();

                var live = _liveCommandCard;
                _liveCommandCard = null;

                live.Title = result.Ok ? "运行命令" : "运行命令（失败）";
                live.Text = result.Output ?? string.Empty;
                live.IsBodyVisible = true;

                UpdateTokens();
                ScrollToEnd();
                return;
            }

            // 刚刚用「操作确认卡」问过的那一次，不再重复出一张结果卡
            if (_askedActions.Remove(action))
            {
                return;
            }

            var writer = ToolTitle(action.Name);
            var path = ToolPath(action);

            // 读文件：内容**不在对话里展开**，卡片只当一条记录 + 入口 ——
            // 点卡片（或文件名）直接在 VS 里打开，比在工具窗口里读一遍强
            var isFileCard = action.Name == "read_file";

            // 「列目录 / 搜索」这类**浏览型**结果默认折叠：
            // 它们往往很长（整串目录、一堆匹配行），摊开会把对话主线挤走；
            // 需要看的时候点一下箭头展开即可其它工具结果仍遵循「长文默认折叠」设置
            var isBrowsing = action.Name == "list_files"
                || action.Name == "list_dir"
                || action.Name == "read_multiple_files"
                || action.Name == "search_text"
                || action.Name == "semantic_search"
                || action.Name == "list_symbols"
                || action.Name == "find_references"
                || action.Name == "get_type_hierarchy"
                || action.Name == "find_implementations";

            message.Blocks.Add(new ChatBlock
            {
                Kind = result.Ok
                    ? (isFileCard ? ChatBlockKind.FileCard : ChatBlockKind.CodeCard)
                    : ChatBlockKind.Note,
                Title = result.Ok ? writer : writer + "（失败）",
                Path = path,
                LineHint = ParseLine(action.Get("start_line")),
                Text = result.Output ?? string.Empty,
                IsBodyVisible = isFileCard || isBrowsing ? false : !_settings.CollapseLongText,
            });

            // 「跟随显示」：读文件时把该文件在编辑器里打开（用户能立刻看到 AI 在看什么）
            if (result.Ok && action.Name == "read_file")
            {
                FollowOpen(path, ParseLine(action.Get("start_line")));
            }

            UpdateTokens();
            ScrollToEnd();
        }

        /// <summary>
        /// 无 diff 动作的确认卡（读取等类别被关掉「自动批准」时才会出现）
        /// </summary>
        private Task<bool> ApproveActionAsync(ChatMessage message, ToolAction action, ToolResult result)
        {
            return Dispatcher.InvokeAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .SwitchToMainThreadAsync();

                var card = new ChatBlock
                {
                    Kind = ChatBlockKind.Approval,
                    Title = ToolTitle(action.Name) + " 需要确认",
                    Path = ToolPath(action),
                    Text = result?.Output ?? string.Empty,
                    IsPendingApproval = true,
                    ApprovalTool = action.Name,
                    ApprovalCommand = string.Equals(action.Name, "run_command", StringComparison.OrdinalIgnoreCase)
                        ? action.Get("command")
                        : string.Empty,
                };

                _askedActions.Add(action);
                message.Blocks.Add(card);

                return await WaitForApprovalAsync(card);
            }).Task.Unwrap();
        }

        /// <summary>
        /// 把窗口顶部的「任务进度」条同步到当前清单卡
        /// </summary>
        private void SyncTodoPanel()
        {
            if (TasksBar == null)
            {
                return;
            }

            var hasTodos = _todoCard != null && _todoCard.HasTodos;

            // 没有清单时整条收起，不占一行高度；有清单时把明细挂到当前这张卡上
            TasksBar.Visibility = hasTodos ? Visibility.Visible : Visibility.Collapsed;
            TodoPanel.DataContext = _todoCard;
            TodoPanel.Visibility = hasTodos ? Visibility.Visible : Visibility.Collapsed;

            if (!hasTodos)
            {
                return;
            }

            // 顶部「任务清单」进度条与「上下文」条同一套样式：按完成百分比分配宽度
            var percent = _todoCard.ProgressPercent;
            TasksFillCol.Width = new GridLength(percent, GridUnitType.Star);
            TasksRestCol.Width = new GridLength(Math.Max(0, 100 - percent), GridUnitType.Star);
            TasksCount.Text = _todoCard.CountLabel;
            TasksPct.Text = percent + "%";
        }

        /// <summary>
        /// 点代码卡（命令输出 / 读文件 / 列目录…）的头部任意位置：展开 / 收起这张卡片。
        /// 不用非得去点右边那个小箭头 —— 箭头设成 <c>IsHitTestVisible=False</c>，
        /// 命中统一交给这一行，一次点击只会被处理一遍
        /// </summary>
        private void CodeCardHeader_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatBlock block)
            {
                block.IsBodyVisible = !block.IsBodyVisible;
                e.Handled = true;
            }
        }

        /// <summary>
        /// 点顶部状态条的概览行：展开 / 收起用量明细
        /// </summary>
        private void StatusHeader_Click(object sender, RoutedEventArgs e)
        {
            RotateChevron(StatusChevron, (sender as ToggleButton)?.IsChecked == true);
        }

        /// <summary>
        /// 点顶部「任务清单」头：展开 / 收起清单明细（默认折叠，进度条常驻）
        /// </summary>
        private void TasksHeader_Click(object sender, RoutedEventArgs e)
        {
            RotateChevron(TasksChevron, (sender as ToggleButton)?.IsChecked == true);
        }

        /// <summary>
        /// 点输入框上方「本次变更」头：展开 / 收起本次会话改动过的文件明细
        /// </summary>
        private void SummaryHeader_Click(object sender, RoutedEventArgs e)
        {
            RotateChevron(SummaryChevron, (sender as ToggleButton)?.IsChecked == true);
        }

        /// <summary>
        /// 折叠箭头：展开时转 90°（chevron-right → 朝下）
        /// </summary>
        private static void RotateChevron(FrameworkElement chevron, bool open)
        {
            if (chevron != null)
            {
                chevron.RenderTransform = new RotateTransform(open ? 90 : 0);
            }
        }

        /// <summary>
        /// 从当前会话的消息里找最后一份任务清单（载入历史会话后恢复顶部进度条）
        /// </summary>
        private void SyncTodoPanelFromMessages()
        {
            _todoCard = _session.Messages
                .SelectMany(message => message.Blocks)
                .LastOrDefault(block => block.Kind == ChatBlockKind.Todo);

            SyncTodoPanel();
        }

        /// <summary>
        /// 更新任务进度卡（长任务编排）
        /// </summary>
        private void UpdateTodoCard(ChatMessage message, IList<TodoItem> items, string progress)
        {
            if (message == null)
            {
                return;
            }

            if (_todoCard == null)
            {
                _todoCard = new ChatBlock
                {
                    Kind = ChatBlockKind.Todo,
                    Title = "任务进度",
                    IsBodyVisible = true,
                };

                message.Blocks.Add(_todoCard);
            }

            _todoCard.Todos.Clear();
            if (items != null)
            {
                foreach (var item in items)
                {
                    _todoCard.Todos.Add(item);
                }
            }

            _todoCard.HasTodos = _todoCard.Todos.Count > 0;
            _todoCard.ProgressText = progress ?? string.Empty;

            // 顶部常驻进度条与卡片流里的那份保持同一数据源
            SyncTodoPanel();

            UpdateTokens();
            ScrollToEnd();
        }

        /// <summary>
        /// 把模型的问题渲染成一张提问卡，等用户回答
        /// </summary>
        private Task<string> AskUserAsync(ChatMessage message, AgentQuestion question)
        {
            return Dispatcher.InvokeAsync(async () =>
            {
                var card = new ChatBlock
                {
                    Kind = ChatBlockKind.Question,
                    Title = "等待你的回答",
                    Text = question?.Question ?? string.Empty,
                    Options = question?.Options ?? [],

                    // 先不点亮选项：并行子任务可能同时提问，等拿到信号量后再成为「当前问题」
                    IsPendingQuestion = false
                };
                message?.Blocks.Add(card);

                await _questionGate.WaitAsync().ConfigureAwait(true);
                try
                {
                    if (_questionCancelled)
                    {
                        card.IsPendingQuestion = false;
                        card.IsAnswered = true;
                        card.AnswerIsSkipped = true;
                        card.AnswerText = "已取消";
                        card.StatusText = "已取消";
                        return string.Empty;
                    }

                    _pendingQuestionCard = card;
                    _pendingQuestion = new TaskCompletionSource<string>();
                    card.IsPendingQuestion = true;

                    RefreshRequestBar();
                    ScrollToEnd();

                    // 这个 TCS 由界面线程（用户点击 / 回车）完成，属于有意为之的跨上下文等待
#pragma warning disable VSTHRD003
                    return await _pendingQuestion.Task.ConfigureAwait(true);
#pragma warning restore VSTHRD003
                }
                finally
                {
                    _questionGate.Release();
                }
            }).Task.Unwrap();
        }

        /// <summary>
        /// 回答模型的问题（空字符串 = 跳过）
        /// </summary>
        private void AnswerQuestion(string answer)
        {
            var pending = _pendingQuestion;
            var card = _pendingQuestionCard;

            _pendingQuestion = null;
            _pendingQuestionCard = null;

            var text = (answer ?? string.Empty).Trim();

            if (card != null)
            {
                card.IsPendingQuestion = false;
                card.IsAnswered = true;
                card.AnswerIsSkipped = text.Length == 0;
                card.AnswerText = text.Length > 0
                    ? text
                    : "已跳过，模型将按最合理的默认方案继续";
                card.StatusText = text.Length > 0 ? "已回答" : "已跳过";
            }

            RefreshRequestBar();
            ScrollToEnd();

            // 最后再放行 Agent —— 它可能立刻往界面里加新卡片，先刷新避免「按钮没了但还在等」的错觉
            pending?.TrySetResult(text);
        }

        /// <summary>
        /// 点提问卡上的备选答案：直接作为回答
        /// </summary>
        private void QuestionOption_Click(object sender, RoutedEventArgs e)
        {
            // 只有「当前这一个未决问题」的按钮算数；已回答的卡片按钮本就不可见（ShowsOptions）
            if (_pendingQuestion == null)
            {
                return;
            }

            AnswerQuestion((sender as FrameworkElement)?.Tag as string ?? string.Empty);
        }

        /// <summary>
        /// 把未回答的提问按「跳过」收尾（置空结果，让 Agent 立刻继续）
        /// </summary>
        private void CancelPendingQuestion(string status)
        {
            _questionCancelled = true;

            var pending = _pendingQuestion;
            var card = _pendingQuestionCard;

            _pendingQuestion = null;
            _pendingQuestionCard = null;

            if (card != null)
            {
                card.IsPendingQuestion = false;
                card.IsAnswered = true;
                card.AnswerIsSkipped = true;
                card.AnswerText = status ?? string.Empty;
                card.StatusText = status ?? string.Empty;
            }

            RefreshRequestBar();
            pending?.TrySetResult(string.Empty);
        }

        /// <summary>
        /// 子任务卡：开始 / 结束各更新一次（结束回填结论文本）
        /// </summary>
        private void ShowSubTask(ChatMessage message, AgentSubTask subTask, bool started)
        {
            if (message == null || subTask == null)
            {
                return;
            }

            if (started)
            {
                var card = new ChatBlock
                {
                    Kind = ChatBlockKind.SubTask,
                    Title = "子任务",
                    Text = subTask.Task,
                    StatusText = "进行中",
                    IsSubTaskRunning = true,
                    IsBodyVisible = true,
                };

                _subTaskCard = card;
                message.Blocks.Add(card);

                // 从这一刻起，子任务内部产生的卡片都缩进一格（父任务的卡片不受影响）
                message.CurrentDepth = 1;
            }
            else
            {
                message.CurrentDepth = 0;

                var card = _subTaskCard;
                if (card != null)
                {
                    card.IsSubTaskRunning = false;
                    card.StatusText = "已完成";

                    // 结论附在子任务描述下面：一眼看出这个子任务做了什么
                    card.Text = subTask.Task + "\n\n结论：" + (subTask.Result ?? string.Empty);
                }

                _subTaskCard = null;
            }

            UpdateTokens();
            ScrollToEnd();
        }

        /// <summary>
        /// 改动类工具（写文件 / 替换 / 结构化编辑 / 补丁 / 重命名）：
        /// 它们在对话里的「记录」由差异卡承担，不再额外出一张通用的工具结果卡
        /// </summary>
        private static bool IsEditTool(string toolName)
        {
            switch (toolName)
            {
                case "write_file":
                case "replace_in_file":
                case "edit_symbol":
                case "apply_patch":
                case "rename_symbol":
                case "delete_file":
                case "move_file":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 改动类工具在对话里关联的「被改文件」路径（移动用源路径，与差异卡的 Path 一致）
        /// </summary>
        private static string EditPathOf(ToolAction action)
        {
            if (action == null)
            {
                return string.Empty;
            }

            return action.Name == "move_file" ? action.Get("from") : action.Get("path");
        }

        /// <summary>
        /// 差异卡标题：区分新增 / 编辑 / 删除 / 移动
        /// </summary>
        private static string EditCardTitle(PendingEdit edit)
        {
            if (edit == null)
            {
                return "编辑文件";
            }

            if (edit.IsDelete)
            {
                return "删除文件";
            }

            if (edit.IsMove)
            {
                return "移动文件 → " + edit.MoveToPath;
            }

            return edit.IsNewFile ? "新增文件" : "编辑文件";
        }

        /// <summary>
        /// 这条消息里是否已经有同一文件的差异卡，并且结论已出（已应用 / 已拒绝）
        /// </summary>
        private static bool HasDecidedDiffCard(ChatMessage message, string path)
        {
            if (message == null || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            // 卡片的 Path 是工作区归一化后的相对路径（'/' 分隔），动作参数里可能写成 '\\'
            var normalized = path.Replace('\\', '/');

            return message.Blocks.Any(block =>
                block.Kind == ChatBlockKind.DiffCard &&
                (block.IsAccepted || block.IsRejected) &&
                string.Equals(
                    (block.Path ?? string.Empty).Replace('\\', '/'),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 把一次「已落盘」的改动记进对话。
        /// 自动批准时根本不走审批卡（<see cref="AgentRunner"/> 会直接落盘），
        /// 于是出现「文件确实被改了，但对话里看不到编辑文件这一条」——
        /// 这里按「是否已经有对应的审批卡」补一张「已应用」的编辑卡，点它就开 VS 差异对比
        /// </summary>
        private static void RecordAppliedEditCard(ChatMessage assistant, PendingEdit edit)
        {
            if (assistant == null || edit == null || string.IsNullOrWhiteSpace(edit.RelativePath))
            {
                return;
            }

            // ① 审批通过的路径：卡片就是按 edit.Diff 建的（同一个列表实例）→ 不重复出卡，
            //    只把「改动前后全文」补上（点卡片开差异对比要用）
            var existing = assistant.Blocks.LastOrDefault(block =>
                block.Kind == ChatBlockKind.DiffCard && ReferenceEquals(block.Lines, edit.Diff));

            if (existing != null)
            {
                existing.BeforeText = edit.OriginalText ?? string.Empty;
                existing.AfterText = edit.NewText ?? string.Empty;
                return;
            }

            // ② 自动批准的路径：对话里没有任何痕迹 → 补一张「已应用」的编辑卡
            assistant.Blocks.Add(new ChatBlock
            {
                Kind = ChatBlockKind.DiffCard,
                Title = EditCardTitle(edit),
                Path = edit.RelativePath,
                Lines = edit.Diff,
                Text = edit.IsMove ? "移动 " + edit.RelativePath + " → " + edit.MoveToPath : null,
                IsAccepted = true,
                StatusText = "已应用",
                IsBodyVisible = false,
                BeforeText = edit.OriginalText ?? string.Empty,
                AfterText = edit.NewText ?? string.Empty,
            });
        }

        /// <summary>
        /// 工具名 → 卡片标题（界面统一用中文）
        /// </summary>
        private static string ToolTitle(string toolName)
        {
            switch (toolName)
            {
                case "list_files": return "列目录";
                case "list_dir": return "目录树";
                case "read_file": return "读文件";
                case "read_multiple_files": return "批量读文件";
                case "search_text": return "搜索";
                case "write_file": return "新增文件";
                case "replace_in_file": return "编辑文件";
                case "delete_file": return "删除文件";
                case "move_file": return "移动文件";
                case "run_command": return "运行命令";
                case "open_url": return "打开链接";
                case "mcp_call": return "MCP 调用";
                case "list_symbols": return "列出符号";
                case "find_references": return "查找引用";
                case "semantic_search": return "混合检索";
                case "get_type_hierarchy": return "类型层次";
                case "find_implementations": return "查找实现";
                case "edit_symbol": return "编辑符号";
                case "apply_patch": return "应用补丁";
                case "rename_symbol": return "重命名符号";
                case "run_tests": return "运行测试";
                case "browser_action": return "浏览器操作";
                case "goto_definition": return "转到定义";
                case "debug_inspect": return "调试检查";
                case "debug_control": return "调试控制";
                default: return toolName ?? string.Empty;
            }
        }

        private static string ToolPath(ToolAction action)
        {
            if (action == null)
            {
                return string.Empty;
            }

            // 移动：副标题显示「源 → 目标」，比单独一个路径更能说明这次调用做了什么
            if (action.Name == "move_file")
            {
                var moveFrom = action.Get("from");
                var moveTo = action.Get("to");
                if (!string.IsNullOrWhiteSpace(moveFrom))
                {
                    return moveFrom + (string.IsNullOrWhiteSpace(moveTo) ? string.Empty : " → " + moveTo);
                }
            }

            // 批量读取：副标题显示文件数量，具体路径在展开的正文里
            if (action.Name == "read_multiple_files")
            {
                var paths = action.Get("paths") ?? string.Empty;
                var count = paths.Split(new[] { '\n', '\r', ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries).Length;
                return count > 0 ? count + " 个文件" : string.Empty;
            }

            var path = action.Get("path");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = action.Get("glob");
            }

            // 命令 / 网页 / MCP：把「做了什么」放在卡片副标题上，一眼能看出这张卡对应哪次调用
            if (string.IsNullOrWhiteSpace(path))
            {
                path = action.Get("command");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                path = action.Get("url");
            }

            // 重命名：副标题显示「旧名 → 新名」，比路径更能说明这次调用做了什么
            if (string.IsNullOrWhiteSpace(path))
            {
                var symbol = action.Get("symbol");
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    var newName = action.Get("new_name");
                    path = string.IsNullOrWhiteSpace(newName) ? symbol : symbol + " → " + newName;
                }
            }

            // 调试器 / 浏览器：副标题显示具体动作（必要时带上表达式）
            if (string.IsNullOrWhiteSpace(path))
            {
                var verb = action.Get("action");
                if (!string.IsNullOrWhiteSpace(verb) &&
                    (action.Name == "debug_inspect" || action.Name == "debug_control" || action.Name == "browser_action"))
                {
                    path = verb;
                    var expression = action.Get("expression");
                    if (!string.IsNullOrWhiteSpace(expression))
                    {
                        path += " · " + expression;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                var server = action.Get("server");
                var tool = action.Get("tool");
                path = string.IsNullOrWhiteSpace(server) ? tool : server + " · " + tool;
            }

            return path ?? string.Empty;
        }

    }
}

