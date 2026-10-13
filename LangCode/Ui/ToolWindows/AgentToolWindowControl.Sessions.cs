using LangCode.Chat;
using LangCode.Domain.Catalog;
using LangCode.Domain.Chat;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：会话新建 / 载入历史 / 主页类定义与字段见 AgentToolWindowControl.xaml.cs
    /// </summary>
    public partial class AgentToolWindowControl
    {
        // ============================================================
        // 会话：新建 / 载入 / 历史联动
        // ============================================================
        private void BtnNewChat_Click(object sender, RoutedEventArgs e) => NewConversation();

        /// <summary>
        /// 开一个新会话
        /// </summary>
        private void NewConversation()
        {
            if (_session.IsBusy)
            {
                StopCurrent();
            }

            _session.NewConversation();
            _session.ReloadHistory();

            _workspaceNoticeShown = false;

            // 新会话：把输入框上方还挂着的提及芯片一并清掉
            TakePendingMentions();

            ResetTurnActions();
            HistoryDrawer.SetActiveConversation(_session.ConversationId);

            UpdateSendState();
            UpdateTokens();
            ScrollToEnd();
            Input.Focus();
        }

        // ============================================================
        // 会话操作：复制提示词 / 压缩上下文 / 删除会话 / 开始新任务
        // ============================================================

        private DispatcherTimer _sessionHintTimer;

        /// <summary>
        /// 复制首条用户提示词到剪贴板
        /// </summary>
        private void CopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            var firstUser = _session.Messages.FirstOrDefault(m => m.IsUser && !string.IsNullOrWhiteSpace(m.Text));
            if (firstUser == null)
            {
                ShowSessionActionHint("还没有可复制的提示词");
                return;
            }

            try
            {
                Clipboard.SetText(firstUser.Text);
                ShowSessionActionHint("已复制提示词");
            }
            catch (Exception)
            {
                ShowSessionActionHint("复制失败");
            }
        }

        /// <summary>
        /// 手动压缩上下文：折叠较早的中间对话（保留首条提示词与最近几条）
        /// </summary>
        private void CompactContext_Click(object sender, RoutedEventArgs e)
        {
            var removed = _session.CompactNow();
            if (removed <= 0)
            {
                ShowSessionActionHint("没有可压缩的上下文");
                return;
            }

            UpdateTokens();
            UpdateSessionHeader();
            ShowSessionActionHint("已压缩 " + removed + " 条较早对话");
        }

        /// <summary>
        /// 删除当前会话（删除后开一个新的空会话）
        /// </summary>
        private void DeleteSession_Click(object sender, RoutedEventArgs e)
        {
            if (_session.IsBusy)
            {
                StopCurrent();
            }

            var answer = MessageBox.Show(
                "确定删除当前会话吗？删除后无法恢复。",
                "LangCode",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK)
            {
                return;
            }

            var id = _session.ConversationId;
            _session.DeleteConversation(id);
            NewConversation();
        }

        /// <summary>
        /// 任务完成后出现在输入框上方：清空会话，开始新任务
        /// </summary>
        private void NewTask_Click(object sender, RoutedEventArgs e) => NewConversation();

        /// <summary>
        /// 是否处于「任务已完成」状态（空闲、无待批 / 待答，且最后一条是已完成的助手消息）
        /// </summary>
        private bool IsTaskComplete()
        {
            if (_session.IsBusy || HasPendingApproval || HasPendingQuestion)
            {
                return false;
            }

            var last = _session.Messages.LastOrDefault();
            return last != null && !last.IsUser && !last.IsStreaming;
        }

        /// <summary>
        /// 同步输入框上方「开始新任务」按钮的显隐：每次任务跑完就出现
        /// </summary>
        private void UpdateNewTaskButton()
        {
            if (NewTaskBtn == null)
            {
                return;
            }

            NewTaskBtn.Visibility = IsTaskComplete() ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 会话操作按钮的短暂提示（约 1.8s 后自动隐藏）
        /// </summary>
        private void ShowSessionActionHint(string text)
        {
            if (SessionActionHint == null)
            {
                return;
            }

            SessionActionHint.Text = text ?? string.Empty;
            SessionActionHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

            _sessionHintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
            _sessionHintTimer.Stop();
            _sessionHintTimer.Tick -= SessionHintTimer_Tick;
            _sessionHintTimer.Tick += SessionHintTimer_Tick;
            _sessionHintTimer.Start();
        }

        private void SessionHintTimer_Tick(object sender, EventArgs e)
        {
            _sessionHintTimer?.Stop();
            if (SessionActionHint != null)
            {
                SessionActionHint.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 重建主页内容：快捷入口 + 最近会话
        /// </summary>
        private void BuildHomePanel()
        {
            if (HomeActions == null || HomeRecents == null)
            {
                return;
            }

            // ① 快捷开始：两列卡片（图标 + 标题 + 一句说明）复用斜杠命令表，主页与输入框里的 / 命令永远一致
            HomeActions.Children.Clear();
            foreach (var command in SlashCommands.All)
            {
                if (command.IsAction || command.InsertText.Length == 0)
                {
                    continue;   // 动作型命令（/clear）不适合摆在主页
                }

                var button = new Button
                {
                    Style = TryFindResource("HomeCard") as Style,
                    Tag = command.InsertText,
                    Margin = new Thickness(0, 0, 9, 9),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ToolTip = command.Description,
                };

                button.Click += HomeAction_Click;

                var body = new StackPanel { Orientation = Orientation.Vertical };
                var cardGlyph = new Ui.Primitive.Icon
                {
                    IconName = IconForCommand(command.Key),
                    Width = 15,
                    Height = 15,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                cardGlyph.SetResourceReference(Ui.Primitive.Icon.ForegroundProperty, "Ap.FgDim");
                body.Children.Add(cardGlyph);

                var cardTitle = new TextBlock
                {
                    Text = command.Title,
                    FontSize = 12.5,
                    Margin = new Thickness(0, 7, 0, 0),
                };
                cardTitle.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgStrong");
                body.Children.Add(cardTitle);

                var cardDesc = new TextBlock
                {
                    Text = command.Description,
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 16,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                cardDesc.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
                body.Children.Add(cardDesc);

                button.Content = body;
                HomeActions.Children.Add(button);
            }

            // ② 最近会话（最多 5 条）
            BuildHomeRecents();
        }

        /// <summary>
        /// 重建主页的「最近会话」列表：历史一变就刷新，保证刚完成的会话实时出现
        /// </summary>
        private void BuildHomeRecents()
        {
            if (HomeRecents == null)
            {
                return;
            }

            HomeRecents.Children.Clear();
            var recents = _session.History.Take(5).ToList();

            if (HomeEmptyHint != null)
            {
                HomeEmptyHint.Visibility = recents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            foreach (var summary in recents)
            {
                HomeRecents.Children.Add(BuildRecentCard(summary));
            }
        }

        /// <summary>
        /// 斜杠命令 → 主页卡片图标（找不到就用 zap 兜底）
        /// </summary>
        private static string IconForCommand(string key)
        {
            switch ((key ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "plan": return "book";
                case "review": return "search";
                case "commit": return "check";
                case "test": return "check";
                case "explain": return "file";
                default: return "zap";
            }
        }

        /// <summary>
        /// 主页上的一行「最近会话」（hover 变浅底，由 HistoryRow 样式提供）
        /// </summary>
        private FrameworkElement BuildRecentCard(ConversationSummary summary)
        {
            var card = new Border
            {
                Tag = summary.Id,
                Style = TryFindResource("HistoryRow") as Style,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                ToolTip = summary.Preview,
            };

            card.MouseLeftButtonUp += HomeRecent_Click;

            var body = new StackPanel { Orientation = Orientation.Vertical };
            var recentTitle = new TextBlock
            {
                Text = summary.Title,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            recentTitle.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgStrong");
            body.Children.Add(recentTitle);

            var recentMeta = new TextBlock
            {
                Text = summary.CountLabel + " · " + summary.TimeLabel,
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0),
            };
            recentMeta.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
            body.Children.Add(recentMeta);

            card.Child = body;
            return card;
        }

        /// <summary>
        /// 主页只在「当前会话没有任何消息」时显示；此时顶部的上下文用量条一并收起
        /// </summary>
        private void UpdateHomeVisibility()
        {
            if (HomePanel == null)
            {
                return;
            }

            var home = _session.Messages.Count == 0;

            HomePanel.Visibility = home ? Visibility.Visible : Visibility.Collapsed;

            if (TokenBar != null)
            {
                TokenBar.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>
        /// 主页快捷入口：把提示词填进输入框（不直接发送，用户还能改）
        /// </summary>
        private void HomeAction_Click(object sender, RoutedEventArgs e)
        {
            var text = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Input.Text = text;
            Input.SelectionStart = Input.Text.Length;
            Input.Focus();
        }

        /// <summary>
        /// 主页的最近会话卡片：点它直接载入
        /// </summary>
        private void HomeRecent_Click(object sender, MouseButtonEventArgs e)
        {
            var id = (sender as FrameworkElement)?.Tag as string;
            if (!string.IsNullOrEmpty(id))
            {
                e.Handled = true;
                LoadConversation(id);
            }
        }

        /// <summary>
        /// 载入一个历史会话（列表点击 / 主页最近会话都走这里）
        /// </summary>
        private void LoadConversation(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || _session.IsBusy)
            {
                return;
            }

            if (!_session.LoadConversation(id))
            {
                return;
            }

            _session.ContextWindow = _settings.ContextWindow;
            ResetTurnActions();
            HistoryDrawer.SetActiveConversation(id);

            // 从历史里选了会话就把抽屉收起来：否则用户还得再点一次「返回」才能看到对话
            HistoryDrawer.Close();

            UpdateTokens();
            ApplySettingsToUi();
            BuildHomePanel();
            UpdateHomeVisibility();
            ScrollToEnd();
        }

        /// <summary>
        /// 历史被删除 / 清空之后：正在看的会话若已不存在，就换成一个空会话
        /// </summary>
        private void OnHistoryChanged()
        {
            // 主页上的「最近的会话」也要跟着变（删掉的那条不该还留在列表里）
            BuildHomePanel();
            _session.ReloadHistory();

            if (!_session.History.Any(h => h.Id == _session.ConversationId))
            {
                NewConversation();
                return;
            }

            HistoryDrawer.SetActiveConversation(_session.ConversationId);
            UpdateTokens();
        }

    }
}

