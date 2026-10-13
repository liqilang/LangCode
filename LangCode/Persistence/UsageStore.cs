using LangCode.Domain.Catalog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LangCode.Persistence
{
    /// <summary>
    /// 落盘的一条用量采样（一次「模型调用」的 token 与费用）
    /// </summary>
    [DataContract(Name = "usageSample")]
    public sealed class StoredUsage
    {
        [DataMember(Name = "atUtc", Order = 1)]
        public DateTime AtUtc { get; set; } = DateTime.UtcNow;

        [DataMember(Name = "model", Order = 2)]
        public string Model { get; set; } = string.Empty;

        [DataMember(Name = "input", Order = 3)]
        public double InputTokens { get; set; }

        [DataMember(Name = "output", Order = 4)]
        public double OutputTokens { get; set; }

        [DataMember(Name = "cost", Order = 5)]
        public double CostUsd { get; set; }

        [DataMember(Name = "conversation", Order = 6)]
        public string ConversationId { get; set; } = string.Empty;

        /// <summary>
        /// 命中提示缓存的输入 token（按折扣价计费）
        /// </summary>
        [DataMember(Name = "cached", Order = 7)]
        public double CachedTokens { get; set; }
    }

    [DataContract(Name = "usageFile")]
    internal sealed class UsageFile
    {
        [DataMember(Name = "version", Order = 1)]
        public int Version { get; set; } = 1;

        [DataMember(Name = "samples", Order = 2)]
        public List<StoredUsage> Samples { get; set; } = [];
    }

    /// <summary>
    /// 用量 / 费用统计的落盘与聚合
    /// </summary>
    public static class UsageStore
    {
        /// <summary>
        /// 内存与文件中保留的采样上限（超出后丢弃最旧的，防止无限增长）
        /// </summary>
        private const int MaxSamples = 20000;

        private static readonly object Gate = new object();
        private static List<StoredUsage> _cache;

        public static string FilePath => Path.Combine(SettingsStore.Directory, "usage.json");

        /// <summary>
        /// 读取全部采样（首次访问时从磁盘载入并缓存）
        /// </summary>
        public static IList<StoredUsage> LoadAll()
        {
            lock (Gate)
            {
                if (_cache != null)
                {
                    return _cache;
                }

                _cache = ReadFromDisk();
                return _cache;
            }
        }

        /// <summary>
        /// 记录一次调用的用量（token 由调用方估算传入；费用按模型单价折算）
        /// </summary>
        public static StoredUsage Record(string model, double inputTokens, double outputTokens, string conversationId)
        {
            return Record(model, inputTokens, outputTokens, conversationId, 0);
        }

        /// <summary>
        /// 记录一次调用的用量；<paramref name="cachedInputTokens"/> 为命中提示缓存的输入 token，
        /// 这部分按折扣价计入费用（这样「开了缓存」在统计上更便宜）
        /// </summary>
        public static StoredUsage Record(string model, double inputTokens, double outputTokens, string conversationId, double cachedInputTokens)
        {
            var sample = new StoredUsage
            {
                AtUtc = DateTime.UtcNow,
                Model = model ?? string.Empty,
                InputTokens = Math.Max(0, inputTokens),
                OutputTokens = Math.Max(0, outputTokens),
                ConversationId = conversationId ?? string.Empty,
                CachedTokens = Math.Max(0, cachedInputTokens),
            };

            sample.CostUsd = ModelPricing.EstimateCost(sample.Model, sample.InputTokens, sample.OutputTokens, sample.CachedTokens);

            lock (Gate)
            {
                var samples = LoadAll().ToList();
                samples.Add(sample);
                if (samples.Count > MaxSamples)
                {
                    samples = samples.OrderByDescending(s => s.AtUtc).Take(MaxSamples).OrderBy(s => s.AtUtc).ToList();
                }

                _cache = samples;
                WriteToDisk(_cache);
            }

            return sample;
        }

        public static void Clear()
        {
            lock (Gate)
            {
                _cache = [];
                WriteToDisk(_cache);
            }
        }

        // ==================== 聚合 ====================

        /// <summary>
        /// 累计（全部时间）
        /// </summary>
        public static UsageTotals Total()
        {
            return Summarize(LoadAll());
        }

        /// <summary>
        /// 今日（本地时区）
        /// </summary>
        public static UsageTotals Today()
        {
            var from = DateTime.Today;
            return Summarize(LoadAll().Where(s => s.AtUtc.ToLocalTime() >= from));
        }

        /// <summary>
        /// 某个会话的累计（<paramref name="conversationId"/> 为空时返回 <see cref="Today"/>）
        /// </summary>
        public static UsageTotals ForConversation(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return Today();
            }

            return Summarize(LoadAll().Where(s => string.Equals(s.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// 把一组采样聚合成总量
        /// </summary>
        public static UsageTotals Summarize(IEnumerable<StoredUsage> samples)
        {
            var totals = new UsageTotals();
            if (samples == null)
            {
                return totals;
            }

            foreach (var sample in samples)
            {
                totals.Add(sample.InputTokens, sample.OutputTokens, sample.CostUsd, sample.CachedTokens);
            }

            return totals;
        }

        /// <summary>
        /// 预算判定：<paramref name="budgetUsd"/> > 0 且累计费用已达到它
        /// </summary>
        public static bool IsOverBudget(double budgetUsd, out UsageTotals totals)
        {
            totals = Total();
            return budgetUsd > 0 && totals.CostUsd >= budgetUsd;
        }

        /// <summary>
        /// 一行摘要（用量面板 / 状态栏用）
        /// </summary>
        public static string Describe(double budgetUsd)
        {
            var today = Today();
            var total = Total();

            var builder = new StringBuilder();
            builder.Append("今日 ").Append(today.CostLabel()).Append(" · 累计 ").Append(total.CostLabel()).Append("（").Append(total.Calls).Append(" 次调用）");

            if (total.CachedTokens > 0)
            {
                builder.Append(" · 缓存命中 ").Append(Math.Round(total.CachedTokens)).Append(" token");
            }

            if (budgetUsd > 0)
            {
                builder.Append(total.CostUsd >= budgetUsd ? " · 已超预算" : " · 预算 $" + budgetUsd.ToString("0.##"));
            }

            return builder.ToString();
        }

        // ==================== 磁盘读写 ====================

        private static List<StoredUsage> ReadFromDisk()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return [];
                }

                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var serializer = new DataContractJsonSerializer(typeof(UsageFile));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var file = serializer.ReadObject(stream) as UsageFile;
                    return file?.Samples ?? [];
                }
            }
            catch (Exception)
            {
                return [];
            }
        }

        private static void WriteToDisk(List<StoredUsage> samples)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.Directory);

                var serializer = new DataContractJsonSerializer(typeof(UsageFile));
                var buffer = new MemoryStream();
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(
                    buffer, Encoding.UTF8, ownsStream: false, indent: true))
                {
                    serializer.WriteObject(writer, new UsageFile { Samples = samples });
                    writer.Flush();
                }

                var json = Encoding.UTF8.GetString(buffer.ToArray());
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }

                File.Move(temp, FilePath);
            }
            catch (Exception)
            {
                // 写失败不阻断对话
            }
        }
    }

    /// <summary>
    /// 一组累计用量（输入端在 <see cref="Domain.Catalog"/> 之外的调用方也能直接用）
    /// </summary>
    public sealed class UsageTotals
    {
        public double InputTokens { get; private set; }

        public double OutputTokens { get; private set; }

        public double CostUsd { get; private set; }

        public int Calls { get; private set; }

        /// <summary>
        /// 其中命中提示缓存的输入 token
        /// </summary>
        public double CachedTokens { get; private set; }

        public double UsedTokens => InputTokens + OutputTokens;

        internal void Add(double input, double output, double cost, double cached)
        {
            InputTokens += Math.Max(0, input);
            OutputTokens += Math.Max(0, output);
            CostUsd += Math.Max(0, cost);
            CachedTokens += Math.Max(0, cached);
            Calls++;
        }

        /// <summary>
        /// 费用短文本（本地模型显示「$0（本地）」）
        /// </summary>
        public string CostLabel()
        {
            if (CostUsd <= 0)
            {
                return "$0（本地）";
            }

            return CostUsd < 0.01 ? "$" + CostUsd.ToString("0.0000") : "$" + CostUsd.ToString("0.00");
        }
    }
}

