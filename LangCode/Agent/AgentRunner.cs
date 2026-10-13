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
            "不要复述计划、也不必征求同意。如果确实已经全部完成、无需再调用任何工具，就直接给出最终结论。";

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

                // 每步都带着完整历史往返：不折叠的话，几十步的长任务迟早把上下文顶爆
                CompactHistory(history);

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
                    ToolsJson = _settings.NativeToolCalling ? ToolCatalog.BuildToolsJson() : string.Empty,
                    AnthropicToolsJson = _settings.NativeToolCalling ? ToolCatalog.BuildAnthropicToolsJson() : string.Empty,

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
                    results.Append(ToolProtocol.FormatForModel(action.Name, result.Ok, result.Output)).Append('\n');

                    // 审计日志（可选）：记录这次工具调用的参数摘要与结果，便于复盘与合规
                    AuditToolCall(action, result);
                }

                history.Add(new ChatTurn("user", "工具执行结果：\n" + results));
            }

            callbacks.OnStepLimit?.Invoke(MaxSteps);
        }

        /// <summary>
        /// 折叠一轮任务内的历史（工具往返记录）
        /// </summary>
        private void CompactHistory(List<ChatTurn> history)
        {
            var window = _settings?.ContextWindow ?? 0;
            if (window <= 0 || history.Count <= 4)
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

            // 最近一段最多占预算的一半：另一半留给系统提示词与下一轮输出
            var recentBudget = budget / 2.0;
            var keepFrom = history.Count;
            var recent = 0.0;

            for (var i = history.Count - 1; i >= 1; i--)
            {
                recent += TokenEstimator.EstimateMessage(history[i].Content);
                keepFrom = i;
                if (recent >= recentBudget)
                {
                    break;
                }
            }

            if (keepFrom <= 1)
            {
                return;
            }

            var compacted = new List<ChatTurn>
            {
                history[0],
                new ChatTurn(
                "assistant",
                "（为节省上下文，较早的 " + (keepFrom - 1) + " 条工具往返记录已折叠需要其中细节时请重新读取相关文件）")
            };

            for (var i = keepFrom; i < history.Count; i++)
            {
                compacted.Add(history[i]);
            }

            history.Clear();
            history.AddRange(compacted);
        }

        /// <summary>
        /// 落盘一次改动，并在成功之后通知界面（界面据此打开 VS 差异对比）
        /// </summary>
        private ToolResult Apply(PendingEdit pending, AgentCallbacks callbacks)
        {
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

    }
}

