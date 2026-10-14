using LangCode.Domain.Catalog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace LangCode.Domain.Settings
{
    /// <summary>
    /// 单个「提供商」的配置档案：每个厂商各自保存 Key / Base URL / 默认模型
    /// </summary>
    [DataContract(Name = "providerProfile")]
    public sealed class ProviderProfile
    {
        /// <summary>
        /// 厂商标识（与 ModelCatalog.Providers 的取值一致，作为主键）
        /// </summary>
        [DataMember(Name = "provider", Order = 1)]
        public string Provider { get; set; } = string.Empty;

        [DataMember(Name = "apiKey", Order = 2)]
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// 留空表示用该厂商的内置默认地址（见 ChatBackendFactory.ResolveBaseUrl）
        /// </summary>
        [DataMember(Name = "baseUrl", Order = 3)]
        public string BaseUrl { get; set; } = string.Empty;

        [DataMember(Name = "defaultModel", Order = 4)]
        public string DefaultModel { get; set; } = string.Empty;

        /// <summary>
        /// 推理强度（"" = 默认；minimal / low / medium / high）
        /// </summary>
        [DataMember(Name = "reasoningEffort", Order = 5)]
        public string ReasoningEffort { get; set; } = string.Empty;

        /// <summary>
        /// 回答详细度（"" = 默认；low / medium / high）
        /// </summary>
        [DataMember(Name = "verbosity", Order = 6)]
        public string Verbosity { get; set; } = string.Empty;

        /// <summary>
        /// 思考预算（token；0 = 默认/不显式指定）仅对支持 thinking 的端点生效
        /// </summary>
        [DataMember(Name = "thinkingBudgetTokens", Order = 7)]
        public int ThinkingBudgetTokens { get; set; }

        /// <summary>
        /// 提供商专属的额外选项（区域 / Azure 版本 / 端点选择等），键值对形式
        /// </summary>
        [DataMember(Name = "options", Order = 8)]
        public Dictionary<string, string> Options { get; set; } = [];

        public ProviderProfile Clone()
        {
            return new ProviderProfile
            {
                Provider = Provider,
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                DefaultModel = DefaultModel,
                ReasoningEffort = ReasoningEffort,
                Verbosity = Verbosity,
                ThinkingBudgetTokens = ThinkingBudgetTokens,
                Options = new Dictionary<string, string>(Options ?? []),
            };
        }
    }

    /// <summary>
    /// 一套「命名配置档案」：用户自己起名（如 AA / BB），内含整套连接信息
    /// </summary>
    [DataContract(Name = "namedProfile")]
    public sealed class NamedProfile
    {
        /// <summary>
        /// 用户起的名字（AA / BB / 公司网关…），也是主键
        /// </summary>
        [DataMember(Name = "name", Order = 1)]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "provider", Order = 2)]
        public string Provider { get; set; } = string.Empty;

        [DataMember(Name = "baseUrl", Order = 3)]
        public string BaseUrl { get; set; } = string.Empty;

        [DataMember(Name = "apiKey", Order = 4)]
        public string ApiKey { get; set; } = string.Empty;

        [DataMember(Name = "model", Order = 5)]
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// 列表里的一行摘要（「OpenAI · gpt-4o」）
        /// </summary>
        public string Summary
        {
            get
            {
                var provider = string.IsNullOrWhiteSpace(Provider) ? "未指定厂商" : Provider;
                return string.IsNullOrWhiteSpace(Model) ? provider : provider + " · " + Model;
            }
        }

        public NamedProfile Clone()
        {
            return new NamedProfile
            {
                Name = Name,
                Provider = Provider,
                BaseUrl = BaseUrl,
                ApiKey = ApiKey,
                Model = Model,
            };
        }
    }

    /// <summary>
    /// 插件设置（1:1 对应「设置」抽屉里的字段）
    /// </summary>
    [DataContract(Name = "appSettings")]
    public sealed class AppSettings
    {
        // ==================== 外观 ====================
        // 主题不提供用户配置：插件始终跟随 Visual Studio 的深色 / 浅色主题（见 Ui/Theme/ThemeManager.cs）

        [DataMember(Name = "language", Order = 2)]
        public string Language { get; set; } = "简体中文";

        [DataMember(Name = "fontSize", Order = 3)]
        public double FontSize { get; set; } = 13;

        // ==================== 行为 ====================
        /// <summary>
        /// 「按类别自动批准」——每个类别一个开关，语义统一是「这类操作**不用再问我**」
        /// </summary>
        /// <summary>
        /// AI 操作文件时在 VS 编辑器里跟随显示：读取 / 新增打开文件，编辑打开差异对比
        /// </summary>
        [DataMember(Name = "followFileChanges", Order = 8)]
        public bool FollowFileChanges { get; set; } = true;

        [DataMember(Name = "autoApproveReads", Order = 9)]
        public bool AutoApproveReads { get; set; } = true;

        [DataMember(Name = "autoApproveEdits", Order = 10)]
        public bool AutoApproveEdits { get; set; } = true;

        [DataMember(Name = "autoApproveCreates", Order = 11)]
        public bool AutoApproveCreates { get; set; }

        [DataMember(Name = "autoApproveCommands", Order = 12)]
        public bool AutoApproveCommands { get; set; }

        [DataMember(Name = "autoApproveBrowser", Order = 13)]
        public bool AutoApproveBrowser { get; set; }

        [DataMember(Name = "autoApproveMcp", Order = 14)]
        public bool AutoApproveMcp { get; set; }

        [DataMember(Name = "showThinking", Order = 20)]
        public bool ShowThinking { get; set; } = true;

        /// <summary>
        /// 思考时是否自动摊开<b>默认关闭</b>：推理内容一律默认折叠，想看再点箭头 ——
        /// 之前「思考时自动展开」会把每一屏都顶开，多步任务里根本读不到正文
        /// </summary>
        [DataMember(Name = "expandThinking", Order = 46)]
        public bool ExpandThinking { get; set; }

        [DataMember(Name = "collapseLongText", Order = 21)]
        public bool CollapseLongText { get; set; } = true;

        [DataMember(Name = "desktopNotifications", Order = 22)]
        public bool DesktopNotifications { get; set; } = true;

        // ==================== 连接 / 模型（活动项） ====================
        /// <summary>
        /// 当前生效的厂商
        /// </summary>
        [DataMember(Name = "provider", Order = 20)]
        public string Provider { get; set; } = "DeepSeek";

        /// <summary>
        /// 当前生效厂商的 API Key（与 <see cref="Profiles"/> 里对应档案保持同步）
        /// </summary>
        [DataMember(Name = "apiKey", Order = 21)]
        public string ApiKey { get; set; } = string.Empty;

        [DataMember(Name = "baseUrl", Order = 22)]
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// 当前模型 id默认取 DeepSeek 当前的旗舰（见 <see cref="ModelCatalog"/> 的核对说明）
        /// 这个下拉是可编辑的，任何新模型都能直接输入
        /// </summary>
        [DataMember(Name = "model", Order = 23)]
        public string Model { get; set; } = "deepseek-flash";

        /// <summary>
        /// 各厂商的独立配置（「同时配置多个模型」的核心）
        /// </summary>
        [DataMember(Name = "profiles", Order = 30)]
        public List<ProviderProfile> Profiles { get; set; } = [];

        /// <summary>
        /// 用户自定义命名的多套配置（AA / BB …）
        /// </summary>
        [DataMember(Name = "namedProfiles", Order = 31)]
        public List<NamedProfile> NamedProfiles { get; set; } = [];

        /// <summary>
        /// 当前生效的命名档案名（空表示没在用命名档案）
        /// </summary>
        [DataMember(Name = "activeProfile", Order = 32)]
        public string ActiveProfileName { get; set; } = string.Empty;

        [DataMember(Name = "maxOutputTokens", Order = 24)]
        public int MaxOutputTokens { get; set; } = 8192;

        /// <summary>
        /// 上下文窗口（token）默认按 128k 保守估计：宁可早一点折叠历史，也别攒到把上下文顶爆
        /// </summary>
        [DataMember(Name = "contextWindow", Order = 25)]
        public int ContextWindow { get; set; } = 131072;

        [DataMember(Name = "temperature", Order = 26)]
        public double Temperature { get; set; } = 0.2;

        [DataMember(Name = "topP", Order = 27)]
        public double TopP { get; set; } = 1.0;

        /// <summary>
        /// 一轮任务内最多允许的「模型 → 工具」往返次数；<b>0 = 不限制</b>（默认）
        /// </summary>
        [DataMember(Name = "maxAgentSteps", Order = 45)]
        public int MaxAgentSteps { get; set; }

        [DataMember(Name = "rateLimit", Order = 28)]
        public bool RateLimit { get; set; }

        [DataMember(Name = "autoRetry", Order = 29)]
        public bool AutoRetry { get; set; } = true;

        // ==================== MCP ====================
        [DataMember(Name = "mcpServers", Order = 40)]
        public List<McpServerSettings> McpServers { get; set; } = [];

        [DataMember(Name = "autoConnectMcp", Order = 41)]
        public bool AutoConnectMcp { get; set; } = true;

        [DataMember(Name = "confirmMcpTools", Order = 42)]
        public bool ConfirmMcpTools { get; set; }

        [DataMember(Name = "logMcpCalls", Order = 43)]
        public bool LogMcpCalls { get; set; } = true;

        // ==================== 编辑器集成 ====================
        [DataMember(Name = "checkpoints", Order = 50)]
        public bool Checkpoints { get; set; } = true;

        [DataMember(Name = "browserTools", Order = 51)]
        public bool BrowserTools { get; set; } = true;

        [DataMember(Name = "autoReadProblems", Order = 52)]
        public bool AutoReadProblems { get; set; } = true;

        [DataMember(Name = "formatOnSave", Order = 53)]
        public bool FormatOnSave { get; set; }

        /// <summary>
        /// 规划模式 
        /// </summary>
        [DataMember(Name = "planMode", Order = 55)]
        public bool PlanMode { get; set; }

        /// <summary>
        /// 命令黑名单（分号 / 换行分隔的子串，命中即拒绝执行）
        /// </summary>
        [DataMember(Name = "commandDenyList", Order = 56)]
        public string CommandDenyList { get; set; } = string.Empty;

        /// <summary>
        /// 命令信任前缀（分号 / 换行分隔）：命中则跳过内置危险检查
        /// </summary>
        [DataMember(Name = "commandAllowPrefixes", Order = 57)]
        public string CommandAllowPrefixes { get; set; } = string.Empty;

        /// <summary>
        /// 原生 function calling：请求里附带 <c>tools</c>，并把流式的 <c>tool_calls</c> 翻译成动作块
        /// </summary>
        [DataMember(Name = "nativeToolCalling", Order = 58)]
        public bool NativeToolCalling { get; set; }

        // ==================== 模型高级选项（按当前提供商生效） ====================
        /// <summary>
        /// 推理强度（"" = 默认；minimal / low / medium / high）
        /// </summary>
        [DataMember(Name = "reasoningEffort", Order = 110)]
        public string ReasoningEffort { get; set; } = string.Empty;

        /// <summary>
        /// 回答详细度（"" = 默认；low / medium / high）
        /// </summary>
        [DataMember(Name = "verbosity", Order = 111)]
        public string Verbosity { get; set; } = string.Empty;

        /// <summary>
        /// 思考预算（token；0 = 默认）对支持的端点会注入 <c>thinking.budget_tokens</c>
        /// </summary>
        [DataMember(Name = "thinkingBudgetTokens", Order = 112)]
        public int ThinkingBudgetTokens { get; set; }

        /// <summary>
        /// 当前提供商专属的额外选项（区域 / Azure 版本 / 端点选择等）
        /// </summary>
        [DataMember(Name = "providerOptions", Order = 113)]
        public Dictionary<string, string> ProviderOptions { get; set; } = [];

        /// <summary>
        /// 读取一个提供商选项（不存在返回空串）
        /// </summary>
        public string GetProviderOption(string key)
        {
            return !string.IsNullOrEmpty(key) && ProviderOptions != null && ProviderOptions.TryGetValue(key, out var value)
                ? (value ?? string.Empty)
                : string.Empty;
        }

        /// <summary>
        /// 写入 / 删除一个提供商选项（值为空即删除）
        /// </summary>
        public void SetProviderOption(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            ProviderOptions ??= [];

            if (string.IsNullOrEmpty(value))
            {
                ProviderOptions.Remove(key);
            }
            else
            {
                ProviderOptions[key] = value;
            }
        }

        // ==================== 上下文自动化 ====================
        /// <summary>
        /// 加载项目规则文件（<c>AGENTS.md</c> / <c>.langcoderules</c> / <c>.langcode/rules/*.md</c> 等）
        /// 并注入系统提示词关闭后只保留全局「自定义指令」
        /// </summary>
        [DataMember(Name = "useProjectRules", Order = 80)]
        public bool UseProjectRules { get; set; } = true;

        /// <summary>
        /// 发送前自动附带上下文（当前文件 / 编辑器选中代码 / 编译诊断），
        /// 用户不必每次手打 <c>@当前文件</c>
        /// </summary>
        [DataMember(Name = "autoContext", Order = 81)]
        public bool AutoContext { get; set; } = true;

        /// <summary>
        /// 自动修复循环：写了文件之后读「错误列表」的诊断，若还有错误就回灌给模型继续修，
        /// 直到修完或达到 <see cref="AutoFixMaxRounds"/> 轮
        /// </summary>
        [DataMember(Name = "autoFixProblems", Order = 82)]
        public bool AutoFixProblems { get; set; } = true;

        /// <summary>
        /// 自动修复的最大轮次（防死循环）
        /// </summary>
        [DataMember(Name = "autoFixMaxRounds", Order = 83)]
        public int AutoFixMaxRounds { get; set; } = 2;

        /// <summary>
        /// 改完文件后自动运行 <see cref="VerifyCommand"/> 并把结果回灌给模型
        /// </summary>
        [DataMember(Name = "autoVerify", Order = 84)]
        public bool AutoVerify { get; set; } = true;

        /// <summary>
        /// 验证命令（如 <c>dotnet build</c> / <c>npm test</c>）；留空表示不做自动验证
        /// </summary>
        [DataMember(Name = "verifyCommand", Order = 85)]
        public string VerifyCommand { get; set; } = string.Empty;

        // ==================== 成本 / 用量 / 治理 ====================
        /// <summary>
        /// 费用预算（美元）<b>0 = 不设预算</b>；达到后界面提示（并可在用量面板里看到）
        /// </summary>
        [DataMember(Name = "budgetUsd", Order = 86)]
        public double BudgetUsd { get; set; }

        /// <summary>
        /// 调用审计日志：开启后每次工具调用（名字 + 参数 + 结果摘要）追加写入
        /// <c>%APPDATA%\LangCode\vsext\audit.log</c>，便于复盘与合规默认关闭
        /// </summary>
        [DataMember(Name = "auditLogEnabled", Order = 87)]
        public bool AuditLogEnabled { get; set; }

        /// <summary>
        /// 提示缓存（prompt caching）：长前缀（系统提示词 / 工具清单 / 历史）在服务端命中缓存后按折扣价计费
        /// </summary>
        [DataMember(Name = "promptCaching", Order = 88)]
        public bool PromptCaching { get; set; } = true;

        [DataMember(Name = "customInstructions", Order = 54)]
        public string CustomInstructions { get; set; } = string.Empty;

        // ==================== 多模态（图片输入） ====================
        /// <summary>
        /// 允许在输入框粘贴 / 拖入图片，走 OpenAI 兼容的 <c>image_url</c> 多模态消息发给视觉模型
        /// </summary>
        [DataMember(Name = "multimodal", Order = 90)]
        public bool Multimodal { get; set; } = true;

        /// <summary>
        /// 单条消息最多附带几张图片（防止一次贴十几张把上下文顶爆）
        /// </summary>
        [DataMember(Name = "maxImagesPerMessage", Order = 91)]
        public int MaxImagesPerMessage { get; set; } = 4;

        /// <summary>
        /// 单张图片的原始字节上限（超出则压缩 / 拒绝），默认 5MB
        /// </summary>
        [DataMember(Name = "imageMaxBytes", Order = 92)]
        public int ImageMaxBytes { get; set; } = 5 * 1024 * 1024;

        // ==================== 浏览器自动化 ====================
        /// <summary>
        /// 是否启用浏览器自动化（<c>browser_action</c>）：通过 Chrome/Edge 的 DevTools 协议
        /// 做导航、点击、填表、截图与读取 console，用于前端自测与 UI 报错定位
        /// </summary>
        [DataMember(Name = "browserAutomation", Order = 93)]
        public bool BrowserAutomation { get; set; } = true;

        /// <summary>
        /// 浏览器可执行文件路径（留空则自动探测 Edge / Chrome 的默认安装位置）
        /// </summary>
        [DataMember(Name = "browserExecutablePath", Order = 94)]
        public string BrowserExecutablePath { get; set; } = string.Empty;

        /// <summary>
        /// 远程调试端口（默认 9222）；已有一个调试实例时会直接复用
        /// </summary>
        [DataMember(Name = "browserRemoteDebuggingPort", Order = 95)]
        public int BrowserRemoteDebuggingPort { get; set; } = 9222;

        /// <summary>
        /// 无头模式（背景运行，不弹窗）前端自测建议开启
        /// </summary>
        [DataMember(Name = "browserHeadless", Order = 96)]
        public bool BrowserHeadless { get; set; } = true;

        /// <summary>
        /// 单次浏览器动作的超时（秒）
        /// </summary>
        [DataMember(Name = "browserTimeoutSeconds", Order = 97)]
        public int BrowserTimeoutSeconds { get; set; } = 30;

        // ==================== 语义索引 / RAG ====================
        /// <summary>
        /// 是否启用本地代码库语义索引（<c>semantic_search</c> 等）
        /// </summary>
        [DataMember(Name = "semanticIndex", Order = 98)]
        public bool SemanticIndex { get; set; } = true;

        /// <summary>
        /// 语义检索返回的片段数量
        /// </summary>
        [DataMember(Name = "semanticSearchResults", Order = 99)]
        public int SemanticSearchResults { get; set; } = 8;

        /// <summary>
        /// 建索引时最多扫描的文件数（超大仓库的保护阀）
        /// </summary>
        [DataMember(Name = "semanticIndexMaxFiles", Order = 100)]
        public int SemanticIndexMaxFiles { get; set; } = 2000;

        /// <summary>
        /// 单个索引片段的最大字符数
        /// </summary>
        [DataMember(Name = "semanticChunkChars", Order = 101)]
        public int SemanticChunkChars { get; set; } = 1600;

        // ==================== 结构化编辑 ====================
        /// <summary>
        /// 是否启用结构化编辑（<c>edit_symbol</c>）：按符号名 / 括号锚点定位代码块再改写，
        /// 比「字符串唯一匹配」更抗格式漂移
        /// </summary>
        [DataMember(Name = "structuredEditing", Order = 102)]
        public bool StructuredEditing { get; set; } = true;

        // ==================== 上下文 / 隐私 ====================
        [DataMember(Name = "autoCompactThreshold", Order = 60)]
        public string AutoCompactThreshold { get; set; } = "60%";

        /// <summary>
        /// 「自动压缩阈值」的数字形式（10~100）解析不出或越界时按 80 处理
        /// </summary>
        public int CompactThresholdPercent
        {
            get
            {
                var value = (AutoCompactThreshold ?? string.Empty).Trim().TrimEnd('%').Trim();
                return int.TryParse(value, out var percent) && percent >= 10 && percent <= 100
                    ? percent
                    : 80;
            }
        }

        [DataMember(Name = "maxFilesPerRead", Order = 61)]
        public int MaxFilesPerRead { get; set; } = 20;

        [DataMember(Name = "anonymousUsage", Order = 62)]
        public bool AnonymousUsage { get; set; }

        [DataMember(Name = "localSessionHistory", Order = 63)]
        public bool LocalSessionHistory { get; set; } = true;

        [DataMember(Name = "autoCheckUpdates", Order = 70)]
        public bool AutoCheckUpdates { get; set; } = true;

        // ==================== 档案操作 ====================

        /// <summary>
        /// 取某厂商的档案（不存在则创建）
        /// </summary>
        public ProviderProfile GetOrCreateProfile(string provider)
        {
            var key = string.IsNullOrWhiteSpace(provider) ? "Anthropic" : provider.Trim();

            Profiles ??= [];

            var profile = Profiles.FirstOrDefault(p =>
                string.Equals(p.Provider, key, StringComparison.OrdinalIgnoreCase));

            if (profile == null)
            {
                profile = new ProviderProfile { Provider = key };
                Profiles.Add(profile);
            }

            return profile;
        }

        /// <summary>
        /// 把当前活动的 Key / Base URL / 模型写回活动厂商的档案
        /// </summary>
        public void StoreActiveProfile()
        {
            var profile = GetOrCreateProfile(Provider);
            profile.ApiKey = ApiKey ?? string.Empty;
            profile.BaseUrl = BaseUrl ?? string.Empty;
            profile.DefaultModel = Model ?? string.Empty;
            profile.ReasoningEffort = ReasoningEffort ?? string.Empty;
            profile.Verbosity = Verbosity ?? string.Empty;
            profile.ThinkingBudgetTokens = ThinkingBudgetTokens;
            profile.Options = new Dictionary<string, string>(ProviderOptions ?? []);
        }

        /// <summary>
        /// 按模型目录里该模型的真实能力，自动对齐「上下文窗口」与「最大输出」。
        /// 模型不在目录里（自定义网关 / 手输 id）时保持原值不动 —— 不拿未知值去覆盖用户的设置。
        /// </summary>
        public void AlignLimitsToModel(string modelId)
        {
            var id = string.IsNullOrWhiteSpace(modelId) ? Model : modelId.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var context = ModelCatalog.ContextTokensOf(id);
            if (context > 0)
            {
                ContextWindow = context;
            }

            var maxOutput = ModelCatalog.MaxOutputTokensOf(id);
            if (maxOutput > 0)
            {
                MaxOutputTokens = maxOutput;
            }
        }

        /// <summary>
        /// 设置里「已配置过」的厂商集合：当前厂商 + 已有独立档案的厂商 + 命名档案里的厂商。
        /// 输入框的模型弹层据此只列「已配置好的模型」，拉取/新增模型一律在设置页完成。
        /// </summary>
        public IList<string> ConfiguredProviders()
        {
            var result = new List<string>();

            if (!string.IsNullOrWhiteSpace(Provider))
            {
                result.Add(Provider);
            }

            foreach (var profile in Profiles ?? [])
            {
                if (!string.IsNullOrWhiteSpace(profile.Provider) &&
                    !result.Contains(profile.Provider, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(profile.Provider);
                }
            }

            foreach (var named in NamedProfiles ?? [])
            {
                if (!string.IsNullOrWhiteSpace(named.Provider) &&
                    !result.Contains(named.Provider, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(named.Provider);
                }
            }

            return result;
        }

        // ==================== 命名配置档案 ====================

        /// <summary>
        /// 按名字找命名档案；找不到返回 null
        /// </summary>
        public NamedProfile FindNamedProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || NamedProfiles == null)
            {
                return null;
            }

            var key = name.Trim();
            return NamedProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 把当前正在用的配置存成一个命名档案（同名则覆盖）
        /// </summary>
        public NamedProfile SaveNamedProfile(string name)
        {
            var key = (name ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                return null;
            }

            NamedProfiles ??= [];

            // 当前活动配置同时也是某个厂商的档案，先落回去再拷进命名档案
            StoreActiveProfile();

            var profile = FindNamedProfile(key);
            if (profile == null)
            {
                profile = new NamedProfile { Name = key };
                NamedProfiles.Add(profile);
            }

            profile.Provider = Provider;
            profile.BaseUrl = BaseUrl ?? string.Empty;
            profile.ApiKey = ApiKey ?? string.Empty;
            profile.Model = Model ?? string.Empty;

            ActiveProfileName = profile.Name;
            return profile;
        }

        /// <summary>
        /// 切换到某个命名档案：Provider / Base URL / Key / 模型整套一起换
        /// </summary>
        /// <returns>档案不存在时返回 false</returns>
        public bool ApplyNamedProfile(string name)
        {
            var profile = FindNamedProfile(name);
            if (profile == null)
            {
                return false;
            }

            // 先把当前改动存回它所属厂商的槽，避免「切走了就丢」
            StoreActiveProfile();

            Provider = string.IsNullOrWhiteSpace(profile.Provider) ? Provider : profile.Provider;
            BaseUrl = profile.BaseUrl ?? string.Empty;
            ApiKey = profile.ApiKey ?? string.Empty;
            Model = profile.Model ?? string.Empty;
            ActiveProfileName = profile.Name;

            // 同步写回「按厂商」的槽位：两条路径（按厂商 / 按名字）看到的是同一套配置
            StoreActiveProfile();
            return true;
        }

        /// <summary>
        /// 删除一个命名档案；若删的是当前生效的那个，就清掉激活标记
        /// </summary>
        public bool RemoveNamedProfile(string name)
        {
            var profile = FindNamedProfile(name);
            if (profile == null)
            {
                return false;
            }

            NamedProfiles.Remove(profile);

            if (string.Equals(ActiveProfileName, profile.Name, StringComparison.OrdinalIgnoreCase))
            {
                ActiveProfileName = string.Empty;
            }

            return true;
        }

        /// <summary>
        /// 切换厂商：先把旧厂商的配置存回它的档案，再装入新厂商的档案
        /// </summary>
        /// <param name="provider">目标厂商</param>
        /// <param name="model">可选的模型 id（为空则用该厂商档案里的默认模型）</param>
        public void SwitchProvider(string provider, string model = null)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                return;
            }

            StoreActiveProfile();

            Provider = provider.Trim();
            var profile = GetOrCreateProfile(Provider);

            ApiKey = profile.ApiKey ?? string.Empty;
            BaseUrl = profile.BaseUrl ?? string.Empty;
            ReasoningEffort = profile.ReasoningEffort ?? string.Empty;
            Verbosity = profile.Verbosity ?? string.Empty;
            ThinkingBudgetTokens = profile.ThinkingBudgetTokens;
            ProviderOptions = new Dictionary<string, string>(profile.Options ?? []);

            if (!string.IsNullOrWhiteSpace(model))
            {
                Model = model.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(profile.DefaultModel))
            {
                Model = profile.DefaultModel;
            }

            profile.DefaultModel = Model ?? string.Empty;
        }

        /// <summary>
        /// 用默认值补齐缺失字段
        /// </summary>
        public AppSettings Normalize()
        {
            var defaults = new AppSettings();

            Language = Fallback(Language, defaults.Language);
            FontSize = FontSize <= 0 ? defaults.FontSize : FontSize;

            // 旧设置里可能存着历史别名（如「Ollama (本地)」）：归一化到当前规范名
            Provider = ProviderCatalog.Canonicalize(Fallback(Provider, defaults.Provider));
            ApiKey ??= string.Empty;
            BaseUrl ??= string.Empty;
            Model = Fallback(Model, defaults.Model);
            ReasoningEffort ??= string.Empty;
            Verbosity ??= string.Empty;
            if (ThinkingBudgetTokens < 0)
            {
                ThinkingBudgetTokens = 0;
            }

            ProviderOptions ??= [];

            if (MaxOutputTokens <= 0)
            {
                MaxOutputTokens = defaults.MaxOutputTokens;
            }

            if (ContextWindow <= 0)
            {
                ContextWindow = defaults.ContextWindow;
            }

            if (Temperature < 0)
            {
                Temperature = defaults.Temperature;
            }

            // 负数没有意义：统一当成「不限制」
            if (MaxAgentSteps < 0)
            {
                MaxAgentSteps = 0;
            }

            if (TopP <= 0)
            {
                TopP = defaults.TopP;
            }

            // ---- 配置档案：先清理，再做双向迁移（档案为真相来源）----
            Profiles ??= [];

            Profiles.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Provider));

            var active = GetOrCreateProfile(Provider);

            // 旧版本只有扁平字段：首次迁移时把它们灌进当前厂商档案
            if (string.IsNullOrEmpty(active.ApiKey) && !string.IsNullOrEmpty(ApiKey))
            {
                active.ApiKey = ApiKey;
            }

            if (string.IsNullOrEmpty(active.BaseUrl) && !string.IsNullOrEmpty(BaseUrl))
            {
                active.BaseUrl = BaseUrl;
            }

            if (string.IsNullOrEmpty(active.DefaultModel) && !string.IsNullOrEmpty(Model))
            {
                active.DefaultModel = Model;
            }

            // 档案 → 活动字段（保证两者一致）
            ApiKey = active.ApiKey ?? string.Empty;
            BaseUrl = active.BaseUrl ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(active.DefaultModel))
            {
                Model = active.DefaultModel;
            }
            else
            {
                active.DefaultModel = Model;
            }

            McpServers ??= [];

            // 每个服务器的环境变量字典补齐（旧设置里没有这个字段）
            foreach (var server in McpServers)
            {
                if (server != null)
                {
                    server.Environment ??= [];
                }
            }

            CustomInstructions ??= string.Empty;
            AutoCompactThreshold = Fallback(AutoCompactThreshold, defaults.AutoCompactThreshold);
            MaxFilesPerRead = MaxFilesPerRead <= 0 ? defaults.MaxFilesPerRead : MaxFilesPerRead;

            // 自动修复轮次：反序列化缺该成员时是 0 —— 0 轮等于功能失效，兜回默认值
            if (AutoFixMaxRounds <= 0)
            {
                AutoFixMaxRounds = defaults.AutoFixMaxRounds;
            }

            VerifyCommand ??= string.Empty;

            // 预算：负值没有意义，统一当成「不设预算」
            if (BudgetUsd < 0)
            {
                BudgetUsd = 0;
            }

            return this;
        }

        private static string Fallback(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        /// <summary>
        /// 反序列化完成后立刻补齐缺省（对任何 JSON 来源都生效）
        /// </summary>
        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            Normalize();
        }

        /// <summary>
        /// 把另一份设置的内容灌进**当前实例**（导入 / 重置用）
        /// </summary>
        public void CopyFrom(AppSettings other)
        {
            if (other == null)
            {
                return;
            }

            var source = other.Normalize();

            Language = source.Language;
            FontSize = source.FontSize;

            FollowFileChanges = source.FollowFileChanges;
            AutoApproveReads = source.AutoApproveReads;
            AutoApproveEdits = source.AutoApproveEdits;
            AutoApproveCreates = source.AutoApproveCreates;
            AutoApproveCommands = source.AutoApproveCommands;
            AutoApproveBrowser = source.AutoApproveBrowser;
            AutoApproveMcp = source.AutoApproveMcp;
            ShowThinking = source.ShowThinking;
            ExpandThinking = source.ExpandThinking;
            CollapseLongText = source.CollapseLongText;
            DesktopNotifications = source.DesktopNotifications;

            Provider = source.Provider;
            ApiKey = source.ApiKey;
            BaseUrl = source.BaseUrl;
            Model = source.Model;
            ReasoningEffort = source.ReasoningEffort;
            Verbosity = source.Verbosity;
            ThinkingBudgetTokens = source.ThinkingBudgetTokens;
            ProviderOptions = new Dictionary<string, string>(source.ProviderOptions ?? []);
            MaxAgentSteps = source.MaxAgentSteps;
            MaxOutputTokens = source.MaxOutputTokens;
            ContextWindow = source.ContextWindow;
            Temperature = source.Temperature;
            TopP = source.TopP;
            RateLimit = source.RateLimit;
            AutoRetry = source.AutoRetry;

            McpServers = (source.McpServers ?? []).Select(s => s.Clone()).ToList();
            AutoConnectMcp = source.AutoConnectMcp;
            ConfirmMcpTools = source.ConfirmMcpTools;
            LogMcpCalls = source.LogMcpCalls;

            Checkpoints = source.Checkpoints;
            BrowserTools = source.BrowserTools;
            AutoReadProblems = source.AutoReadProblems;
            FormatOnSave = source.FormatOnSave;
            PlanMode = source.PlanMode;
            CommandDenyList = source.CommandDenyList;
            CommandAllowPrefixes = source.CommandAllowPrefixes;
            NativeToolCalling = source.NativeToolCalling;

            // 多模态 / 浏览器自动化 / 语义索引 / 结构化编辑（此前遗漏，克隆与导入会丢）
            Multimodal = source.Multimodal;
            MaxImagesPerMessage = source.MaxImagesPerMessage;
            ImageMaxBytes = source.ImageMaxBytes;
            BrowserAutomation = source.BrowserAutomation;
            BrowserExecutablePath = source.BrowserExecutablePath;
            BrowserRemoteDebuggingPort = source.BrowserRemoteDebuggingPort;
            BrowserHeadless = source.BrowserHeadless;
            BrowserTimeoutSeconds = source.BrowserTimeoutSeconds;
            SemanticIndex = source.SemanticIndex;
            SemanticSearchResults = source.SemanticSearchResults;
            SemanticIndexMaxFiles = source.SemanticIndexMaxFiles;
            SemanticChunkChars = source.SemanticChunkChars;
            StructuredEditing = source.StructuredEditing;

            UseProjectRules = source.UseProjectRules;
            AutoContext = source.AutoContext;
            AutoFixProblems = source.AutoFixProblems;
            AutoFixMaxRounds = source.AutoFixMaxRounds;
            AutoVerify = source.AutoVerify;
            VerifyCommand = source.VerifyCommand;
            BudgetUsd = source.BudgetUsd;
            AuditLogEnabled = source.AuditLogEnabled;
            PromptCaching = source.PromptCaching;

            // 命名档案整体拷一份（含里面的对象，避免两个 AppSettings 共享同一个列表元素）
            NamedProfiles = (source.NamedProfiles ?? []).Select(p => p.Clone()).ToList();
            ActiveProfileName = source.ActiveProfileName ?? string.Empty;
            CustomInstructions = source.CustomInstructions;

            AutoCompactThreshold = source.AutoCompactThreshold;
            MaxFilesPerRead = source.MaxFilesPerRead;
            AnonymousUsage = source.AnonymousUsage;
            LocalSessionHistory = source.LocalSessionHistory;
            AutoCheckUpdates = source.AutoCheckUpdates;

            Profiles = (source.Profiles ?? []).Select(p => p.Clone()).ToList();
        }

        /// <summary>
        /// 深拷贝（导出 / 重置前备份用）
        /// </summary>
        public AppSettings Clone()
        {
            var clone = new AppSettings
            {
                Language = Language,
                FontSize = FontSize,
                FollowFileChanges = FollowFileChanges,
                AutoApproveReads = AutoApproveReads,
                AutoApproveEdits = AutoApproveEdits,
                AutoApproveCreates = AutoApproveCreates,
                AutoApproveCommands = AutoApproveCommands,
                AutoApproveBrowser = AutoApproveBrowser,
                AutoApproveMcp = AutoApproveMcp,
                ShowThinking = ShowThinking,
                ExpandThinking = ExpandThinking,
                CollapseLongText = CollapseLongText,
                DesktopNotifications = DesktopNotifications,
                Provider = Provider,
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                Model = Model,
                ReasoningEffort = ReasoningEffort,
                Verbosity = Verbosity,
                ThinkingBudgetTokens = ThinkingBudgetTokens,
                ProviderOptions = new Dictionary<string, string>(ProviderOptions ?? []),
                MaxAgentSteps = MaxAgentSteps,
                MaxOutputTokens = MaxOutputTokens,
                ContextWindow = ContextWindow,
                Temperature = Temperature,
                TopP = TopP,
                RateLimit = RateLimit,
                AutoRetry = AutoRetry,
                McpServers = (McpServers ?? []).Select(s => s.Clone()).ToList(),
                AutoConnectMcp = AutoConnectMcp,
                ConfirmMcpTools = ConfirmMcpTools,
                LogMcpCalls = LogMcpCalls,
                Checkpoints = Checkpoints,
                BrowserTools = BrowserTools,
                AutoReadProblems = AutoReadProblems,
                FormatOnSave = FormatOnSave,
                PlanMode = PlanMode,
                CommandDenyList = CommandDenyList,
                CommandAllowPrefixes = CommandAllowPrefixes,
                NativeToolCalling = NativeToolCalling,
                Multimodal = Multimodal,
                MaxImagesPerMessage = MaxImagesPerMessage,
                ImageMaxBytes = ImageMaxBytes,
                BrowserAutomation = BrowserAutomation,
                BrowserExecutablePath = BrowserExecutablePath,
                BrowserRemoteDebuggingPort = BrowserRemoteDebuggingPort,
                BrowserHeadless = BrowserHeadless,
                BrowserTimeoutSeconds = BrowserTimeoutSeconds,
                SemanticIndex = SemanticIndex,
                SemanticSearchResults = SemanticSearchResults,
                SemanticIndexMaxFiles = SemanticIndexMaxFiles,
                SemanticChunkChars = SemanticChunkChars,
                StructuredEditing = StructuredEditing,
                UseProjectRules = UseProjectRules,
                AutoContext = AutoContext,
                AutoFixProblems = AutoFixProblems,
                AutoFixMaxRounds = AutoFixMaxRounds,
                AutoVerify = AutoVerify,
                VerifyCommand = VerifyCommand,
                BudgetUsd = BudgetUsd,
                AuditLogEnabled = AuditLogEnabled,
                PromptCaching = PromptCaching,
                CustomInstructions = CustomInstructions,
                AutoCompactThreshold = AutoCompactThreshold,
                MaxFilesPerRead = MaxFilesPerRead,
                AnonymousUsage = AnonymousUsage,
                LocalSessionHistory = LocalSessionHistory,
                AutoCheckUpdates = AutoCheckUpdates,
                Profiles = (Profiles ?? []).Select(p => p.Clone()).ToList(),
                NamedProfiles = (NamedProfiles ?? []).Select(p => p.Clone()).ToList(),
                ActiveProfileName = ActiveProfileName,
            };

            return clone;
        }
    }

    /// <summary>
    /// 一个 MCP 服务器条目（设置页 MCP 分页用）
    /// </summary>
    [DataContract(Name = "mcpServer")]
    public sealed class McpServerSettings
    {
        [DataMember(Name = "name", Order = 1)]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "command", Order = 2)]
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// 启动参数（如 <c>-y @modelcontextprotocol/server-filesystem /path</c>）
        /// </summary>
        [DataMember(Name = "arguments", Order = 3)]
        public string Arguments { get; set; } = string.Empty;

        [DataMember(Name = "enabled", Order = 4)]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 启动该服务器时注入的环境变量（API Key / Token 等）。
        /// 值为空串的项**不会**被注入（市场条目会先写入空占位，等用户填写）。
        /// </summary>
        [DataMember(Name = "environment", Order = 5)]
        public Dictionary<string, string> Environment { get; set; } = [];

        /// <summary>
        /// 连接后拿到的工具数量（运行时填充，不持久化）
        /// </summary>
        [IgnoreDataMember]
        public int ToolCount { get; set; }

        /// <summary>
        /// on / off / error / unknown —— 决定设置页状态点的颜色
        /// </summary>
        [IgnoreDataMember]
        public string Status { get; set; } = "unknown";

        /// <summary>
        /// 状态说明（连接失败原因等）
        /// </summary>
        [IgnoreDataMember]
        public string StatusText { get; set; } = string.Empty;

        /// <summary>
        /// 深拷贝（AppSettings 的 Clone / CopyFrom 用：避免两个实例共享同一个 Environment 字典）
        /// </summary>
        public McpServerSettings Clone()
        {
            return new McpServerSettings
            {
                Name = Name,
                Command = Command,
                Arguments = Arguments,
                Enabled = Enabled,
                Environment = new Dictionary<string, string>(Environment ?? []),
            };
        }
    }
}

