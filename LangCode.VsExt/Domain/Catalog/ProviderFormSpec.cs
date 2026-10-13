using System;
using System.Collections.Generic;

namespace LangCode.VsExt.Domain.Catalog
{
    /// <summary>
    /// 提供商专属字段的控件类型
    /// </summary>
    public enum ProviderFieldKind
    {
        /// <summary>
        /// 单行文本框
        /// </summary>
        Text,

        /// <summary>
        /// 密码框（API Key）
        /// </summary>
        Password,

        /// <summary>
        /// 下拉选择
        /// </summary>
        Select,
    }

    /// <summary>
    /// Base URL 的呈现方式 
    /// </summary>
    public enum ProviderBaseUrlMode
    {
        /// <summary>
        /// 没有可填地址（固定官方端点）
        /// </summary>
        None,

        /// <summary>
        /// 直接显示一个可编辑地址框（如 Ollama / LM Studio / LiteLLM / OpenAI Compatible）
        /// </summary>
        Plain,

        /// <summary>
        /// 先勾选「使用自定义 Base URL」，再显示地址框（如 OpenAI / OpenRouter / Requesty）
        /// </summary>
        Toggle,
    }

    /// <summary>
    /// 提供商的一个额外字段（地址选择、区域、Azure 版本等）
    /// </summary>
    public sealed class ProviderFieldSpec
    {
        public ProviderFieldKind Kind { get; set; } = ProviderFieldKind.Text;

        /// <summary>
        /// 持久化键（存在 <c>ProviderOptions</c> 里）
        /// </summary>
        public string Key { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public string Placeholder { get; set; } = string.Empty;

        public string Hint { get; set; } = string.Empty;

        /// <summary>
        /// 下拉选项（值, 显示名）
        /// </summary>
        public IList<KeyValuePair<string, string>> Options { get; set; }

        /// <summary>
        /// 仅当当前模型是 Codestral 时才显示（Mistral 专用）
        /// </summary>
        public bool OnlyForCodestral { get; set; }
    }

    /// <summary>
    /// 一家提供商在「设置 → API」里的表单描述 
    /// </summary>
    public sealed class ProviderFormSpec
    {
        public string Provider { get; set; } = string.Empty;

        /// <summary>
        /// API Key 的字段标签（如「OpenAI API Key」）；为空表示该提供商不需要 Key
        /// </summary>
        public string ApiKeyLabel { get; set; } = string.Empty;

        public string ApiKeyPlaceholder { get; set; } = "输入 API Key…";

        /// <summary>
        /// 「获取 API Key」链接地址
        /// </summary>
        public string ApiKeyUrl { get; set; } = string.Empty;

        /// <summary>
        /// 「获取 API Key」按钮文案
        /// </summary>
        public string ApiKeyUrlText { get; set; } = "获取 API Key";

        /// <summary>
        /// Key 是否可选（本地推理 / 自建网关 / OAuth 厂家）
        /// </summary>
        public bool ApiKeyOptional { get; set; }

        public ProviderBaseUrlMode BaseUrlMode { get; set; } = ProviderBaseUrlMode.None;

        public string BaseUrlLabel { get; set; } = "Base URL";

        public string BaseUrlPlaceholder { get; set; } = string.Empty;

        /// <summary>
        /// OAuth / 订阅类厂商的登录说明（显示在字段区顶部）
        /// </summary>
        public string SignInNote { get; set; } = string.Empty;

        /// <summary>
        /// 额外字段
        /// </summary>
        public IList<ProviderFieldSpec> Extras { get; set; } = [];
    }

    /// <summary>
    /// 提供商表单描述表
    /// </summary>
    public static class ProviderForms
    {
        private static readonly Dictionary<string, ProviderFormSpec> Specs = Build();

        /// <summary>
        /// 取某提供商的表单描述（未特别定义的厂商给一份「通用 API Key」表单）
        /// </summary>
        public static ProviderFormSpec Of(string provider)
        {
            var key = ProviderCatalog.Canonicalize(provider);
            return Specs.TryGetValue(key, out var spec) ? spec : Generic(key);
        }

