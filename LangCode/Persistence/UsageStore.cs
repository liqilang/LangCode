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

    /// <summary>
    /// 旧版单文件格式（<c>usage.json</c>）：仅用于一次性迁移
    /// </summary>
    [DataContract(Name = "usageFile")]
    internal sealed class LegacyUsageFile
    {
        [DataMember(Name = "version", Order = 1)]
        public int Version { get; set; } = 1;

        [DataMember(Name = "samples", Order = 2)]
        public List<StoredUsage> Samples { get; set; } = [];
    }

    /// <summary>
    /// 用量 / 费用统计的落盘与聚合 
    /// </para>
    /// </summary>
    public static class UsageStore
    {
        /// <summary>
        /// 内存与文件中保留的采样上限（超出后丢弃最旧的，防止无限增长）
        /// </summary>
        private const int MaxSamples = 20000;

        /// <summary>
        /// 达到上限后再多留一批才裁剪：避免刚好卡在 20000 时每次记账都整体重写文件。
        /// </summary>
        private const int TrimBatch = 1000;

        private static readonly object Gate = new object();
        private static List<StoredUsage> _cache;
        private static UsageTotals _totalCache;
        private static bool _migrated;

        /// <summary>NDJSON 存储路径</summary>
        public static string FilePath => Path.Combine(SettingsStore.Directory, "usage.jsonl");

        /// <summary>旧版单文件路径（迁移来源）</summary>
        private static string LegacyFilePath => Path.Combine(SettingsStore.Directory, "usage.json");

        /// <summary>
        /// 读取全部采样（首次访问时从磁盘载入并缓存）
        /// </summary>
        public static IList<StoredUsage> LoadAll()
        {
            lock (Gate)
            {
                EnsureMigratedLocked();

                if (_cache != null)
                {
                    return _cache;
                }

                _cache = ReadFromDiskLocked();
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
                EnsureMigratedLocked();

                var samples = _cache ?? ReadFromDiskLocked();
                samples = new List<StoredUsage>(samples);
                samples.Add(sample);

                if (samples.Count > MaxSamples + TrimBatch)
                {
                    // 超出一批才整体重写一次（裁掉最旧的）
                    samples = samples.OrderByDescending(s => s.AtUtc).Take(MaxSamples).OrderBy(s => s.AtUtc).ToList();
                    _cache = samples;
                    WriteAllLocked(_cache);
                }
                else
                {
                    // 常规路径：内存追加 + 磁盘追加一行（不全量重写）
                    _cache = samples;
                    AppendLocked(sample);
                    _totalCache?.Add(sample.InputTokens, sample.OutputTokens, sample.CostUsd, sample.CachedTokens);
                }
            }

            return sample;
        }

        public static void Clear()
        {
            lock (Gate)
            {
                EnsureMigratedLocked();

                _cache = [];
                _totalCache = null;

                try
                {
                    if (File.Exists(FilePath))
                    {
                        File.Delete(FilePath);
                    }
                }
                catch (Exception)
                {
                    // 删不掉（被占用等）：内存已清空，下次写入会覆盖
                }
            }
        }

        // ==================== 聚合 ====================

        /// <summary>
        /// 累计（全部时间）
        /// </summary>
        public static UsageTotals Total()
        {
            lock (Gate)
            {
                EnsureMigratedLocked();

                if (_cache == null)
                {
                    _cache = ReadFromDiskLocked();
                }

                return _totalCache ??= Summarize(_cache);
            }
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

        // ==================== 磁盘读写（调用方须持 <see cref="Gate"/>） ====================

        /// <summary>
        /// 逐行读取 NDJSON（单行损坏只跳过该行）
        /// </summary>
        private static List<StoredUsage> ReadFromDiskLocked()
        {
            var result = new List<StoredUsage>();

            try
            {
                if (!File.Exists(FilePath))
                {
                    return result;
                }

                foreach (var line in File.ReadLines(FilePath, Encoding.UTF8))
                {
                    var text = line?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    try
                    {
                        var sample = Deserialize<StoredUsage>(text);
                        if (sample != null)
                        {
                            result.Add(sample);
                        }
                    }
                    catch (Exception)
                    {
                        // 单行损坏：跳过，不影响其余采样
                    }
                }
            }
            catch (Exception)
            {
                return result;
            }

            return result;
        }

        /// <summary>
        /// 追加一行（常规记账路径）
        /// </summary>
        private static void AppendLocked(StoredUsage sample)
        {
            try
            {
                System.IO.Directory.CreateDirectory(SettingsStore.Directory);
                File.AppendAllText(FilePath, Serialize(sample) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception)
            {
                // 写失败不阻断对话
            }
        }

        /// <summary>
        /// 整体重写（裁剪 / 迁移时用；原子替换）
        /// </summary>
        private static void WriteAllLocked(List<StoredUsage> samples)
        {
            _totalCache = null;

            try
            {
                System.IO.Directory.CreateDirectory(SettingsStore.Directory);

                var builder = new StringBuilder();
                foreach (var sample in samples ?? [])
                {
                    builder.Append(Serialize(sample)).Append('\n');
                }

                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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

        /// <summary>
        /// 首次访问时把旧的单文件 <c>usage.json</c> 迁移到 NDJSON（须在锁内调用）
        /// </summary>
        private static void EnsureMigratedLocked()
        {
            if (_migrated)
            {
                return;
            }

            _migrated = true;

            try
            {
                System.IO.Directory.CreateDirectory(SettingsStore.Directory);

                if (!File.Exists(LegacyFilePath))
                {
                    return;
                }

                var legacy = Deserialize<LegacyUsageFile>(File.ReadAllText(LegacyFilePath, Encoding.UTF8));
                var samples = legacy?.Samples ?? [];

                if (samples.Count > 0)
                {
                    // 与已有 NDJSON 合并（通常 NDJSON 还不存在），按时间排序并限制总量
                    var merged = ReadFromDiskLocked().Concat(samples).OrderBy(s => s.AtUtc).ToList();
                    if (merged.Count > MaxSamples)
                    {
                        merged = merged.OrderByDescending(s => s.AtUtc).Take(MaxSamples).OrderBy(s => s.AtUtc).ToList();
                    }

                    WriteAllLocked(merged);
                }

                // 迁移成功：把旧文件改名备份，避免下次重复迁移
                try
                {
                    File.Move(LegacyFilePath, LegacyFilePath + ".migrated");
                }
                catch (Exception)
                {
                    // 改名失败无妨：下次会重新迁移（幂等）
                }
            }
            catch (Exception)
            {
                // 迁移失败：保持旧文件不动，下次再试
            }
        }

        // ==================== 序列化 ====================

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var buffer = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(buffer, Encoding.UTF8, ownsStream: false, indent: false))
                {
                    serializer.WriteObject(writer, value);
                    writer.Flush();
                }

                return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r", string.Empty).Replace("\n", string.Empty);
            }
        }

        private static T Deserialize<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
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
