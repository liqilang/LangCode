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

namespace LangCode.Agent
{
    /// <summary>
    /// Agent 循环对外的事件回调（由 UI 实现；回调可能在后台线程触发，UI 侧自行切线程）
    /// </summary>
    public sealed class AgentCallbacks
    {
        /// <summary>
        /// 模型正文增量（流式）
        /// </summary>
        public Action<string> OnDelta { get; set; }

        /// <summary>
        /// 某一轮请求即将开始（界面据此清掉上一轮的推理过程块，避免多步推理叠在一起）
        /// </summary>
        public Action OnTurnStart { get; set; }

        /// <summary>
        /// 模型推理过程增量（各推理模型的思考内容，字段名由后端统一兼容）
        /// </summary>
        public Action<string> OnReasoning { get; set; }

        /// <summary>
        /// 一轮模型输出结束：原始文本 + 是否包含动作
        /// </summary>
        public Action<string, bool> OnTurnComplete { get; set; }

        /// <summary>
        /// 一次工具执行结束（界面据此渲染工具卡片；带上动作以便取 path/glob）
        /// </summary>
        public Action<ToolAction, ToolResult> OnToolResult { get; set; }

        /// <summary>
        /// 写入类动作的审批：返回 true 表示应用这次改动
        /// </summary>
        public Func<PendingEdit, Task<bool>> ApproveAsync { get; set; }

        /// <summary>
        /// 一次改动**真的落盘之后**的回调（自动批准与用户批准都会走到）
        /// </summary>
        public Action<PendingEdit> OnEditApplied { get; set; }

        /// <summary>
        /// 无 diff 的动作（读取 / 将来还有命令等）的审批：返回 true 表示允许执行
        /// </summary>
        public Func<ToolAction, ToolResult, Task<bool>> ApproveActionAsync { get; set; }

        /// <summary>
        /// 达到最大步数（防死循环）
        /// </summary>
        public Action<int> OnStepLimit { get; set; }

        /// <summary>
        /// 命令输出的一行（流式；可能在后台线程触发，界面侧自行切线程）
        /// </summary>
        public Action<string> OnCommandOutput { get; set; }

        /// <summary>
        /// 请在编辑器里「转到定义」（参数：相对路径、行号；0 表示不定位）
        /// </summary>
        public Action<string, int> GoToDefinition { get; set; }

        /// <summary>
        /// 任务清单被更新（长任务编排的进度卡）参数：清单条目、进度文案
        /// </summary>
        public Action<IList<TodoItem>, string> OnTodoUpdate { get; set; }

        /// <summary>
        /// 模型向用户提问：返回用户的回答（空字符串 = 用户跳过）
        /// </summary>
        public Func<AgentQuestion, Task<string>> AskQuestionAsync { get; set; }

        /// <summary>
        /// 子任务开始 / 结束（true = 开始）界面据此渲染子任务卡与进度
        /// </summary>
        public Action<AgentSubTask, bool> OnSubTask { get; set; }

        /// <summary>
        /// 读取当前工作区的编译诊断（VS「错误列表」里的错误）返回空 / null 表示没有诊断或不可用
        /// </summary>
        public Func<CancellationToken, Task<string>> ReadDiagnosticsAsync { get; set; }

        /// <summary>
        /// 自动化（自动验证 / 自动修复）过程中的一行提示（界面渲染成便签卡）
        /// </summary>
        public Action<string> OnNotice { get; set; }

        /// <summary>
        /// 调试器操作（断点 / 单步 / 调用栈 / 求值）：返回给模型看的文本。
        /// DTE 只能在 UI 线程访问，界面侧负责封送到 Dispatcher
        /// </summary>
        public Func<DebugCommand, CancellationToken, Task<string>> DebugControlAsync { get; set; }

        /// <summary>
        /// 文件是否在 VS 编辑器里有未保存改动。返回 true 时拒绝直接写盘，
        /// 避免 AI 的磁盘写入与用户未保存的缓冲区互相覆盖。
        /// </summary>
        public Func<string, bool> IsFileDirty { get; set; }
    }

    /// <summary>
    /// Agent 循环：模型 → 解析动作 → 执行工具 → 回灌结果 → 直到模型给出最终答复
    /// </summary>
    public sealed partial class AgentRunner
    {
        /// <summary>
        /// 一轮用户请求内最多允许的「模型 → 工具」往返次数；<b>0 = 不限制</b>（默认）
        /// </summary>
        public int MaxSteps { get; set; }

