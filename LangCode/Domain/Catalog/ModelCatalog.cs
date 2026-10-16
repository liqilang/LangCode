using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LangCode.Domain.Catalog
{
    /// <summary>
    /// 一个可选模型
    /// </summary>
    public sealed class ModelInfo
    {
        /// <summary>
        /// 兼容用的精简构造（只有 id / 显示名 / 厂商 / 颜色 / 容量标注 / 上下文）
        /// </summary>
        public ModelInfo(string id, string displayName, string provider, string colorHex, string contextLabel, int contextTokens = 0)
            : this(id, displayName, provider, colorHex, contextLabel, contextTokens, 0, false, false, true, 0, 0)
        {
        }

        public ModelInfo(
            string id,
            string displayName,
            string provider,
            string colorHex,
            string contextLabel,
            int contextTokens,
            int maxOutputTokens,
            bool supportsImages,
            bool supportsReasoning,
            bool supportsTools,
            double inputPricePerMillion,
            double outputPricePerMillion)
        {
            Id = id;
            DisplayName = displayName;
            Provider = provider;
            ColorHex = colorHex;
            ContextLabel = contextLabel;
            ContextTokens = contextTokens;
            MaxOutputTokens = maxOutputTokens;
            SupportsImages = supportsImages;
            SupportsReasoning = supportsReasoning;
            SupportsTools = supportsTools;
            InputPricePerMillion = inputPricePerMillion;
            OutputPricePerMillion = outputPricePerMillion;
        }

        /// <summary>
        /// 发给 API 的 model 字段（也是设置里持久化的值）
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// 界面显示名
        /// </summary>
        public string DisplayName { get; }

        public string Provider { get; }

        /// <summary>
        /// 列表里的小圆点颜色
        /// </summary>
        public string ColorHex { get; }

        /// <summary>
        /// 容量标注（200k / 1M / Ollama …）
        /// </summary>
        public string ContextLabel { get; }

        /// <summary>
        /// 上下文窗口（token 数）选中该模型后，令牌条的「分母」会用它
        /// </summary>
        public int ContextTokens { get; }

        /// <summary>
        /// 最大输出 token（0 = 未知，沿用全局设置）
        /// </summary>
        public int MaxOutputTokens { get; }

        /// <summary>
        /// 是否支持图片输入（多模态）
        /// </summary>
        public bool SupportsImages { get; }

        /// <summary>
        /// 是否是推理模型（会在流里回传推理过程）
        /// </summary>
        public bool SupportsReasoning { get; }

        /// <summary>
        /// 是否支持原生工具调用（function calling）
        /// </summary>
        public bool SupportsTools { get; }

        /// <summary>
        /// 输入单价（美元 / 百万 token；0 = 未知或免费）
        /// </summary>
        public double InputPricePerMillion { get; }

        /// <summary>
        /// 输出单价（美元 / 百万 token；0 = 未知或免费）
        /// </summary>
        public double OutputPricePerMillion { get; }

        /// <summary>
        /// 是否带有可信的单价信息
        /// </summary>
        public bool HasPricing => InputPricePerMillion > 0 || OutputPricePerMillion > 0;

        /// <summary>
        /// 能力标签（列表 / 提示用）
        /// </summary>
        public string CapabilityLabel
        {
            get
            {
                var parts = new List<string>();
                if (SupportsReasoning)
                {
                    parts.Add("推理");
                }

                if (SupportsImages)
                {
                    parts.Add("视觉");
                }

                if (SupportsTools)
                {
                    parts.Add("工具");
                }

                return string.Join(" · ", parts);
            }
        }

        /// <summary>
        /// 便于调试/日志的可读形式
        /// </summary>
        public override string ToString()
        {
            return DisplayName + " (" + Id + ")";
        }
    }

    /// <summary>
    /// 一组模型（按厂商分组显示）
    /// </summary>
    public sealed class ModelGroup
    {
        public ModelGroup(string provider, string label, IList<ModelInfo> models)
        {
            Provider = provider;
            Label = label;
            Models = models;
        }

        public string Provider { get; }

        public string Label { get; }

        public IList<ModelInfo> Models { get; }
    }

    // ==================== 外部覆盖文件（models.json）的契约 ====================

    [DataContract(Name = "modelCatalog")]
    internal sealed class ModelCatalogOverride
    {
        [DataMember(Name = "groups", Order = 1)]
        public List<ModelGroupOverride> Groups { get; set; } = [];
    }

    [DataContract(Name = "group")]
    internal sealed class ModelGroupOverride
    {
        [DataMember(Name = "provider", Order = 1)]
        public string Provider { get; set; } = string.Empty;

        [DataMember(Name = "label", Order = 2)]
        public string Label { get; set; } = string.Empty;

        [DataMember(Name = "models", Order = 3)]
        public List<ModelEntryOverride> Models { get; set; } = [];
    }

    [DataContract(Name = "model")]
    internal sealed class ModelEntryOverride
    {
        [DataMember(Name = "id", Order = 1)]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "displayName", Order = 2)]
        public string DisplayName { get; set; } = string.Empty;

        [DataMember(Name = "colorHex", Order = 3)]
        public string ColorHex { get; set; } = string.Empty;

        [DataMember(Name = "contextLabel", Order = 4)]
        public string ContextLabel { get; set; } = string.Empty;

        [DataMember(Name = "contextTokens", Order = 5)]
        public int ContextTokens { get; set; }

        [DataMember(Name = "maxOutputTokens", Order = 6)]
        public int MaxOutputTokens { get; set; }

        [DataMember(Name = "supportsImages", Order = 7)]
        public bool SupportsImages { get; set; }

        [DataMember(Name = "supportsReasoning", Order = 8)]
        public bool SupportsReasoning { get; set; }

        [DataMember(Name = "supportsTools", Order = 9)]
        public bool SupportsTools { get; set; } = true;

        [DataMember(Name = "inputPrice", Order = 10)]
        public double InputPrice { get; set; }

        [DataMember(Name = "outputPrice", Order = 11)]
        public double OutputPrice { get; set; }
    }

    /// <summary>
    /// 模型 / 厂商目录
    /// </summary>
    public static class ModelCatalog
    {
        /// <summary>
        /// 覆盖文件名（放在插件数据目录下）
        /// </summary>
        public const string OverrideFileName = "models.json";

        /// <summary>
        /// 通用「OpenAI Compatible」提供商名（保留旧常量名做兼容）
        /// </summary>
        public const string OpenAiCompatibleProvider = ProviderCatalog.OpenAiCompatible;

        private static readonly object Gate = new object();

        private static List<ModelGroup> _groups;

        /// <summary>
        /// 构建中的重入标记（[ThreadStatic]）：构建过程里若有 helper 又摸了 Groups，
        /// 直接返回空目录，绝不递归构建（否则就是死循环）
        /// </summary>
        [ThreadStatic]
        private static bool _building;

        /// <summary>
        /// 目录是否已经加载进内存（<b>不会触发加载</b>）：UI 线程据此决定「现在能不能读目录」
        /// </summary>
        public static bool IsLoaded => BuiltGroups != null;

        /// <summary>
        /// 按厂商分组的模型列表（界面直接绑定）
        /// </summary>
        public static IList<ModelGroup> Groups
        {
            get
            {
                var ready = BuiltGroups;
                if (ready != null)
                {
                    return ready;
                }

                if (_building)
                {
                    return Array.Empty<ModelGroup>();
                }

                _building = true;
                List<ModelGroup> built;
                try
                {
                    // 读盘 + 合并**全部在锁外**做：
                    // 以前是在 lock 里构建，导致任何线程（尤其 UI 线程）都要在锁上干等磁盘 I/O，
                    // 表现就是「界面卡住」。
                    built = BuildBuiltIn();
                    TryApplyOverrideFile(built);

                    // 「从端点发现的模型」再叠一层：合并式，不改动内置与手工覆盖
                    TryApplyDiscoveredFile(built);
                }
                finally
                {
                    _building = false;
                }

                lock (Gate)
                {
                    // 并发构建时以先发布者为准（另一份丢弃），避免把已有结果覆盖掉
                    _groups ??= built;
                    return _groups;
                }
            }
        }

        /// <summary>
        /// 可选提供商（设置页「API 提供商」下拉用）：全部提供商 + 覆盖文件里新出现的厂商
        /// </summary>
        public static IList<string> Providers
        {
            get
            {
                var result = new List<string>(ProviderCatalog.Keys);

                // 只并入「已在内存里」的目录（不触发读盘）：ProviderCatalog.Keys 已经覆盖全部内置厂商，
                // 覆盖文件里新出现的厂商在目录就绪后自会出现
                var groups = BuiltGroups;
                if (groups != null)
                {
                    foreach (var group in groups)
                    {
                        if (!string.IsNullOrWhiteSpace(group.Provider) &&
                            !result.Contains(group.Provider, StringComparer.OrdinalIgnoreCase))
                        {
                            result.Add(group.Provider);
                        }
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// 全部模型（扁平）
        /// </summary>
        public static IEnumerable<ModelInfo> All => Groups.SelectMany(g => g.Models);

        /// <summary>
        /// 按 id 或显示名查找（找不到返回 null）
        /// </summary>
        public static ModelInfo Find(string idOrDisplayName)
        {
            return FindIn(Groups, idOrDisplayName);
        }

        /// <summary>
        /// 不触发目录加载的查找：目录还没进内存时返回 null。
        /// 供 UI 线程上的「显示名 / 颜色 / 限额」这类<b>可以先给兜底值、稍后再刷新</b>的读取使用，
        /// 避免为了取一个显示名就在 UI 线程读盘。
        /// </summary>
        public static ModelInfo TryFind(string idOrDisplayName)
        {
            return FindIn(BuiltGroups, idOrDisplayName);
        }

        /// <summary>
        /// 已在内存里的目录（<b>不触发加载</b>）；尚未加载时返回 null
        /// </summary>
        private static List<ModelGroup> BuiltGroups
        {
            get
            {
                lock (Gate)
                {
                    return _groups;
                }
            }
        }

        private static ModelInfo FindIn(IList<ModelGroup> groups, string idOrDisplayName)
        {
            if (string.IsNullOrWhiteSpace(idOrDisplayName) || groups == null)
            {
                return null;
            }

            foreach (var group in groups)
            {
                foreach (var model in group.Models)
                {
                    if (string.Equals(model.Id, idOrDisplayName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(model.DisplayName, idOrDisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        return model;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 某个厂商的全部模型
        /// </summary>
        public static IList<ModelInfo> ForProvider(string provider)
        {
            var key = ProviderCatalog.Canonicalize(provider);
            return Groups
                .Where(g => string.Equals(ProviderCatalog.Canonicalize(g.Provider), key, StringComparison.OrdinalIgnoreCase))
                .SelectMany(g => g.Models)
                .ToList();
        }

        /// <summary>
        /// 模型的点色（未知模型给一个中性色）
        /// </summary>
        public static string ColorOf(string idOrDisplayName)
        {
            // 用 TryFind：拿不到就给中性色，绝不为了取颜色在 UI 线程读盘
            return TryFind(idOrDisplayName)?.ColorHex ?? "#4EC9B0";
        }

        /// <summary>
        /// 模型的显示名（未知模型原样显示）
        /// </summary>
        public static string DisplayOf(string idOrDisplayName)
        {
            // 目录还没加载好时先原样显示 id，等预热完成后再刷新为友好名
            var info = TryFind(idOrDisplayName);
            return info?.DisplayName ?? (idOrDisplayName ?? string.Empty);
        }

        /// <summary>
        /// 模型的上下文窗口 token 数（未知返回 0）
        /// </summary>
        public static int ContextTokensOf(string idOrDisplayName)
        {
            return TryFind(idOrDisplayName)?.ContextTokens ?? 0;
        }

        /// <summary>
        /// 模型的最大输出 token（未知返回 0）
        /// </summary>
        public static int MaxOutputTokensOf(string idOrDisplayName)
        {
            return TryFind(idOrDisplayName)?.MaxOutputTokens ?? 0;
        }

        /// <summary>
        /// 数据目录（覆盖文件 <c>models.json</c> 所在目录）
        /// 领域层不引用持久化层，由宿主在启动时注入（Ui 侧传 <c>SettingsStore.Directory</c>）
        /// </summary>
        public static string DataDirectory { get; set; }

        /// <summary>
        /// 覆盖文件路径
        /// </summary>
        public static string OverridePath => Path.Combine(DataDirectory ?? string.Empty, OverrideFileName);

        /// <summary>
        /// 最近一次覆盖文件解析失败的原因（供诊断/自检显示）
        /// </summary>
        public static string LastOverrideError { get; private set; }

        /// <summary>
        /// 丢弃缓存（改了覆盖文件后调用）
        /// </summary>
        public static void Reload()
        {
            lock (Gate)
            {
                _groups = null;
            }
        }

        // ==================== 动态发现的模型（/models） ====================

        /// <summary>
        /// 「从端点发现的模型」文件名（与手工覆盖文件分开，便于单独清理）
        /// </summary>
        public const string DiscoveredFileName = "models.discovered.json";

        /// <summary>
        /// 发现结果文件路径
        /// </summary>
        public static string DiscoveredPath => Path.Combine(DataDirectory ?? string.Empty, DiscoveredFileName);

        /// <summary>
        /// 把一次「从端点拉取到的模型 id」按厂商存下来并合并进目录（同厂商整体替换）
        /// </summary>
        /// <returns>成功返回 true；失败把原因写进 <see cref="LastOverrideError"/></returns>
        public static bool SaveDiscovered(string provider, IEnumerable<string> modelIds)
        {
            var key = (provider ?? string.Empty).Trim();
            if (key.Length == 0 || modelIds == null)
            {
                return false;
            }

            try
            {
                var ids = modelIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var file = ReadDiscovered() ?? new ModelCatalogOverride();

                var group = file.Groups.FirstOrDefault(g =>
                    string.Equals(g.Provider, key, StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
                    group = new ModelGroupOverride { Provider = key, Label = key.ToUpperInvariant() };
                    file.Groups.Add(group);
                }

                // 端点 /models 只回 id：对内置目录里**已经认识**的模型，把它的容量 / 价格 / 能力一起带上。
                // 否则「发现一次」就把内置目录里这些模型的上下文窗口与最大输出抹成 0 ——
                // 设置页再选到它们时对不出限额（下拉停在别的模型留下的数字上）
                var builtIn = BuildBuiltIn();

                group.Models = ids
                    .Select(id => DiscoveredEntry(id, key, builtIn))
                    .ToList();

                WriteDiscovered(file);
                Reload();
                return true;
            }
            catch (Exception ex)
            {
                LastOverrideError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 由「端点发现的模型 id」生成一条目录条目：内置目录认识的就照抄它的信息
        /// （显示名 / 容量 / 价格 / 能力），不认识的只留 id，等用户在设置页补
        /// </summary>
        private static ModelEntryOverride DiscoveredEntry(string id, string provider, IList<ModelGroup> builtIn)
        {
            // 优先取**同厂商**的那条：同一个 id 在不同厂商下的价格 / 容量可能不同
            ModelInfo info = null;
            for (var i = 0; i < builtIn.Count && info == null; i++)
            {
                if (string.Equals(builtIn[i].Provider, provider, StringComparison.OrdinalIgnoreCase))
                {
                    info = FindIn([builtIn[i]], id);
                }
            }

            info ??= FindIn(builtIn, id);

            return new ModelEntryOverride
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(info?.DisplayName) ? id : info.DisplayName,
                ColorHex = string.IsNullOrWhiteSpace(info?.ColorHex) ? "#4EC9B0" : info.ColorHex,
                ContextLabel = info?.ContextLabel ?? string.Empty,
                ContextTokens = info?.ContextTokens ?? 0,
                MaxOutputTokens = info?.MaxOutputTokens ?? 0,
                SupportsImages = info?.SupportsImages ?? false,
                SupportsReasoning = info?.SupportsReasoning ?? false,
                SupportsTools = info?.SupportsTools ?? true,
                InputPrice = info?.InputPricePerMillion ?? 0,
                OutputPrice = info?.OutputPricePerMillion ?? 0,
            };
        }

        /// <summary>
        /// 异步版保存：<b>写盘 + 重新加载目录全部在线程池上完成</b>，调用方 await 即可，
        /// 绝不占用 UI 线程（目录序列化 / 文件重建在大目录下是实打实的 IO 开销）
        /// </summary>
        public static Task<bool> SaveDiscoveredAsync(string provider, IEnumerable<string> modelIds)
        {
            // 先把惰性序列固化下来，避免枚举跨线程时才执行
            var ids = modelIds == null ? new List<string>() : modelIds.ToList();
            return Task.Run(() => SaveDiscovered(provider, ids));
        }

        /// <summary>
        /// 后台预热目录（内置 + 覆盖文件 + 已发现文件）：把「首次访问时才读盘」提前到线程池上做，
        /// 避免第一次打开工具窗 / 设置页时在 UI 线程读盘卡顿
        /// </summary>
        public static Task WarmUpAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    _ = Groups;
                }
                catch (Exception)
                {
                    // 预热失败无需处理：真正访问时会再走一次并抛出/降级
                }
            });
        }

        private static void TryApplyDiscoveredFile(List<ModelGroup> groups)
        {
            try
            {
                var path = DiscoveredPath;
                if (File.Exists(path))
                {
                    ApplyOverrideJson(groups, File.ReadAllText(path, Encoding.UTF8));
                }
            }
            catch (Exception)
            {
                // 发现文件坏了就忽略：内置目录仍然可用
            }
        }

        private static ModelCatalogOverride ReadDiscovered()
        {
            var path = DiscoveredPath;
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var serializer = new DataContractJsonSerializer(typeof(ModelCatalogOverride));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path, Encoding.UTF8))))
                {
                    return serializer.ReadObject(stream) as ModelCatalogOverride;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteDiscovered(ModelCatalogOverride file)
        {
            System.IO.Directory.CreateDirectory(DataDirectory ?? string.Empty);

            var serializer = new DataContractJsonSerializer(typeof(ModelCatalogOverride));
            var buffer = new MemoryStream();
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(buffer, Encoding.UTF8, ownsStream: false, indent: true))
            {
                serializer.WriteObject(writer, file);
                writer.Flush();
            }

            File.WriteAllText(DiscoveredPath, Encoding.UTF8.GetString(buffer.ToArray()),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // ==================== 覆盖 ====================

        private static void TryApplyOverrideFile(List<ModelGroup> groups)
        {
            try
            {
                var path = OverridePath;
                if (File.Exists(path))
                {
                    ApplyOverrideJson(groups, File.ReadAllText(path, Encoding.UTF8));
                }
            }
            catch (Exception)
            {
                // 覆盖文件坏了就忽略：内置目录仍然可用
            }
        }

        /// <summary>
        /// 把一份覆盖 JSON 合并进目录（同 provider+id 覆盖，否则追加）失败返回 false
        /// </summary>
        public static bool ApplyOverrideJson(List<ModelGroup> groups, string json)
        {
            if (groups == null || string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            ModelCatalogOverride parsed;
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(ModelCatalogOverride));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    parsed = serializer.ReadObject(stream) as ModelCatalogOverride;
                }
            }
            catch (Exception ex)
            {
                LastOverrideError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            if (parsed?.Groups == null || parsed.Groups.Count == 0)
            {
                LastOverrideError = "解析成功但 groups 为空";
                return false;
            }

            LastOverrideError = null;

            foreach (var groupOverride in parsed.Groups)
            {
                if (string.IsNullOrWhiteSpace(groupOverride.Provider))
                {
                    continue;
                }

                var target = groups.FirstOrDefault(g =>
                    string.Equals(g.Provider, groupOverride.Provider, StringComparison.OrdinalIgnoreCase));

                if (target == null)
                {
                    var models = new List<ModelInfo>();
                    foreach (var entry in groupOverride.Models ?? [])
                    {
                        var built = ToModelInfo(entry, groupOverride.Provider);
                        if (built != null)
                        {
                            models.Add(built);
                        }
                    }

                    groups.Add(new ModelGroup(
                        groupOverride.Provider,
                        string.IsNullOrWhiteSpace(groupOverride.Label) ? groupOverride.Provider : groupOverride.Label,
                        models));
                    continue;
                }

                foreach (var entry in groupOverride.Models ?? [])
                {
                    var built = ToModelInfo(entry, groupOverride.Provider);
                    if (built == null)
                    {
                        continue;
                    }

                    // 同 id 覆盖，否则追加
                    for (var i = 0; i < target.Models.Count; i++)
                    {
                        if (string.Equals(target.Models[i].Id, built.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            target.Models.RemoveAt(i);
                            break;
                        }
                    }

                    target.Models.Add(built);
                }
            }

            return true;
        }

        private static ModelInfo ToModelInfo(ModelEntryOverride entry, string provider)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Id))
            {
                return null;
            }

            var label = entry.ContextLabel;
            if (string.IsNullOrWhiteSpace(label) && entry.ContextTokens > 0)
            {
                label = FormatTokens(entry.ContextTokens);
            }

            return new ModelInfo(
                entry.Id.Trim(),
                string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Id.Trim() : entry.DisplayName.Trim(),
                provider,
                string.IsNullOrWhiteSpace(entry.ColorHex) ? "#8A8A8A" : entry.ColorHex.Trim(),
                label ?? string.Empty,
                entry.ContextTokens,
                entry.MaxOutputTokens,
                entry.SupportsImages,
                entry.SupportsReasoning,
                entry.SupportsTools,
                entry.InputPrice,
                entry.OutputPrice);
        }

        /// <summary>
        /// 容量的显示写法。模型容量本来就分两种叫法，这里按"哪种读起来是整数就用哪种"来输出：
        ///
        ///   1. 十进制刚好整齐的（400000 = 400K、1050000 = 1.05M、1000000 = 1M）→ 按 1000 进制；
        ///   2. 否则按二进制 1024 进制 —— 大多数厂商的容量是这种：
        ///      131072 = <b>128K</b>（不是 131.1k）、163840 = 160K、204800 = 200K、1048576 = 1M；
        ///   3. 两种都不整齐时，取 1024 进制下的近似值（例：1047576 → 1M）。
        ///
        /// 以前一律按 1000 除，于是 131072 显示成「131.1k」、两组 1M 档位（1048576 / 1050000）
        /// 还会同时显示成「1.05M」—— 下拉里根本分不清谁是谁
        /// </summary>
        internal static string FormatTokens(int tokens)
        {
            if (tokens <= 0)
            {
                return string.Empty;
            }

            // 1) 十进制整齐的：按 1000 进制（小数点后只允许 1 位，163.84 这种不算整齐）
            if (tokens >= 1000000)
            {
                var millions = tokens / 1000000.0;
                if (IsWhole(millions, 2))
                {
                    return millions.ToString("0.##") + "M";
                }
            }
            else
            {
                var thousands = tokens / 1000.0;
                if (IsWhole(thousands, 1))
                {
                    return thousands.ToString("0.#") + "K";
                }
            }

            // 2) 二进制整齐的：按 1024 进制（K 档必须小于 1024，否则交给下面的 M 档）
            if (tokens % 1048576 == 0)
            {
                return (tokens / 1048576) + "M";
            }

            if (tokens % 1024 == 0 && tokens / 1024 < 1024)
            {
                return (tokens / 1024) + "K";
            }

            // 3) 兜底：1024 进制下的近似值，小数点后保留两位
            return tokens >= 1000000
                ? (tokens / 1048576.0).ToString("0.##") + "M"
                : (tokens / 1024.0).ToString("0.#") + "K";
        }

        /// <summary>
        /// 是否是"整齐"的数字（小数位数不超过 <paramref name="decimals"/>，且没有多余零头）
        /// </summary>
        private static bool IsWhole(double value, int decimals)
        {
            var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            return Math.Abs(value - rounded) < 0.0000001;
        }

        // ==================== 内置目录 ====================
        //
        // 颜色约定（与界面点色一致）：
        //   Anthropic 系 #C586C0 / #4EC9B0；OpenAI 系 #3794FF / #9CDCFE；
        //   Google #DCB67A / #B5CEA8；xAI #E0E0E0；Mistral #FF7000；
        //   DeepSeek #4D6BFE；Qwen #CE9178；GLM #3B6EF6；Kimi #00B3A4

        /// <summary>
        /// 紧凑构造：容量标注由上下文 token 推导
        /// </summary>
        private static ModelInfo M(
            string provider,
            string color,
            string id,
            string name,
            int contextTokens,
            int maxOutput = 0,
            bool images = false,
            bool reasoning = false,
            double input = 0,
            double output = 0,
            bool tools = true)
        {
            return new ModelInfo(
                id, name, provider, color, FormatTokens(contextTokens), contextTokens,
                maxOutput, images, reasoning, tools, input, output);
        }

        private static List<ModelGroup> BuildBuiltIn()
        {
            var groups = new List<ModelGroup>();

            void Add(string provider, params ModelInfo[] models)
            {
                var def = ProviderCatalog.Find(provider);
                groups.Add(new ModelGroup(
                    provider,
                    def?.GroupLabel ?? provider.ToUpperInvariant(),
                    models.ToList()));
            }

            // ---------------- Baseten ----------------
            Add("Baseten",
                M("Baseten", "#4D6BFE", "deepseek-ai/DeepSeek-V3-0324", "DeepSeek V3 0324", 163840, 16384, false, false, 0.77, 0.77),
                M("Baseten", "#7C4DFF", "deepseek-ai/DeepSeek-R1", "DeepSeek R1", 131072, 32768, false, true, 2.55, 6.50),
                M("Baseten", "#CE9178", "Qwen/Qwen3-Coder-480B-A35B-Instruct", "Qwen3 Coder 480B", 262144, 32768, false, false, 0.38, 1.53),
                M("Baseten", "#00B3A4", "moonshotai/Kimi-K2-Instruct", "Kimi K2 Instruct", 131072, 32768, false, false, 0.60, 2.50),
                M("Baseten", "#9CDCFE", "meta-llama/Llama-4-Maverick-17B-128E-Instruct", "Llama 4 Maverick", 131072, 16384, true, false, 0.24, 0.97));

            // ---------------- DeepSeek ----------------
            Add("DeepSeek",
                M("DeepSeek", "#4D6BFE", "deepseek-chat", "DeepSeek Chat (V3.2)", 163840, 8192, false, false, 0.28, 0.42),
                M("DeepSeek", "#7C4DFF", "deepseek-reasoner", "DeepSeek Reasoner (R1)", 163840, 65536, false, true, 0.55, 2.19));

            // ---------------- Fireworks AI ----------------
            Add("Fireworks AI",
                M("Fireworks AI", "#F55036", "accounts/fireworks/models/deepseek-r1", "DeepSeek R1 (Fireworks)", 163840, 32768, false, true, 3.00, 8.00),
                M("Fireworks AI", "#4D6BFE", "accounts/fireworks/models/deepseek-v3", "DeepSeek V3 (Fireworks)", 131072, 16384, false, false, 0.90, 0.90),
                M("Fireworks AI", "#00B3A4", "accounts/fireworks/models/kimi-k2-instruct", "Kimi K2 (Fireworks)", 131072, 32768, false, false, 0.60, 2.50),
                M("Fireworks AI", "#CE9178", "accounts/fireworks/models/qwen3-coder-480b-a35b-instruct", "Qwen3 Coder 480B (Fireworks)", 262144, 32768, false, false, 0.45, 1.80),
                M("Fireworks AI", "#9CDCFE", "accounts/fireworks/models/llama-v3p3-70b-instruct", "Llama 3.3 70B (Fireworks)", 131072, 16384, false, false, 0.90, 0.90));

            // ---------------- Friendli ----------------
            Add("Friendli",
                M("Friendli", "#9CDCFE", "meta-llama-3.3-70b-instruct", "Llama 3.3 70B (Friendli)", 131072, 8192, false, false, 0.60, 0.60),
                M("Friendli", "#4D6BFE", "deepseek-ai/DeepSeek-V3", "DeepSeek V3 (Friendli)", 131072, 16384, false, false, 0.60, 0.60),
                M("Friendli", "#CE9178", "Qwen/Qwen3-235B-A22B-Instruct-2507", "Qwen3 235B (Friendli)", 262144, 32768, false, false, 0.20, 0.60));

            // ---------------- GCP Vertex AI ----------------
            // 说明：Vertex 需区域化端点 + 服务账号鉴权；这里保留模型目录供参考，地址需手填
            Add("GCP Vertex AI",
                M("GCP Vertex AI", "#DCB67A", "gemini-2.5-pro", "Gemini 2.5 Pro (Vertex)", 1048576, 65536, true, true, 1.25, 10.00),
                M("GCP Vertex AI", "#B5CEA8", "gemini-2.5-flash", "Gemini 2.5 Flash (Vertex)", 1048576, 65536, true, true, 0.30, 2.50),
                M("GCP Vertex AI", "#C586C0", "claude-sonnet-4-5@20250929", "Claude Sonnet 4.5 (Vertex)", 200000, 64000, true, true, 3.00, 15.00));

            // ---------------- Google Gemini ----------------
            Add("Google Gemini",
                M("Google Gemini", "#DCB67A", "gemini-2.5-pro", "Gemini 2.5 Pro", 1048576, 65536, true, true, 1.25, 10.00),
                M("Google Gemini", "#B5CEA8", "gemini-2.5-flash", "Gemini 2.5 Flash", 1048576, 65536, true, true, 0.30, 2.50),
                M("Google Gemini", "#B5CEA8", "gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite", 1048576, 65536, true, true, 0.10, 0.40),
                M("Google Gemini", "#9CDCFE", "gemini-2.0-flash", "Gemini 2.0 Flash", 1048576, 8192, true, false, 0.10, 0.40),
                M("Google Gemini", "#8A8A8A", "gemini-2.0-flash-lite", "Gemini 2.0 Flash Lite", 1048576, 8192, true, false, 0.075, 0.30));

            // ---------------- Kenari ----------------
            Add("Kenari",
                M("Kenari", "#8A8A8A", "kenari-default", "Kenari 默认模型（请按端点支持的 id 修改）", 131072, 8192, false, false));

            // ---------------- Kimi Code ----------------
            Add("Kimi Code",
                M("Kimi Code", "#00B3A4", "kimi-k2-0905-preview", "Kimi K2 0905", 262144, 32768, false, false, 0.60, 2.50),
                M("Kimi Code", "#00B3A4", "kimi-k2-turbo-preview", "Kimi K2 Turbo", 262144, 32768, false, false, 1.15, 8.00),
                M("Kimi Code", "#00B3A4", "kimi-latest", "Kimi Latest", 131072, 8192, false, false, 0.60, 2.50),
                M("Kimi Code", "#00B3A4", "moonshot-v1-128k", "Moonshot v1 128k", 131072, 8192, false, false, 0.60, 2.50));

            // ---------------- LiteLLM ----------------
            Add("LiteLLM",
                M("LiteLLM", "#9AA3B6", "gpt-4o", "gpt-4o (LiteLLM 代理)", 128000, 16384, true, false, 2.50, 10.00),
                M("LiteLLM", "#9AA3B6", "claude-sonnet-4-5", "Claude Sonnet 4.5 (LiteLLM 代理)", 200000, 64000, true, true, 3.00, 15.00));

            // ---------------- LM Studio ----------------
            Add("LM Studio",
                M("LM Studio", "#9AA3B6", "local-model", "LM Studio 当前加载模型", 32768, 8192, true, false));

            // ---------------- MiniMax ----------------
            Add("MiniMax",
                M("MiniMax", "#F0A030", "MiniMax-M2", "MiniMax M2", 204800, 32768, false, true, 0.30, 1.20),
                M("MiniMax", "#F0A030", "MiniMax-Text-01", "MiniMax Text 01", 1000000, 32768, false, true, 0.20, 1.10),
                M("MiniMax", "#F0A030", "abab6.5s-chat", "abab6.5s-chat", 245760, 8192, false, false, 0.14, 0.14));

            // ---------------- Mistral ----------------
            Add("Mistral",
                M("Mistral", "#FF7000", "mistral-large-latest", "Mistral Large (latest)", 131072, 32768, false, false, 2.00, 6.00),
                M("Mistral", "#FF7000", "mistral-medium-latest", "Mistral Medium (latest)", 131072, 32768, false, false, 0.40, 2.00),
                M("Mistral", "#FF7000", "codestral-latest", "Codestral (latest)", 262144, 32768, false, false, 0.30, 0.90),
                M("Mistral", "#FF7000", "devstral-medium-latest", "Devstral Medium", 131072, 32768, false, false, 0.40, 2.00),
                M("Mistral", "#FF7000", "magistral-medium-latest", "Magistral Medium", 131072, 32768, false, true, 2.00, 5.00),
                M("Mistral", "#FF7000", "ministral-8b-latest", "Ministral 8B (latest)", 131072, 16384, false, false, 0.10, 0.10));

            // ---------------- Moonshot ----------------
            Add("Moonshot",
                M("Moonshot", "#00B3A4", "kimi-k2-0905-preview", "Kimi K2 0905", 262144, 32768, false, false, 0.60, 2.50),
                M("Moonshot", "#00B3A4", "kimi-k2-turbo-preview", "Kimi K2 Turbo", 262144, 32768, false, false, 1.15, 8.00),
                M("Moonshot", "#00B3A4", "moonshot-v1-8k", "Moonshot v1 8k", 8192, 4096, false, false, 0.12, 0.12),
                M("Moonshot", "#00B3A4", "moonshot-v1-32k", "Moonshot v1 32k", 32768, 8192, false, false, 0.24, 0.24),
                M("Moonshot", "#00B3A4", "moonshot-v1-128k", "Moonshot v1 128k", 131072, 8192, false, false, 0.60, 0.60));

            // ---------------- NanoGPT ----------------
            Add("NanoGPT",
                M("NanoGPT", "#8A8A8A", "gpt-4o", "gpt-4o (NanoGPT)", 128000, 16384, true, false, 2.50, 10.00),
                M("NanoGPT", "#8A8A8A", "claude-sonnet-4-5", "Claude Sonnet 4.5 (NanoGPT)", 200000, 64000, true, true, 3.00, 15.00));

            // ---------------- Ollama ----------------
            Add("Ollama",
                M("Ollama", "#CE9178", "qwen3-coder:30b", "Qwen3 Coder 30B (Ollama)", 262144, 32768),
                M("Ollama", "#B5CEA8", "gpt-oss:120b", "GPT-OSS 120B (Ollama)", 131072, 32768),
                M("Ollama", "#7C4DFF", "deepseek-r1:14b", "DeepSeek R1 14B (Ollama)", 65536, 8192, false, true));

            // ---------------- OpenAI ----------------
            Add("OpenAI",
                M("OpenAI", "#3794FF", "gpt-5", "GPT-5", 400000, 128000, true, true, 1.25, 10.00),
                M("OpenAI", "#3794FF", "gpt-5-mini", "GPT-5 mini", 400000, 128000, true, true, 0.25, 2.00),
                M("OpenAI", "#3794FF", "gpt-5-nano", "GPT-5 nano", 400000, 128000, true, true, 0.05, 0.40),
                M("OpenAI", "#9CDCFE", "gpt-4.1", "GPT-4.1", 1047576, 32768, true, false, 2.00, 8.00),
                M("OpenAI", "#9CDCFE", "gpt-4.1-mini", "GPT-4.1 mini", 1047576, 32768, true, false, 0.40, 1.60),
                M("OpenAI", "#9CDCFE", "gpt-4.1-nano", "GPT-4.1 nano", 1047576, 32768, true, false, 0.10, 0.40),
                M("OpenAI", "#4EC9B0", "o3", "o3", 200000, 100000, true, true, 2.00, 8.00),
                M("OpenAI", "#4EC9B0", "o4-mini", "o4-mini", 200000, 100000, true, true, 1.10, 4.40),
                M("OpenAI", "#9CDCFE", "gpt-4o", "GPT-4o", 128000, 16384, true, false, 2.50, 10.00),
                M("OpenAI", "#8A8A8A", "gpt-4o-mini", "GPT-4o mini", 128000, 16384, true, false, 0.15, 0.60));

            // ---------------- OpenAI - ChatGPT Plus/Pro ----------------
            Add("OpenAI - ChatGPT Plus/Pro",
                M("OpenAI - ChatGPT Plus/Pro", "#3794FF", "gpt-5-codex", "GPT-5-Codex (订阅)", 400000, 128000, true, true),
                M("OpenAI - ChatGPT Plus/Pro", "#3794FF", "gpt-5", "GPT-5 (订阅)", 400000, 128000, true, true));

            // ---------------- OpenAI Compatible ----------------
            Add("OpenAI Compatible",
                M("OpenAI Compatible", "#8A8A8A", "gpt-4o", "gpt-4o（示例 id，请按端点修改）", 128000, 16384, true, false));

            // ---------------- Opencode Go ----------------
            Add("Opencode Go",
                M("Opencode Go", "#4EC9B0", "claude-sonnet-4-5", "Claude Sonnet 4.5 (Opencode)", 200000, 64000, true, true, 3.00, 15.00),
                M("Opencode Go", "#3794FF", "gpt-5", "GPT-5 (Opencode)", 400000, 128000, true, true, 1.25, 10.00));

            // ---------------- OpenRouter ----------------
            Add("OpenRouter",
                M("OpenRouter", "#4EC9B0", "anthropic/claude-sonnet-4.5", "Claude Sonnet 4.5 (OpenRouter)", 1000000, 64000, true, true, 3.00, 15.00),
                M("OpenRouter", "#3794FF", "openai/gpt-5", "GPT-5 (OpenRouter)", 400000, 128000, true, true, 1.25, 10.00),
                M("OpenRouter", "#4D6BFE", "deepseek/deepseek-chat-v3.1", "DeepSeek V3.1 (OpenRouter)", 163840, 32768, false, false, 0.20, 0.80),
                M("OpenRouter", "#DCB67A", "google/gemini-2.5-pro", "Gemini 2.5 Pro (OpenRouter)", 1048576, 65536, true, true, 1.25, 10.00),
                M("OpenRouter", "#E0E0E0", "x-ai/grok-4", "Grok 4 (OpenRouter)", 256000, 32768, true, true, 3.00, 15.00),
                M("OpenRouter", "#00B3A4", "moonshotai/kimi-k2", "Kimi K2 (OpenRouter)", 131072, 32768, false, false, 0.60, 2.50),
                M("OpenRouter", "#3B6EF6", "z-ai/glm-4.5", "GLM-4.5 (OpenRouter)", 131072, 32768, false, false, 0.60, 2.20),
                M("OpenRouter", "#CE9178", "qwen/qwen3-coder", "Qwen3 Coder (OpenRouter)", 262144, 32768, false, false, 0.30, 1.20));

            // ---------------- Poe ----------------
            Add("Poe",
                M("Poe", "#3794FF", "GPT-5", "GPT-5 (Poe)", 400000, 128000, true, true),
                M("Poe", "#4EC9B0", "Claude-Sonnet-4.5", "Claude Sonnet 4.5 (Poe)", 200000, 64000, true, true),
                M("Poe", "#DCB67A", "Gemini-2.5-Pro", "Gemini 2.5 Pro (Poe)", 1048576, 65536, true, true));

            // ---------------- Qwen Code ----------------
            Add("Qwen Code",
                M("Qwen Code", "#CE9178", "qwen3-coder-plus", "Qwen3 Coder Plus", 1000000, 65536, false, false, 0.40, 1.60),
                M("Qwen Code", "#CE9178", "qwen3-coder-flash", "Qwen3 Coder Flash", 1000000, 65536, false, false, 0.15, 0.60));

            // ---------------- Requesty ----------------
            Add("Requesty",
                M("Requesty", "#3794FF", "openai/gpt-5", "GPT-5 (Requesty)", 400000, 128000, true, true, 1.25, 10.00),
                M("Requesty", "#4EC9B0", "anthropic/claude-sonnet-4-5", "Claude Sonnet 4.5 (Requesty)", 200000, 64000, true, true, 3.00, 15.00),
                M("Requesty", "#DCB67A", "google/gemini-2.5-pro", "Gemini 2.5 Pro (Requesty)", 1048576, 65536, true, true, 1.25, 10.00));

            // ---------------- SambaNova ----------------
            Add("SambaNova",
                M("SambaNova", "#9CDCFE", "Meta-Llama-3.3-70B-Instruct", "Llama 3.3 70B (SambaNova)", 131072, 16384, false, false, 0.60, 1.20),
                M("SambaNova", "#7C4DFF", "DeepSeek-R1", "DeepSeek R1 (SambaNova)", 32768, 16384, false, true, 5.00, 7.00),
                M("SambaNova", "#CE9178", "Qwen3-235B-A22B-Instruct-2507", "Qwen3 235B (SambaNova)", 131072, 16384, false, false, 0.40, 0.40),
                M("SambaNova", "#9CDCFE", "Llama-4-Maverick-17B-128E-Instruct", "Llama 4 Maverick (SambaNova)", 131072, 16384, true, false, 0.63, 1.80));

            // ---------------- Unbound ----------------
            Add("Unbound",
                M("Unbound", "#4EC9B0", "anthropic/claude-sonnet-4.5", "Claude Sonnet 4.5 (Unbound)", 200000, 64000, true, true, 3.00, 15.00),
                M("Unbound", "#3794FF", "openai/gpt-5", "GPT-5 (Unbound)", 400000, 128000, true, true, 1.25, 10.00));

            // ---------------- Vercel AI Gateway ----------------
            Add("Vercel AI Gateway",
                M("Vercel AI Gateway", "#4EC9B0", "anthropic/claude-sonnet-4.5", "Claude Sonnet 4.5 (Vercel)", 1000000, 64000, true, true, 3.00, 15.00),
                M("Vercel AI Gateway", "#3794FF", "openai/gpt-5", "GPT-5 (Vercel)", 400000, 128000, true, true, 1.25, 10.00),
                M("Vercel AI Gateway", "#DCB67A", "google/gemini-2.5-pro", "Gemini 2.5 Pro (Vercel)", 1048576, 65536, true, true, 1.25, 10.00));

            // ---------------- VS Code LM API ----------------
            Add("VS Code LM API",
                M("VS Code LM API", "#8A8A8A", "vscode-lm-default", "VS Code 语言模型（仅在 VS Code 可用）", 128000, 8192));

            // ---------------- xAI (Grok) ----------------
            Add("xAI (Grok)",
                M("xAI (Grok)", "#E0E0E0", "grok-4", "Grok 4", 256000, 32768, true, true, 3.00, 15.00),
                M("xAI (Grok)", "#E0E0E0", "grok-4-fast-reasoning", "Grok 4 Fast (Reasoning)", 2000000, 32768, true, true, 0.20, 0.50),
                M("xAI (Grok)", "#E0E0E0", "grok-3", "Grok 3", 131072, 16384, false, false, 3.00, 15.00),
                M("xAI (Grok)", "#E0E0E0", "grok-3-mini", "Grok 3 mini", 131072, 16384, false, true, 0.30, 0.50),
                M("xAI (Grok)", "#B5CEA8", "grok-code-fast-1", "Grok Code Fast 1", 256000, 32768, false, true, 0.20, 1.50));

            // ---------------- Xiaomi MiMo ----------------
            Add("Xiaomi MiMo",
                M("Xiaomi MiMo", "#FF6900", "MiMo-7B-RL", "MiMo 7B RL", 32768, 8192, false, true),
                M("Xiaomi MiMo", "#FF6900", "MiMo-VL-7B-RL", "MiMo VL 7B RL", 32768, 8192, true, true));

            // ---------------- Z.ai ----------------
            Add("Z.ai",
                M("Z.ai", "#3B6EF6", "glm-4.6", "GLM-4.6", 204800, 131072, false, false, 0.60, 2.20),
                M("Z.ai", "#3B6EF6", "glm-4.5", "GLM-4.5", 131072, 98304, false, false, 0.60, 2.20),
                M("Z.ai", "#6A9955", "glm-4.5-air", "GLM-4.5 Air", 131072, 98304, false, false, 0.20, 1.10),
                M("Z.ai", "#3B6EF6", "glm-4.5-flash", "GLM-4.5 Flash（免费）", 131072, 98304, false, false));

            // ---------------- Anthropic ----------------
            Add("Anthropic",
                M("Anthropic", "#C586C0", "claude-opus-4-5", "Claude Opus 4.5", 200000, 64000, true, true, 5.00, 25.00),
                M("Anthropic", "#4EC9B0", "claude-sonnet-4-5", "Claude Sonnet 4.5", 200000, 64000, true, true, 3.00, 15.00),
                M("Anthropic", "#DCB67A", "claude-haiku-4-5", "Claude Haiku 4.5", 200000, 64000, true, true, 1.00, 5.00),
                M("Anthropic", "#4EC9B0", "claude-sonnet-4-20250514", "Claude Sonnet 4", 200000, 64000, true, true, 3.00, 15.00));

            // ---------------- Groq ----------------
            Add("Groq",
                M("Groq", "#9CDCFE", "llama-3.3-70b-versatile", "Llama 3.3 70B (Groq)", 131072, 32768, false, false, 0.59, 0.79),
                M("Groq", "#3794FF", "openai/gpt-oss-120b", "GPT-OSS 120B (Groq)", 131072, 32768, false, true, 0.15, 0.75),
                M("Groq", "#3794FF", "openai/gpt-oss-20b", "GPT-OSS 20B (Groq)", 131072, 32768, false, true, 0.075, 0.30),
                M("Groq", "#00B3A4", "moonshotai/kimi-k2-instruct", "Kimi K2 (Groq)", 131072, 16384, false, false, 1.00, 3.00));

            // ---------------- SiliconFlow ----------------
            Add("SiliconFlow",
                M("SiliconFlow", "#4D6BFE", "deepseek-ai/DeepSeek-V3", "DeepSeek V3 (SiliconFlow)", 65536, 8192, false, false, 0.27, 1.10),
                M("SiliconFlow", "#CE9178", "Qwen/Qwen3-Coder-480B-A35B-Instruct", "Qwen3 Coder 480B (SiliconFlow)", 262144, 32768, false, false, 0.45, 1.80));

            // ---------------- 智谱 GLM ----------------
            Add("智谱 GLM",
                M("智谱 GLM", "#3B6EF6", "glm-4.6", "GLM-4.6", 204800, 131072, false, false, 0.60, 2.20),
                M("智谱 GLM", "#6A9955", "glm-4.5-flash", "GLM-4.5 Flash（免费）", 131072, 98304, false, false),
                M("智谱 GLM", "#3B6EF6", "glm-4.5", "GLM-4.5", 131072, 98304, false, false, 0.60, 2.20),
                M("智谱 GLM", "#6A9955", "glm-4.5-air", "GLM-4.5 Air", 131072, 98304, false, false, 0.20, 1.10));

            // ---------------- 通义千问 (DashScope) ----------------
            Add("通义千问 (DashScope)",
                M("通义千问 (DashScope)", "#CE9178", "qwen3-coder-plus", "Qwen3 Coder Plus", 1000000, 65536, false, false, 0.40, 1.60),
                M("通义千问 (DashScope)", "#CE9178", "qwen3-coder-flash", "Qwen3 Coder Flash", 1000000, 65536, false, false, 0.15, 0.60),
                M("通义千问 (DashScope)", "#CE9178", "qwen3-coder", "Qwen3 Coder", 262144, 32768, false, false, 0.30, 1.20),
                M("通义千问 (DashScope)", "#D7BA7D", "qwen3-max", "Qwen3 Max", 262144, 32768, false, false, 1.20, 6.00));

            return groups;
        }
    }
}

