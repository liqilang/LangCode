using System;
using System.Collections.Generic;
using System.Linq;

namespace LangCode.Domain.Catalog
{
    /// <summary>
    /// 模型单价表（美元 / 百万 token）
    /// </summary>
    public static class ModelPricing
    {
        /// <summary>
        /// 未收录模型的兜底价（输入 / 输出）
        /// </summary>
        private const double DefaultInputPerMillion = 1.0;
        private const double DefaultOutputPerMillion = 3.0;

        /// <summary>
        /// 命中提示缓存的输入 token 相对普通输入的计费折扣（约 10%）
        /// </summary>
        public const double CachedInputDiscount = 0.1;

        private struct Rate
        {
            public Rate(string key, double input, double output)
            {
                Key = key;
                Input = input;
                Output = output;
            }

            /// <summary>
            /// 模型 id 里的特征片段（小写），或精确 id
            /// </summary>
            public string Key;

            public double Input;

            public double Output;
        }

        /// <summary>
        /// 按「特征片段」匹配；顺序有意义，更具体的放前面
        /// </summary>
        private static readonly List<Rate> Rates =
        [
            // ---- 本地推理不产生费用（必须排在最前，避免被 "qwen"/"llama" 等片段误计费）----
            new Rate("ollama", 0, 0),
            new Rate("lm studio", 0, 0),
            new Rate("lmstudio", 0, 0),
            new Rate("local-model", 0, 0),
            new Rate("vscode-lm", 0, 0),
            new Rate("mimo", 0, 0),

            // ---- Anthropic ----
            new Rate("claude-opus", 5, 25),
            new Rate("claude-sonnet", 3, 15),
            new Rate("claude-haiku", 1, 5),

            // ---- OpenAI ----
            new Rate("gpt-5.1-codex", 1.25, 10),
            new Rate("gpt-5-codex", 1.25, 10),
            new Rate("gpt-5-nano", 0.05, 0.4),
            new Rate("gpt-5-mini", 0.25, 2),
            new Rate("gpt-5", 1.25, 10),
            new Rate("o4-mini", 1.1, 4.4),
            new Rate("o3-mini", 1.1, 4.4),
            new Rate("o3", 2, 8),
            new Rate("gpt-4.1-nano", 0.1, 0.4),
            new Rate("gpt-4.1-mini", 0.4, 1.6),
            new Rate("gpt-4.1", 2, 8),
            new Rate("gpt-4o-mini", 0.15, 0.6),
            new Rate("gpt-4o", 2.5, 10),
            new Rate("gpt-oss", 0.15, 0.75),

            // ---- DeepSeek ----
            new Rate("deepseek-v4-pro", 0.55, 2.19),
            new Rate("deepseek-reasoner", 0.55, 2.19),
            new Rate("deepseek-r1", 0.55, 2.19),
            new Rate("deepseek", 0.28, 0.42),

            // ---- Google ----
            new Rate("gemini-2.5-pro", 1.25, 10),
            new Rate("gemini-2.5-flash-lite", 0.1, 0.4),
            new Rate("gemini-2.5-flash", 0.3, 2.5),
            new Rate("gemini-2.0-flash-lite", 0.075, 0.3),
            new Rate("gemini-2.0-flash", 0.1, 0.4),
            new Rate("gemini", 0.3, 2.5),

            // ---- Moonshot / Kimi ----
            new Rate("kimi-k2-turbo", 1.15, 8),
            new Rate("kimi-k2", 0.6, 2.5),
            new Rate("kimi", 0.6, 2.5),
            new Rate("moonshot", 0.5, 2),

            // ---- Z.ai / 智谱 GLM ----
            new Rate("glm-4.5-air", 0.2, 1.1),
            new Rate("glm-4", 0.6, 2.2),
            new Rate("glm", 0.6, 2.2),

            // ---- Qwen ----
            new Rate("qwen3-coder", 0.4, 1.6),
            new Rate("qwen", 0.4, 1.2),

            // ---- xAI ----
            new Rate("grok-4-fast", 0.2, 0.5),
            new Rate("grok-code-fast", 0.2, 1.5),
            new Rate("grok-3-mini", 0.3, 0.5),
            new Rate("grok", 3, 15),

            // ---- Mistral ----
            new Rate("mistral-large", 2, 6),
            new Rate("mistral-medium", 0.4, 2),
            new Rate("magistral", 2, 5),
            new Rate("codestral", 0.3, 0.9),
            new Rate("devstral", 0.4, 2),
            new Rate("ministral", 0.1, 0.1),

            // ---- MiniMax ----
            new Rate("minimax", 0.3, 1.2),
            new Rate("abab", 0.14, 0.14),

            // ---- 其它开源模型 ----
            new Rate("llama-4", 0.24, 0.97),
            new Rate("llama", 0.6, 0.6),
        ];

