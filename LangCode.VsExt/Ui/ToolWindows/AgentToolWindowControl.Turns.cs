using LangCode.VsExt.Agent;
using LangCode.VsExt.Chat;
using LangCode.VsExt.Domain.Chat;
using LangCode.VsExt.VsIntegration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LangCode.VsExt.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：发送流程与 Agent 调用类定义与字段见 AgentToolWindowControl.xaml.cs
    /// </summary>
    public partial class AgentToolWindowControl
    {
        /// <summary>
        /// 发送当前输入；有工作区时走 Agent 工具循环，否则单轮对话
        /// </summary>
        private async Task SendCurrentAsync()
        {
            // 本方法从 UI 事件启动，但 await 之后可能 resume 到线程池；
            // 后续要碰控件、DTE，因此先切回主线程（VSTHRD109 要求的写法）
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // 正文 + 待发送的提及芯片（芯片在界面上只显示名称，这里还原成 @路径）
            var text = ComposeInputText();
            if ((text.Length == 0 && _pendingImages.Count == 0) || _session.IsBusy)
            {
                return;
            }

            // 工作区是「懒定位」的：工具窗口可能在解决方案打开之前就已创建，
            // 用户也可能先开窗口再打开文件夹 —— 每次发送都再确认一次，取到就自动升级为 Agent 模式
            EnsureWorkspace();

            Input.Text = string.Empty;
            TakePendingMentions();
            ClosePopups();

            // 新一轮开始：上一轮的「可继续 / 可恢复 / 可重试 / 可撤销」全部作废
            // （编辑重发走 SaveEditedAsync，不经过这里）
            ResetTurnFlags();
            _turnEdits.Clear();

            // 取走待发送图片（附件条随之清空）：它们随这条用户消息一起发给视觉模型
            var images = TakePendingImages();
            var userMessage = _session.AddUserMessage(text, images);
            UpdateSendState();

            // 用户主动发送：这一轮采集「自动上下文」（当前文件 / 选中代码 / 编译诊断）
            await RunTurnAsync(userMessage, includeAutoContext: true).ConfigureAwait(true);
        }

        /// <summary>
        /// 跑一轮：建助手消息 → 调后端 / Agent 循环 → 收尾 → 队列非空就接着跑下一条
        /// </summary>
        private async Task RunTurnAsync(ChatMessage userMessage, string resumeHint = null, bool includeAutoContext = false)
        {
            // 新一轮 = 新的一份任务清单 / 新的子任务：上一轮的进度卡与子任务卡都不再复用
            _todoCard = null;
            _subTaskCard = null;
            SyncTodoPanel();

            // 命令输出的流式卡也一并作废
            _liveCommandCard = null;
            lock (_commandGate)
            {
                _commandBuffer.Clear();
                _commandTarget = null;
                _commandFlushScheduled = false;
            }

            _commandTimer?.Stop();

            // 用户刚发了消息：无论之前在看哪段历史，都跟回最新消息
            _followTail = true;

            ScrollToEnd();
            UpdateTokens();

            var assistant = _session.BeginAssistantMessage(_settings.Model);

            // 拿不到工作区就明确告诉用户「本轮是纯对话」，而不是静默地什么都不做
            if (_workspace == null && !_workspaceNoticeShown)
            {
                _workspaceNoticeShown = true;
                assistant.Blocks.Add(new ChatBlock
                {
                    Kind = ChatBlockKind.Note,
                    Text = "未检测到 VS 工作区（没有打开解决方案或文件夹），本轮按纯对话处理，不会读写文件" +
                           "在 VS 里打开解决方案/文件夹后再发一次即可启用文件工具",
                });
            }

            _session.IsBusy = true;
            UpdateSendState();
            ScrollToEnd();

            _cts = new CancellationTokenSource();
            var backend = ChatBackendFactory.Create(_settings);
            var turns = _session.BuildTurns();

            // 自动上下文：把「当前文件 / 选中代码 / 编译诊断」附在这一轮请求里
            // 只在用户主动发送时采集（续跑 / 排队不重复采集，避免噪声叠加）
            if (includeAutoContext)
            {
                var autoContext = BuildAutoContext();
                if (!string.IsNullOrWhiteSpace(autoContext))
                {
                    turns.Add(new ChatTurn("user", autoContext));
                }
            }

            // 「继续 / 恢复 / 重试」：提示只发给模型，不写进对话历史（历史里不该有用户没说过的话）
            if (resumeHint != null)
            {
                turns.Add(new ChatTurn("user", resumeHint));
            }

            try
            {
                if (_workspace == null)
                {
                    await RunPlainAsync(backend, turns, assistant, _cts.Token).ConfigureAwait(true);
                }
                else
                {
                    await RunAgentAsync(backend, turns, assistant, _cts.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
                // 用户自己按的「取消」：保留已产出的内容，并给一个「恢复任务」的出口
                // 这里刻意不置 _turnCanRetry —— 没出错，叫「重试」会误导
                _turnInterrupted = true;
            }
            catch (Exception ex)
            {
                // 真抛异常了才叫「重试」
                _turnCanRetry = true;
                Dispatcher.Invoke(new Action(() => ChatSession.AppendText(assistant, "\n\n⚠ " + ex.Message)));
            }
            finally
            {
                ChatSession.Complete(assistant);
                _session.IsBusy = false;

                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }

                // 未决的审批在中断后一律视为拒绝，避免 Agent 线程永久等待
                CancelPendingApprovals("已中断");

                // 未决的提问同理：不置空的话 Agent 会永远 await 在那个问题上
                CancelPendingQuestion("已中断");

                // 流式尾巴落干净，再收尾（否则最后几个字会丢）；
                // 最后一步的思考也收起来，保持「一块一块」的读法
                StopStreaming();
                CloseStepThinking();

                UpdateSendState();
                UpdateTokens();
                ScrollToEnd();

                _session.SaveCurrent(_settings);

                // 长任务（改过文件 / 撞到上限 / 出错 / 产生多张卡片）结束时提示一声：
                // 这几分钟里用户多半已经切到别的窗口去了
                if (_settings.DesktopNotifications &&
                    (_turnEdits.Count > 0 || _turnCanContinue || _turnCanRetry ||
                     (assistant != null && assistant.Blocks.Count > 3)))
                {
                    VsNotifier.Notify(
                        _turnCanRetry ? "LangCode：任务出错，可重试"
                        : _turnInterrupted ? "LangCode：任务已中断"
                        : "LangCode：任务完成",
                        beep: !_turnInterrupted);
                }
            }

            // 排队中的任务：当前这轮结束后自动接着跑（消息已经在列表里标着「排队中」）
            if (_queue.Count > 0)
            {
                await RunQueuedAsync(_queue.Dequeue()).ConfigureAwait(true);
            }
        }

        /// <summary>
        /// 跑队列里的下一条消息早就进了列表，这里只把它从「排队中」改成正式执行
        /// </summary>
        private async Task RunQueuedAsync(QueuedTask next)
        {
            if (next?.Message == null)
            {
                return;
            }

            next.Message.IsQueued = false;

            ResetTurnFlags();
            _turnEdits.Clear();
            RefreshRequestBar();

            await RunTurnAsync(next.Message).ConfigureAwait(true);
        }

        /// <summary>
        /// 无工作区：单轮对话
        /// </summary>
        private async Task RunPlainAsync(IChatBackend backend, IList<ChatTurn> turns, ChatMessage assistant, CancellationToken token)
        {
            var request = new ChatRequest
            {
                Model = _settings.Model,
                SystemPrompt = "你是 LangCode，一个集成在 Visual Studio 里的编程助手回答简洁、可执行，代码优先",
                Turns = turns,
                Temperature = _settings.Temperature,
                TopP = _settings.TopP,
                MaxTokens = _settings.MaxOutputTokens,

                // 各厂商的模型高级选项（默认留空 = 不带这些字段）
                ReasoningEffort = _settings.ReasoningEffort,
                Verbosity = _settings.Verbosity,
                ThinkingBudgetTokens = _settings.ThinkingBudgetTokens,
                OnReasoning = ReasoningSink(assistant),

                // 自定义网关的私有参数（例：vLLM/Qwen3 开思考）
                ExtraBodyJson = _settings.GetProviderOption("caps.extraBody") ?? string.Empty,
            };

            _turnText = null;

            await backend
                .SendAsync(request, delta => AppendStreaming(assistant, delta), token)
                .ConfigureAwait(true);
        }

        /// <summary>
        /// 有工作区：Agent 工具循环（读/搜/写/改 + 审批）
        /// </summary>
        private async Task RunAgentAsync(IChatBackend backend, IList<ChatTurn> turns, ChatMessage assistant, CancellationToken token)
        {
            var runner = new AgentRunner(backend, _workspace, _settings);

            var callbacks = new AgentCallbacks
            {
                // 每轮请求开始前：清掉推理块（多步推理混在一个块里会看不懂），并重置本轮正文块
                OnTurnStart = () => Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        // 每一步新起一块思考：先把上一步那块收起来，
                        // 下一次推理增量会自动在**末尾**新建一块（不再共用同一块）
                        CloseStepThinking();
                        _turnText = null;
                    }),
                    DispatcherPriority.Background),

                OnReasoning = ReasoningSink(assistant),

                // 流式增量走合流：一帧一次，不再每个分片都重排一遍界面与滚动
                OnDelta = delta => AppendStreaming(assistant, delta),

                // 一轮结束：以**本轮完整输出**定稿（流式累积不作数），并去掉动作块
                OnTurnComplete = (raw, hasActions) => Dispatcher.BeginInvoke(
                    new Action(() => FinalizeTurn(assistant, raw, hasActions)),
                    DispatcherPriority.Background),

                OnToolResult = (action, result) => Dispatcher.BeginInvoke(
                    new Action(() => AddToolCard(assistant, action, result)),
                    DispatcherPriority.Background),

                // 达到最大步数：必须给用户一句明确交代，否则看起来就是「说着说着没了」
                OnStepLimit = steps => Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        FinalizeTurn(assistant, null, false);
                        assistant.Blocks.Add(new ChatBlock
                        {
                            Kind = ChatBlockKind.Note,
                            Text = "已达到本轮最大步数（" + steps + " 步），任务在这里暂停" +
                                   "输入框上方会出现「继续任务」，点它我就接着做；也可以补充说明后重发",
                        });

                        // 任务没做完 → 请求条上出现「继续任务」（这条标记是它会出现的唯一原因）
                        _turnCanContinue = true;

                        UpdateTokens();
                        RefreshRequestBar();
                        ScrollToEnd();
                    }),
                    DispatcherPriority.Background),

                ApproveAsync = edit => ApproveEditAsync(assistant, edit),

                // 改动真的落盘之后：记下来（供「撤销修改」回滚），并在 VS 里打开差异对比
                //（必须在落盘之后，否则比到的是旧内容）
                OnEditApplied = edit => Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (edit != null)
                        {
                            _turnEdits.Add(edit);

                            // 「本次变更」摘要：把这一次真正落盘的改动按文件累计下来
                            //（增删行数取自 diff 行），会话结束时一眼看清动过哪些文件
                            _session.RecordChange(
                                edit.IsNewFile ? SessionChangeKind.Add : SessionChangeKind.Modify,
                                edit.RelativePath,
                                edit.Diff.Count(line => line.Kind == DiffLineKind.Add),
                                edit.Diff.Count(line => line.Kind == DiffLineKind.Delete));
                        }

                        FollowEdit(edit);
                        RefreshRequestBar();
                    }),
                    DispatcherPriority.Background),

                // 读取等无 diff 的操作被关掉自动批准后，走这里逐次确认
                ApproveActionAsync = (action, result) => ApproveActionAsync(assistant, action, result),

                // 长任务编排：任务进度卡（每次 update_todo_list 都整体刷新同一张卡）
                OnTodoUpdate = (items, progress) => Dispatcher.BeginInvoke(
                    new Action(() => UpdateTodoCard(assistant, items, progress)),
                    DispatcherPriority.Background),

                // 模型提问：渲染问题卡并等用户回答（点选项 / 在输入框回车都算回答）
                AskQuestionAsync = question => AskUserAsync(assistant, question),

                // 子任务：开始与结束各更新一次那张卡（子任务内部的工具卡片照常显示）
                OnSubTask = (subTask, started) => Dispatcher.BeginInvoke(
                    new Action(() => ShowSubTask(assistant, subTask, started)),
                    DispatcherPriority.Background),

                // 命令输出流式回显（可能在后台线程触发，内部自行切线程）
                OnCommandOutput = line => AppendCommandOutput(assistant, line),

                // 转到定义：借用 VS 自带的 Roslyn，必须在 UI 线程执行
                GoToDefinition = (path, line) => Dispatcher.BeginInvoke(
                    new Action(() => VsFileOpener.GoToDefinition(ResolveInRoot(path), line)),
                    DispatcherPriority.Background),

                // 自动修复：Agent 需要读编译诊断时回调到 UI 线程采集（DTE 只能在主线程访问）
                ReadDiagnosticsAsync = async cancellationToken =>
                {
                    if (_settings == null || !_settings.AutoReadProblems)
                    {
                        return null;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return await Dispatcher
                        .InvokeAsync(() => VsDiagnosticsReader.TryRead())
                        .Task
                        .ConfigureAwait(true);
                },

                // 调试器操作（断点 / 单步 / 栈 / 求值）：DTE 只能在 UI 线程访问，
                // 因此封送到 Dispatcher 执行，再把结果文本回灌给模型
                DebugControlAsync = (command, cancellationToken) => Dispatcher
                    .InvokeAsync(() => VsDebuggerController.Execute(command, _workspaceRoot))
                    .Task,

                // 自动验证 / 自动修复过程中的便签（复用当前助手消息）
                OnNotice = text => Dispatcher.BeginInvoke(
                    new Action(() => AddNote(text)),
                    DispatcherPriority.Background),
            };

            await runner.RunAsync(turns, callbacks, token).ConfigureAwait(true);
        }

        /// <summary>
        /// 一轮模型输出定稿：把**完整文本**写回本轮正文块
        /// </summary>
        private void FinalizeTurn(ChatMessage assistant, string raw, bool hasActions)
        {
            if (assistant == null)
            {
                return;
            }

            // 定稿用的是完整文本（下面会整体写回），缓冲里那点尾巴不能再来一遍
            DiscardStreaming();

            if (_turnText == null && raw == null)
            {
                return;
            }

            // 本轮一次正文都没流出来（例如模型只回了动作块）：补一个正文块，让文本有地方落
            if (_turnText == null)
            {
                _turnText = new ChatBlock { Kind = ChatBlockKind.Text };
                assistant.Blocks.Add(_turnText);
            }

            if (raw != null)
            {
                _turnText.Text = hasActions ? ToolProtocol.StripActions(raw) : raw;
            }

            UpdateTokens();
            ScrollToEnd();
        }

        /// <summary>
        /// 按设置发一条通知（关掉「桌面通知」时什么都不做）
        /// </summary>
        private void NotifyIfEnabled(string message, bool beep)
        {
            if (!_settings.DesktopNotifications)
            {
                return;
            }

            VsNotifier.Notify(message, beep);
        }

        // ============================================================
        // 自动上下文（当前文件 / 选中代码 / 编译诊断）
        // ============================================================
        /// <summary>
        /// 采集「自动上下文」并拼成一段可发给模型的文本
        /// </summary>
        private string BuildAutoContext()
        {
            if (_settings == null || !_settings.AutoContext || _workspace == null)
            {
                return null;
            }

            var parts = new List<string>();

            var document = VsContextProvider.DescribeActiveDocument(_workspaceRoot);
            if (!string.IsNullOrWhiteSpace(document))
            {
                parts.Add(document);
            }

            var selection = VsFileOpener.TryGetSelection();
            if (!string.IsNullOrWhiteSpace(selection))
            {
                parts.Add("【编辑器选中的代码】\n```\n" + ClipText(selection, 4000) + "\n```");
            }

            if (_settings.AutoReadProblems)
            {
                var diagnostics = VsDiagnosticsReader.TryRead();
                if (!string.IsNullOrWhiteSpace(diagnostics))
                {
                    parts.Add("【编译诊断（错误列表中的错误）】\n" + diagnostics);
                }
            }

            if (parts.Count == 0)
            {
                return null;
            }

            return "以下是系统自动附带的上下文（无需重复读取，除非内容明显不完整）：\n\n" +
                   string.Join("\n\n", parts);
        }

        /// <summary>
        /// 截断长文本（保留开头，带省略标注）
        /// </summary>
        private static string ClipText(string text, int maxChars)
        {
            var value = (text ?? string.Empty).Trim();
            return value.Length <= maxChars
                ? value
                : value.Substring(0, maxChars) + "\n…（已截断）";
        }

    }
}

