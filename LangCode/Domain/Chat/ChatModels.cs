using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace LangCode.Domain.Chat
{
    /// <summary>
    /// 可通知对象基类（WPF 绑定用）
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            Raise(propertyName);
            return true;
        }
    }

    /// <summary>
    /// 消息里的一块内容（对应设计稿的不同卡片形态）
    /// </summary>
    public enum ChatBlockKind
    {
        /// <summary>
        /// markdown 文本段落
        /// </summary>
        Text,

        /// <summary>
        /// Thinking（可折叠）
        /// </summary>
        Thinking,

        /// <summary>
        /// 代码卡片（工具读取的文件内容等）
        /// </summary>
        CodeCard,

        /// <summary> 
        /// 点卡片（或文件名）直接在 VS 里打开 —— 对话里只留一条「AI 动过这个文件」的线索
        /// </summary>
        FileCard,

        /// <summary>
        /// Diff 卡片
        /// </summary>
        DiffCard,

        /// <summary>
        /// 操作确认卡（读取 / 命令等「无 diff 但要用户点头」的动作）
        /// 复用差异卡的模板：标题 + 目标，只是不显示 +N/−N 统计；
        /// 允许 / 拒绝在输入框上方的请求条上点（见 <see cref="RequestBar"/>）
        /// </summary>
        Approval,

        /// <summary>
        /// 批注条（对某个卡片追加的说明）
        /// </summary>
        Note,

        /// <summary>
        /// 完成提示条
        /// </summary>
        Done,

        /// <summary>
        /// 任务进度卡（长任务编排）：模型用 <c>update_todo_list</c> 维护的待办清单
        /// 这是「长任务看得见进度」的落点 —— 每完成一项就地打勾，而不是等全部做完才知道
        /// </summary>
        Todo,

        /// <summary>
        /// 提问卡：模型用 <c>ask_followup_question</c> 主动问用户，等一个回答再继续
        /// 备选答案直接点；要自由回答就在输入框里打字回车
        /// </summary>
        Question,

        /// <summary>
        /// 子任务卡：模型用 <c>new_task</c> 把一个相对独立的活拆出去独立完成
        /// </summary>
        SubTask,
    }

    /// <summary>
    /// Diff 行的类型
    /// </summary>
    public enum DiffLineKind
    {
        Context,
        Add,
        Delete,
    }

    /// <summary>
    /// 一行 diff / 一行代码
    /// </summary>
    public sealed class ChatBlockLine
    {
        public ChatBlockLine(DiffLineKind kind, string sign, string text)
        {
            Kind = kind;
            Sign = sign;
            Text = text;
        }

        public DiffLineKind Kind { get; }

        /// <summary>
        /// 行首符号（"+" / "−" / " "）
        /// </summary>
        public string Sign { get; }

        public string Text { get; }
    }

    /// <summary>
    /// 消息里附带的一张图片（多模态）
    /// </summary>
    public sealed class ChatImage
    {
        public ChatImage(string mimeType, string base64)
        {
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType.Trim();
            Base64 = base64 ?? string.Empty;
        }

        /// <summary>
        /// 如 image/png、image/jpeg
        /// </summary>
        public string MimeType { get; }

        /// <summary>
        /// 不含 <c>data:</c> 前缀的 base64
        /// </summary>
        public string Base64 { get; }

        /// <summary>
        /// 可直接放进 <c>image_url.url</c> 的 data URL
        /// </summary>
        public string DataUrl => "data:" + MimeType + ";base64," + Base64;

        /// <summary>
        /// 界面附件条上的一行说明（如「PNG · 120 KB」）
        /// </summary>
        public string Summary
        {
            get
            {
                var kind = MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    ? MimeType.Substring("image/".Length).ToUpperInvariant()
                    : MimeType.ToUpperInvariant();
                var bytes = (int)(Base64.Length * 3L / 4);
                return kind + " · " + Math.Max(1, bytes / 1024) + " KB";
            }
        }
    }

    /// <summary>
    /// 消息里的一块内容
    /// </summary>
    public sealed class ChatBlock : ObservableObject
    {
        private string _text = string.Empty;
        private string _statusText = string.Empty;
        private bool _isExpanded;

        /// <summary>
        /// 卡片主体是否可见**默认折叠** —— 工具卡片（读文件 / 列目录 / 搜索 / 差异）与推理过程
        /// 都先收成一行标题，需要时点箭头展开，正文不被大段代码挤走
        /// </summary>
        private bool _isBodyVisible;
        private bool _isAccepted;
        private bool _isRejected;
        private bool _isPendingApproval;

        public ChatBlockKind Kind { get; set; } = ChatBlockKind.Text;

        /// <summary>
        /// 卡片标题（Read File / Applied Diff / 完成提示…）
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// 卡片副标题（文件路径）
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// 待批动作对应的工具名（审批界面「本次会话始终允许」用）
        /// </summary>
        public string ApprovalTool { get; set; } = string.Empty;

        /// <summary>
        /// 待批的完整命令文本（run_command 的会话授权只放行这一条命令）
        /// </summary>
        public string ApprovalCommand { get; set; } = string.Empty;

        /// <summary>
        /// 点击路径跳转时用的行号（0 = 不跳）
        /// </summary>
        public int LineHint { get; set; }

        /// <summary>
        /// 纯文本内容（Text / Thinking / CodeCard 用）
        /// </summary>
        public string Text
        {
            get { return _text; }
            set
            {
                if (Set(ref _text, value ?? string.Empty))
                {
                    // 这两个是派生属性：文本变化会影响「能否展开」与行数，必须一并通知
                    Raise(nameof(CanExpand));
                    Raise(nameof(LineCount));
                }
            }
        }

        /// <summary>
        /// Diff 行（DiffCard 用）
        /// </summary>
        public IList<ChatBlockLine> Lines { get; set; } = [];

        /// <summary>
        /// 右上角状态文案（已应用 / 已取消）
        /// </summary>
        public string StatusText
        {
            get { return _statusText; }
            set
            {
                if (Set(ref _statusText, value ?? string.Empty))
                {
                    Raise(nameof(ShowsFooter));
                }
            }
        }

        /// <summary>
        /// 这张卡片是否正在等用户拍板（只影响视觉强调与强制展开）
        /// </summary>
        public bool IsPendingApproval
        {
            get { return _isPendingApproval; }
            set
            {
                if (!Set(ref _isPendingApproval, value))
                {
                    return;
                }

                // 等用户拍板的写操作必须**看得见差异**：卡片默认是折叠的，
                // 如果这里不展开，用户就只能对着一个「Proposed Diff」标题猜自己批的是什么
                if (value)
                {
                    IsBodyVisible = true;
                }
            }
        }

        /// <summary>
        /// 底部状态条是否可见：待批时显示「等待你在输入框上方确认」，有结论时显示结论文案
        /// 两者都没有就整条收起（只读工具卡片就是这种），不会留一条空栏
        /// </summary>
        public bool ShowsFooter => _isPendingApproval || _statusText.Length > 0;

        public bool IsAccepted
        {
            get { return _isAccepted; }
            set { Set(ref _isAccepted, value); }
        }

        public bool IsRejected
        {
            get { return _isRejected; }
            set { Set(ref _isRejected, value); }
        }

        /// <summary>
        /// 长文是否已展开（默认折叠）
        /// </summary>
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { Set(ref _isExpanded, value); }
        }

        /// <summary>
        /// 卡片主体是否可见（卡片头部的折叠按钮控制，默认折叠）
        /// </summary>
        public bool IsBodyVisible
        {
            get { return _isBodyVisible; }
            set { Set(ref _isBodyVisible, value); }
        }

        private string _thinkingLabel = "思考";

        /// <summary>
        /// 思考行的文案界面在推理期间把它换成循环的「思考中. / .. / ...」，
        /// 这一步想完再改成「思考完成」—— 只靠一个静止的标题看不出「它还在想」还是「想完了」
        /// </summary>
        public string ThinkingLabel
        {
            get { return _thinkingLabel; }
            set { Set(ref _thinkingLabel, value ?? string.Empty); }
        }

        /// <summary>
        /// 是否有可折叠长文（超过阈值的代码）
        /// </summary>
        public bool CanExpand => Kind == ChatBlockKind.CodeCard && Text.Length > 400;

        /// <summary>
        /// 示意性行数（卡片上的「· N 行」）
        /// </summary>
        public int LineCount => Text.Length == 0 ? 0 : Text.Split('\n').Length;

        /// <summary>
        /// 是否显示 +N/−N 统计（只有真正的差异卡才显示）
        /// </summary>
        public bool ShowsDiffStats => Kind == ChatBlockKind.DiffCard;

        /// <summary>
        /// 编辑类卡片（DiffCard）的「改动前」全文：点卡片开 VS 差异对比时用。
        /// 只驻留内存 —— 载入历史会话后为空，届时退回 git HEAD 里的版本作基线
        /// </summary>
        public string BeforeText { get; set; } = string.Empty;

        /// <summary>
        /// 编辑类卡片（DiffCard）的「改动后」全文（未审批时是**提议**的内容）
        /// </summary>
        public string AfterText { get; set; } = string.Empty;

        /// <summary>
        /// Diff 卡片的「+N」
        /// </summary>
        public string AddLabel => "+" + Lines.Count(l => l.Kind == DiffLineKind.Add);

        /// <summary>
        /// Diff 卡片的「−N」
        /// </summary>
        public string DeleteLabel => "−" + Lines.Count(l => l.Kind == DiffLineKind.Delete);

        // ==================== 长任务编排（待办 / 提问 / 子任务） ====================

        /// <summary>
        /// 任务清单（<see cref="ChatBlockKind.Todo"/> 用）
        /// </summary>
        public ObservableCollection<TodoItem> Todos { get; } = [];

        private string _progressText = string.Empty;

        /// <summary>
        /// 清单进度文案（「2/5 已完成 · 40%」）
        /// </summary>
        public string ProgressText
        {
            get { return _progressText; }
            set { Set(ref _progressText, value ?? string.Empty); }
        }

        private bool _hasTodos;

        /// <summary>
        /// 清单里是否有条目（决定「暂无任务」空状态是否显示）
        /// </summary>
        public bool HasTodos
        {
            get { return _hasTodos; }
            set { Set(ref _hasTodos, value); }
        }

        /// <summary>
        /// 清单里已完成的条目数（顶部「任务清单」进度条用）
        /// </summary>
        public int CompletedCount
        {
            get
            {
                var count = 0;
                foreach (var item in Todos)
                {
                    if (item.IsCompleted)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// 清单条目总数
        /// </summary>
        public int TodoTotal => Todos.Count;

        /// <summary>
        /// 完成百分比（0–100，顶部进度条宽度用）
        /// </summary>
        public int ProgressPercent
        {
            get
            {
                var total = Todos.Count;
                return total <= 0 ? 0 : (int)Math.Round(CompletedCount * 100.0 / total);
            }
        }

        /// <summary>
        /// 「已完成 / 总数」计数文案
        /// </summary>
        public string CountLabel => CompletedCount + " / " + Todos.Count;

        private IList<string> _options = [];

        /// <summary>
        /// 提问卡的备选答案（点击即回答）
        /// </summary>
        public IList<string> Options
        {
            get { return _options; }
            set
            {
                if (Set(ref _options, value ?? []))
                {
                    Raise(nameof(ShowsOptions));
                }
            }
        }

        private bool _isPendingQuestion;

        /// <summary>
        /// 提问卡：正在等用户回答（也是「输入框里的内容算回答」的开关）
        /// </summary>
        public bool IsPendingQuestion
        {
            get { return _isPendingQuestion; }
            set
            {
                if (Set(ref _isPendingQuestion, value))
                {
                    Raise(nameof(ShowsOptions));
                }
            }
        }

        private bool _isAnswered;

        /// <summary>
        /// 提问卡：已回答（按钮收起，只留结论）
        /// </summary>
        public bool IsAnswered
        {
            get { return _isAnswered; }
            set
            {
                if (Set(ref _isAnswered, value))
                {
                    Raise(nameof(ShowsAnswerCheck));
                }
            }
        }

        private bool _answerSkipped;

        /// <summary>
        /// 提问卡：这次是「跳过」而不是真回答
        /// </summary>
        public bool AnswerIsSkipped
        {
            get { return _answerSkipped; }
            set
            {
                if (Set(ref _answerSkipped, value))
                {
                    Raise(nameof(ShowsAnswerCheck));
                }
            }
        }

        /// <summary>
        /// 已作答且不是跳过 —— 结论行前显示 ✓（对齐 UIUX 模板的 .ask .picked）
        /// </summary>
        public bool ShowsAnswerCheck => _isAnswered && !_answerSkipped;

        private string _answerText = string.Empty;

        /// <summary>
        /// 提问卡：用户给出的回答
        /// </summary>
        public string AnswerText
        {
            get { return _answerText; }
            set { Set(ref _answerText, value ?? string.Empty); }
        }

        /// <summary>
        /// 是否显示备选答案按钮（等待回答且确实有选项）
        /// </summary>
        public bool ShowsOptions => _isPendingQuestion && _options != null && _options.Count > 0;

        private bool _isSubTaskRunning;

        /// <summary>
        /// 子任务卡：子任务是否还在跑（跑的时候标题上有个进行中标记）
        /// </summary>
        public bool IsSubTaskRunning
        {
            get { return _isSubTaskRunning; }
            set { Set(ref _isSubTaskRunning, value); }
        }

        private int _depth;

        /// <summary>
        /// 嵌套层级（0 = 顶层；子任务内部的卡片为 1）
        /// </summary>
        public int Depth
        {
            get { return _depth; }
            set
            {
                if (!Set(ref _depth, value))
                {
                    return;
                }

                Raise(nameof(CardMargin));
                Raise(nameof(NoteMargin));
            }
        }

        /// <summary>
        /// 卡片类（代码卡 / 差异卡）的外边距 —— 按层级左缩进
        /// </summary>
        public Thickness CardMargin => new Thickness(Depth * 16, 9, 0, 0);

        /// <summary>
        /// 提示条类（批注 / 待办 / 提问 / 子任务）的外边距 —— 按层级左缩进
        /// </summary>
        public Thickness NoteMargin => new Thickness(8 + (Depth * 16), 6, 8, 0);
    }

    /// <summary>
    /// 一条消息
    /// </summary>
    public sealed class ChatMessage : ObservableObject
    {
        public ChatMessage()
        {
            // 新加入的内容块自动继承当前嵌套层级
            // 子任务跑起来之后，它内部产生的每一张卡片（读文件 / 改文件 / 思考 / diff）都会自动缩进一格，
            // 不必在每个创建点都记得设置一次 —— 漏掉一处就会出现「这张卡到底是谁的」的困惑
            Blocks.CollectionChanged += OnBlocksChanged;
        }

        private void OnBlocksChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems == null || CurrentDepth <= 0)
            {
                return;
            }

            foreach (var item in e.NewItems)
            {
                if (item is ChatBlock block && block.Depth == 0)
                {
                    block.Depth = CurrentDepth;
                }
            }
        }

        /// <summary>
        /// 之后新加入的内容块要继承的嵌套层级（子任务运行期间为 1）
        /// </summary>
        public int CurrentDepth { get; set; }

        private string _text = string.Empty;
        private string _thinking = string.Empty;
        private bool _isStreaming;

        /// <summary>
        /// user / assistant
        /// </summary>
        public string Role { get; set; } = "assistant";

        public bool IsUser => Role == "user";

        /// <summary>
        /// 产生这条消息的模型（历史列表的徽标用）
        /// </summary>
        public string Model { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>
        /// 正文（流式增长）
        /// </summary>
        public string Text
        {
            get { return _text; }
            set
            {
                if (Set(ref _text, value ?? string.Empty))
                {
                    Raise(nameof(HasText));
                }
            }
        }

        /// <summary>
        /// Thinking（流式增长，可折叠）
        /// </summary>
        public string Thinking
        {
            get { return _thinking; }
            set
            {
                if (Set(ref _thinking, value ?? string.Empty))
                {
                    Raise(nameof(HasThinking));
                    Raise(nameof(IsThinkingExpanded));
                }
            }
        }

        private bool _thinkingExpanded;

        /// <summary>
        /// 推理过程是否展开（默认折叠：需要时点开看，不抢正文的位置）
        /// </summary>
        public bool IsThinkingExpanded
        {
            get { return _thinkingExpanded; }
            set { Set(ref _thinkingExpanded, value); }
        }

        /// <summary>
        /// 是否正在流式输出（决定是否显示光标/忙碌态）
        /// </summary>
        public bool IsStreaming
        {
            get { return _isStreaming; }
            set { Set(ref _isStreaming, value); }
        }

        private bool _isLast;

        /// <summary>
        /// 是否是当前会话的最后一条消息（会话内部维护）
        /// </summary>
        public bool IsLast
        {
            get { return _isLast; }
            set { Set(ref _isLast, value); }
        }

        private bool _isQueued;

        /// <summary>
        /// 这条用户消息还排在队列里等着发
        /// </summary>
        public bool IsQueued
        {
            get { return _isQueued; }
            set { Set(ref _isQueued, value); }
        }

        private string _checkpointId = string.Empty;

        /// <summary>
        /// 这条消息发出<b>之前</b>工作区的检查点 id（"时间旅行"的落点）。
        /// 每轮开始时打一个点挂在这条用户消息上，消息上的还原按钮据此把文件退回去。
        /// 随会话一起落盘，重开 VS 后按钮依然可用（检查点在影子仓库里长期保留）
        /// </summary>
        public string CheckpointId
        {
            get { return _checkpointId; }
            set
            {
                if (Set(ref _checkpointId, value ?? string.Empty))
                {
                    Raise(nameof(HasCheckpoint));
                }
            }
        }

        /// <summary>
        /// 有检查点：操作栏上出现「还原到此处」（关掉检查点功能 / 老会话里没有这个点时为 false）
        /// </summary>
        public bool HasCheckpoint => _checkpointId.Length > 0;

        /// <summary>
        /// 检查点的短 id（提示文案用）
        /// </summary>
        public string CheckpointShortId => _checkpointId.Length > 7 ? _checkpointId.Substring(0, 7) : _checkpointId;

        private bool _isEditing;
        private string _editText = string.Empty;

        /// <summary>
        /// 这条消息正在<b>就地编辑</b>（气泡本身变成输入框，不把内容搬回下面的输入框）
        /// </summary>
        public bool IsEditing
        {
            get { return _isEditing; }
            set
            {
                if (Set(ref _isEditing, value))
                {
                    Raise(nameof(ShowsBubble));
                    Raise(nameof(ShowsEditor));
                }
            }
        }

        /// <summary>
        /// 就地编辑的草稿
        /// </summary>
        public string EditText
        {
            get { return _editText; }
            set { Set(ref _editText, value ?? string.Empty); }
        }

        /// <summary>
        /// 非编辑态：显示气泡本体
        /// </summary>
        public bool ShowsBubble => !_isEditing;

        /// <summary>
        /// 编辑态：气泡位置换成编辑器
        /// </summary>
        public bool ShowsEditor => _isEditing;

        /// <summary>
        /// 只有用户消息可以「就地编辑并重发」
        /// </summary>
        public bool CanEdit => IsUser;

        private bool _canContinue;
        private bool _canRetry;
        private bool _canUndo;

        /// <summary>
        /// 本轮因步数上限暂停 —— 请求条上出现「继续任务」
        /// </summary>
        public bool CanContinue
        {
            get { return _canContinue; }
            set { Set(ref _canContinue, value); }
        }

        /// <summary>
        /// 本轮失败 / 被中断 —— 请求条上出现「重试」
        /// </summary>
        public bool CanRetry
        {
            get { return _canRetry; }
            set { Set(ref _canRetry, value); }
        }

        /// <summary>
        /// 本轮真的改过磁盘上的文件 —— 请求条上出现「撤销修改」
        /// </summary>
        public bool CanUndo
        {
            get { return _canUndo; }
            set { Set(ref _canUndo, value); }
        }

        public bool HasText => _text.Length > 0;

        public bool HasThinking => _thinking.Length > 0;

        /// <summary>
        /// 结构化内容块（代码 / diff / 完成提示）
        /// </summary>
        public ObservableCollection<ChatBlock> Blocks { get; } = [];

        /// <summary>
        /// 用户消息附带的图片（多模态）
        /// </summary>
        public ObservableCollection<ChatImage> Images { get; } = [];

        /// <summary>
        /// 这条消息是否带图（决定是否渲染图片缩略条）
        /// </summary>
        public bool HasImages => Images.Count > 0;
    }
}

