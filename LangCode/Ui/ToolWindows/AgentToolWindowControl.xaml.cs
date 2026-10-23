using LangCode.Agent;
using LangCode.Chat;
using LangCode.Domain.Catalog;
using LangCode.Domain.Chat;
using LangCode.Domain.Settings;
using LangCode.Mcp;
using LangCode.Persistence;
using LangCode.VsIntegration;
using LangCode.Workspace;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// LangCode 工具窗口主界面（数据驱动）
    /// </summary>
    public partial class AgentToolWindowControl : UserControl
    {
        private readonly ChatSession _session = new ChatSession();

        /// <summary>
        /// 并行子任务可能同时要求审批：用信号量把审批串行化，
        /// 避免后一个请求覆盖前一个的 TaskCompletionSource。
        /// </summary>
        private readonly SemaphoreSlim _approvalGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 同上：模型提问也串行化
        /// </summary>
        private readonly SemaphoreSlim _questionGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 本轮已被取消：排队中的审批 / 提问拿到信号量后直接返回，不再弹卡片。
        /// </summary>
        private bool _approvalCancelled;
        private bool _questionCancelled;

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
        /// 本轮真正落盘的改动（按发生顺序），「撤销修改」按相反顺序回滚它们
        /// </summary>
        private readonly List<PendingEdit> _turnEdits = [];

        /// <summary>
        /// 本次会话里每个被改文件「改动前」的内容（第一次改到它时抓取）——
        /// 「本次变更」里点击文件名时用它开 VS 差异对比。只驻留内存；
        /// 重开会话后内存里没有基线，会退回用 git HEAD 里的版本
        /// </summary>
        private readonly Dictionary<string, string> _sessionBaselines = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 本轮因步数上限暂停 —— 请求条上出现「继续任务」。
        /// 直接读写 <see cref="ChatSession.TurnPaused"/>：随会话一起落盘，重开 VS 后能恢复
        /// </summary>
        private bool _turnCanContinue
        {
            get { return _session.TurnPaused; }
            set { _session.TurnPaused = value; }
        }

        /// <summary>
        /// 本轮真的出错了（异常） —— 请求条上出现「重试」（同样随会话落盘）
        /// </summary>
        private bool _turnCanRetry
        {
            get { return _session.TurnCanRetry; }
            set { _session.TurnCanRetry = value; }
        }

        /// <summary>
        /// 本轮是用户按「取消」中断的 —— 出现「恢复任务」（没出错，所以不叫重试；同样随会话落盘）
        /// </summary>
        private bool _turnInterrupted
        {
            get { return _session.TurnInterrupted; }
            set { _session.TurnInterrupted = value; }
        }

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
        /// 语义索引：跨轮次复用并在后台预热；换工作区时按新根目录重建
        /// </summary>
        private SemanticIndex _semanticIndex;

        /// <summary>
        /// 上面那个语义索引对应的根目录
        /// </summary>
        private string _semanticIndexRoot = string.Empty;

        /// <summary>
        /// 建语义索引时用的规模参数（设置里调整后需要按新值重建）
        /// </summary>
        private int _semanticIndexMaxFiles;

        private int _semanticIndexMaxChunkChars;

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
        /// 本会话里是否已经弹过「选择工作区目录」对话框：用户取消后本轮不再反复弹
        /// </summary>
        private bool _workspacePromptedInSession;

        /// <summary>
        /// 用户手动选定的工作区根目录（VS 里没有打开解决方案/文件夹时的兜底）。
        /// 一旦 VS 里打开了解决方案/文件夹，以 VS 的为准
        /// </summary>
        private string _userPickedWorkspaceRoot;

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

        /// <summary>
        /// 本轮正文的**原始**累积文本（含 ```action 工具块）。
        /// 界面上的正文块显示的是它的「去动作」版本，但游标必须攒在原文上 ——
        /// 否则动作块被隐藏后再来的增量会粘到正文里（见 <see cref="AppendTurnText"/>）
        /// </summary>
        private StringBuilder _turnRawText;

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

            // MCP 抽屉：独立的 MCP 视图（已安装 / 市场）；改的是「已生效」的设置，立即落盘
            McpDrawer.CloseRequested += (_, __) => McpDrawer.Close();
            McpDrawer.Attach(_settings);
            McpDrawer.SettingsChanged += (_, __) => OnMcpChanged();

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
                    // 目录就绪后再按当前模型对齐「上下文窗口 / 最大输出」（此前只是兜底值）——
                    // 只处理「还是出厂默认」的限额：用户调过的 / 配置档案里恢复出来的值一律不动，
                    // 否则每次开窗口都会把 A 配置的窗口改写成"模型能力"
                    if (_settings.LimitsAreDefault())
                    {
                        _settings.AlignLimitsToModel(_settings.Model);
                    }

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
        /// 定位 / 刷新工作区根目录（= VS 已打开的解决方案 / 文件夹所在目录）
        /// <para>
        /// 每次调用都重新向 VS 询问当前根目录：从 A 解决方案切到 B 解决方案后，
        /// 根目录会变，这里会重建工作区（早期实现「拿到一次就长期缓存」，导致切到 B 后仍用 A）。
        /// </para>
        /// <para>
        /// VS 里没有打开解决方案 / 文件夹时：若有用户手动选定的目录就用它；
        /// 否则在 <paramref name="canPromptIfMissing"/> 为真时弹出「选择目录」对话框，
        /// 让用户指定 / 新建一个目录作为工作区（选定后即可启用文件工具）。
        /// </para>
        /// </summary>
        /// <param name="canPromptIfMissing">
        /// 是否允许在「完全没有工作区」时弹目录选择框（仅用户主动发送时允许，避免打开窗口就弹）
        /// </param>
        private void EnsureWorkspace(bool canPromptIfMissing = false)
        {
            // DTE 只能在 UI 线程访问（VSTHRD010）；显式断言，也便于分析器跟踪
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var vsRoot = VsWorkspaceLocator.TryGetSolutionDirectory();

            // ① VS 里有解决方案 / 打开文件夹：以它为准 —— 切换项目时 vsRoot 会与 _workspaceRoot 不同，据此重建
            if (!string.IsNullOrEmpty(vsRoot))
            {
                if (!string.Equals(vsRoot, _workspaceRoot, StringComparison.OrdinalIgnoreCase) ||
                    _workspace == null)
                {
                    CreateWorkspace(vsRoot);
                    // 工作区可用了：允许以后再提示（例如之后又把解决方案关掉）
                    _workspaceNoticeShown = false;
                }

                return;
            }

            // ② VS 里没有工作区：优先沿用用户手动选定的目录（若该目录仍在）
            if (!string.IsNullOrEmpty(_userPickedWorkspaceRoot) &&
                Directory.Exists(_userPickedWorkspaceRoot))
            {
                if (!string.Equals(_userPickedWorkspaceRoot, _workspaceRoot, StringComparison.OrdinalIgnoreCase) ||
                    _workspace == null)
                {
                    CreateWorkspace(_userPickedWorkspaceRoot);
                    _workspaceNoticeShown = false;
                }

                return;
            }

            // ③ 完全没有工作区：用户主动发送时弹一次目录选择框（取消后本会话不再重复弹）
            if (canPromptIfMissing && !_workspacePromptedInSession)
            {
                _workspacePromptedInSession = true;
                var picked = PromptForWorkspaceFolder();
                if (!string.IsNullOrEmpty(picked))
                {
                    _userPickedWorkspaceRoot = picked;
                    CreateWorkspace(picked);
                    _workspaceNoticeShown = false;
                    return;
                }
            }

            // ④ 兜底：清掉可能残留的旧工作区（例如把原解决方案关掉、且用户没有手动选定目录），
            //    避免继续用已经过期的项目目录
            if (_workspace != null)
            {
                _workspace = null;
                _mentions = null;
                _semanticIndex = null;
                _semanticIndexRoot = string.Empty;
                _workspaceRoot = string.Empty;
                _workspaceNoticeShown = false;
            }
        }

        /// <summary>
        /// 按根目录构建 <see cref="FileSystemWorkspace"/> 与 @ 提及索引（构建失败则退化为无工作区）
        /// </summary>
        private void CreateWorkspace(string root)
        {
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

                // 两个索引都在后台预热：用户真正点 @ / 让 Agent 检索时，数据已经就绪
                _mentions.StartBackgroundScan();
                EnsureSemanticIndex();
            }
            catch (Exception)
            {
                _workspace = null;
                _mentions = null;
                _semanticIndex = null;
                _semanticIndexRoot = string.Empty;
                _workspaceRoot = string.Empty;
            }
        }

        /// <summary>
        /// 取当前工作区的语义索引（懒建）：根目录变了就按新根目录重建，并在后台预热。
        /// 索引跨轮次复用，避免每轮对话都重新扫一遍仓库。
        /// </summary>
        private SemanticIndex EnsureSemanticIndex()
        {
            var root = _workspaceRoot;
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var maxFiles = _settings?.SemanticIndexMaxFiles ?? 2000;
            var maxChunkChars = _settings?.SemanticChunkChars ?? 1600;

            // 多根工作区：索引全部根；用拼接后的根集合做「是否需要重建」的键
            var roots = _workspace?.Roots != null && _workspace.Roots.Count > 0 ? _workspace.Roots : new List<string> { root };
            var rootsKey = string.Join("|", roots.Select(r => r.ToLowerInvariant()));

            if (_semanticIndex == null ||
                !string.Equals(_semanticIndexRoot, rootsKey, StringComparison.OrdinalIgnoreCase) ||
                maxFiles != _semanticIndexMaxFiles ||
                maxChunkChars != _semanticIndexMaxChunkChars)
            {
                _semanticIndex = new SemanticIndex(root, roots)
                {
                    MaxFiles = maxFiles,
                    MaxChunkChars = maxChunkChars,
                };
                _semanticIndexRoot = rootsKey;
                _semanticIndexMaxFiles = maxFiles;
                _semanticIndexMaxChunkChars = maxChunkChars;
            }

            // 向量检索配置跟随设置热更新（签名没变时是空操作）
            _semanticIndex.Embedding = EmbeddingOptions.FromSettings(_settings);

            // 设置里关掉语义索引时不预热（工具调用本来也会拒绝）
            if (_settings == null || _settings.SemanticIndex)
            {
                _semanticIndex.StartBackgroundBuild();
            }

            return _semanticIndex;
        }

        // ============================================================
        // 检查点（时间旅行）：每轮开始打一个点，消息上的按钮据此还原
        // ============================================================

        /// <summary>
        /// 检查点仓库（懒建）：换工作区（切项目）时按新根目录重建
        /// </summary>
        private CheckpointStore _checkpoints;

        /// <summary>
        /// 上面那个仓库对应的根目录
        /// </summary>
        private string _checkpointsRoot = string.Empty;

        /// <summary>
        /// 取当前工作区的检查点仓库：关掉「检查点」或没有工作区时返回 null（调用方优雅跳过）
        /// </summary>
        private CheckpointStore EnsureCheckpoints()
        {
            if (_settings == null || !_settings.Checkpoints || string.IsNullOrWhiteSpace(_workspaceRoot))
            {
                return null;
            }

            if (_checkpoints == null || !string.Equals(_checkpointsRoot, _workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                _checkpoints = new CheckpointStore(_workspaceRoot);
                _checkpointsRoot = _workspaceRoot;
            }

            return _checkpoints;
        }

        /// <summary>
        /// 为「这一轮对话」打一个检查点，并挂在触发它的那条用户消息上。
        /// 必须在 Agent 动手改文件<b>之前</b>调用 —— 这样消息上的还原按钮才是
        /// 「回到这条消息发出之前」的代码状态。git 操作放后台线程：
        /// 大仓库里 git add -A 要几百毫秒，别卡住界面
        /// </summary>
        private async Task CreateTurnCheckpointAsync(ChatMessage userMessage)
        {
            if (userMessage == null)
            {
                return;
            }

            var store = EnsureCheckpoints();
            if (store == null || !store.IsAvailable)
            {
                return;
            }

            // 标签用 ASCII：它会经命令行传给 git，中文在非 UTF-8 代码页下会被写坏
            var label = "checkpoint " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            try
            {
                var info = await Task.Run(() => store.Create(label)).ConfigureAwait(true);
                if (info != null)
                {
                    userMessage.CheckpointId = info.Id;
                }
            }
            catch (Exception)
            {
                // 打点失败不影响这一轮对话：没有检查点就没有还原按钮
            }
        }

        /// <summary>
        /// 取一段文本的首行（检查点提示文案用）：太长就截断
        /// </summary>
        private static string FirstLineOf(string text, int max = 28)
        {
            var line = text ?? string.Empty;
            var cut = line.IndexOfAny(new[] { '\r', '\n' });
            if (cut >= 0)
            {
                line = line.Substring(0, cut);
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                return "（空消息）";
            }

            return line.Length > max ? line.Substring(0, max) + "…" : line;
        }

        /// <summary>
        /// 弹出「选择工作区目录」对话框；选定返回绝对路径，取消返回 null。
        /// 用 WinForms 的 <see cref="System.Windows.Forms.FolderBrowserDialog"/>（net472 的 WPF 没有选目录对话框），
        /// 它自带「新建文件夹」按钮，可以直接建一个新目录当工作区
        /// </summary>
        private static string PromptForWorkspaceFolder()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择一个目录作为 LangCode 的工作区（AI 会在其中读写文件，可直接在这里新建文件夹）",
                ShowNewFolderButton = true,
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            })
            {
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    ? dialog.SelectedPath
                    : null;
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

            // 先按当前 VS 项目 / 用户选定目录刷新工作区：切换项目时 _workspaceRoot 会在这里更新，
            // 并顺带重建下面的 @ 索引（不弹目录选择框，避免点开 @ 就弹窗）
            EnsureWorkspace();

            var root = _workspaceRoot;
            if (string.IsNullOrEmpty(root))
            {
                // 没有工作区：保持现状，界面会给出提示
                return;
            }

            // EnsureWorkspace 在重建工作区时会一并重建 @ 索引；这里只兜底「索引缺失」与「主动重扫」
            if (_mentions == null)
            {
                _mentions = new WorkspaceIndex(root);
                _mentions.StartBackgroundScan();
            }
            else if (forceRescan)
            {
                _mentions.Invalidate();
                _mentions.StartBackgroundScan();
            }

            // 语义索引同样按当前根目录预热（已经是同一个索引时只是确保后台建好）
            EnsureSemanticIndex();
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
        /// MCP 配置被改动：立即落盘，并让连接池与新配置对齐（停用 / 删除的会被关掉）
        /// </summary>
        private void OnMcpChanged()
        {
            SettingsStore.Save(_settings);
            McpManager.Sync(_settings.McpServers);
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

            // 语义索引开关 / 规模调整后按新配置预热（关掉时不预热；参数没变时是空操作）
            EnsureSemanticIndex();
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

