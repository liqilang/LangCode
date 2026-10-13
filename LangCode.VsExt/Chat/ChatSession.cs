using LangCode.VsExt.Domain.Catalog;
using LangCode.VsExt.Domain.Chat;
using LangCode.VsExt.Domain.Settings;
using LangCode.VsExt.Persistence;
using LangCode.VsExt.Ui.Presentation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace LangCode.VsExt.Chat
{
    /// <summary>
    /// 上下文用量（令牌条用）
    /// </summary>
    public struct TokenUsage
    {
        public double Input;
        public double Output;

        public double Used => Input + Output;
    }

    /// <summary>
    /// 一次会话的运行时状态：消息列表、历史列表、令牌估算与落盘 
    /// </summary>
    public sealed class ChatSession : ObservableObject
    {
        private const int MaxHistoryItems = 50;

        /// <summary>
        /// 令牌估算的缓存 
        /// </summary>
        private TokenUsage _cachedUsage;
        private int _cachedMessageCount = -1;
        private int _cachedCharCount = -1;

        private string _currentModel = string.Empty;
        private int _contextWindow = 200000;
        private bool _isBusy;

        public ChatSession()
        {
            ConversationId = Guid.NewGuid().ToString("N");

            // 「本次变更」摘要的汇总属性都由这份集合派生：集合一变就整体通知，
            // 界面（标题栏的 +add / −del / N 文件）不必自己再算一遍
            SessionChanges.CollectionChanged += (_, __) =>
            {
                Raise(nameof(HasSessionChanges));
                Raise(nameof(ChangedFileCount));
                Raise(nameof(ChangedFileLabel));
                Raise(nameof(ChangedAddTotal));
                Raise(nameof(ChangedDelTotal));
                Raise(nameof(ChangedAddLabel));
                Raise(nameof(ChangedDelLabel));
            };
        }

        public string ConversationId { get; private set; }

        /// <summary>
        /// 会话消息（UI 直接绑定）
        /// </summary>
        public ObservableCollection<ChatMessage> Messages { get; } = [];

        /// <summary>
        /// 历史记录（UI 直接绑定）
        /// </summary>
        public ObservableCollection<ConversationSummary> History { get; } = [];

        /// <summary>
        /// 本次会话改动过的文件（「本次变更」摘要区，按文件聚合、UI 直接绑定）
        /// </summary>
        public ObservableCollection<SessionChange> SessionChanges { get; } = [];

        /// <summary>
        /// 本次会话是否改过文件（决定摘要区是否显示）
        /// </summary>
        public bool HasSessionChanges => SessionChanges.Count > 0;

        /// <summary>
        /// 改动过的文件数
        /// </summary>
        public int ChangedFileCount => SessionChanges.Count;

        /// <summary>
        /// 累计新增行数
        /// </summary>
        public int ChangedAddTotal
        {
            get
            {
                var total = 0;
                foreach (var change in SessionChanges)
                {
                    total += change.Added;
                }

                return total;
            }
        }

        /// <summary>
        /// 累计删除行数
        /// </summary>
        public int ChangedDelTotal
        {
            get
            {
                var total = 0;
                foreach (var change in SessionChanges)
                {
                    total += change.Deleted;
                }

                return total;
            }
        }

        /// <summary>
        /// 标题上的「+N」
        /// </summary>
        public string ChangedAddLabel => "+" + ChangedAddTotal;

        /// <summary>
        /// 标题上的「−N」
        /// </summary>
        public string ChangedDelLabel => "−" + ChangedDelTotal;

        /// <summary>
        /// 标题上的「N 文件」
        /// </summary>
        public string ChangedFileLabel => ChangedFileCount + " 文件";

        /// <summary>
        /// 记录一次真正落盘的改动（按文件聚合：同一文件改多次只留一行，行数累加）
        /// </summary>
        /// <returns>聚合后的那一行（路径为空时返回 null）</returns>
        public SessionChange RecordChange(SessionChangeKind kind, string path, int added, int deleted)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var relative = path.Trim();
            var existing = SessionChanges.FirstOrDefault(
                change => string.Equals(change.Path, relative, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new SessionChange
                {
                    Kind = kind,
                    Path = relative,
                    Added = Math.Max(0, added),
                    Deleted = Math.Max(0, deleted),
                };
                SessionChanges.Add(existing);
                return existing;
            }

            existing.Added = Math.Max(0, existing.Added + added);
            existing.Deleted = Math.Max(0, existing.Deleted + deleted);

            // 后续又被「新建」覆盖（例如先改后建）：以新增为准，标记仍能一眼看出
            if (kind == SessionChangeKind.Add && existing.Kind == SessionChangeKind.Modify)
            {
                existing.Kind = SessionChangeKind.Add;
            }

            return existing;
        }

        /// <summary>
        /// 清空「本次变更」摘要（换 / 新建会话时调用）
        /// </summary>
        public void ClearSessionChanges()
        {
            SessionChanges.Clear();
        }

        public string CurrentModel
        {
            get { return _currentModel; }
            set { Set(ref _currentModel, value ?? string.Empty); }
        }

        public int ContextWindow
        {
            get { return _contextWindow; }
            set { Set(ref _contextWindow, value <= 0 ? 200000 : value); }
        }

        private int _compactThresholdPercent = 80;

        /// <summary>
        /// 历史折叠阈值（百分比，设置页的「自动压缩阈值」）
        /// 上下文估算超过它时，<see cref="BuildTurns"/> 会把较早的轮次折叠掉
        /// </summary>
        public int CompactThresholdPercent
        {
            get { return _compactThresholdPercent; }
            set { _compactThresholdPercent = value <= 0 ? 80 : Math.Min(100, value); }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            set { Set(ref _isBusy, value); }
        }

        // ==================== 消息 ====================

        public ChatMessage AddUserMessage(string text)
        {
            return AddUserMessage(text, null);
        }

        /// <summary>
        /// 加入一条用户消息（可附带图片）
        /// </summary>
        public ChatMessage AddUserMessage(string text, IList<ChatImage> images)
        {
            var message = new ChatMessage
            {
                Role = "user",
                Text = (text ?? string.Empty).Trim(),
                Model = CurrentModel,
            };

            if (images != null)
            {
                foreach (var image in images)
                {
                    if (image != null)
                    {
                        message.Images.Add(image);
                    }
                }
            }

            Messages.Add(message);
            MarkLast();
            return message;
        }

        public ChatMessage BeginAssistantMessage(string model)
        {
            var message = new ChatMessage
            {
                Role = "assistant",
                Model = model ?? CurrentModel,
                IsStreaming = true,
            };
            Messages.Add(message);
            MarkLast();
            return message;
        }

        /// <summary>
        /// 从指定消息开始（含它）丢弃其后所有内容 —— 「编辑已发送的消息并重发」用 
        /// </summary> 
        public int TruncateFrom(ChatMessage message)
        {
            var index = message == null ? -1 : Messages.IndexOf(message);
            if (index < 0)
            {
                return 0;
            }

            var removed = Messages.Count - index;
            for (var i = Messages.Count - 1; i >= index; i--)
            {
                Messages.RemoveAt(i);
            }

            MarkLast();
            return removed;
        }

        /// <summary>
        /// 「从这里分叉」：保留到指定消息为止（含），丢弃其后内容，并换一个新的会话 id ——
        /// 相当于把这段对话复制成一个新分支继续，原分支的内容仍在历史里
        /// </summary>
        /// <returns>消息不在当前会话里时返回 false</returns>
        public bool ForkFrom(ChatMessage message)
        {
            var index = message == null ? -1 : Messages.IndexOf(message);
            if (index < 0)
            {
                return false;
            }

            for (var i = Messages.Count - 1; i > index; i--)
            {
                Messages.RemoveAt(i);
            }

            ConversationId = Guid.NewGuid().ToString("N");
            MarkLast();
            return true;
        }

        /// <summary>
        /// 在当前会话里全文搜索消息（用户正文、助手正文、代码卡、推理过程、待办、问答）
        /// </summary>
        public IList<ChatMessage> Search(string query)
        {
            var result = new List<ChatMessage>();
            var text = (query ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return result;
            }

            foreach (var message in Messages)
            {
                if (ContainsText(message, text))
                {
                    result.Add(message);
                }
            }

            return result;
        }

        /// <summary>
        /// 某条消息是否包含关键字（大小写不敏感）
        /// </summary>
        public static bool ContainsText(ChatMessage message, string query)
        {
            if (message == null || string.IsNullOrEmpty(query))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(message.Text) &&
                message.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(message.Thinking) &&
                message.Thinking.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            foreach (var block in message.Blocks)
            {
                if (!string.IsNullOrEmpty(block.Text) &&
                    block.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (block.Kind == ChatBlockKind.Question &&
                    !string.IsNullOrEmpty(block.AnswerText) &&
                    block.AnswerText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (block.Kind == ChatBlockKind.Todo)
                {
                    foreach (var item in block.Todos)
                    {
                        if (!string.IsNullOrEmpty(item.Text) &&
                            item.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 把当前会话导出成 Markdown（标题取首条用户消息）
        /// </summary>
        public string ExportMarkdown()
        {
            var firstUser = Messages.FirstOrDefault(m => m.IsUser && !string.IsNullOrWhiteSpace(m.Text));
            var title = firstUser != null ? firstUser.Text : string.Empty;
            return TranscriptExporter.ToMarkdown(title, Messages);
        }

        /// <summary>
        /// 维护「最后一条」标记（会话内部用）
        /// </summary>
        private void MarkLast()
        {
            for (var i = 0; i < Messages.Count; i++)
            {
                Messages[i].IsLast = i == Messages.Count - 1;
            }
        }

        /// <summary>
        /// 向助手消息追加正文增量 
        /// </summary>
        public static ChatBlock AppendText(ChatMessage message, string delta)
        {
            if (message == null || string.IsNullOrEmpty(delta))
            {
                return null;
            }

            var block = message.Blocks.LastOrDefault();
            if (block == null || block.Kind != ChatBlockKind.Text)
            {
                block = new ChatBlock { Kind = ChatBlockKind.Text };
                message.Blocks.Add(block);
            }

            block.Text += delta;
            return block;
        }

        /// <summary>
        /// 向助手消息追加 Thinking 增量（复用消息里第一块思考）
        /// </summary>
        public static void AppendThinking(ChatMessage message, string delta)
        {
            if (message == null || string.IsNullOrEmpty(delta))
            {
                return;
            }

            var block = message.Blocks.FirstOrDefault(b => b.Kind == ChatBlockKind.Thinking);
            if (block == null)
            {
                // 默认**折叠**：推理过程只在用户想看的时候点开，不占正文位置
                block = new ChatBlock
                {
                    Kind = ChatBlockKind.Thinking,
                    Title = "思考中",
                    IsBodyVisible = false,
                };
                message.Blocks.Insert(0, block);
            }

            block.Text += delta;
        }

        /// <summary>
        /// 开一块<b>新的</b>思考块并追加到消息末尾（每一步一块）
        /// </summary>
        public static ChatBlock BeginThinking(ChatMessage message)
        {
            var block = new ChatBlock
            {
                Kind = ChatBlockKind.Thinking,

                // 标题就叫「思考中」 ：一行「思考中...」，点一下展开看内容
                // 那一串点由界面按 ThinkingDots 循环刷新，这里先给第一帧
                Title = "思考中",
                ThinkingLabel = ThinkingDots.Label(0),

                // **默认折叠**：每一步留一行，想看细节再点开，不占正文位置
                IsBodyVisible = false,
            };

            message.Blocks.Add(block);
            return block;
        }

        /// <summary>
        /// 开一块<b>新的</b>正文块并追加到消息末尾（每一步一块）
        /// </summary>
        public static ChatBlock BeginText(ChatMessage message)
        {
            var block = new ChatBlock { Kind = ChatBlockKind.Text };
            message.Blocks.Add(block);
            return block;
        }

        /// <summary>
        /// 开启新的正文块（在已有正文后插入卡片时用）
        /// </summary>
        public static ChatBlock BeginBlock(ChatMessage message, ChatBlockKind kind, string title = null, string path = null)
        {
            var block = new ChatBlock
            {
                Kind = kind,
                Title = title ?? string.Empty,
                Path = path ?? string.Empty,
            };
            message.Blocks.Add(block);
            return block;
        }

        public static void Complete(ChatMessage message)
        {
            if (message != null)
            {
                message.IsStreaming = false;
            }
        }

        /// <summary>
        /// 把消息历史转成后端需要的轮次（助手消息取纯文本拼接，并在接近上限时折叠早期轮次）
        /// </summary>
        public IList<ChatTurn> BuildTurns()
        {
            var turns = new List<ChatTurn>();
            foreach (var message in Messages)
            {
                if (message.IsUser)
                {
                    // 纯图（没打字）也要发出去：多模态场景里「只贴一张报错截图」很常见
                    if (!string.IsNullOrWhiteSpace(message.Text) || message.Images.Count > 0)
                    {
                        turns.Add(new ChatTurn("user", message.Text ?? string.Empty, [.. message.Images]));
                    }

                    continue;
                }

                var text = PlainTextOf(message);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    turns.Add(new ChatTurn("assistant", text));
                }
            }

            return Compact(turns);
        }

        /// <summary>
        /// 接近上下文上限时折叠较早的轮次 
        /// </summary>
        private IList<ChatTurn> Compact(IList<ChatTurn> turns)
        {
            if (turns.Count <= 2 || ContextWindow <= 0)
            {
                return turns;
            }

            var threshold = Math.Max(10, Math.Min(100, _compactThresholdPercent));
            var budget = ContextWindow * (threshold / 100.0);

            if (TotalTokens(turns) <= budget)
            {
                return turns;
            }

            // 最近一段最多占预算的一半：另一半留给系统提示词与下一轮输出
            var recentBudget = budget / 2.0;
            var keepFrom = turns.Count;
            var recent = 0.0;

            for (var i = turns.Count - 1; i >= 0; i--)
            {
                recent += EstimateTokensFor(turns[i].Content) + 4;
                keepFrom = i;
                if (recent >= recentBudget)
                {
                    break;
                }
            }

            // 已经能装下全部（或只剩首条要保留）：没有可折叠的部分
            if (keepFrom <= 1)
            {
                return turns;
            }

            var compacted = new List<ChatTurn>
            {
                turns[0],
                new ChatTurn(
                "assistant",
                "（为节省上下文，较早的 " + (keepFrom - 1) + " 条对话已折叠需要其中细节时，请让我重新读取相关文件）")
            };

            for (var i = keepFrom; i < turns.Count; i++)
            {
                compacted.Add(turns[i]);
            }

            return compacted;
        }

        private static double TotalTokens(IList<ChatTurn> turns)
        {
            double total = 0;
            foreach (var turn in turns)
            {
                total += TokenEstimator.EstimateMessage(turn.Content);
            }

            return total;
        }

        /// <summary>
        /// 助手消息的纯文本（正文 + 代码卡 + 长任务编排卡片，供再次发回模型时使用）
        /// </summary>
        private static string PlainTextOf(ChatMessage message)
        {
            return string.Join(
                "\n",
                message.Blocks
                    .Select(PlainTextOfBlock)
                    .Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        /// <summary>
        /// 单个内容块的纯文本（不同块取文本的地方不一样）
        /// </summary>
        private static string PlainTextOfBlock(ChatBlock block)
        {
            switch (block.Kind)
            {
                case ChatBlockKind.Text:
                case ChatBlockKind.CodeCard:
                    return block.Text;

                case ChatBlockKind.Todo:
                    // 清单不在 Text 里，而是 Todos 集合 —— 用 Render 还原成 [ ]/[-]/[x] 文本
                    return TodoListParser.Render(block.Todos);

                case ChatBlockKind.Question:
                    return block.IsAnswered && block.AnswerText.Length > 0
                        ? block.Text + "\n" + block.AnswerText
                        : string.Empty;

                case ChatBlockKind.SubTask:
                    return "子任务：" + block.Text;

                default:
                    return string.Empty;
            }
        }

        // ==================== 令牌估算 ====================

        public TokenUsage EstimateTokens()
        {
            // ① 廉价指纹：消息数 + 字符数没变就复用上次结果，避免反复拼接整段对话
            var count = Messages.Count;
            var chars = 0;

            foreach (var message in Messages)
            {
                chars += message.Text.Length;
                if (message.IsUser)
                {
                    continue;
                }

                foreach (var block in message.Blocks)
                {
                    chars += block.Text.Length;

                    // 清单内容与提问回答不在 Text 里，也要计入指纹，否则它们变了缓存却不失效
                    if (block.Kind == ChatBlockKind.Todo)
                    {
                        foreach (var item in block.Todos)
                        {
                            chars += item.Text.Length;
                        }
                    }
                    else if (block.Kind == ChatBlockKind.Question)
                    {
                        chars += block.AnswerText.Length;
                    }
                }
            }

            if (count == _cachedMessageCount && chars == _cachedCharCount)
            {
                return _cachedUsage;
            }

            double input = 0;
            double output = 0;

            foreach (var message in Messages)
            {
                if (message.IsUser)
                {
                    input += EstimateTokensFor(message.Text);
                }
                else
                {
                    output += EstimateTokensFor(PlainTextOf(message));
                }
            }

            var usage = new TokenUsage { Input = Math.Round(input), Output = Math.Round(output) };

            _cachedMessageCount = count;
            _cachedCharCount = chars;
            _cachedUsage = usage;

            return usage;
        }

        /// <summary>
        /// 粗粒度 token 估算 —— 实现放在 <see cref="TokenEstimator"/>，
        /// 与 Agent 循环里那份「工具往返历史」的估算保持同一口径
        /// </summary>
        private static double EstimateTokensFor(string text)
        {
            return TokenEstimator.Estimate(text);
        }

        // ==================== 会话生命周期 ====================

        public void NewConversation()
        {
            ConversationId = Guid.NewGuid().ToString("N");
            Messages.Clear();

            // 新会话 = 空白的「本次变更」：上一会话改动过哪些文件与这里无关
            ClearSessionChanges();
        }

        /// <summary>
        /// 加载已有会话（替换当前消息）
        /// </summary>
        public void ReplaceMessages(IEnumerable<ChatMessage> messages)
        {
            Messages.Clear();
            foreach (var message in messages ?? Enumerable.Empty<ChatMessage>())
            {
                Messages.Add(message);
            }

            MarkLast();
        }

        /// <summary>
        /// 把当前会话写入历史文件（受「本地会话记录」开关控制），随后刷新历史列表
        /// </summary>
        public void SaveCurrent(AppSettings settings)
        {
            if (settings != null && !settings.LocalSessionHistory)
            {
                return;
            }

            if (Messages.Count == 0)
            {
                return;
            }

            var all = ConversationStore.LoadAll();
            var snapshot = ConversationStore.Snapshot(ConversationId, CurrentModel, Messages);
            var index = all.FindIndex(c => c.Id == ConversationId);
            if (index >= 0)
            {
                all[index] = snapshot;
            }
            else
            {
                all.Insert(0, snapshot);
            }

            if (all.Count > MaxHistoryItems)
            {
                all = all.OrderByDescending(c => c.UpdatedUtc).Take(MaxHistoryItems).ToList();
            }

            ConversationStore.SaveAll(all);
            ReloadHistory();
        }

        public void ReloadHistory()
        {
            History.Clear();
            foreach (var stored in ConversationStore.LoadAll().OrderByDescending(c => c.UpdatedUtc))
            {
                History.Add(new ConversationSummary
                {
                    Id = stored.Id,
                    Title = stored.Title,
                    Preview = stored.Preview,
                    Model = stored.Model,
                    UpdatedUtc = stored.UpdatedUtc,
                    MessageCount = stored.Messages?.Count ?? 0,
                });
            }
        }

        public bool LoadConversation(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            var stored = ConversationStore.LoadAll().FirstOrDefault(c => c.Id == id);
            if (stored == null)
            {
                return false;
            }

            ConversationId = stored.Id;
            if (!string.IsNullOrWhiteSpace(stored.Model))
            {
                CurrentModel = stored.Model;
            }

            ReplaceMessages(ConversationStore.ToMessages(stored));
            return true;
        }

        /// <summary>
        /// 手动压缩上下文：保留首条用户提示词与最近 <paramref name="keepRecent"/> 条消息，
        /// 把中间较早的对话合并成一条说明，立即释放上下文占用
        /// </summary>
        /// <returns>被折叠掉的消息条数（0 表示没有可压缩的内容）</returns>
        public int CompactNow(int keepRecent = 6)
        {
            if (keepRecent < 1)
            {
                keepRecent = 1;
            }

            // 定位首条用户提示词：它是「任务是什么」的锚点，必须保留
            var firstUserIndex = -1;
            for (var i = 0; i < Messages.Count; i++)
            {
                if (Messages[i].IsUser && !string.IsNullOrWhiteSpace(Messages[i].Text))
                {
                    firstUserIndex = i;
                    break;
                }
            }

            var keepFrom = Math.Max(0, Messages.Count - keepRecent);

            // 没有首条提示词，或最近的消息已经紧挨着它：没有可折叠的中间段
            if (firstUserIndex < 0 || keepFrom <= firstUserIndex + 1)
            {
                return 0;
            }

            var removed = keepFrom - (firstUserIndex + 1);

            for (var i = keepFrom - 1; i > firstUserIndex; i--)
            {
                Messages.RemoveAt(i);
            }

            Messages.Insert(firstUserIndex + 1, new ChatMessage
            {
                Role = "assistant",
                Text = "（已压缩 " + removed + " 条较早的对话以释放上下文；需要其中细节时，请让我重新读取相关文件）",
            });

            MarkLast();
            return removed;
        }

        public void DeleteConversation(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var all = ConversationStore.LoadAll();
            all.RemoveAll(c => c.Id == id);
            ConversationStore.SaveAll(all);
            ReloadHistory();
        }

        public void ClearHistory()
        {
            ConversationStore.SaveAll([]);
            History.Clear();
        }
    }
}

