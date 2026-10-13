using System;
using System.Collections.Generic;
using System.Linq;

namespace LangCode.Domain.Catalog
{
    /// <summary>
    /// 单个「模型提供商」的连接元数据
    /// </summary>
    public sealed class ProviderDefinition
    {
        public ProviderDefinition(
            string key,
            string groupLabel,
            string defaultBaseUrl,
            bool needsApiKey = true,
            bool needsManualBaseUrl = false,
            bool isLocal = false,
            string note = null,
            params string[] aliases)
        {
            Key = key;
            GroupLabel = string.IsNullOrWhiteSpace(groupLabel) ? (key ?? string.Empty).ToUpperInvariant() : groupLabel;
            DefaultBaseUrl = defaultBaseUrl ?? string.Empty;
            NeedsApiKey = needsApiKey;
            NeedsManualBaseUrl = needsManualBaseUrl;
            IsLocal = isLocal;
            Note = note ?? string.Empty;
            Aliases = aliases ?? new string[0];
        }

        /// <summary>
        /// 厂商标识（也是设置里持久化的 provider 值、档案主键）
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// 模型列表分组标题（界面里那一行大写小标题）
        /// </summary>
        public string GroupLabel { get; }

        /// <summary>
        /// 内置默认 Base URL；为空表示必须由用户填写
        /// </summary>
        public string DefaultBaseUrl { get; }

        /// <summary>
        /// 是否必须提供 API Key（本地推理 / 自建网关可为 false）
        /// </summary>
        public bool NeedsApiKey { get; }

        /// <summary>
        /// 是否必须由用户手动填 Base URL（没有可靠的内置地址）
        /// </summary>
        public bool NeedsManualBaseUrl { get; }

        /// <summary>
        /// 本地推理（不产生费用）
        /// </summary>
        public bool IsLocal { get; }

        /// <summary>
        /// 给用户的说明（OAuth、区域化端点、需要额外参数等）
        /// </summary>
        public string Note { get; }

        /// <summary>
        /// 旧版本使用过的别名（设置迁移 / 地址解析时归一化用）
        /// </summary>
        public IList<string> Aliases { get; }
    }

    /// <summary>
    /// 模型提供商目录 
    /// </summary>
    public static class ProviderCatalog
    {
        /// <summary>
        /// 通用「OpenAI Compatible」提供商名（端点与模型都由用户自填）
        /// </summary>
        public const string OpenAiCompatible = "OpenAI Compatible";

        /// <summary>
        /// 本地 Ollama
        /// </summary>
        public const string Ollama = "Ollama";

        /// <summary>
        /// 本地 LM Studio
        /// </summary>
        public const string LmStudio = "LM Studio";

        /// <summary>
        /// OpenRouter（有私有 reasoning 参数）
        /// </summary>
        public const string OpenRouter = "OpenRouter";

        /// <summary>
        /// Anthropic（原生协议未接入，需指向兼容网关）
        /// </summary>
        public const string Anthropic = "Anthropic";