        /// <summary>
        /// 模型只「宣布意图」却不发动作时，最多催它几次真正动手（防止来回空转）
        /// </summary>
        private const int MaxActionNudges = 2;

        /// <summary>
        /// 催跑提示：明确要求「直接动手」，避免模型又把计划复述一遍
        /// </summary>
        private const string ActionNudgePrompt =
            "你上一条只描述了打算怎么做，但**没有发出任何 ```action 代码块**，所以实际什么都没执行。\n" +
            "请立刻动手：直接输出 ```action 代码块来调用工具（read_file / write_file / replace_in_file / edit_symbol / run_command 等），\n" +
            "不要复述计划、也不必征求同意。需要多个互不依赖的动作时，请在同一轮里一起发出\n" +
            "如果确实已经全部完成、无需再调用任何工具，就直接给出最终结论。";

        /// <summary>
        /// 动作块存在但 JSON 解析失败时的续跑提示。
        /// 典型场景：把含 Markdown 代码块的长文本塞进 new_task / write_file 的字符串参数。
        /// </summary>
        private const string MalformedActionNudgePrompt =
            "你上一条包含 action 代码块，但系统没有解析出任何工具调用。常见原因是动作 JSON 不合法：\n" +
            "1) 字符串里的换行必须写成 \\n，不能直接换行；\n" +
            "2) 字符串里出现三反引号时写成 \\u0060\\u0060\\u0060；\n" +
            "3) 如果 task/content 很长且包含 Markdown 代码块，优先改用 apply_patch，或把整段内容作为正确转义的 JSON 字符串。\n" +
            "请直接重发合法的 action 块，不要只解释。";

