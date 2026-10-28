using LangCode.Agent;
using LangCode.Chat;
using LangCode.Domain.Chat;
using LangCode.VsIntegration;
using LangCode.Workspace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LangCode.Ui.ToolWindows
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
            // 用户也可能先开窗口再打开文件夹 / 切换项目 —— 每次发送都重新定位一次，
            // 根目录变了就重建（切到 B 项目后不再用 A）。
            // 若 VS 里完全没有工作区，这里会弹一次「选择目录」框让用户指定 / 新建目录
            EnsureWorkspace(canPromptIfMissing: true);

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
            // 恢复 / 继续 / 排队也走这里：上一轮取消时置位的标志必须在这里清掉，
            // 否则新一轮的审批会被误判成「已取消」。
            _approvalCancelled = false;
            _questionCancelled = false;

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

            // 「突然中断」保险丝：先把「任务进行中」落盘。若 VS 被强杀来不及走收尾，
            // 下次打开该会话时就能据此恢复「继续任务」
            _session.TurnInProgress = true;
            _session.SaveCurrent(_settings);

            var assistant = _session.BeginAssistantMessage(_settings.Model);

            // 拿不到工作区就明确告诉用户「本轮是纯对话」，而不是静默地什么都不做
            if (_workspace == null && !_workspaceNoticeShown)
            {
                _workspaceNoticeShown = true;
                assistant.Blocks.Add(new ChatBlock
                {
                    Kind = ChatBlockKind.Note,
                    Text = "未检测到 VS 工作区（没有打开解决方案或文件夹），本轮按纯对话处理，不会读写文件。" +
                           "在 VS 里打开解决方案/文件夹后再发一次即可启用文件工具；" +
                           "或开一个新会话发送消息，按提示选择一个目录作为工作区（AI 可在其中创建新项目）",
                });
            }

            _session.IsBusy = true;
            UpdateSendState();
            ScrollToEnd();

            // 「检查点」：这一轮还没动手改文件，先打一个点挂在这条用户消息上 ——
            // 之后想回到「这条消息发出之前」的代码状态，点消息上的还原按钮即可。
            // 放在这里是为了让界面先进入"生成中"（大仓库里 git add -A 要一点时间，
            // 期间用户能看到忙碌态，而不是以为没反应）；此后到 Agent 动手前不会再写文件
            if (userMessage != null)
            {
                await CreateTurnCheckpointAsync(userMessage).ConfigureAwait(true);
            }

            _cts = new CancellationTokenSource();
            var backend = ChatBackendFactory.Create(_settings);

            // 接近上下文上限时，先用一次模型调用把较早的中间段压成结构化摘要，
            // 而不是直接丢弃 —— 避免模型丢上下文后反复重读文件；本身未达阈值时是空操作
            await _session
                .CompactIfNeededAsync(backend, _settings, _cts.Token)
                .ConfigureAwait(true);

            var turns = _session.BuildTurns();

            // 自动上下文：把「当前文件 / 选中代码 / 编译诊断」附在这一轮请求里
            // 只在用户主动发送时采集（续跑 / 排队不重复采集，避免噪声叠加）
            // 采集过程要读 DTE：显式切回 UI 线程（async 方法里用 Switch 而不是抛异常）
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                .SwitchToMainThreadAsync();
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

                // 收尾：任务不再「进行中」；随后照常落盘（写入最终的继续 / 恢复 / 重试状态）
                _session.TurnInProgress = false;
                _session.SaveCurrent(_settings);

                // 长任务（改过文件 / 撞到上限 / 出错 / 产生多张卡片）结束时提示一声：
                // 这几分钟里用户多半已经切到别的窗口去了
                if (_settings.DesktopNotifications &&
                    (_turnEdits.Count > 0 || _turnCanContinue || _turnCanRetry ||
                     (assistant != null && assistant.Blocks.Count > 3)))
                {
                    // 上面几处 await 之后仍要求回到 UI 线程（通知要写状态栏）
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
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
            _turnRawText = null;

            await backend.SendAsync(request, delta => AppendStreaming(assistant, delta), token).ConfigureAwait(true);
        }

        /// <summary>
        /// 有工作区：Agent 工具循环（读/搜/写/改 + 审批）
        /// </summary>
        private async Task RunAgentAsync(IChatBackend backend, IList<ChatTurn> turns, ChatMessage assistant, CancellationToken token)
        {
            // 语义索引跨轮次复用（界面在后台预热过）：首轮 semantic_search 不必再全仓库同步扫描
            var runner = new AgentRunner(backend, _workspace, _settings, EnsureSemanticIndex(), _sessionGrants)
            {
                // 用量 / 审计按会话归集：之前这里没赋值，所有记录都落在空 id 上
                ConversationId = _session.ConversationId,
            };

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
                        _turnRawText = null;
                    }),
                    DispatcherPriority.Background),

                OnReasoning = ReasoningSink(assistant),

                // 流式增量走合流：一帧一次，不再每个分片都重排一遍界面与滚动
                OnDelta = delta => AppendStreaming(assistant, delta),

                // 一轮结束：以**本轮完整输出**定稿（流式累积不作数），并去掉动作块
                OnTurnComplete = (raw, hasActions) => Dispatcher.BeginInvoke(new Action(() => FinalizeTurn(assistant, raw, hasActions)), DispatcherPriority.Background),

                OnToolResult = (action, result) => Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                    AddToolCard(assistant, action, result);
                }), DispatcherPriority.Background),

                // 达到最大步数：必须给用户一句明确交代，否则看起来就是「说着说着没了」
                OnStepLimit = steps => Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    FinalizeTurn(assistant, null, false);
                    assistant.Blocks.Add(new ChatBlock
                    {
                        Kind = ChatBlockKind.Note,
                        Text = "已达到本轮最大步数（" + steps + " 步），任务在这里暂停" + "输入框上方会出现「继续任务」，点它我就接着做；也可以补充说明后重发",
                    });

                    // 任务没做完 → 请求条上出现「继续任务」（这条标记是它会出现的唯一原因）
                    _turnCanContinue = true;

                    UpdateTokens();
                    RefreshRequestBar();
                    ScrollToEnd();

                    // 立刻落盘：这个回调经 Dispatcher 异步派发，可能晚于本轮收尾的那次保存
                    _session.SaveCurrent(_settings);
                }), DispatcherPriority.Background),

                ApproveAsync = edit => ApproveEditAsync(assistant, edit),

                // 改动真的落盘之后：记下来（供「撤销修改」回滚），并在 VS 里打开差异对比
                //（必须在落盘之后，否则比到的是旧内容）
                OnEditApplied = edit => Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

                        if (edit != null)
                        {
                            _turnEdits.Add(edit);

                            // 第一次改到某个文件时，记下它「改动前」的内容：
                            // 「本次变更」里点击文件名时要用它开 VS 差异对比
                            if (!string.IsNullOrWhiteSpace(edit.RelativePath) &&
                                !_sessionBaselines.ContainsKey(edit.RelativePath))
                            {
                                _sessionBaselines[edit.RelativePath] = edit.OriginalText ?? string.Empty;
                            }

                            // 「本次变更」摘要：把这一次真正落盘的改动按文件累计下来
                            //（增删行数取自 diff 行），会话结束时一眼看清动过哪些文件
                            var addedLines = edit.Diff.Count(line => line.Kind == DiffLineKind.Add);
                            var deletedLines = edit.Diff.Count(line => line.Kind == DiffLineKind.Delete);

                            if (edit.IsDelete)
                            {
                                _session.RecordChange(SessionChangeKind.Delete, edit.RelativePath, 0, deletedLines);
                            }
                            else if (edit.IsMove)
                            {
                                // 移动在摘要里体现为「旧路径删除 + 新路径新增」
                                _session.RecordChange(SessionChangeKind.Delete, edit.RelativePath, 0, deletedLines);
                                _session.RecordChange(SessionChangeKind.Add, edit.MoveToPath, addedLines, 0);

                                if (!string.IsNullOrWhiteSpace(edit.MoveToPath) &&
                                    !_sessionBaselines.ContainsKey(edit.MoveToPath))
                                {
                                    // 新路径此前不存在：以空内容作基线，点摘要即可看到「从无到有」的差异
                                    _sessionBaselines[edit.MoveToPath] = string.Empty;
                                }
                            }
                            else
                            {
                                _session.RecordChange(
                                    edit.IsNewFile ? SessionChangeKind.Add : SessionChangeKind.Modify,
                                    edit.RelativePath,
                                    addedLines,
                                    deletedLines);
                            }

                            // 新建 / 删除文件后 @ 提及索引与工具链探测缓存要失效
                            _mentions?.Invalidate();
                            ToolchainDetector.Invalidate();

                            // 对话里的「编辑文件 / 新增文件」记录：自动批准不会经过审批卡，
                            // 不在这里补一张卡的话，文件被改了对话里却什么都看不到
                            RecordAppliedEditCard(assistant, edit);
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
                    new Action(() =>
                    {
                        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                        VsFileOpener.GoToDefinition(ResolveInRoot(path), line);
                    }),
                    DispatcherPriority.Background),

                // 自动修复：Agent 需要读编译诊断时回调到 UI 线程采集（DTE 只能在主线程访问）
                ReadDiagnosticsAsync = async cancellationToken =>
                {
                    if (_settings == null || !_settings.AutoReadProblems)
                    {
                        return null;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return await Dispatcher.InvokeAsync(() =>
                    {
                        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                        return VsDiagnosticsReader.TryRead();
                    }).Task.ConfigureAwait(true);
                },

                // 调试器操作（断点 / 单步 / 栈 / 求值）：DTE 只能在 UI 线程访问，
                // 因此封送到 Dispatcher 执行，再把结果文本回灌给模型
                DebugControlAsync = (command, cancellationToken) => Dispatcher.InvokeAsync(() =>
                {
                    Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                    return VsDebuggerController.Execute(command, _workspaceRoot);
                }).Task,

                // 未保存的编辑器缓冲区不能被 AI 的磁盘写入覆盖
                IsFileDirty = relativePath => Dispatcher.Invoke(() =>
                {
                    Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                    return VsFileOpener.IsDocumentDirty(ResolveInRoot(relativePath));
                }),

                // 自动验证 / 自动修复过程中的便签（复用当前助手消息）
                OnNotice = text => Dispatcher.BeginInvoke(new Action(() => AddNote(text)), DispatcherPriority.Background),
            };

            await runner.RunAsync(turns, callbacks, token).ConfigureAwait(true);
        }

        /// <summary>
        /// 把一段流式正文并入本轮正文块：界面显示的是**去掉工具动作块**后的文本。
        /// 模型是边想边吐的，工具动作（```action …）吐完之前正文区会先闪出
        /// 「list_files(...)」之类的原始动作代码；这里在流式期间就把它藏起来，
        /// 让用户直接看到随后出现的卡片，而不是先看一眼工具代码
        /// </summary>
        private void AppendTurnText(ChatMessage target, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _turnRawText ??= new StringBuilder();
            _turnRawText.Append(text);

            // 原文攒在 _turnRawText 上，展示用的正文每次都由原文重新去除动作块 ——
            // 不能直接把增量追加到已过滤的正文上，否则动作块消失后紧跟着的增量会粘错位置
            var visible = ToolProtocol.StripActionsForDisplay(_turnRawText.ToString());

            // 这一步还一个字都没露出来（整段都还是工具动作）：先不建正文块 ——
            // 否则任务收尾处会留下若干**空白正文块**（看着就是平白多出几段空隙）
            if (_turnText == null && string.IsNullOrEmpty(visible))
            {
                return;
            }

            _turnText ??= ChatSession.BeginText(target);
            _turnText.Text = visible;
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

            var text = raw == null ? null : (hasActions ? ToolProtocol.StripActions(raw) : raw);

            // 本轮一次正文都没流出来（例如模型只回了动作块）：只有真的还有正文才补块，
            // 空块在界面上就是一段平白多出来的空隙 —— 任务收尾时能连出好几段
            if (_turnText == null)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                _turnText = new ChatBlock { Kind = ChatBlockKind.Text };
                assistant.Blocks.Add(_turnText);
            }

            if (raw != null)
            {
                _turnText.Text = text ?? string.Empty;
            }

            UpdateTokens();
            ScrollToEnd();
        }

        /// <summary>
        /// 按设置发一条通知（关掉「桌面通知」时什么都不做）
        /// </summary>
        private void NotifyIfEnabled(string message, bool beep)
        {
            // 通知会写 VS 状态栏，只能在 UI 线程调用
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

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
            // 下面读活动文档 / 选中代码 / 编译诊断，全部依赖 DTE，必须处于 UI 线程
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

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