        private static readonly List<ProviderDefinition> Defs =
        [
            new ProviderDefinition("Baseten", "BASETEN", "https://inference.baseten.co/v1"),

            new ProviderDefinition("DeepSeek", "DEEPSEEK", "https://api.deepseek.com/v1"),

            new ProviderDefinition("Fireworks AI", "FIREWORKS", "https://api.fireworks.ai/inference/v1",
                aliases: new[] { "Fireworks" }),

            new ProviderDefinition("Friendli", "FRIENDLI", "https://api.friendli.ai/serverless/v1"),

            new ProviderDefinition(
                "GCP Vertex AI", "VERTEX", string.Empty,
                needsManualBaseUrl: true,
                note: "Vertex 使用 Google 区域化端点并需服务账号鉴权，请填写一个 OpenAI 兼容网关地址后使用",
                aliases: new[] { "Vertex", "Google Vertex", "GCP Vertex AI (Vertex)" }),

            new ProviderDefinition("Google Gemini", "GOOGLE", "https://generativelanguage.googleapis.com/v1beta/openai",
                aliases: new[] { "Gemini", "Google" }),

            new ProviderDefinition(
                "Kenari", "KENARI", string.Empty,
                needsManualBaseUrl: true,
                note: "请填写 Kenari 的 OpenAI 兼容端点地址"),

            new ProviderDefinition("Kimi Code", "KIMI CODE", "https://api.moonshot.cn/v1",
                aliases: new[] { "Kimi" }),

            new ProviderDefinition(
                "LiteLLM", "LITELLM", "http://localhost:4000/v1",
                needsApiKey: false,
                note: "LiteLLM 代理默认监听本机 4000 端口；如部署在别处请在 Base URL 里改"),

            new ProviderDefinition(LmStudio, "本地", "http://localhost:1234/v1",
                needsApiKey: false, isLocal: true,
                aliases: new[] { "LM Studio (本地)", "lmstudio" }),

            new ProviderDefinition("MiniMax", "MINIMAX", "https://api.minimax.io/v1",
                aliases: new[] { "MiniMax (海螺)", "minimax" }),

            new ProviderDefinition("Mistral", "MISTRAL", "https://api.mistral.ai/v1"),

            new ProviderDefinition("Moonshot", "MOONSHOT", "https://api.moonshot.cn/v1",
                aliases: new[] { "Moonshot (Kimi)", "Moonshot AI" }),

            new ProviderDefinition("NanoGPT", "NANOGPT", "https://nano-gpt.com/api/v1"),

            new ProviderDefinition(Ollama, "本地", "http://localhost:11434/v1",
                needsApiKey: false, isLocal: true,
                aliases: new[] { "Ollama (本地)", "Ollama (local)" }),

            new ProviderDefinition("OpenAI", "OPENAI", "https://api.openai.com/v1"),

            new ProviderDefinition(
                "OpenAI - ChatGPT Plus/Pro", "OPENAI (CODEX)", "https://chatgpt.com/backend-api/codex",
                needsApiKey: false,
                needsManualBaseUrl: true,
                note: "ChatGPT Plus/Pro 走 OAuth 订阅通道（非 API Key）请填写一个可用的兼容网关地址后使用",
                aliases: new[] { "OpenAI Codex", "openai-native", "ChatGPT Plus/Pro" }),

            new ProviderDefinition(OpenAiCompatible, "CUSTOM", string.Empty,
                needsApiKey: false, needsManualBaseUrl: true,
                note: "填任意 OpenAI 兼容网关，通常以 /v1 结尾"),

            new ProviderDefinition(
                "Opencode Go", "OPENCODE", "https://opencode.ai/zen/v1",
                aliases: new[] { "OpenCode Go", "opencode-go" }),

            new ProviderDefinition(OpenRouter, "OPENROUTER", "https://openrouter.ai/api/v1"),

            new ProviderDefinition("Poe", "POE", "https://api.poe.com/v1"),

            new ProviderDefinition(
                "Qwen Code", "QWEN CODE", string.Empty,
                needsManualBaseUrl: true,
                note: "Qwen Code 走 OAuth 订阅通道，请填写一个兼容网关地址后使用"),

            new ProviderDefinition("Requesty", "REQUESTY", "https://router.requesty.ai/v1"),

            new ProviderDefinition("SambaNova", "SAMBANOVA", "https://api.sambanova.ai/v1",
                aliases: new[] { "SambaNova Cloud" }),

            new ProviderDefinition("Unbound", "UNBOUND", "https://api.getunbound.ai/v1"),

            new ProviderDefinition("Vercel AI Gateway", "VERCEL", "https://ai-gateway.vercel.sh/v1",
                aliases: new[] { "Vercel", "Vercel AI Gateway (v0)" }),

            new ProviderDefinition(
                "VS Code LM API", "VSCODE", string.Empty,
                needsApiKey: false, needsManualBaseUrl: true,
                note: "VS Code 语言模型 API 仅在 VS Code 中可用；本扩展请改用其它提供商"),

            new ProviderDefinition("xAI (Grok)", "XAI", "https://api.x.ai/v1",
                aliases: new[] { "xAI", "Grok" }),

            new ProviderDefinition(
                "Xiaomi MiMo", "MIMO", string.Empty,
                needsManualBaseUrl: true,
                note: "请填写小米 MiMo 开放平台的 OpenAI 兼容端点地址",
                aliases: new[] { "MiMo", "小米 MiMo" }),

            new ProviderDefinition("Z.ai", "ZAI", "https://api.z.ai/api/paas/v4",
                aliases: new[] { "Z.ai (GLM)", "ZAI", "z-ai" }),

            // ---- 保留的历史提供商（不属于本次新增清单，但不删除，避免既有配置失效）----
            // Anthropic 已接入原生 Messages API：默认走官方端点；若用户改填第三方 OpenAI 兼容网关，
            // ChatBackendFactory 会自动改走兼容协议（既有配置不失效）
            new ProviderDefinition(Anthropic, "ANTHROPIC", "https://api.anthropic.com",
                note: "已接入 Anthropic 原生 Messages API；如需走第三方兼容网关，填写网关地址即可自动切换协议"),

            new ProviderDefinition("Groq", "GROQ", "https://api.groq.com/openai/v1"),

            new ProviderDefinition("SiliconFlow", "SILICONFLOW", "https://api.siliconflow.cn/v1",
                aliases: new[] { "SiliconFlow (硅基流动)" }),

            new ProviderDefinition("智谱 GLM", "ZHIPU", "https://open.bigmodel.cn/api/paas/v4",
                aliases: new[] { "Zhipu", "GLM" }),

            new ProviderDefinition("通义千问 (DashScope)", "QWEN", "https://dashscope.aliyuncs.com/compatible-mode/v1",
                aliases: new[] { "DashScope", "Qwen" }),
        ];