        /// <summary>
        /// 精确表（小写 id → 单价）；首次访问时由模型目录构建并缓存
        /// </summary>
        private static readonly object Gate = new object();
        private static Dictionary<string, Rate> _exact;

        private static Dictionary<string, Rate> Exact
        {
            get
            {
                lock (Gate)
                {
                    if (_exact != null)
                    {
                        return _exact;
                    }
                }

                // 关键：**在锁外**先把目录快照出来。
                // ModelCatalog.All 首次访问可能要读盘；若在锁内做，会把这把锁一直攥着，
                // 于是「用量记账」（后台线程）会被堵住、界面也可能连带卡住。
                List<ModelInfo> snapshot;
                try
                {
                    snapshot = ModelCatalog.All.ToList();
                }
                catch (Exception)
                {
                    snapshot = new List<ModelInfo>();
                }

                var table = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var model in snapshot)
                    {
                        if (string.IsNullOrWhiteSpace(model.Id))
                        {
                            continue;
                        }

                        var local = ProviderCatalog.IsLocal(model.Provider);
                        if (!model.HasPricing && !local)
                        {
                            // 没有可信单价：交给特征片段表兜底
                            continue;
                        }

                        var key = model.Id.Trim();
                        if (table.ContainsKey(key))
                        {
                            continue;
                        }

                        table[key] = new Rate(
                            key.ToLowerInvariant(),
                            local ? 0 : model.InputPricePerMillion,
                            local ? 0 : model.OutputPricePerMillion);
                    }
                }
                catch (Exception)
                {
                    // 目录不可用（极端情况）时退回特征片段表，不抛异常
                }

                lock (Gate)
                {
                    // 只有拿到过目录才缓存：避免「目录尚未就绪」时把一个空表永久缓存下来
                    if (snapshot.Count > 0)
                    {
                        _exact ??= table;
                    }

                    return _exact ?? table;
                }
            }
        }

        /// <summary>
        /// 丢弃缓存（模型目录热更新后调用）
        /// </summary>
        public static void Reload()
        {
            lock (Gate)
            {
                _exact = null;
            }
        }

        /// <summary>
        /// 该模型是否命中过单价表（未命中时用兜底价，界面可据此提示「估算」）
        /// </summary>
        public static bool IsKnown(string modelId)
        {
            return Find(modelId) != null;
        }

        /// <summary>
        /// 取模型的输入 / 输出单价（未收录返回 false，并用兜底价输出）
        /// </summary>
        public static bool TryGetPrice(string modelId, out double input, out double output)
        {
            var rate = Find(modelId);
            input = rate?.Input ?? DefaultInputPerMillion;
            output = rate?.Output ?? DefaultOutputPerMillion;
            return rate != null;
        }

        /// <summary>
        /// 估算一次用量（token 数）折算成美元
        /// </summary>
        public static double EstimateCost(string modelId, double inputTokens, double outputTokens)
        {
            return EstimateCost(modelId, inputTokens, outputTokens, 0);
        }

        /// <summary>
        /// 估算一次用量，并把 <paramref name="cachedInputTokens"/>（命中提示缓存的输入）按折扣价计费 ——
        /// 这样「开了缓存」在费用统计上才是真的更便宜
        /// </summary>
        public static double EstimateCost(string modelId, double inputTokens, double outputTokens, double cachedInputTokens)
        {
            var rate = Find(modelId);
            var inputPrice = rate?.Input ?? DefaultInputPerMillion;
            var outputPrice = rate?.Output ?? DefaultOutputPerMillion;

            var cached = Math.Max(0, Math.Min(cachedInputTokens, inputTokens));
            var fresh = Math.Max(0, inputTokens - cached);

            return ((fresh / 1_000_000.0) * inputPrice)
                 + ((cached / 1_000_000.0) * inputPrice * CachedInputDiscount)
                 + ((outputTokens / 1_000_000.0) * outputPrice);
        }

        /// <summary>
        /// 格式化成界面上的短文本（例如 <c>$0.0123</c>）
        /// </summary>
        public static string FormatCost(string modelId, double inputTokens, double outputTokens)
        {
            var cost = EstimateCost(modelId, inputTokens, outputTokens);

            if (cost <= 0)
            {
                return "$0（本地）";
            }

            return cost < 0.01
                ? "$" + cost.ToString("0.0000")
                : "$" + cost.ToString("0.00");
        }

        private static Rate? Find(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return null;
            }

            var id = modelId.Trim();

            // 1) 精确表（模型目录里的官方 id）
            if (Exact.TryGetValue(id, out var exact))
            {
                return exact;
            }

            // 2) 特征片段表（用户手输 / 自定义端点上的 id）
            var lower = id.ToLowerInvariant();
            foreach (var rate in Rates)
            {
                if (lower.IndexOf(rate.Key, StringComparison.Ordinal) >= 0)
                {
                    return rate;
                }
            }

            return null;
        }
    }
}