        private static ProviderFormSpec Generic(string provider)
        {
            var def = ProviderCatalog.Find(provider);
            var spec = new ProviderFormSpec
            {
                Provider = provider,
                ApiKeyLabel = provider + " API Key",
            };

            if (def != null && !def.NeedsApiKey)
            {
                spec.ApiKeyLabel = string.Empty;
            }

            if (def != null && def.NeedsManualBaseUrl)
            {
                spec.BaseUrlMode = ProviderBaseUrlMode.Plain;
                spec.BaseUrlPlaceholder = "https://your-gateway.example.com/v1";
            }

            return spec;
        }

        /// <summary>
        /// 构造一个下拉字段选项以「值, 显示名」成对传入（用字符串对而非值元组：
        /// 值元组数组在部分旧版 Roslyn 上会触发编译器崩溃）
        /// </summary>
        private static ProviderFieldSpec Select(string key, string label, params string[] valueLabelPairs)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (var i = 0; i + 1 < valueLabelPairs.Length; i += 2)
            {
                list.Add(new KeyValuePair<string, string>(valueLabelPairs[i], valueLabelPairs[i + 1]));
            }

            return new ProviderFieldSpec { Kind = ProviderFieldKind.Select, Key = key, Label = label, Options = list };
        }

        private static Dictionary<string, ProviderFormSpec> Build()
        {
            var map = new Dictionary<string, ProviderFormSpec>(StringComparer.OrdinalIgnoreCase);

            void Add(ProviderFormSpec spec)
            {
                map[spec.Provider] = spec;
            }

            Add(new ProviderFormSpec
            {
                Provider = "Baseten",
                ApiKeyLabel = "Baseten API Key",
                ApiKeyUrl = "https://app.baseten.co/settings/api_keys",
                ApiKeyUrlText = "获取 Baseten API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "DeepSeek",
                ApiKeyLabel = "DeepSeek API Key",
                ApiKeyUrl = "https://platform.deepseek.com/",
                ApiKeyUrlText = "获取 DeepSeek API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Fireworks AI",
                ApiKeyLabel = "Fireworks API Key",
                ApiKeyUrl = "https://fireworks.ai/",
                ApiKeyUrlText = "获取 Fireworks API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Friendli",
                ApiKeyLabel = "Friendli API Key",
                ApiKeyUrl = "https://friendli.ai/",
                ApiKeyUrlText = "获取 Friendli API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "GCP Vertex AI",
                ApiKeyLabel = string.Empty,
                SignInNote = "Google Cloud Vertex：需先在 GCP 启用 Vertex AI，并提供服务账号凭据（下方字段）",
                Extras =
                [
                    new ProviderFieldSpec { Key = "vertexProjectId", Label = "Google Cloud 项目 ID", Placeholder = "my-project-id" },
                    new ProviderFieldSpec { Key = "vertexRegion", Label = "区域", Placeholder = "us-central1", Hint = "如 us-central1 / europe-west1 / asia-east1" },
                    new ProviderFieldSpec { Kind = ProviderFieldKind.Password, Key = "vertexJsonCredentials", Label = "服务账号 JSON 凭据", Placeholder = "粘贴 JSON 内容" },
                    new ProviderFieldSpec { Key = "vertexKeyFile", Label = "服务账号密钥文件路径（可选）", Placeholder = "C:\\path\\to\\key.json" },
                ],
            });

            Add(new ProviderFormSpec
            {
                Provider = "Google Gemini",
                ApiKeyLabel = "Gemini API Key",
                ApiKeyUrl = "https://ai.google.dev/",
                ApiKeyUrlText = "获取 Gemini API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Kenari",
                ApiKeyLabel = "Kenari API Key",
                ApiKeyUrl = "https://kenari.id/login?next=/keys",
                ApiKeyUrlText = "获取 Kenari API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Kimi Code",
                ApiKeyLabel = "Kimi Code API Key",
                ApiKeyOptional = true,
                ApiKeyUrl = "https://www.kimi.com/code",
                ApiKeyUrlText = "Kimi Code 文档",
                SignInNote = "Kimi Code 支持使用 Kimi 账号登录（OAuth）；也可直接填入 API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "LiteLLM",
                ApiKeyLabel = "LiteLLM API Key",
                ApiKeyOptional = true,
                BaseUrlMode = ProviderBaseUrlMode.Plain,
                BaseUrlLabel = "LiteLLM Base URL",
                BaseUrlPlaceholder = "http://localhost:4000",
            });

            Add(new ProviderFormSpec
            {
                Provider = "LM Studio",
                ApiKeyLabel = string.Empty,
                BaseUrlMode = ProviderBaseUrlMode.Plain,
                BaseUrlLabel = "LM Studio Base URL",
                BaseUrlPlaceholder = "http://localhost:1234",
            });

            Add(new ProviderFormSpec
            {
                Provider = "MiniMax",
                ApiKeyLabel = "MiniMax API Key",
                ApiKeyUrl = "https://www.minimax.io/platform/user-center/basic-information/interface-key",
                ApiKeyUrlText = "获取 MiniMax API Key",
                Extras =
                [
                    Select("endpoint", "MiniMax Entrypoint",
                        "https://api.minimax.io/v1", "api.minimax.io",
                        "https://api.minimaxi.com/v1", "api.minimaxi.com"),
                ],
            });

            Add(new ProviderFormSpec
            {
                Provider = "Mistral",
                ApiKeyLabel = "Mistral API Key",
                ApiKeyUrl = "https://console.mistral.ai/",
                ApiKeyUrlText = "获取 Mistral / Codestral API Key",
                Extras =
                [
                    new ProviderFieldSpec
                    {
                        Kind = ProviderFieldKind.Text,
                        Key = "mistralCodestralUrl",
                        Label = "Codestral Base URL（可选）",
                        Placeholder = "https://codestral.mistral.ai",
                        Hint = "为 Codestral 模型设置一个替代地址",
                        OnlyForCodestral = true,
                    },
                ],
            });

            Add(new ProviderFormSpec
            {
                Provider = "Moonshot",
                ApiKeyLabel = "Moonshot API Key",
                ApiKeyUrl = "https://platform.moonshot.cn/",
                ApiKeyUrlText = "获取 Moonshot API Key",
                BaseUrlMode = ProviderBaseUrlMode.Plain,
                BaseUrlLabel = "Moonshot Entrypoint",
                BaseUrlPlaceholder = "https://api.moonshot.cn/v1",
            });

            Add(new ProviderFormSpec
            {
                Provider = "NanoGPT",
                ApiKeyLabel = "NanoGPT API Key",
                ApiKeyUrl = "https://nano-gpt.com/api",
                ApiKeyUrlText = "获取 NanoGPT API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Ollama",
                ApiKeyLabel = "Ollama API Key",
                ApiKeyOptional = true,
                BaseUrlMode = ProviderBaseUrlMode.Plain,
                BaseUrlLabel = "Ollama Base URL",
                BaseUrlPlaceholder = "http://localhost:11434",
            });

            Add(new ProviderFormSpec
            {
                Provider = "OpenAI",
                ApiKeyLabel = "OpenAI API Key",
                ApiKeyUrl = "https://platform.openai.com/api-keys",
                ApiKeyUrlText = "获取 OpenAI API Key",
                BaseUrlMode = ProviderBaseUrlMode.Toggle,
                BaseUrlLabel = "Base URL",
                BaseUrlPlaceholder = "https://api.openai.com/v1",
                Extras =
                [
                    Select("openAiServiceTier", "Service tier",
                        "default", "Standard",
                        "flex", "Flex",
                        "priority", "Priority"),
                ],
            });

            Add(new ProviderFormSpec
            {
                Provider = "OpenAI - ChatGPT Plus/Pro",
                ApiKeyLabel = string.Empty,
                SignInNote = "ChatGPT Plus/Pro 走 OAuth 订阅通道（非 API Key）",
            });

            Add(new ProviderFormSpec
            {
                Provider = "OpenAI Compatible",
                ApiKeyLabel = "API Key",
                ApiKeyOptional = true,
                BaseUrlMode = ProviderBaseUrlMode.Plain,
                BaseUrlLabel = "Base URL",
                BaseUrlPlaceholder = "https://api.example.com",
                Extras =
                [
                    new ProviderFieldSpec { Key = "azureApiVersion", Label = "Azure API 版本（可选）", Placeholder = "2024-10-01-preview" },
                ],
            });

            Add(new ProviderFormSpec
            {
                Provider = "Opencode Go",
                ApiKeyLabel = "Opencode Go API Key",
                ApiKeyUrl = "https://opencode.ai/docs/go/",
                ApiKeyUrlText = "获取 Opencode Go API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "OpenRouter",
                ApiKeyLabel = "OpenRouter API Key",
                ApiKeyUrl = "https://openrouter.ai/settings/keys",
                ApiKeyUrlText = "获取 OpenRouter API Key",
                BaseUrlMode = ProviderBaseUrlMode.Toggle,
                BaseUrlLabel = "Base URL",
                BaseUrlPlaceholder = "默认：https://openrouter.ai/api/v1",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Poe",
                ApiKeyLabel = "Poe API Key",
                ApiKeyUrl = "https://poe.com/api_key",
                ApiKeyUrlText = "获取 Poe API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Qwen Code",
                ApiKeyLabel = "Qwen Code API Key",
                ApiKeyOptional = true,
                ApiKeyUrl = "https://github.com/QwenLM/qwen-code/blob/main/README.md",
                ApiKeyUrlText = "Qwen Code 文档",
                SignInNote = "Qwen Code 支持 OAuth 登录；也可直接填入 API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Requesty",
                ApiKeyLabel = "Requesty API Key",
                ApiKeyUrl = "https://app.requesty.ai/api-keys",
                ApiKeyUrlText = "创建新的 Requesty API Key",
                BaseUrlMode = ProviderBaseUrlMode.Toggle,
                BaseUrlLabel = "Base URL",
                BaseUrlPlaceholder = "https://router.requesty.ai/v1",
            });

            Add(new ProviderFormSpec
            {
                Provider = "SambaNova",
                ApiKeyLabel = "SambaNova API Key",
                ApiKeyUrl = "https://cloud.sambanova.ai/",
                ApiKeyUrlText = "获取 SambaNova API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Unbound",
                ApiKeyLabel = "Unbound API Key",
                ApiKeyUrl = "https://gateway.getunbound.ai",
                ApiKeyUrlText = "获取 Unbound API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Vercel AI Gateway",
                ApiKeyLabel = "Vercel AI Gateway API Key",
                ApiKeyUrl = "https://vercel.com/d?to=%2F%5Bteam%5D%2F%7E%2Fai%2Fapi-keys",
                ApiKeyUrlText = "获取 Vercel AI Gateway API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "VS Code LM API",
                ApiKeyLabel = string.Empty,
                SignInNote = "VS Code 语言模型 API 仅在 VS Code 中可用；本扩展请改用其它提供商",
            });

            Add(new ProviderFormSpec
            {
                Provider = "xAI (Grok)",
                ApiKeyLabel = "xAI API Key",
                ApiKeyUrl = "https://api.x.ai/docs",
                ApiKeyUrlText = "获取 xAI API Key",
            });

            Add(new ProviderFormSpec
            {
                Provider = "Xiaomi MiMo",
                ApiKeyLabel = "MiMo API Key",
                ApiKeyUrl = "https://platform.xiaomimimo.com",
                ApiKeyUrlText = "获取 MiMo API Key",
                Extras =
                [
                    Select("endpoint", "MiMo Entrypoint",
                        "https://api.mimo.ai/v1", "Token Plan - Singapore（默认）",
                        "https://api.mimo.cn/v1", "Token Plan - China",
                        "https://api.mimo-eu.ai/v1", "Token Plan - Europe (AMS)",
                        "https://api.mimo.ai/payg/v1", "按量付费"),
                ],
            });

            Add(new ProviderFormSpec
            {
                Provider = "Z.ai",
                ApiKeyLabel = "Z AI API Key",
                ApiKeyUrl = "https://z.ai/manage-apikey/apikey-list",
                ApiKeyUrlText = "获取 Z AI API Key",
            });

            // ---- 保留的历史提供商 ----
            Add(new ProviderFormSpec
            {
                Provider = "Anthropic",
                ApiKeyLabel = "Anthropic API Key",
                ApiKeyUrl = "https://console.anthropic.com/settings/keys",
                ApiKeyUrlText = "获取 Anthropic API Key",
                BaseUrlMode = ProviderBaseUrlMode.Plain,
                BaseUrlLabel = "Base URL",
                BaseUrlPlaceholder = "https://your-gateway.example.com/v1",
            });

            return map;
        }
    }
}

