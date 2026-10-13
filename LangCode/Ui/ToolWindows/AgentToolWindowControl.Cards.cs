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
            if (message == null || action == null)
            {
                return;
            }

            if (action.Name == "write_file" || action.Name == "replace_in_file")
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

                live.Title = result.Ok ? "Run Command" : "Run Command（失败）";
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

            // 「读取文件 / 列目录 / 搜索」这类**浏览型**结果默认折叠：
            // 它们往往很长（整个文件、整串目录、一堆匹配行），摊开会把对话主线挤走；
            // 需要看的时候点一下箭头展开即可其它工具结果仍遵循「长文默认折叠」设置
            var isBrowsing = action.Name == "read_file"
                || action.Name == "list_files"
                || action.Name == "search_text"
                || action.Name == "semantic_search"
                || action.Name == "list_symbols"
                || action.Name == "find_references"
                || action.Name == "get_type_hierarchy"
                || action.Name == "find_implementations";

            message.Blocks.Add(new ChatBlock
            {
                Kind = result.Ok ? ChatBlockKind.CodeCard : ChatBlockKind.Note,
                Title = result.Ok ? writer : writer + "（失败）",
                Path = path,
                LineHint = ParseLine(action.Get("start_line")),
                Text = result.Output ?? string.Empty,
                IsBodyVisible = isBrowsing ? false : !_settings.CollapseLongText,
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
            return Dispatcher.InvokeAsync(() =>
            {
                var card = new ChatBlock
                {
                    Kind = ChatBlockKind.Approval,
                    Title = ToolTitle(action.Name) + " 需要确认",
                    Path = ToolPath(action),
                    Text = result?.Output ?? string.Empty,
                    IsPendingApproval = true,
                };

                _askedActions.Add(action);
                message.Blocks.Add(card);

                return WaitForApproval(card);
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
                    Title = "Task Progress",
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
            return Dispatcher.InvokeAsync(() =>
            {
                var card = new ChatBlock
                {
                    Kind = ChatBlockKind.Question,
                    Title = "Waiting for your answer",
                    Text = question?.Question ?? string.Empty,
                    Options = question?.Options ?? [],
                    IsPendingQuestion = true
                };
                message?.Blocks.Add(card);

                _pendingQuestionCard = card;
                _pendingQuestion = new TaskCompletionSource<string>();

                RefreshRequestBar();
                ScrollToEnd();

                return _pendingQuestion.Task;
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
                    Title = "Sub Task",
                    Text = subTask.Task,
                    StatusText = "进行中",
                    IsSubTaskRunning = true,
                    IsBodyVisible = true,
                };

                _subTaskCard = card;
                message.Blocks.Add(card);

                // 从这一刻起，子任务内部产生的卡片都缩进一格（父任务的卡片不受影响）
                _subTaskVisualDepth = 1;
                message.CurrentDepth = 1;
            }
            else
            {
                _subTaskVisualDepth = 0;
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

        private static string ToolTitle(string toolName)
        {
            switch (toolName)
            {
                case "list_files": return "List Files";
                case "read_file": return "Read File";
                case "search_text": return "Search";
                case "write_file": return "Create File";
                case "replace_in_file": return "Edit File";
                case "run_command": return "Run Command";
                case "open_url": return "Open URL";
                case "mcp_call": return "MCP Call";
                case "list_symbols": return "List Symbols";
                case "find_references": return "Find References";
                case "semantic_search": return "Semantic Search";
                case "get_type_hierarchy": return "Type Hierarchy";
                case "find_implementations": return "Find Implementations";
                case "edit_symbol": return "Edit Symbol";
                case "apply_patch": return "Apply Patch";
                case "rename_symbol": return "Rename Symbol";
                case "run_tests": return "Run Tests";
                case "browser_action": return "Browser";
                case "goto_definition": return "Go To Definition";
                case "debug_inspect": return "Debug Inspect";
                case "debug_control": return "Debug Control";
                default: return toolName ?? string.Empty;
            }
        }

        private static string ToolPath(ToolAction action)
        {
            if (action == null)
            {
                return string.Empty;
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

