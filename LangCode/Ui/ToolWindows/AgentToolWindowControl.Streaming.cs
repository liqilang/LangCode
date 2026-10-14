using LangCode.Chat;
using LangCode.Domain.Catalog;
using LangCode.Domain.Chat;
using LangCode.Persistence;
using LangCode.Ui.Presentation;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：流式合流 / 思考点 / 令牌条 / 滚动类定义与字段见 AgentToolWindowControl.xaml.cs
    /// </summary>
    public partial class AgentToolWindowControl
    {
        // ==================== 平滑吐字参数 ====================

        /// <summary>
        /// 最低吐字速率（约 1 字/帧的「打字」节奏）
        /// </summary>
        private const double MinRevealCharsPerSecond = 24;

        /// <summary>
        /// 积压文本的目标追平时间：越多字单帧吐得越多，但仍在约 0.45s 内追上
        /// </summary>
        private const double RevealCatchUpSeconds = 0.45;

        /// <summary>
        /// 单帧吐字上限：防止极端积压时一帧刷下一大段（那正是「一下蹦出来」）
        /// </summary>
        private const int MaxRevealCharsPerTick = 240;

        /// <summary>
        /// 吐字计时器的帧间隔（约 60fps）
        /// </summary>
        private static readonly TimeSpan RevealInterval = TimeSpan.FromMilliseconds(16);

        /// <summary>
        /// 收集流式增量：只把增量攒进缓冲区并唤醒吐字计时器，<b>不直接写界面</b>
        /// </summary>
        private void AppendStreaming(ChatMessage message, string delta)
        {
            if (message == null || string.IsNullOrEmpty(delta))
            {
                return;
            }

            lock (_streamGate)
            {
                _streamTarget = message;
                _streamBuffer.Append(delta);
            }

            // 计时器只能在 UI 线程创建/启动；这里把它压成「一次 BeginInvoke」，
            // 之后的每个增量都只是往缓冲区追加，不再各自排一次队
            ScheduleFlush();
        }

        /// <summary>
        /// 安排一次吐字（正文与推理共用同一条计时器）
        /// </summary>
        private void ScheduleFlush()
        {
            if (_streamScheduled)
            {
                return;
            }

            _streamScheduled = true;

            // Render 优先级：保证同一帧里「先吐字 → 再重建 Markdown → 再滚动 → 最后渲染」，
            // 不会像 Background 那样被布局/渲染饿死而攒成一大坨一次性蹦出
            Dispatcher.BeginInvoke(new Action(EnsureStreamTimer), DispatcherPriority.Render);
        }

        private void EnsureStreamTimer()
        {
            if (_streamTimer == null)
            {
                _streamTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
                {
                    Interval = RevealInterval,
                };
                _streamTimer.Tick += (_, __) => RevealTick();
            }

            if (!_streamTimer.IsEnabled)
            {
                _revealLastUtc = DateTime.UtcNow;
                _streamTimer.Start();
            }
        }

        /// <summary>
        /// 这一帧该吐多少字：按<b>真实帧间隔</b>与积压量自适应
        /// </summary>
        private int CountReveal(int backlog)
        {
            var now = DateTime.UtcNow;
            var dtMs = _revealLastUtc == DateTime.MinValue
                ? RevealInterval.TotalMilliseconds
                : (now - _revealLastUtc).TotalMilliseconds;
            _revealLastUtc = now;

            // 首次、时钟回拨或卡顿太久：按一帧算，避免一次性吐一大段
            if (dtMs <= 0 || dtMs > 250)
            {
                dtMs = RevealInterval.TotalMilliseconds;
            }

            var charsPerSecond = Math.Max(MinRevealCharsPerSecond, backlog / RevealCatchUpSeconds);
            var count = (int)Math.Round(charsPerSecond * dtMs / 1000.0);

            if (count < 1)
            {
                count = 1;
            }

            if (count > MaxRevealCharsPerTick)
            {
                count = MaxRevealCharsPerTick;
            }

            return Math.Min(count, backlog);
        }

        /// <summary>
        /// 吐字帧（UI 线程）：把这一帧该显示的部分从缓冲区搬进正文块，并顺带处理推理
        /// </summary>
        private void RevealTick()
        {
            string text = null;
            string reasoning = null;
            ChatMessage target;
            bool idle;

            lock (_streamGate)
            {
                target = _streamTarget;

                if (_reasoningBuffer.Length > 0)
                {
                    reasoning = _reasoningBuffer.ToString();
                    _reasoningBuffer.Clear();
                }

                var available = _streamBuffer.Length - _revealOffset;

                if (target != null && available > 0)
                {
                    var count = CountReveal(available);
                    text = _streamBuffer.ToString(_revealOffset, count);
                    _revealOffset += count;

                    if (_revealOffset >= _streamBuffer.Length)
                    {
                        // 追上末尾：整体清空（同时把读游标归零）
                        _streamBuffer.Clear();
                        _revealOffset = 0;
                    }
                    else if (_revealOffset >= 4096)
                    {
                        // 读游标跑得够远了，回收一次前缀，避免缓冲区无限增长
                        _streamBuffer.Remove(0, _revealOffset);
                        _revealOffset = 0;
                    }
                }

                idle = _streamBuffer.Length - _revealOffset <= 0 && _reasoningBuffer.Length == 0;
            }

            if (target == null)
            {
                StopStreamTimer();
                return;
            }

            // 推理先落：它属于「这一步」的思考块，正文块排在它之后
            if (!string.IsNullOrEmpty(reasoning))
            {
                ChatDiagnostics.Log("UI：本步推理 " + reasoning.Length + " 字符 → 落思考块");
                AppendStepThinking(target, reasoning);
            }

            if (!string.IsNullOrEmpty(text))
            {
                // 本步还没有正文块 → 新开一块
                // **不能复用上一步的**：每步定稿都会整体写回「本轮完整文本」，
                // 两步共用一块的话后一步会把前一步的正文覆盖掉 —— 看起来就是「前面几步消失了」
                _turnText ??= ChatSession.BeginText(target);

                _turnText.Text += text;
            }

            if (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(reasoning))
            {
                // 流式期间节流刷新：令牌条不需要每帧重算一次（那是一次全量遍历）
                UpdateTokens(force: false);
                ScrollToEnd();
            }

            if (idle)
            {
                StopStreamTimer();
            }
        }

        /// <summary>
        /// 把缓冲里的增量<b>立刻</b>全部写进界面（UI 线程）：收尾 / 跨步时需要一次性落干净
        /// </summary>
        private void FlushStreaming()
        {
            string text = null;
            string reasoning = null;
            ChatMessage target;

            lock (_streamGate)
            {
                target = _streamTarget;

                if (_reasoningBuffer.Length > 0)
                {
                    reasoning = _reasoningBuffer.ToString();
                    _reasoningBuffer.Clear();
                }

                var available = _streamBuffer.Length - _revealOffset;
                if (target != null && available > 0)
                {
                    text = _streamBuffer.ToString(_revealOffset, available);
                }

                _streamBuffer.Clear();
                _revealOffset = 0;
            }

            if (target == null)
            {
                return;
            }

            // 推理先落：它属于「这一步」的思考块，正文块排在它之后
            if (!string.IsNullOrEmpty(reasoning))
            {
                AppendStepThinking(target, reasoning);
            }

            if (!string.IsNullOrEmpty(text))
            {
                _turnText ??= ChatSession.BeginText(target);

                _turnText.Text += text;
            }
        }

        /// <summary>
        /// 命令输出行的流式入口（可能在后台线程被调用）
        /// </summary>
        private void AppendCommandOutput(ChatMessage message, string line)
        {
            if (message == null || string.IsNullOrEmpty(line))
            {
                return;
            }

            lock (_commandGate)
            {
                _commandTarget = message;
                _commandBuffer.AppendLine(line);
            }

            if (_commandFlushScheduled)
            {
                return;
            }

            _commandFlushScheduled = true;
            Dispatcher.BeginInvoke(new Action(EnsureCommandTimer), DispatcherPriority.Background);
        }

        private void EnsureCommandTimer()
        {
            if (_commandTimer == null)
            {
                _commandTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(200),
                };

                _commandTimer.Tick += (_, __) => FlushCommandOutput();
            }

            if (!_commandTimer.IsEnabled)
            {
                _commandTimer.Start();
            }
        }

        /// <summary>
        /// 把缓冲里的命令行一次性写进「命令输出」卡（UI 线程）
        /// </summary>
        private void FlushCommandOutput()
        {
            string text;
            ChatMessage target;

            lock (_commandGate)
            {
                if (_commandBuffer.Length == 0 || _commandTarget == null)
                {
                    _commandFlushScheduled = false;
                    _commandTimer?.Stop();
                    return;
                }

                text = _commandBuffer.ToString().TrimEnd('\r', '\n');
                _commandBuffer.Clear();
                target = _commandTarget;
            }

            if (_liveCommandCard == null)
            {
                _liveCommandCard = new ChatBlock
                {
                    Kind = ChatBlockKind.CodeCard,
                    Title = "Run Command",
                    IsBodyVisible = true,
                };

                target.Blocks.Add(_liveCommandCard);
            }

            // 输出太长就不再往卡片里堆：最终结果卡里另有一份（按 MaxOutputChars 截断过的）
            if (_liveCommandCard.Text.Length < 8000 && text.Length > 0)
            {
                _liveCommandCard.Text = _liveCommandCard.Text.Length == 0
                    ? text
                    : _liveCommandCard.Text + "\n" + text;
            }

            // 命令输出期间同样节流：每秒 5 次全量令牌遍历没必要
            UpdateTokens(force: false);
            ScrollToEnd();
        }

        /// <summary>
        /// 一轮收尾：把还没吐完的字一次性落干净再停表（纯对话那条路径靠它）
        /// </summary>
        private void StopStreaming()
        {
            FlushStreaming();

            lock (_streamGate)
            {
                _streamTarget = null;
                _streamBuffer.Clear();
                _revealOffset = 0;
                _reasoningBuffer.Clear();
            }

            StopStreamTimer();
        }

        /// <summary>
        /// 丢掉还没落进界面的增量
        /// </summary>
        private void DiscardStreaming()
        {
            lock (_streamGate)
            {
                _streamBuffer.Clear();
                _revealOffset = 0;
                _reasoningBuffer.Clear();
                _streamTarget = null;
            }

            StopStreamTimer();
        }

        /// <summary>
        /// 停掉吐字计时器，并允许下一次增量重新排一次刷新
        /// </summary>
        private void StopStreamTimer()
        {
            _streamTimer?.Stop();
            _streamScheduled = false;
            _revealLastUtc = DateTime.MinValue;
        }

        /// <summary>
        /// 推理过程增量 → 「这一步」的思考块
        /// </summary>
        private Action<string> ReasoningSink(ChatMessage assistant)
        {
            var loggedFirst = false;
            return delta =>
            {
                if (assistant == null || string.IsNullOrEmpty(delta))
                {
                    return;
                }

                if (!loggedFirst)
                {
                    loggedFirst = true;
                    ChatDiagnostics.Log("UI：推理链路已收到首个增量（" + delta.Length + " 字符）");
                }

                // 与正文一样先缓冲，再由吐字计时器统一落盘
                // 以前每个推理分片都各自 BeginInvoke + 滚动，是「推理时界面发飘」的主因
                lock (_streamGate)
                {
                    _streamTarget = assistant;
                    _reasoningBuffer.Append(delta);
                }

                ScheduleFlush();
            };
        }

        /// <summary>
        /// 当前这一步开始思考的时刻（用于在「思考中…」后显示用时）
        /// </summary>
        private DateTime _thinkingStartedUtc = DateTime.MinValue;

        /// <summary>
        /// 「思考用时」的短文本：不足 1 分钟用秒，否则 m分s秒
        /// </summary>
        private string ThinkingElapsed()
        {
            if (_thinkingStartedUtc == DateTime.MinValue)
            {
                return string.Empty;
            }

            var seconds = (DateTime.UtcNow - _thinkingStartedUtc).TotalSeconds;
            return seconds < 60
                ? seconds.ToString("0.0") + "s"
                : (int)(seconds / 60) + "m" + (int)(seconds % 60) + "s";
        }

        /// <summary>
        /// 把推理增量写进「当前这一步」的思考块；这一步还没有块就新起一块（追加在末尾）
        /// </summary>
        private void AppendStepThinking(ChatMessage message, string delta)
        {
            if (message == null || string.IsNullOrEmpty(delta))
            {
                return;
            }

            if (_turnThinking == null)
            {
                _turnThinking = ChatSession.BeginThinking(message);

                // 推理内容**默认折叠** ：只留一行「思考中...」，点它才看内容
                // 「思考时自动展开」是设置里的可选项，默认关闭 —— 展开会把正文顶下去
                _turnThinking.IsBodyVisible = _settings.ExpandThinking;

                // 记下开始时刻：行上顺带显示「思考中… · 3.2s」，推理模型的等待有了量级感
                _thinkingStartedUtc = DateTime.UtcNow;
                _turnThinking.ThinkingLabel = ThinkingDots.Label(_dotsPhase) + " · " + ThinkingElapsed();

                StartThinkingDots();
            }

            _turnThinking.Text += delta;
        }

        /// <summary>
        /// 「这一步想完了」：收起这块并交出所有权（下一步的推理会自动新起一块）
        /// </summary>
        private void CloseStepThinking()
        {
            // 收尾前先把还在缓冲里的推理落干净，否则这一步最后的思考会被算到下一步头上
            FlushStreaming();

            if (_turnThinking == null)
            {
                return;
            }

            // 想完了：点动画停下并给出结论（带总用时），行本身留在对话里（随时可展开看全文）
            _turnThinking.ThinkingLabel = ThinkingDots.Done + " · " + ThinkingElapsed();
            _turnThinking.IsBodyVisible = false;
            _turnThinking = null;
            _thinkingStartedUtc = DateTime.MinValue;

            _dotsTimer?.Stop();
        }

        /// <summary>
        /// 启动「思考中...」的点动画（一直来回，直到这一步想完）
        /// </summary>
        private void StartThinkingDots()
        {
            if (_dotsTimer == null)
            {
                _dotsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(320),
                };
                _dotsTimer.Tick += (_, __) => TickThinkingDots();
            }

            if (!_dotsTimer.IsEnabled)
            {
                _dotsTimer.Start();
            }
        }

        private void TickThinkingDots()
        {
            if (_turnThinking == null)
            {
                _dotsTimer?.Stop();
                return;
            }

            _dotsPhase = ThinkingDots.Next(_dotsPhase);
            _turnThinking.ThinkingLabel = ThinkingDots.Label(_dotsPhase) + " · " + ThinkingElapsed();
        }

        private void UpdateSendState()
        {
            var busy = _session.IsBusy;

            // 只挂提及 / 只贴图也算「有内容」，发送按钮该亮起
            var hasInput = Input.Text.Trim().Length > 0
                || _pendingImages.Count > 0
                || _pendingMentions.Count > 0;

            // 生成中按钮**仍然是发送**：点它就是排队，不会再有「发送/停止」两副面孔
            SendIcon.IconName = "send-up";
            SendBtn.ToolTip = busy
                ? "发送（当前任务结束后自动开始）(Ctrl+Enter)"
                : "发送 (Ctrl+Enter)";
            SendBtn.IsEnabled = hasInput;

            RefreshRequestBar();
            UpdateSessionHeader();
            UpdateNewTaskButton();
        }

        /// <summary>
        /// 刷新会话头：标题取首条用户消息，live dot 随「是否正在生成」变色
        /// （生成中为强调色，空闲为灰）没有消息时显示「新会话」
        /// </summary>
        private void UpdateSessionHeader()
        {
            if (SessionTitle == null)
            {
                return;
            }

            var firstUser = _session.Messages
                .FirstOrDefault(message => message.IsUser && !string.IsNullOrWhiteSpace(message.Text));

            SessionTitle.Text = firstUser != null
                ? firstUser.Text.Trim()
                : "新会话";
            SessionTitle.ToolTip = SessionTitle.Text;

            if (LiveDot != null)
            {
                var brush = TryFindResource(_session.IsBusy ? "Ap.Teal" : "Ap.IconDim") as Brush;
                if (brush != null)
                {
                    LiveDot.Fill = brush;
                }
            }
        }

        /// <summary>
        /// 刷新令牌条
        /// </summary>
        private void UpdateTokens(bool force = true)
        {
            if (!force && DateTime.UtcNow - _lastTokenRefreshUtc < TokenRefreshInterval)
            {
                return;
            }

            _lastTokenRefreshUtc = DateTime.UtcNow;

            var usage = _session.EstimateTokens();
            var total = _session.ContextWindow;

            var inPct = total <= 0 ? 0 : Math.Min(100, usage.Input / total * 100);
            var outPct = total <= 0 ? 0 : Math.Min(100, usage.Output / total * 100);
            var restPct = Math.Max(0, 100 - inPct - outPct);

            TkInCol.Width = new GridLength(inPct, GridUnitType.Star);
            TkOutCol.Width = new GridLength(outPct, GridUnitType.Star);
            TkRestCol.Width = new GridLength(restPct, GridUnitType.Star);

            var usedPct = total <= 0 ? 0 : usage.Used / total * 100;
            TkNum.Text = Format(usage.Used) + " / " + Format(total);
            TkPct.Text = usedPct.ToString("0.0") + "%";
            // 明细行是「左标签 / 右数值」两栏，这里只写数值（标签在 XAML 里写死）
            TkInTxt.Text = Format(usage.Input);
            TkOutTxt.Text = Format(usage.Output);
            TkFree.Text = Format(Math.Max(0, total - usage.Used));

            // 费用估算（整个会话）：本地模型为 0，未收录的模型按兜底价折算
            var cost = "≈ " + ModelPricing.FormatCost(_settings.Model, usage.Input, usage.Output);

            // 提示缓存命中（P2）：把「服务端按折扣价计费的那部分输入」也显示出来，
            // 让用户能直观看到缓存有没有生效 —— 命中率越高，长任务的输入花费越低
            var conversation = UsageStore.ForConversation(_session.ConversationId);
            if (conversation.CachedTokens > 0 && conversation.InputTokens > 0)
            {
                var hitRate = conversation.CachedTokens / conversation.InputTokens * 100.0;
                cost += " · 缓存 " + Format(conversation.CachedTokens) + "（" + hitRate.ToString("0") + "%）";
            }

            TkCost.Text = cost;
        }

        private static string Format(double value)
        {
            return value < 1000 ? value.ToString("0") : (value / 1000.0).ToString("0.0") + "k";
        }

        private void AutoResizeInput()
        {
            // 每敲一个字符都会调到这里：同一帧内只安排一次量高，避免快速输入时排队一堆无用布局
            if (_autoResizeScheduled)
            {
                return;
            }

            _autoResizeScheduled = true;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _autoResizeScheduled = false;
                    var lines = Math.Max(2, Input.LineCount);
                    Input.Height = Math.Min(150, Math.Max(52, (lines * 19) + 20));
                }),
                DispatcherPriority.Background);
        }

        /// <summary>
        /// 滚到最新消息 —— <b>仅当用户还贴在底部时</b>
        /// </summary>
        private void ScrollToEnd()
        {
            if (!_followTail)
            {
                ShowJumpToBottom(true);
                return;
            }

            // 同一帧内多次请求只排一次：流式期间每帧都在调，重复排队的滚动会拖慢渲染
            if (_scrollScheduled)
            {
                return;
            }

            _scrollScheduled = true;

            // 与吐字 / Markdown 重建同用 Render 优先级：内容长高的那一帧就把视口跟到底，
            // 不会出现「文字已经长到下面、视口还停在上一帧」的跳动
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _scrollScheduled = false;
                    ScrollChatToBottom();
                    ShowJumpToBottom(false);
                }),
                DispatcherPriority.Render);
        }

        /// <summary>
        /// 真正滚到底：优先用内部 ScrollViewer（虚拟化下它也正确），
        /// 拿不到时退化成 ScrollIntoView 最后一项，避免「滚动完全失效」
        /// </summary>
        private void ScrollChatToBottom()
        {
            if (_chatScrollViewer == null)
            {
                EnsureChatScrollHook();
            }

            if (_chatScrollViewer != null)
            {
                _chatScrollViewer.ScrollToEnd();
                return;
            }

            var last = _session.Messages.LastOrDefault();
            if (last != null)
            {
                ChatScroll.ScrollIntoView(last);
            }
        }

        /// <summary>
        /// 点「回到底部」：恢复跟随并滚到最新消息
        /// </summary>
        private void JumpToBottom_Click(object sender, RoutedEventArgs e)
        {
            _followTail = true;
            ShowJumpToBottom(false);
            ScrollChatToBottom();
        }

        private void ShowJumpToBottom(bool visible)
        {
            if (JumpToBottomBtn == null)
            {
                return;
            }

            JumpToBottomBtn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 拿到 ListBox 内部的 ScrollViewer 并挂上滚动监听（只做一次）
        /// </summary>
        private void EnsureChatScrollHook()
        {
            if (_chatScrollViewer != null || ChatScroll == null)
            {
                return;
            }

            _chatScrollViewer = FindScrollViewer(ChatScroll);
            if (_chatScrollViewer != null)
            {
                _chatScrollViewer.ScrollChanged += OnChatScrollChanged;
            }
        }

        /// <summary>
        /// 记录用户是否贴在底部
        /// </summary>
        private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeight <= 0)
            {
                return;
            }

            // 纯内容增长（ExtentHeightChange != 0 且垂直偏移没动）→ 维持原状态
            if (Math.Abs(e.ExtentHeightChange) > 0.5 && Math.Abs(e.VerticalChange) < 0.5)
            {
                return;
            }

            var distanceToBottom = e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight);
            _followTail = distanceToBottom <= TailThreshold;

            if (_followTail)
            {
                ShowJumpToBottom(false);
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer viewer)
                {
                    return viewer;
                }

                var nested = FindScrollViewer(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}