        /// <summary>
        /// 判断模型是不是「只说要干、却没干」：短文本里出现第一人称的行动预告。
        /// 长文本通常已经是完整答复，不催 —— 免得把正常问答也拖去执行工具。
        /// </summary>
        private static bool LooksLikeActionAnnouncement(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            if (trimmed.Length > 300)
            {
                return false;
            }

            string[] cues =
            [
                "我先", "让我先", "我先来", "我来先", "我先去", "先读取", "先查看", "先看看",
                "先了解", "先分析", "先检查", "先确认", "首先我", "接下来我", "我需要先", "准备先",
                "let me first", "i'll first", "i will first", "first, i'll", "first i'll",
                "let me read", "let me look", "let me check",
            ];

            foreach (var cue in cues)
            {
                if (trimmed.IndexOf(cue, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 当前会话 id（用于用量按会话归集与审计日志）界面创建 Runner 时赋值即可；为空也能记账
        /// </summary>
        public string ConversationId { get; set; } = string.Empty;

        /// <summary>
        /// 子任务嵌套深度（0 = 顶层任务）
        /// </summary>
        public int SubTaskDepth { get; set; }

        /// <summary>
        /// 当前任务清单（由 <c>update_todo_list</c> 维护，供后续回灌与统计）
        /// </summary>
        private IList<TodoItem> _todos = [];

        /// <summary>
        /// 本轮是否真的落盘过改动 —— 没有改动就不必跑自动验证 / 自动修复
        /// </summary>
        private bool _editApplied;

        /// <summary>
        /// 本轮是否已经跑过一次自动验证（只跑一次，避免改一处验一次）
        /// </summary>
        private bool _verifiedThisRun;

        /// <summary>
        /// 本轮已经回灌了几轮自动修复（受设置里的上限约束）
        /// </summary>
        private int _fixRounds;

        /// <summary>
        /// 历史前缀的「冻结」边界：<c>history[0.._frozenCount)</c> 是本次任务最初的轮次，
        /// 压缩时**绝不改写**，保证前后两次请求的前缀逐字节一致、提示缓存能命中
        /// </summary>
        private int _frozenCount;

        /// <summary>
        /// 摘要区长度：紧跟在冻结前缀之后、由压缩**追加**的摘要块（只增不改写）
        /// </summary>
        private int _summaryCount;

        /// <summary>
        /// 本次 run 内「只读工具 → 结果指纹」表：命中说明同样的调用产生了同样的结果，
        /// 回灌时只发一句「未变化」，省掉重复的大段文件内容
        /// </summary>
        private readonly Dictionary<string, ulong> _resultDigests = new(StringComparer.Ordinal);

        /// <summary>
        /// 参与「结果去重」的只读工具（重复调用且结果一致时省略内容）
        /// </summary>
        private static readonly HashSet<string> DedupTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "read_file", "list_files", "search_text", "list_symbols",
            "find_references", "semantic_search", "get_type_hierarchy", "find_implementations",
        };

        /// <summary>
        /// 摘要块在回灌历史里的固定前缀（便于识别，也便于排查「这张摘要从哪来」）
        /// </summary>
        private const string SummaryMarker = "【上下文摘要】";

        /// <summary>
        /// 单轮自动化（验证 / 修复）回灌给模型的输出上限：验证输出动辄几千行
        /// </summary>
        private const int MaxAutomationOutputChars = 6000;

        private readonly IChatBackend _backend;
        private readonly FileSystemWorkspace _workspace;
        private readonly AppSettings _settings;

        /// <summary>
        /// 命令类别的工具实现
        /// </summary>
        private readonly CommandRunner _commands;

        /// <summary>
        /// 浏览器类别的工具实现（打开网页 + 抓正文）
        /// </summary>
        private readonly BrowserTool _browser;

        /// <summary>
        /// 浏览器自动化（DevTools 协议：点击 / 填表 / 截图 …）
        /// </summary>
        private readonly BrowserAutomation _browserAutomation;

        /// <summary>
        /// 本地代码库语义索引（RAG）
        /// </summary>
        private readonly SemanticIndex _index;

        /// <summary>
        /// 本轮自动探测出的工作区工具链（技术栈 / 验证命令 / 本地开发地址）
        /// </summary>
        private ToolchainInfo _toolchain;

        /// <summary>
        /// MCP 类别的工具实现
        /// </summary>
        private readonly McpManager _mcp = new McpManager();

        public AgentRunner(IChatBackend backend, FileSystemWorkspace workspace, AppSettings settings)
        {
            _backend = backend;
            _workspace = workspace;
            _settings = settings;

            MaxSteps = settings?.MaxAgentSteps ?? 0;

            var root = workspace?.Root ?? string.Empty;
            _commands = new CommandRunner(root)
            {
                // 命令安全策略：内置规则 + 用户自己的黑名单 / 信任前缀
                DenyList = settings?.CommandDenyList,
                AllowPrefixes = settings?.CommandAllowPrefixes,
            };
            _browser = new BrowserTool(root);
            _browserAutomation = new BrowserAutomation(root)
            {
                Port = settings?.BrowserRemoteDebuggingPort ?? 9222,
                Headless = settings?.BrowserHeadless ?? true,
                ExecutablePath = settings?.BrowserExecutablePath ?? string.Empty,
                TimeoutSeconds = settings?.BrowserTimeoutSeconds ?? 30,
            };
            _index = new SemanticIndex(root)
            {
                MaxFiles = settings?.SemanticIndexMaxFiles ?? 2000,
                MaxChunkChars = settings?.SemanticChunkChars ?? 1600,
            };
            _mcp.WorkingDirectory = root;

            // 服务器列表变了（删了 / 停用了）就把对应连接释放掉
            McpManager.Sync(settings?.McpServers);
        }

        public async Task RunAsync(IList<ChatTurn> turns, AgentCallbacks callbacks, CancellationToken cancellationToken)
        {
            callbacks ??= new AgentCallbacks();
            var history = new List<ChatTurn>(turns ?? []);

            // 调试：本轮关键上下文（排查「看不到推理过程」时先看这几行）
            ChatDiagnostics.Section("一轮任务开始");
            ChatDiagnostics.Log("provider=" + (_settings?.Provider ?? "?")
                + " model=" + (_settings?.Model ?? "?")
                + " baseUrl=" + (_settings?.BaseUrl ?? string.Empty)
                + " showThinking=" + (_settings?.ShowThinking.ToString() ?? "?")
                + " expandThinking=" + (_settings?.ExpandThinking.ToString() ?? "?")
                + " reasoningEffort=" + (_settings?.ReasoningEffort ?? string.Empty)
                + " thinkingBudget=" + (_settings?.ThinkingBudgetTokens.ToString() ?? "?")
                + " nativeToolCalling=" + (_settings?.NativeToolCalling.ToString() ?? "?")
                + " turnCount=" + history.Count
                + " onReasoningCallback=" + (callbacks.OnReasoning == null ? "null" : "set"));

            // 每轮重置自动化状态：上一次 run 的「改过没有 / 验过没有 / 修了几轮」不该串到这一轮
            _editApplied = false;
            _verifiedThisRun = false;
            _fixRounds = 0;

            // 压缩相关的运行态同样每轮重置：本次任务最初的轮次构成「冻结前缀」，
            // 之后的压缩只会往它后面**追加**摘要，绝不改写前面已发送过的字节
            _frozenCount = history.Count;
            _summaryCount = 0;
            _resultDigests.Clear();

            // 本轮是否真正执行过工具、以及「只说不做」已经催过几次（用于判断要不要续跑）
            var toolUsesThisRun = 0;
            var actionNudges = 0;

            // 自动探测工作区工具链：用户什么都不配，也能自动跑验证、自动用本地地址开浏览器
            _toolchain = ToolchainDetector.Detect(_workspace?.Root);
            if (_toolchain != null && !string.IsNullOrWhiteSpace(_toolchain.DevUrl))
            {
                _browserAutomation.DefaultUrl = _toolchain.DevUrl;
            }

            // 项目规则只读一次（一轮内不会变）：读盘 + 拼接有成本，不必每一步都做
            var projectRules = _settings != null && _settings.UseProjectRules ? ProjectRules.Load(_workspace?.Root) : null;

            // 系统提示词在整轮任务里不变（工具清单 / 项目规则 / 工作区环境都不会中途改变）：
            // 循环外构建一次即可 —— 既省去每步重复拼接，也让提示缓存的前缀保持稳定、更易命中
            var systemPrompt = AgentPrompts.BuildSystemPrompt(_settings, projectRules, _toolchain?.Describe());

            // MaxSteps <= 0 = 不限制：循环由「模型不再要求用工具」或用户取消来结束
            for (var step = 0; MaxSteps <= 0 || step < MaxSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 费用预算硬停：达到预算就不再发起新的模型调用
                // （此前只是「提示」，长任务会继续烧钱，用户只能眼睁睁看着）
                if (_settings != null && UsageStore.IsOverBudget(_settings.BudgetUsd, out var usageNow))
                {
                    callbacks.OnNotice?.Invoke(
                        "已达到费用预算上限（预算 $" + _settings.BudgetUsd.ToString("0.00") +
                        " / 累计约 $" + usageNow.CostUsd.ToString("0.00") + "），本轮已停止。" +
                        "可在「设置 → 用量」调整预算或清空用量后重试");
                    return;
                }

                // 每步都带着完整历史往返：不折叠的话，几十步的长任务迟早把上下文顶爆。
                // 折叠采用「总结式 + 只追加」，见 CompactHistoryAsync
                await CompactHistoryAsync(history, cancellationToken).ConfigureAwait(false);

                callbacks.OnTurnStart?.Invoke();

                var buffer = new StringBuilder();

                // 服务端若回报了真实用量（含缓存命中），就以它为准记账；否则回退到本地估算
                var usageReported = false;

                var request = new ChatRequest
                {
                    Model = _settings.Model,
                    SystemPrompt = systemPrompt,
                    Turns = history,
                    Temperature = _settings.Temperature,
                    TopP = _settings.TopP,
                    MaxTokens = _settings.MaxOutputTokens,

                    // 各厂商的模型高级选项（默认留空 = 不带这些字段，不影响兼容性）
                    ReasoningEffort = _settings.ReasoningEffort,
                    Verbosity = _settings.Verbosity,
                    ThinkingBudgetTokens = _settings.ThinkingBudgetTokens,
                    OnReasoning = callbacks.OnReasoning,

                    // 提示缓存：长前缀（系统提示词 / 工具清单 / 历史）在服务端命中缓存后按折扣价计费
                    PromptCaching = _settings.PromptCaching,
                    OnUsage = usage =>
                    {
                        usageReported = true;
                        UsageStore.Record(_settings.Model, usage.InputTokens, usage.OutputTokens, ConversationId, usage.CachedInputTokens);
                    },

                    // 原生 function calling（可选）：后端会把 tool_calls 翻译成动作块，
                    // 因此这里的循环完全不用改 —— 两条路径共用同一套解析与执行。
                    // OpenAI 兼容与 Anthropic 原生两套 tools 格式同时给出，各后端各取所需
                    // 规划模式下只暴露只读工具（与系统提示词里的清单保持一致）
                    ToolsJson = _settings.NativeToolCalling ? ToolCatalog.BuildToolsJson(_settings.PlanMode) : string.Empty,
                    AnthropicToolsJson = _settings.NativeToolCalling ? ToolCatalog.BuildAnthropicToolsJson(_settings.PlanMode) : string.Empty,

                    // 自定义网关的私有参数（例：vLLM/Qwen3 开思考）
                    ExtraBodyJson = _settings.GetProviderOption("caps.extraBody") ?? string.Empty,
                };

                await _backend
                    .SendAsync(request, delta => { buffer.Append(delta); callbacks.OnDelta?.Invoke(delta); }, cancellationToken)
                    .ConfigureAwait(false);

                var raw = buffer.ToString();
                var actions = ToolProtocol.ExtractActions(raw);
                callbacks.OnTurnComplete?.Invoke(raw, actions.Count > 0);

                // 用量 / 费用记账：服务端给了真实用量就用它（已在上面的回调里记账），
                // 没给才回退到本地估算 —— 供用量面板与预算提示使用
                if (!usageReported)
                {
                    RecordUsage(systemPrompt, history, raw);
                }

                if (actions.Count == 0)
                {
                    ChatDiagnostics.Log("本轮模型输出未含任何动作：" + raw);

                    // 动作围栏存在但解析失败：不能当成「任务完成」直接收尾，
                    // 否则用户看到的就是「任务没跑完就自动结束」。
                    if (ToolProtocol.ContainsActionFence(raw) && actionNudges < MaxActionNudges)
                    {
                        actionNudges++;
                        ChatDiagnostics.Log("检测到 action 围栏但解析失败，注入第 " + actionNudges + " 次修复提示");
                        callbacks.OnNotice?.Invoke("检测到动作块但解析失败（通常是 JSON 字符串里有未转义的换行或三反引号），已自动提示模型重发");
                        history.Add(new ChatTurn("assistant", raw));
                        history.Add(new ChatTurn("user", MalformedActionNudgePrompt));
                        continue;
                    }

                    // 模型给出最终答复若本轮改过文件、且「自动验证 / 自动修复」还有活要干，
                    // 就把后续请求注入历史继续跑，而不是就此收尾 —— 这是「改完还能跑通」的关键
                    var followUp = await BuildFollowUpAsync(callbacks, cancellationToken).ConfigureAwait(false);
                    if (followUp == null)
                    {
                        // 模型只「宣布要做」却没发出任何 ```action：催它一次真正动手。
                        // 本地小模型（vLLM 上的 Qwen 等）很常见：说了「我先读取文件」就收尾，
                        // 不加这一步，用户看到的就是「任务还没执行就结束了」。
                        if (actionNudges < MaxActionNudges && toolUsesThisRun == 0 && LooksLikeActionAnnouncement(raw))
                        {
                            actionNudges++;
                            ChatDiagnostics.Log("判定为「只说不做」，注入第 " + actionNudges + " 次续跑提示");
                            callbacks.OnNotice?.Invoke("模型只说了要做什么、并没有实际调用工具，已自动提示它继续执行");
                            history.Add(new ChatTurn("assistant", raw));
                            history.Add(new ChatTurn("user", ActionNudgePrompt));
                            continue;
                        }

                        return;
                    }

                    history.Add(new ChatTurn("assistant", raw));
                    history.Add(new ChatTurn("user", followUp));
                    continue;
                }

                toolUsesThisRun += actions.Count;

                // 回灌历史前折叠动作里的长参数：write_file 的 content 动辄几千字符，
                // 那段代码会在后面每一步请求里重发一遍，是长任务里最大的一笔冤枉开销
                // （界面渲染用的是上面 OnTurnComplete 收到的原始文本，不受影响）
                raw = FoldActionArguments(raw);

                history.Add(new ChatTurn("assistant", raw));

                var results = new StringBuilder();

                // 一次模型输出里可能有多个动作互不依赖的「子任务」可以并行跑（Windsurf / Whisperflow
                // 的 parallel agents 思路）：先把它们一起发出去，再按原顺序收结果 —— 顺序稳定，
                // 「谁在等谁」也仍然读得懂；文件写入由工作区的按路径锁保护其余动作按顺序执行
                var parallelSubTasks = SubTaskDepth == 0 && CountSubTasks(actions) > 1;
                var orderedResults = new ToolResult[actions.Count];
                var parallelTasks = new List<KeyValuePair<int, Task<ToolResult>>>();

                for (var i = 0; i < actions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var action = actions[i];

                    if (parallelSubTasks && action.Name == "new_task")
                    {
                        // 并行子任务：不做流式转发（两个模型的分片混进同一条消息会串行错乱）
                        parallelTasks.Add(new KeyValuePair<int, Task<ToolResult>>(i, ExecuteActionAsync(action, callbacks, cancellationToken, suppressStreaming: true)));
                        continue;
                    }

                    orderedResults[i] = await ExecuteActionAsync(action, callbacks, cancellationToken, suppressStreaming: false).ConfigureAwait(false);
                }

                if (parallelTasks.Count > 0)
                {
                    callbacks.OnNotice?.Invoke("并行执行 " + parallelTasks.Count + " 个子任务…");

                    // 任务在派发时已全部启动（真并行）；这里逐个 await 只是收结果，
                    // 总耗时仍是「最慢的那个」而不是累加；单个失败不影响其余
                    foreach (var pair in parallelTasks)
                    {
                        try
                        {
                            orderedResults[pair.Key] = await pair.Value.ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            orderedResults[pair.Key] = new ToolResult
                            {
                                Ok = false,
                                Output = "子任务执行失败：" + ex.GetBaseException().Message,
                            };
                        }
                    }
                }

                // 按模型给出动作的原顺序回灌结果 + 渲染卡片
                for (var i = 0; i < actions.Count; i++)
                {
                    var action = actions[i];
                    var result = orderedResults[i];
                    if (result == null)
                    {
                        continue;
                    }

                    callbacks.OnToolResult?.Invoke(action, result);

                    // 单条结果也过一道预算：一次 read_file / 命令输出就能有几万字符，
                    // 原样留在历史里意味着后面每一步都要重发
                    var fed = ShrinkToolResult(result.Output);

                    // 只读工具去重：同样的调用 + 同样的结果说明内容没变，
                    // 回灌一句「未变化」即可，不必把整段文件内容再发一次
                    if (result.Ok && IsDuplicateResult(action, fed))
                    {
                        fed = "（" + action.Name + " 的结果与本次任务中上一次完全相同，内容未变化，已省略）";
                    }

                    results.Append(ToolProtocol.FormatForModel(action.Name, result.Ok, fed)).Append('\n');

                    // 审计日志（可选）：记录这次工具调用的参数摘要与结果，便于复盘与合规
                    AuditToolCall(action, result);
                }

                history.Add(new ChatTurn("user", "工具执行结果：\n" + results));
            }

            callbacks.OnStepLimit?.Invoke(MaxSteps);
        }

        /// <summary>
        /// 折叠一轮任务内的历史（工具往返记录）。
        /// </summary>
        /// <remarks>
        /// 与旧实现的两点关键差异（都是为了让长任务更省 token）：
        /// <list type="number">
        /// <item><b>总结式</b>：被折叠的较早记录先交给模型压成一段结构化摘要，而不是直接丢弃 ——
        /// 直接丢弃会让模型丢掉目标 / 当前状态，转而去重新读文件，反而更费 token。</item>
        /// <item><b>只追加，不改写</b>：最早的一批轮次（<see cref="_frozenCount"/>）与已生成的摘要块
        /// （<see cref="_summaryCount"/>）永不改写，压缩只是在摘要区**追加**一条 ——
        /// 这样前后两次请求的前缀逐字节一致，提示缓存才能命中（旧实现每步都重排前缀，缓存全废）。</item>
        /// </list>
        /// </remarks>
        private async Task CompactHistoryAsync(List<ChatTurn> history, CancellationToken cancellationToken)
        {
            var window = _settings?.ContextWindow ?? 0;
            if (window <= 0)
            {
                return;
            }

            var budget = window * ((_settings?.CompactThresholdPercent ?? 80) / 100.0);

            double total = 0;
            foreach (var turn in history)
            {
                total += TokenEstimator.EstimateMessage(turn.Content);
            }

            if (total <= budget)
            {
                return;
            }

            // live tail 从「冻结前缀 + 已追加摘要」之后开始；只有它会被折叠
            var tailStart = _frozenCount + _summaryCount;
            if (tailStart >= history.Count)
            {
                return;
            }

            // 最近一段最多占预算的一半：另一半留给系统提示词与下一轮输出
            var recentBudget = budget / 2.0;
            var keepFrom = history.Count;
            var recent = 0.0;

            for (var i = history.Count - 1; i >= tailStart; i--)
            {
                recent += TokenEstimator.EstimateMessage(history[i].Content);
                keepFrom = i;
                if (recent >= recentBudget)
                {
                    break;
                }
            }

            // 连最近一段都超预算（单步输出就极长）：没有可折叠的中间段
            if (keepFrom <= tailStart)
            {
                return;
            }

            var dropped = history.GetRange(tailStart, keepFrom - tailStart);

            // 总结式压缩：让模型把被折叠段压成一段结构化摘要（失败 / 太短则退回固定说明）
            string summary = null;
            try
            {
                summary = await ContextSummarizer
                    .SummarizeAsync(_backend, _settings, dropped, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                summary = null;
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = "（为节省上下文，较早的 " + dropped.Count +
                          " 条工具往返记录已折叠；需要其中细节时请重新读取相关文件）";
            }
            else
            {
                summary = SummaryMarker + "（已折叠较早的 " + dropped.Count + " 条记录，以下为要点）\n" +
                          Trim(summary, 4000);
            }

            // 只在摘要区**追加**一条：tailStart 之前的字节一个都不动（缓存前缀保持稳定）
            history.RemoveRange(tailStart, keepFrom - tailStart);
            history.Insert(tailStart, new ChatTurn("assistant", summary));
            _summaryCount++;
        }

        /// <summary>
        /// 只读工具的重复结果判定：同样的调用 + 同样的结果 ⇒ 内容未变化，回灌可省略。
        /// </summary>
        private bool IsDuplicateResult(ToolAction action, string output)
        {
            if (!DedupTools.Contains(action.Name))
            {
                return false;
            }

            var key = action.Name + "|" + StableArgs(action);
            var digest = StableHash(output ?? string.Empty);

            if (_resultDigests.TryGetValue(key, out var previous) && previous == digest)
            {
                return true;
            }

            _resultDigests[key] = digest;
            return false;
        }

        /// <summary>
        /// 参数拼成稳定字符串（排序后拼接）：保证「同样一次调用」得到同样的 key。
        /// </summary>
        private static string StableArgs(ToolAction action)
        {
            var parts = new List<string>(action.Arguments.Count);
            foreach (var pair in action.Arguments)
            {
                parts.Add(pair.Key + "=" + (pair.Value ?? string.Empty));
            }

            parts.Sort(StringComparer.Ordinal);
            return string.Join("&", parts);
        }

        /// <summary>
        /// FNV-1a 64 位哈希：只用来判断「两次结果是否相同」，不做安全用途。
        /// </summary>
        private static ulong StableHash(string text)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            var hash = offset;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= prime;
            }

            return hash;
        }
        /// <summary>
        /// 单条工具结果回灌给模型时的字符上限（超出即掐掉中段）
        /// </summary>
        private const int MaxToolResultChars = 12000;

        /// <summary>
        /// 压缩一条工具结果
        /// </summary>
        /// <remarks>
        /// 读文件 / 搜索 / 命令输出动辄几万字符，而它们会**永久留在历史里、每一步重发一遍**：
        /// 不设上限的话，一次长任务几十步下来，光工具结果就能把上下文顶爆。
        /// </remarks>
        /// <summary>
        /// 动作围栏标记（三个反引号）运行时用 new string 拼出，而不是写字符串字面量：
        /// 本文件自身的读写也走 Markdown 围栏协议，源码里出现连续三个反引号会把围栏提前截断
        /// </summary>
        private static readonly string ActionFence = new string('`', 3);

        /// <summary>
        /// 回灌历史前折叠动作块里的**长参数**
        /// </summary>
        /// <remarks>
        /// 带动作的模型输出会原样进入历史，之后每一步请求都要重发一遍。
        /// 而 write_file 的 content 动辄几千字符，同一段代码重发 N 次，
        /// 是「改代码类任务」烧 token 的头号原因。
        /// 模型并不需要重读自己刚写下的内容（真要复查，读对应文件即可），
        /// 因此这里把超长参数换成「多少字符、已省略」的摘要。
        /// 只在**回灌历史**时折叠；界面渲染用的仍是原始文本。
        /// </remarks>
        private static string FoldActionArguments(string assistantText, int threshold = 600)
        {
            if (string.IsNullOrEmpty(assistantText) ||
                assistantText.IndexOf(ActionFence, StringComparison.Ordinal) < 0)
            {
                return assistantText;
            }

            // 与解析共用同一套范围识别：结束围栏可以内联在 JSON 后面，也可以独占一行
            var blocks = ToolProtocol.FindActionBlocks(assistantText);
            if (blocks.Count == 0)
            {
                return assistantText;
            }

            var builder = new StringBuilder(assistantText.Length);
            var cursor = 0;

            foreach (var block in blocks)
            {
                if (block.Start < cursor)
                {
                    continue;
                }

                // 开头围栏 + 语言标注原样搬走
                builder.Append(assistantText, cursor, block.BodyStart - cursor);

                if (!block.IsClosed)
                {
                    // 尚未闭合：原样保留，绝不破坏正在生成的动作
                    builder.Append(assistantText, block.BodyStart, assistantText.Length - block.BodyStart);
                    cursor = assistantText.Length;
                    break;
                }

                var body = assistantText.Substring(block.BodyStart, block.BodyEnd - block.BodyStart);
                builder.Append(FoldActionBodyKeepingWhitespace(body, threshold));
                cursor = block.BodyEnd;
            }

            if (cursor < assistantText.Length)
            {
                builder.Append(assistantText, cursor, assistantText.Length - cursor);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 折叠动作正文，但保留首尾空白（尤其是动作块开头那个换行）
        /// </summary>
        private static string FoldActionBodyKeepingWhitespace(string body, int threshold)
        {
            var lead = 0;
            while (lead < body.Length && char.IsWhiteSpace(body[lead]))
            {
                lead++;
            }

            var tail = body.Length;
            while (tail > lead && char.IsWhiteSpace(body[tail - 1]))
            {
                tail--;
            }

            if (lead == 0 && tail == body.Length)
            {
                return FoldActionBody(body, threshold);
            }

            return body.Substring(0, lead) +
                   FoldActionBody(body.Substring(lead, tail - lead), threshold) +
                   body.Substring(tail);
        }

        /// <summary>
        /// 折叠单个动作的正文（解析不出工具名时原样返回，绝不破坏动作协议）
        /// </summary>
        private static string FoldActionBody(string body, int threshold)
        {
            var parsed = LiteJson.ParseFlatObject(body);
            if (parsed == null || !parsed.ContainsKey("tool"))
            {
                return body;
            }

            var builder = new StringBuilder("{");
            var first = true;
            foreach (var pair in parsed)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(LiteJson.Quote(pair.Key)).Append(':');

                var value = pair.Value ?? string.Empty;
                builder.Append(value.Length <= threshold
                    ? LiteJson.Quote(value)
                    : LiteJson.Quote("（此处省略 " + value.Length + " 字符：内容已写入或已用于定位，需要复查时请读取对应文件）"));
            }

            return builder.Append('}').ToString();
        }

        private static string ShrinkToolResult(string output)
        {
            // 与跨轮回灌共用同一口径（见 ToolOutputTrimmer），避免两处截断逻辑漂移
            return ToolOutputTrimmer.Shrink(output, MaxToolResultChars);
        }


        /// <summary>
        /// 落盘一次改动，并在成功之后通知界面（界面据此打开 VS 差异对比）
        /// </summary>
        private ToolResult Apply(PendingEdit pending, AgentCallbacks callbacks)
        {
            var blocked = CheckFileDirty(pending, callbacks);
            if (blocked != null)
            {
                return blocked;
            }

            var result = _workspace.Apply(pending);

            if (result.Ok)
            {
                // 有改动落盘 = 值得跑自动验证 / 自动修复（见 BuildFollowUpAsync）
                _editApplied = true;
                callbacks.OnEditApplied?.Invoke(pending);

                // 改过文件后语义索引立刻失效：下一次检索重建，用户不必手动刷新
                _index.Invalidate();
            }

            return result;
        }

        /// <summary>
        /// 统一检查「编辑器里是否有未保存改动」。所有落盘路径（单文件 / 补丁 / 重命名）都必须先过这一关。
        /// </summary>
        private static ToolResult CheckFileDirty(PendingEdit pending, AgentCallbacks callbacks)
        {
            if (pending == null || callbacks?.IsFileDirty == null)
            {
                return null;
            }

            if (!callbacks.IsFileDirty(pending.RelativePath))
            {
                return null;
            }

            return new ToolResult
            {
                Ok = false,
                Output = "文件在 Visual Studio 编辑器中还有未保存的改动，已拒绝直接写盘。" + "请先保存（Ctrl+S）后再让我重试；这样不会覆盖你正在编辑的内容：" + pending.RelativePath,
            };
        }

    }
}

