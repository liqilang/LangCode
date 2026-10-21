using LangCode.Domain.Chat;
using LangCode.Ui.Presentation;
using LangCode.Workspace;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：请求条 / 审批 / 撤销 / 编辑 / 队列类定义与字段见 AgentToolWindowControl.xaml.cs
    /// </summary>
    public partial class AgentToolWindowControl
    {
        /// <summary>
        /// 清掉「上一轮」的动作状态（可撤销 / 可继续 / 可重试 / 编辑中 / 排队）
        /// </summary>
        private void ResetTurnActions()
        {
            _turnEdits.Clear();
            ResetTurnFlags();

            // 思考块的归属也要断开：换了会话，之前那块已经不是「这一步」的了
            _turnThinking = null;
            _dotsTimer?.Stop();

            // 就地编辑态也一并关掉：换了会话之后，那条消息已经不在界面上了
            foreach (var message in _session.Messages.Where(m => m.IsEditing).ToList())
            {
                message.IsEditing = false;
            }

            _queue.Clear();

            // 换了会话 = 换了一批消息：重新贴到底部跟随
            _followTail = true;

            // 顶部常驻的任务进度：换了会话就重新从当前会话里找最后一份清单（没有就收起）
            SyncTodoPanelFromMessages();

            RefreshRequestBar();
        }

        // ============================================================
        // 请求条：输入框上方的「允许 / 拒绝 / 继续任务 / 重试 / 撤销修改」
        // ============================================================
        /// <summary>
        /// 是否有动作正等着拍板（阻塞式：不点就走不下去）
        /// </summary>
        private bool HasPendingApproval => _pendingApproval != null;

        /// <summary>
        /// 模型是否正在等用户回答一个问题（同样是阻塞式）
        /// </summary>
        private bool HasPendingQuestion => _pendingQuestion != null;

        /// <summary>
        /// 清掉「上一轮可以接着跑」的三个标记（新的一轮开始、或换了会话）
        /// </summary>
        private void ResetTurnFlags()
        {
            _turnCanContinue = false;
            _turnInterrupted = false;
            _turnCanRetry = false;

            // 新一轮开始：允许重新弹审批 / 提问
            _approvalCancelled = false;
            _questionCancelled = false;
        }

        /// <summary>
        /// 请求条上的按钮统一走这里：按钮的 <c>Tag</c> 是 <see cref="RequestAction"/> 名
        /// </summary>
        private void RequestAction_Click(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(tag) || !Enum.TryParse(tag, out RequestAction action))
            {
                return;
            }

            switch (action)
            {
                case RequestAction.Approve:
                    DecideApproval(true);
                    break;

                case RequestAction.Reject:
                    DecideApproval(false);
                    break;

                case RequestAction.Continue:
                case RequestAction.Resume:
                case RequestAction.Retry:
                    // 三种叫法对应三种起因，做的事一模一样：拿现有历史接着往下跑
                    _ = ResumeAsync();
                    break;

                case RequestAction.Undo:
                    UndoLastTurn();
                    break;

                case RequestAction.UndoStep:
                    // 检查点：只退掉刚刚那一步，保留此前已完成的改动
                    UndoLastStep();
                    break;

                case RequestAction.SkipQuestion:
                    // 不回答：空字符串就是「跳过」的信号，模型会按最合理的默认方案继续
                    AnswerQuestion(string.Empty);
                    break;

                case RequestAction.Cancel:
                    // 生成中唯一的出口：停下这一轮（未决的审批一并按「拒绝」收尾）
                    StopCurrent();
                    break;
            }
        }

        /// <summary>
        /// 拍板：把结论交给正在等待的 Agent 循环，并在那张卡片上写下结论文案
        /// </summary>
        private void DecideApproval(bool approved)
        {
            var pending = _pendingApproval;
            var card = _pendingCard;

            _pendingApproval = null;
            _pendingCard = null;

            if (card != null)
            {
                card.IsAccepted = approved;
                card.IsRejected = !approved;
                card.StatusText = approved ? "已应用" : "已拒绝";
                card.IsPendingApproval = false;
            }

            RefreshRequestBar();
            ScrollToEnd();

            // 最后再放行 Agent —— 它可能立刻往界面里加新卡片，先刷新避免出现「按钮已消失但还在等」的错觉
            pending?.TrySetResult(approved);
        }

        /// <summary>
        /// 上一次请求条的指纹：没变就不重建按钮，避免每敲一个字都重排一遍界面
        /// </summary>
        private string _lastRequestSignature = "\u0000";

        /// <summary>
        /// 重算请求条：显示哪些动作完全交给 <see cref="RequestBar.Actions"/>（纯逻辑，自检直接断言）
        /// </summary>
        private void RefreshRequestBar()
        {
            if (RequestBarPanel == null)
            {
                return;
            }

            // ① 统一审批区：有待批动作时单独占一块（.appr），通用请求条让位 ——
            //    待批是阻塞的，一次只可能有一个动作在等拍板
            RefreshApprovalPanel(HasPendingApproval);
            if (HasPendingApproval)
            {
                RequestBarPanel.Visibility = Visibility.Collapsed;

                // 指纹置空：等审批结束后强制重建通用请求条
                _lastRequestSignature = "\u0000";
                return;
            }

            var busy = _session.IsBusy;

            var actions = RequestBar.Actions(
                HasPendingApproval,
                busy,
                _turnCanContinue,
                _turnInterrupted,
                _turnCanRetry,
                _turnEdits.Count > 0,
                HasPendingQuestion,
                canUndoStep: _settings.Checkpoints && _turnEdits.Count > 0);

            var title = RequestBar.Title(HasPendingApproval, HasPendingQuestion);
            var detail = DetailText();

            // 指纹 = 动作集合 + 标题 + 详情输入框每敲一个字符都会走到这里，
            // 无脑 Clear + 重建按钮是「打字发涩」的来源之一；没变化就直接返回
            var signature = string.Join(",", actions.Select(a => a.ToString())) + "\u0001" + title + "\u0001" + detail;
            if (string.Equals(signature, _lastRequestSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastRequestSignature = signature;

            RequestButtons.Children.Clear();
            RequestWide.Children.Clear();

            foreach (var action in actions)
            {
                // 「取消」是通栏按钮：单独占一行并横向铺满，闭着眼也知道点哪
                if (RequestBar.IsWide(action))
                {
                    RequestWide.Children.Add(RequestButton(action, wide: true));
                    continue;
                }

                RequestButtons.Children.Add(RequestButton(action));
            }

            RequestWide.Visibility = RequestWide.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            RequestTitle.Text = title;
            RequestDetail.Text = detail;
            RequestDetail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            RequestBarPanel.Visibility = actions.Count > 0 || RequestTitle.Text.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// 请求条的第二行：说清要批的是什么 / 正在编辑哪句 / 能撤销几处
        /// </summary>
        private string DetailText()
        {
            if (_pendingQuestionCard != null)
            {
                // 提问同样是阻塞的：把问题再复述一遍，用户不必往上翻找
                return _pendingQuestionCard.Text;
            }

            if (_pendingCard != null)
            {
                var path = _pendingCard.Path;
                return string.IsNullOrWhiteSpace(path)
                    ? _pendingCard.Title
                    : _pendingCard.Title + " · " + path;
            }

            if (_turnEdits.Count > 0)
            {
                return "本轮改动 " + _turnEdits.Count + " 处：" +
                       string.Join("、", _turnEdits.Select(edit => edit.RelativePath).Distinct().Take(3));
            }

            return string.Empty;
        }

        /// <summary>
        /// 按纯逻辑给出的动作建一颗按钮（样式取自主题里现成的那几套）
        /// </summary>
        private FrameworkElement RequestButton(RequestAction action, bool wide = false)
        {
            string styleKey;
            if (RequestBar.IsPrimary(action))
            {
                styleKey = "ActPrimary";
            }
            else if (RequestBar.IsDanger(action))
            {
                styleKey = "ActReject";
            }
            else
            {
                styleKey = "ActButton";
            }

            var button = new Button
            {
                Style = TryFindResource(styleKey) as Style,
                Tag = action.ToString(),
                Margin = wide ? new Thickness(0) : new Thickness(6, 0, 0, 0),
                HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            };

            button.Click += RequestAction_Click;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = wide ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            };
            row.Children.Add(new Ui.Primitive.Icon
            {
                IconName = RequestBar.Icon(action),
                Width = 11,
                Height = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = RequestBar.Label(action),
                Style = TryFindResource("ActLabel") as Style,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

            button.Content = row;
            return button;
        }

        // ============================================================
        // 统一审批区：输入框上方的任务拍板面板（对齐 UIUX 模板 .appr）
        // ============================================================

        /// <summary>
        /// 上一次审批面板内容的指纹：没变就不重排（输入框每敲一下都会走到这里）
        /// </summary>
        private string _lastApprovalSignature = "\u0000";

        /// <summary>
        /// 同步输入框上方的「统一审批区」
        /// </summary>
        private void RefreshApprovalPanel(bool pendingApproval)
        {
            if (ApprovalPanel == null)
            {
                return;
            }

            if (!pendingApproval || _pendingCard == null)
            {
                ApprovalPanel.Visibility = Visibility.Collapsed;
                _lastApprovalSignature = "\u0000";
                return;
            }

            var card = _pendingCard;
            var signature = (card.Title ?? string.Empty) + "\u0001"
                + (card.Path ?? string.Empty) + "\u0001"
                + card.Kind + "\u0001"
                + card.Lines.Count + "\u0001"
                + (card.Text ?? string.Empty).Length;

            if (!string.Equals(signature, _lastApprovalSignature, StringComparison.Ordinal))
            {
                _lastApprovalSignature = signature;

                var isDiff = card.Kind == ChatBlockKind.DiffCard && card.Lines.Count > 0;

                ApprovalTitle.Text = card.Title ?? string.Empty;
                ApprovalIcon.IconName = isDiff ? "edit" : "zap";

                // 元信息：差异卡显示 +N −N，其余显示目标路径
                var meta = isDiff
                    ? card.AddLabel + " " + card.DeleteLabel
                    : (card.Path ?? string.Empty);
                ApprovalMeta.Text = meta;
                ApprovalMeta.Visibility = string.IsNullOrWhiteSpace(meta)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                // 详情：差异卡给 diff 行，操作确认卡给纯文本；两者都没有就不显示「查看详情」
                var hasDetail = isDiff || !string.IsNullOrWhiteSpace(card.Text);
                ApprovalToggle.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;

                ApprovalDiff.ItemsSource = isDiff ? card.Lines : null;
                ApprovalDiff.Visibility = isDiff ? Visibility.Visible : Visibility.Collapsed;
                ApprovalDetailText.Text = isDiff ? string.Empty : (card.Text ?? string.Empty);
                ApprovalDetailText.Visibility = !isDiff && !string.IsNullOrWhiteSpace(card.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                // 新一次审批：详情默认收起，箭头朝下
                ApprovalDetail.Visibility = Visibility.Collapsed;
                ApprovalToggleLabel.Text = "查看详情";
                ApprovalChevron.RenderTransform = new RotateTransform(0);

                ApprovalCount.Text = "1 项";
            }

            ApprovalPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 点「查看详情」：展开 / 收起待批动作的差异或目标内容
        /// </summary>
        private void ApprovalToggle_Click(object sender, RoutedEventArgs e)
        {
            var willOpen = ApprovalDetail.Visibility != Visibility.Visible;
            ApprovalDetail.Visibility = willOpen ? Visibility.Visible : Visibility.Collapsed;
            ApprovalToggleLabel.Text = willOpen ? "收起详情" : "查看详情";
            ApprovalChevron.RenderTransform = new RotateTransform(willOpen ? 180 : 0);
        }

        /// <summary>
        /// 统一审批区：批准并继续
        /// </summary>
        private void ApprovalApprove_Click(object sender, RoutedEventArgs e)
        {
            DecideApproval(true);
        }

        /// <summary>
        /// 统一审批区：拒绝
        /// </summary>
        private void ApprovalReject_Click(object sender, RoutedEventArgs e)
        {
            DecideApproval(false);
        }

        /// <summary>
        /// 复制**一段**助手正文（正文右下角那个小图标）：只复制这一段，不夹带其它卡片
        /// </summary>
        private void CopyBlock_Click(object sender, RoutedEventArgs e)
        {
            var block = (sender as FrameworkElement)?.DataContext as ChatBlock;
            var text = block?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowSessionActionHint("这段文字是空的");
                return;
            }

            try
            {
                Clipboard.SetText(text);
                ShowSessionActionHint("已复制 " + text.Length + " 个字符");
            }
            catch (Exception)
            {
                ShowSessionActionHint("复制失败");
            }
        }

        /// <summary>
        /// 复制这条消息的内容：用户消息取正文，助手消息取正文 + 代码卡 / 清单等纯文本
        /// </summary>
        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatMessage message)
            {
                return;
            }

            var text = message.IsUser
                ? message.Text
                : LangCode.Chat.ChatSession.PlainTextOf(message);

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowSessionActionHint("这条消息没有可复制的文字");
                return;
            }

            try
            {
                Clipboard.SetText(text);
                ShowSessionActionHint("已复制 " + text.Length + " 个字符");
            }
            catch (Exception)
            {
                ShowSessionActionHint("复制失败");
            }
        }

        /// <summary>
        /// 把工作区文件还原到这条用户消息发出<b>之前</b>的状态（检查点）。
        /// 只动磁盘上的文件，对话记录保持不动 —— 想连对话一起退回，
        /// 用「编辑并重新发送」或「分叉」；想全面回滚，用请求条上的「撤销本轮」
        /// </summary>
        private async void RestoreCheckpoint_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatMessage message || !message.HasCheckpoint)
            {
                return;
            }

            if (_session.IsBusy)
            {
                AddNote("正在生成中，等这一轮结束后再还原检查点");
                return;
            }

            var store = EnsureCheckpoints();
            if (store == null || !store.IsAvailable)
            {
                AddNote("检查点不可用：请在「设置 → 编辑器」里打开「检查点（Checkpoints）」，并确认环境里能调用 git");
                return;
            }

            var confirmed = await Controls.InWindowDialog.ConfirmAsync(
                sender as FrameworkElement,
                "还原到检查点 " + message.CheckpointShortId,
                "把工作区文件还原到这条消息发出之前吗？\n\n" +
                "「" + FirstLineOf(message.Text) + "」\n\n" +
                "· 此后 AI 对文件的改动都会被覆盖掉（未跟踪的新建文件保留原样）\n" +
                "· 对话记录不受影响，仍保留在这里\n" +
                "· 该操作不可撤销",
                confirmText: "还原",
                iconName: "undo");

            if (!confirmed)
            {
                return;
            }

            var id = message.CheckpointId;
            var result = await Task.Run(() => store.Restore(id)).ConfigureAwait(true);

            // await 之后可能落在后台线程：接下来要往界面里写卡片，先切回主线程
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            AddNote(result != null && result.Ok
                ? "已把工作区还原到「" + FirstLineOf(message.Text) + "」之前 · 检查点 " + message.CheckpointShortId
                : "还原失败：" + (result?.Output ?? "检查点不可用"));

            RefreshRequestBar();
            UpdateTokens();
            ScrollToEnd();
        }

        /// <summary>
        /// 点用户消息上的编辑按钮：在<b>原地</b>把它变成输入框 ——
        /// 不把内容搬到下面的输入框改完点「保存并重新发送」即可丢弃它之后的对话并用新内容重跑
        /// </summary>
        private void EditMessage_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatMessage message || !message.IsUser)
            {
                return;
            }

            // 同一时刻只留一条处于编辑态：两个编辑器同时在页面上会让人不知道哪个算数
            foreach (var other in _session.Messages.Where(m => m.IsEditing && m != message).ToList())
            {
                other.IsEditing = false;
            }

            message.EditText = message.Text;
            message.IsEditing = true;

            RefreshRequestBar();
        }

        /// <summary>
        /// 放弃就地编辑：关掉编辑器，原内容原样留在气泡里
        /// </summary>
        private void CancelEditMessage_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatMessage message)
            {
                return;
            }

            message.IsEditing = false;
            RefreshRequestBar();
        }

        /// <summary>
        /// 保存就地编辑并重新发送
        /// </summary>
        private void SaveEditMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ChatMessage message })
            {
                _ = SaveEditedAsync(message);
            }
        }

        /// <summary>
        /// 编辑后的重发
        /// </summary>
        private async Task SaveEditedAsync(ChatMessage message)
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var text = (message.EditText ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                AddNote("编辑后的内容不能为空");
                return;
            }

            if (_session.IsBusy)
            {
                AddNote("正在生成中，等这一轮结束后再点「保存并重新发送」");
                return;
            }

            EnsureWorkspace();

            // 这一步会把它自己一并从对话里移除，之后按新内容重新加入
            _session.TruncateFrom(message);

            ResetTurnFlags();
            _turnEdits.Clear();

            var userMessage = _session.AddUserMessage(text);
            UpdateSendState();

            await RunTurnAsync(userMessage).ConfigureAwait(true);
        }

        /// <summary>
        /// 从这条消息分叉：保留到它为止（含）的对话，丢弃其后的内容并换一个新的会话 id ——
        /// 相当于把当前这段对话复制成一个新分支继续，原会话仍完整保留在历史里。
        /// 与「编辑重发」的区别：编辑重发会改写原会话，分叉则两者都留下，便于对比不同走向。
        /// </summary>
        private async void ForkMessage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if ((sender as FrameworkElement)?.DataContext is not ChatMessage message)
                {
                    return;
                }

                if (_session.IsBusy)
                {
                    ShowSessionActionHint("正在生成中，等这一轮结束后再分叉");
                    return;
                }

                EnsureWorkspace();

                // 先把「分叉前的原始对话」写进历史，确保它不受后续截断影响
                _session.SaveCurrent(_settings);

                if (!_session.ForkFrom(message))
                {
                    return;
                }

                // 分叉后的这段作为新会话落盘，立刻出现在历史里
                _session.SaveCurrent(_settings);

                ResetTurnActions();
                UpdateSendState();
                UpdateTokens();

                HistoryDrawer.SetActiveConversation(_session.ConversationId);
                ScrollToEnd();

                ShowSessionActionHint("已从此处分叉为新会话；原会话仍保留在历史记录里");
            }
            catch (Exception)
            {
                // 分叉失败不影响现有会话
            }
        }

        /// <summary>
        /// 接着跑：「继续任务 / 恢复任务 / 重试」都走这里
        /// </summary>
        private async Task ResumeAsync()
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_session.IsBusy)
            {
                // 生成中本来就不该出现这些按钮；真被点到也当没点，避免两轮同时跑
                return;
            }

            EnsureWorkspace();

            _turnCanContinue = false;
            _turnInterrupted = false;
            _turnCanRetry = false;

            RefreshRequestBar();
            await RunTurnAsync(null, "继续").ConfigureAwait(true);
        }

        private void Enqueue(string text)
        {
            var message = _session.AddUserMessage(text);
            message.IsQueued = true;
            _queue.Enqueue(new QueuedTask { Message = message, Text = text });

            RefreshRequestBar();
            ScrollToEnd();
        }

        /// <summary>
        /// 撤销本轮真正落盘的改动：按<b>相反顺序</b>把「改动前」写回去，新建的文件删掉
        /// </summary>
        /// <summary>
        /// 撤销<b>上一步</b>落盘的改动（检查点）
        /// </summary>
        private void UndoLastStep()
        {
            if (_turnEdits.Count == 0 || _workspace == null)
            {
                return;
            }

            var edit = _turnEdits[_turnEdits.Count - 1];
            _turnEdits.RemoveAt(_turnEdits.Count - 1);

            var result = _workspace.Revert(edit);

            AddNote(result != null && result.Ok
                ? "已撤销上一步改动：" + edit.RelativePath
                : "撤销失败：" + (result?.Output ?? "没有可撤销的改动"));

            RefreshRequestBar();
            UpdateTokens();
            ScrollToEnd();
        }

        private void UndoLastTurn()
        {
            if (_turnEdits.Count == 0)
            {
                return;
            }

            var count = 0;
            for (var i = _turnEdits.Count - 1; i >= 0; i--)
            {
                var result = _workspace?.Revert(_turnEdits[i]);
                if (result != null && result.Ok)
                {
                    count++;
                }
            }

            _turnEdits.Clear();

            var assistant = _session.Messages.LastOrDefault(m => !m.IsUser);
            assistant?.Blocks.Add(new ChatBlock
            {
                Kind = ChatBlockKind.Note,
                Text = count > 0
                        ? "已撤销 " + count + " 处改动，文件已还原到本轮之前"
                        : "撤销失败：文件可能已被移动或删除",
            });

            RefreshRequestBar();
            UpdateSendState();
            ScrollToEnd();
        }

        /// <summary>
        /// 审批一次写入。是否自动批准已由 <see cref="AgentRunner"/> 按工具的真实类别
        /// （新增 / 编辑）判定；走到这里就一定要让用户看到差异并拍板，
        /// 不能再用 <c>AutoApproveEdits</c> 二次判断，否则会绕过「新增文件」的独立开关。
        /// </summary>
        private Task<bool> ApproveEditAsync(ChatMessage message, PendingEdit edit)
        {
            return Dispatcher.InvokeAsync(async () =>
            {
                // 派发到界面线程执行；用 Switch 而不是抛异常，Task 返回的 lambda 里
                // 抛断言会被分析器判为错误（VSTHRD010 的 async 变体）
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .SwitchToMainThreadAsync();

                // 标题说清这是「新增文件」还是「编辑文件」；是否已落盘由状态文案（已应用）与
                // 底栏（等待确认）表达，标题本身不必再分 Proposed / Applied
                var card = new ChatBlock
                {
                    Kind = ChatBlockKind.DiffCard,
                    Title = edit.IsNewFile ? "新增文件" : "编辑文件",
                    Path = edit.RelativePath,
                    Lines = edit.Diff,

                    // 点卡片直接在 VS 里开差异对比：记住改动前后全文，
                    // 待审批时看的是「提议的内容」（此时磁盘上还没有改动）
                    BeforeText = edit.OriginalText ?? string.Empty,
                    AfterText = edit.NewText ?? string.Empty,
                    IsPendingApproval = true,
                };

                message.Blocks.Add(card);
                return await WaitForApprovalAsync(card);
            }).Task.Unwrap();
        }

        /// <summary>
        /// 把一张待批卡片接到请求条上，返回「等用户点允许 / 拒绝」的任务。
        /// 并行子任务可能同时请求审批，这里用信号量串行化：一次只显示一张。
        /// </summary>
        private async Task<bool> WaitForApprovalAsync(ChatBlock card)
        {
            await _approvalGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (_approvalCancelled)
                {
                    card.IsPendingApproval = false;
                    card.IsRejected = true;
                    card.StatusText = "已取消";
                    return false;
                }

                _pendingCard = card;
                _pendingApproval = new TaskCompletionSource<bool>();

                // 上面等信号量的 await 之后要重新确保回到 UI 线程（通知与界面更新都需要）
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .SwitchToMainThreadAsync();

                // 待批是阻塞的：用户在别的窗口时也要能立刻知道「这里卡住了」
                NotifyIfEnabled("LangCode：有一个操作等待你确认", beep: true);

                RefreshRequestBar();
                ScrollToEnd();

                // 这个 TCS 由界面线程（用户点击）完成，属于有意为之的跨上下文等待
#pragma warning disable VSTHRD003
                return await _pendingApproval.Task.ConfigureAwait(true);
#pragma warning restore VSTHRD003
            }
            finally
            {
                _approvalGate.Release();
            }
        }

    }
}