        /// <summary>
        /// 全部提供商（按设置页下拉顺序）
        /// </summary>
        public static IList<ProviderDefinition> All => Defs;

        /// <summary>
        /// 全部提供商标识（设置页「API 提供商」下拉的数据源）
        /// </summary>
        public static IList<string> Keys => Defs.Select(d => d.Key).ToList();

        /// <summary>
        /// 按 key / 别名查找提供商（大小写不敏感；找不到返回 null）
        /// </summary>
        public static ProviderDefinition Find(string provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                return null;
            }

            var key = provider.Trim();

            var exact = Defs.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            return Defs.FirstOrDefault(d => d.Aliases.Any(a =>
                string.Equals(a, key, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// 把（可能来自旧设置的）别名归一化成当前规范 key
        /// </summary>
        public static string Canonicalize(string provider)
        {
            var def = Find(provider);
            return def != null ? def.Key : (provider ?? string.Empty).Trim();
        }

        /// <summary>
        /// 该提供商是否必须提供 API Key（未知提供商按「需要」处理）
        /// </summary>
        public static bool NeedsApiKey(string provider)
        {
            var def = Find(provider);
            return def == null || def.NeedsApiKey;
        }

        /// <summary>
        /// 该提供商是否必须由用户手填 Base URL
        /// </summary>
        public static bool NeedsManualBaseUrl(string provider)
        {
            var def = Find(provider);
            return def != null && def.NeedsManualBaseUrl;
        }

        /// <summary>
        /// 是否本地推理（不产生费用）
        /// </summary>
        public static bool IsLocal(string provider)
        {
            var def = Find(provider);
            return def != null && def.IsLocal;
        }

        /// <summary>
        /// 是否为 OpenRouter（决定是否附带其私有 reasoning 参数）
        /// </summary>
        public static bool IsOpenRouter(string provider)
        {
            return string.Equals(Canonicalize(provider), OpenRouter, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 该提供商的说明文案（没有则为空串）
        /// </summary>
        public static string NoteOf(string provider)
        {
            var def = Find(provider);
            return def?.Note ?? string.Empty;
        }

        /// <summary>
        /// 把「提供商」映射到默认 Base URL（显式配置优先）
        /// </summary>
        public static string ResolveBaseUrl(string provider, string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            var def = Find(provider);
            return def != null ? def.DefaultBaseUrl : string.Empty;
        }
    }
}

