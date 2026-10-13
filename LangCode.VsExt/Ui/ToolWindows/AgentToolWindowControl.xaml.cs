using LangCode.VsExt.Agent;
using LangCode.VsExt.Chat;
using LangCode.VsExt.Domain.Catalog;
using LangCode.VsExt.Domain.Chat;
using LangCode.VsExt.Domain.Settings;
using LangCode.VsExt.Mcp;
using LangCode.VsExt.Persistence;
using LangCode.VsExt.VsIntegration;
using LangCode.VsExt.Workspace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LangCode.VsExt.Ui.ToolWindows
{
    /// <summary>
    /// LangCode 工具窗口主界面（数据驱动）
    /// </summary>
    public partial class AgentToolWindowControl : UserControl
    {
        private readonly ChatSession _session = new ChatSession();

        /// <summary>
        /// 当前唯一在等用户拍板的任务（Agent 循环一次只会 await 一个）
        /// </summary>
        private TaskCompletionSource<bool> _pendingApproval;

        /// <summary>
        /// 待批动作对应的卡片（点允许 / 拒绝后在这里写结论文案）
        /// </summary>
        private ChatBlock _pendingCard;

        /// <summary>
        /// 当前唯一在等用户回答的提问（Agent 循环一次只会 await 一个问题）
        /// </summary>
        private TaskCompletionSource<string> _pendingQuestion;

        /// <summary>
        /// 待回答的问题卡片（回答后收起选项、写上结论）
        /// </summary>
        private ChatBlock _pendingQuestionCard;

        /// <summary>
        /// 本轮的任务进度卡
        /// </summary>
        private ChatBlock _todoCard;

        /// <summary>
        /// 当前正在跑的子任务卡（子任务结束时回填结论）
        /// </summary>
        private ChatBlock _subTaskCard;

        /// <summary>
        /// 子任务运行期间的视觉层级：1 = 正在跑子任务，期间新建的卡片都缩进一格
        /// 由 <see cref="ShowSubTask"/> 在开始 / 结束时切换
        /// </summary>
        private int _subTaskVisualDepth;

        /// <summary>
        /// 本轮真正落盘的改动（按发生顺序），「撤销修改」按相反顺序回滚它们
        /// </summary>
        private readonly List<PendingEdit> _turnEdits = [];

        /// <summary>
        /// 本轮因步数上限暂停 —— 请求条上出现「继续任务」
        /// </summary>
        private bool _turnCanContinue;

        /// <summary>
        /// 本轮真的出错了（异常） —— 请求条上出现「重试」
        /// </summary>
        private bool _turnCanRetry;

        /// <summary>
        /// 本轮是用户按「取消」中断的 —— 出现「恢复任务」（没出错，所以不叫重试）
        /// </summary>
        private bool _turnInterrupted;

        /// <summary>
        /// 排队等待发送的任务（生成中也允许继续发，只是排队）
        /// </summary>
        private readonly Queue<QueuedTask> _queue = new Queue<QueuedTask>();

        /// <summary>
        /// 排队中的一条任务：消息已经进了列表（标着「排队中」），轮到时直接拿它跑
        /// </summary>
        private sealed class QueuedTask
        {
            public ChatMessage Message { get; set; }

            public string Text { get; set; }
        }

        private AppSettings _settings;
        private DispatcherTimer _saveTimer;
        private FileSystemWorkspace _workspace;

        /// <summary>
        /// @ 提及的上下文芯片前缀（与文件提及共用同一个 Click 处理器）
        /// </summary>
        private const string ContextTagPrefix = "#context:";

        /// <summary>
        /// 斜杠命令：插入提示词（Tag 前缀，避免和文件提及的 Tag 混在一起）
        /// </summary>
        private const string SlashInsertPrefix = "#slash-insert:";

        /// <summary>
        /// 斜杠命令：直接执行一个动作（例如 /clear）
        /// </summary>
        private const string SlashActionPrefix = "#slash-action:";

        /// <summary>
        /// @ 提及的文件索引（懒建，跟随工作区根目录）
        /// </summary>
        private WorkspaceIndex _mentions;

        /// <summary>
        /// 工作区根目录（提及 / 跟随打开 / Git 都要用）
        /// </summary>
        private string _workspaceRoot = string.Empty;

        private CancellationTokenSource _cts;

        /// <summary>
        /// 本窗口会话里是否已经提示过「没有工作区」，避免每轮都刷一遍同样的提示
        /// </summary>
        private bool _workspaceNoticeShown;

        /// <summary>
        /// 消息区内部那个 ScrollViewer（判断「是否贴底」要用它，ListBox 模板应用后才拿得到）
        /// </summary>
        private ScrollViewer _chatScrollViewer;

        /// <summary>
        /// 是否跟随最新消息
        /// </summary>
        private bool _followTail = true;

        /// <summary>
        /// 距底部多少像素内算「贴底」
        /// </summary>
        private const double TailThreshold = 32;

        /// <summary>
        /// 令牌条上次刷新的时间（流式期间按间隔节流）
        /// </summary>
        private DateTime _lastTokenRefreshUtc = DateTime.MinValue;

        /// <summary>
        /// 令牌条刷新间隔：不必每个流式分片都重算一次
        /// </summary>
        private static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// 已经被「操作确认卡」问过的动作（AddToolCard 不再为它重复出一张结果卡）
        /// </summary>
        private readonly HashSet<ToolAction> _askedActions = [];

        /// <summary>
        /// 当前这一轮模型输出落到的正文块（每轮开始清空）
        /// </summary>
        private ChatBlock _turnText;

        // ============================================================
        // 流式合流 + 平滑吐字：模型吐字很快时，逐条刷新会把界面拖卡
        // ============================================================

        /// <summary>
        /// 已收到、但还没显示到界面上的正文增量（一边收一边按帧「吐」给界面）
        /// </summary>
        private readonly StringBuilder _streamBuffer = new StringBuilder();

        /// <summary>
        /// 缓冲区里已经吐出去的位置
        /// </summary>
        private int _revealOffset;

        /// <summary>
        /// 上次吐字的时间（按真实帧间隔算这一帧该吐多少字）
        /// </summary>
        private DateTime _revealLastUtc = DateTime.MinValue;

        /// <summary>
        /// 缓冲区的锁（增量来自读取线程，刷新在 UI 线程）
        /// </summary>
        private readonly object _streamGate = new object();

        /// <summary>
        /// 本轮增量要落到哪条助手消息上
        /// </summary>
        private ChatMessage _streamTarget;

        /// <summary>
        /// 是否已经安排了刷新（避免每个增量都往 Dispatcher 里塞一个工作项）
        /// </summary>
        private bool _streamScheduled;

        /// <summary>
        /// 吐字用的计时器（约 60fps，只在 UI 线程创建/启动）
        /// </summary>
        private DispatcherTimer _streamTimer;

        /// <summary>
        /// 还没写进界面的<b>推理</b>增量
        /// </summary>
        private readonly StringBuilder _reasoningBuffer = new StringBuilder();

        /// <summary>
        /// 是否已经排了一次「滚到底」（避免同一帧重复排队）
        /// </summary>
        private bool _scrollScheduled;

        /// <summary>
        /// 是否已经排了一次输入框自动量高（避免快速输入时排队一堆无用布局）
        /// </summary>
        private bool _autoResizeScheduled;

        // ============================================================
        // 命令输出的流式回显（npm install 那种几千行输出不能逐行刷新界面）
        // ============================================================

        /// <summary>
        /// 还没写进卡片的命令输出
        /// </summary>
        private readonly StringBuilder _commandBuffer = new StringBuilder();

        /// <summary>
        /// 命令输出缓冲的锁（输出来自进程的读取线程）
        /// </summary>
        private readonly object _commandGate = new object();

        /// <summary>
        /// 命令输出要落到哪条助手消息上
        /// </summary>
        private ChatMessage _commandTarget;

        /// <summary>
        /// 是否已经安排了刷新
        /// </summary>
        private bool _commandFlushScheduled;

        /// <summary>
        /// 命令输出的合流计时器
        /// </summary>
        private DispatcherTimer _commandTimer;

        /// <summary>
        /// 当前正在流式显示输出的「命令」卡（结束后由最终结果接管）
        /// </summary>
        private ChatBlock _liveCommandCard;

        /// <summary>
        /// 「思考中...」的点动画帧（见 ThinkingDots）
        /// </summary>
        private int _dotsPhase;

        /// <summary>
        /// 点动画的计时器（只驱动「当前这一步」的思考行）
        /// </summary>
        private DispatcherTimer _dotsTimer;

        /// <summary>
        /// 「当前这一步」的推理块
        /// </summary>
        private ChatBlock _turnThinking;

        public AgentToolWindowControl()
        {
            InitializeComponent();

            // 调色板（Ap.*）注入：跟随 VS 主题（深/浅），并支持运行期切换
            Theme.ThemeManager.Attach(this);

            DataContext = _session;

            // 领域层不直接引用持久化层：把数据目录注入给模型目录（覆盖文件 models.json 定位用）
            // 必须在第一次触碰 ModelCatalog 之前注入（首次访问会缓存目录）
            ModelCatalog.DataDirectory = SettingsStore.Directory;

            // Auto 弹层的类别开关：与「设置 → 批准」分页同一份定义，共用同一个行工厂
            foreach (var category in ApprovalCatalog.All)
            {
                AutoList.Children.Add(Controls.ApprovalRows.Create(this, category, AutoToggle_Click));
            }

            _settings = SettingsStore.Load();
            _session.CurrentModel = _settings.Model;
            _session.ContextWindow = _settings.ContextWindow;

            // 模型目录（内置 + 覆盖文件 + 已发现）首次访问要读盘 + 合并：整体放后台，
            // 完成后回 UI 线程按当前模型对齐限额并绑定下拉 —— UI 线程全程只 await，不读盘
            _ = LoadCatalogAsync();

            HistoryDrawer.CloseRequested += (_, __) => HistoryDrawer.Close();
            HistoryDrawer.Attach(_session);
            HistoryDrawer.ConversationSelected += (_, id) => LoadConversation(id);
            HistoryDrawer.NewConversationRequested += (_, __) =>
            {
                HistoryDrawer.Close();
                NewConversation();
            };
            HistoryDrawer.HistoryChanged += (_, __) => OnHistoryChanged();

            // 历史一变（新会话落盘 / 删除 / 清空）就刷新主页「最近会话」，避免显示滞后
            _session.History.CollectionChanged += (_, __) => BuildHomeRecents();

            SettingsDrawer.CloseRequested += (_, __) => SettingsDrawer.Close();

            // 设置抽屉编辑这份 settings 的草稿副本，点「保存」时回写（含每个厂商各自的 Key / Base URL / 默认模型）
            SettingsDrawer.Attach(_settings);
            SettingsDrawer.SettingsChanged += (_, __) => OnSettingsChanged();

            // 启动时不再自动载入上次会话：先给一个主页（带「最近的会话」列表），
            // 由用户自己决定继续哪一个 —— 直接跳进去会让人分不清「这是新任务还是旧任务」
            _session.ReloadHistory();
            BuildHomePanel();

            // 消息列表一变就同步主页显隐（只有空会话才显示主页）
            _session.Messages.CollectionChanged += (_, __) => UpdateHomeVisibility();
            UpdateHomeVisibility();

            ApplySettingsToUi();

            // 多模态：挂上图片粘贴处理（剪贴板里有图就转成附件）
            HookImagePaste();

            Loaded += OnLoaded;
        }

        /// <summary>
        /// 后台加载模型目录，完成后回 UI 线程对齐限额并重绑下拉。
        /// 目录首次访问会读盘（models.json / models.discovered.json）并做合并，
        /// 放在 UI 线程上就是实打实的卡顿，所以统一在这里异步化。
        /// </summary>
        private async Task LoadCatalogAsync()
        {
            try
            {
                await ModelCatalog.WarmUpAsync();

                await Dispatcher.InvokeAsync(() =>
                {
                    // 目录就绪后再按当前模型对齐「上下文窗口 / 最大输出」（此前只是兜底值）
                    _settings.AlignLimitsToModel(_settings.Model);
                    _session.ContextWindow = _settings.ContextWindow;
                    _session.CurrentModel = _settings.Model;

                    RefreshModelList();
                    UpdateTokens();
                });
            }
            catch (Exception)
            {
                // 目录加载失败不该影响工具窗可用
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Loaded 必然在 UI 线程；显式声明以通过 VSTHRD010（DTE 只能在主线程访问）
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            // ListBox 内部的 ScrollViewer 要先拿到，才能判断「用户是否还在底部」
            ChatScroll.Loaded += (_, __) => EnsureChatScrollHook();
            Dispatcher.BeginInvoke(new Action(EnsureChatScrollHook), DispatcherPriority.Loaded);

            EnsureWorkspace();

            UpdateSendState();
            UpdateTokens();
            AutoResizeInput();
            ScrollToEnd();

            // 「设置 → MCP → 启动时自动连接」：在后台把已启用的服务器连上（失败只记状态，不打扰用户）
            if (_settings.AutoConnectMcp)
            {
                _ = ConnectMcpServersAsync();
            }
        }

        /// <summary>
        /// 后台预连接已启用的 MCP 服务器（结果会显示在「设置 → MCP」的卡片上）
        /// </summary>
        private async Task ConnectMcpServersAsync()
        {
            var servers = (_settings.McpServers ?? [])
                .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Command))
                .ToList();

            if (servers.Count == 0)
            {
                return;
            }

            McpManager.Sync(servers);
            var manager = new McpManager();

            foreach (var server in servers)
            {
                try
                {
                    // 不带工具名 = 只做握手并列出工具
                    await manager.CallAsync(_settings, server.Name, string.Empty, string.Empty, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // 单个服务器连不上不影响其它服务器
                }
            }
        }

        /// <summary>
        /// 懒定位工作区根目录（= VS 已打开的解决方案 / 文件夹所在目录）
        /// </summary>
        private void EnsureWorkspace()
        {
            // DTE 只能在 UI 线程访问（VSTHRD010）；显式断言，也便于分析器跟踪
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (_workspace != null)
            {
                return;
            }

            var root = VsWorkspaceLocator.TryGetSolutionDirectory();
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            try
            {
                _workspace = new FileSystemWorkspace(root)
                {
                    MaxFileChars = 200_000,
                    MaxResults = 200,
                };

                // @ 提及的候选表：只跟根目录有关，和文件工具是否可用无关
                _mentions = new WorkspaceIndex(root);
                _workspaceRoot = root;
            }
            catch (Exception)
            {
                _workspace = null;
                _mentions = null;
            }
        }

        /// <summary>
        /// 刷新 <c>@</c> 提及索引：每次打开弹层都重新确认工作区根目录，并按需重建 / 重扫
        /// <list type="bullet">
        /// <item>根目录变了或还没有索引 → 重建 <see cref="WorkspaceIndex"/>（顺带把工作区补上）；</item>
        /// <item>根目录没变但 <paramref name="forceRescan"/> 为真 → 让缓存失效，下次访问重扫
        ///       （新加 / 删除的文件才反映得出来）</item>
        /// </list>
        /// </summary>
        private void RefreshMentionIndex(bool forceRescan)
        {
            // DTE 只能在 UI 线程访问（VSTHRD010）
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var root = VsWorkspaceLocator.TryGetSolutionDirectory();
            if (string.IsNullOrEmpty(root))
            {
                // 没有工作区：保持现状，界面会给出提示
                return;
            }

            var rootChanged =
                !string.Equals(root, _workspaceRoot, StringComparison.OrdinalIgnoreCase);

            if (rootChanged || _workspace == null)
            {
                try
                {
                    _workspace = new FileSystemWorkspace(root)
                    {
                        MaxFileChars = 200_000,
                        MaxResults = 200,
                    };
                }
                catch (Exception)
                {
                    _workspace = null;
                }

                _workspaceRoot = root;
            }

            if (rootChanged || _mentions == null || !_mentions.IsReady)
            {
                _mentions = new WorkspaceIndex(root);
                _workspaceRoot = root;
            }
            else if (forceRescan)
            {
                _mentions.Invalidate();
            }
        }

        // ============================================================
        // 设置 → 界面
        // ============================================================
        private void ApplySettingsToUi()
        {
            var model = _settings.Model ?? string.Empty;

            // 激活了命名档案时，模型按钮直接显示那套配置的自定义名称（只显示自定义名）
            ModelName.Text = !string.IsNullOrWhiteSpace(_settings.ActiveProfileName)
                ? _settings.ActiveProfileName
                : (model.Length == 0 ? "未指定模型" : ModelCatalog.DisplayOf(model));
            ModelDot.Fill = BrushFor(ModelCatalog.ColorOf(model));
            // 只有「已接入」的类别全部放开，Auto 才显示为已开启
            AutoChip.IsChecked = ApprovalCatalog.AllWiredApproved(_settings);

            // 芯片上直接写出「当前放开了几类」：不必点开弹层才知道自己放开了什么
            AutoChipText.Text = ApprovalCatalog.DescribeAutoApproval(_settings);

            // 设置页的「字号」→ 整个工具窗口的字号；正文的 MarkdownView 通过
            // BaseFontSize 绑定到这里，于是标题/表格/代码块会一起等比缩放
            FontSize = _settings.FontSize > 0 ? _settings.FontSize : 13;

            // 上下文用到多少就把较早的轮次折叠掉（设置页的「自动压缩阈值」）
            // 解析规则统一放在 AppSettings.CompactThresholdPercent，避免两处口径不一致
            _session.CompactThresholdPercent = _settings.CompactThresholdPercent;

            // 计划 / 执行模式（输入框下方的两个芯片，互斥）
            PlanChip.IsChecked = _settings.PlanMode;
            ActChip.IsChecked = !_settings.PlanMode;
        }

        /// <summary>
        /// 设置被改动：防抖后落盘 + 刷新界面（避免在 Key 输入框里每敲一个字符就写一次磁盘）
        /// </summary>
        private void OnSettingsChanged()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _saveTimer.Tick += (_, __) =>
                {
                    _saveTimer.Stop();
                    SettingsStore.Save(_settings);
                };
            }

            _saveTimer.Stop();
            _saveTimer.Start();

            _session.ContextWindow = _settings.ContextWindow;
            ApplySettingsToUi();
            UpdateTokens();
        }

        private static Brush BrushFor(string hex)
        {
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch (Exception)
            {
                return new SolidColorBrush(Colors.Gray);
            }
        }

    }
}

