using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Workspace
{
    /// <summary>
    /// 语义索引里的一个代码片段（一段可被检索的文本）
    /// </summary>
    public sealed class CodeChunk
    {
        public string Path = string.Empty;
        public int StartLine;
        public int EndLine;

        /// <summary>
        /// 该片段归属的符号名（没有则是空串，例如文件头注释块）
        /// </summary>
        public string Symbol = string.Empty;

        /// <summary>
        /// 符号类别（类型 / 方法 / 函数 / 文件）
        /// </summary>
        public string Kind = string.Empty;

        public string Text = string.Empty;
    }

    /// <summary>
    /// 索引里的一个类型节点（用于继承 / 实现关系查询）
    /// </summary>
    public sealed class SymbolNode
    {
        public string Name = string.Empty;
        public string Kind = string.Empty;
        public string Path = string.Empty;
        public int Line;

        /// <summary>
        /// 基类 / 实现的接口名（已去掉泛型参数）
        /// </summary>
        public List<string> Bases = [];
    }

    /// <summary>
    /// 本地代码库语义索引（RAG）
    /// </summary>
    public sealed class SemanticIndex
    {
        private static readonly string[] IgnoredDirectories =
        {
            ".git", ".vs", ".idea", ".vscode", ".langcode", "bin", "obj", "node_modules", "packages",
            "TestResults", ".next", "dist", "build", "out", "target", "coverage", "__pycache__",
            ".venv", "venv", "vendor", "Debug", "Release",
        };

        /// <summary>
        /// 建索引时要读源码的扩展名（其它文件不进索引，避免二进制 / 资源噪声）
        /// </summary>
        private static readonly HashSet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".vb", ".fs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte",
            ".java", ".kt", ".kts", ".scala", ".go", ".rs", ".rb", ".php", ".py", ".swift",
            ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".m", ".mm", ".cshtml", ".razor",
            ".sql", ".sh", ".ps1", ".psm1", ".bat", ".cmd", ".yml", ".yaml", ".toml",
            ".json", ".xml", ".md", ".txt", ".gradle", ".dockerfile",
        };

        /// <summary>
        /// 类型声明 + 可选基类列表：<c>class Foo : Bar, IBaz</c>
        /// </summary>
        private static readonly Regex TypeDeclRegex = new Regex(
            @"(?<![\w])(?<kind>class|interface|struct|record|trait|enum)\s+(?<name>[A-Za-z_]\w*)\s*(?:<[^>{]*>)?\s*(?::\s*(?<bases>[^{;]+))?",
            RegexOptions.CultureInvariant);

        private static readonly Regex TokenSplitRegex = new Regex(
            "[A-Za-z_][A-Za-z0-9_]*|[\u4e00-\u9fff]+",
            RegexOptions.CultureInvariant);

        private static readonly Regex CamelSplitRegex = new Regex(
            "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])",
            RegexOptions.CultureInvariant);

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "this", "that", "from", "not", "are", "was", "has", "have",
            "var", "let", "const", "public", "private", "protected", "internal", "static", "void",
            "return", "new", "class", "interface", "string", "int", "bool", "true", "false", "null",
            "using", "import", "export", "function", "async", "await", "def", "self", "end",
        };

        /// <summary>
        /// 全部索引根（首个为主根）：多根工作区会把所有根一起索引
        /// </summary>
        private readonly IList<string> _roots;

        /// <summary>
        /// 主根（用于兼容旧逻辑 / 诊断）
        /// </summary>
        private readonly string _root;

        /// <summary>
        /// 索引磁盘缓存路径（%LOCALAPPDATA%\LangCode\vsext\index\<root-hash>.idx.gz）
        /// </summary>
        private readonly string _cachePath;

        /// <summary>
        /// 缓存文件格式版本：结构变化时递增，旧缓存会被忽略
        /// </summary>
        private const int CacheFormatVersion = 1;

        private const string CacheMagic = "LCIDX1";

        /// <summary>
        /// 只保护「构建任务 / 快照发布」这件事；检索本身不加锁（读的是不可变快照）
        /// </summary>
        private readonly object _buildGate = new object();

        /// <summary>
        /// 建好的一份索引快照：发布后只读，检索线程可以无锁并发使用
        /// </summary>
        private sealed class IndexSnapshot
        {
            public List<CodeChunk> Chunks = new List<CodeChunk>();

            // 正文 / 符号名 / 路径三路倒排：混合检索分别打分后再融合，
            // 避免「符号名完全匹配」被大段注释里的词频淹没
            public Dictionary<string, List<int>> Postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            public Dictionary<string, int> DocumentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, List<int>> SymbolPostings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            public Dictionary<string, int> SymbolDocumentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, List<int>> PathPostings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            public Dictionary<string, int> PathDocumentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, int> PathFrequency = new Dictionary<string, int>(StringComparer.Ordinal);

            public List<SymbolNode> Symbols = new List<SymbolNode>();
            public Dictionary<string, List<SymbolNode>> SymbolsByName = new Dictionary<string, List<SymbolNode>>(StringComparer.Ordinal);
            public int ChunkCount = 1;
            public double AvgChunkLength = 1;
            public double AvgSymbolLength = 1;
            public double AvgPathLength = 1;
            public DateTime BuiltAtUtc = DateTime.MinValue;

            /// <summary>构建时的文件条目（用于在确认本次结果未被失效后再写缓存）</summary>
            public List<CachedFileIndex> Files = new List<CachedFileIndex>();

            /// <summary>本次构建是否产生了需要落盘的缓存变化</summary>
            public bool CacheChanged;
        }

        private volatile IndexSnapshot _snapshot;

        /// <summary>
        /// 当前的建索引任务（避免并发查询各建一次）
        /// </summary>
        private Task _buildTask;

        /// <summary>
        /// 版本号：<see cref="Invalidate"/> 时自增；构建期间被失效的结果会被丢弃
        /// </summary>
        private int _generation;

        /// <summary>
        /// 索引有效期：太长会让刚改的代码检索不到；太短则每次检索都重扫
        /// </summary>
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

        /// <summary>
        /// 改动后延迟多久在后台重建：连续编辑期间只重建最后一次
        /// </summary>
        private static readonly TimeSpan RebuildDelay = TimeSpan.FromSeconds(3);

        public SemanticIndex(string root) : this(root, null)
        {
        }

        /// <summary>
        /// 多根工作区：除主根外还会索引 <paramref name="extraRoots"/> 里的源码
        /// </summary>
        public SemanticIndex(string root, IEnumerable<string> extraRoots)
        {
            var roots = new List<string>();
            AddRoot(roots, root);

            if (extraRoots != null)
            {
                foreach (var extra in extraRoots)
                {
                    AddRoot(roots, extra);
                }
            }

            _roots = roots;
            _root = roots.Count > 0 ? roots[0] : string.Empty;
            _cachePath = BuildCachePath(roots);
        }

        private static void AddRoot(IList<string> roots, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(candidate).TrimEnd('\\', '/');
                if (full.Length > 0 && !roots.Any(existing => string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)))
                {
                    roots.Add(full);
                }
            }
            catch (Exception)
            {
                // 非法路径：忽略该根，不让整个索引不可用
            }
        }

        /// <summary>
        /// 全部索引根（首个为主根）
        /// </summary>
        public IList<string> Roots => _roots;

        /// <summary>
        /// 索引磁盘缓存位置（诊断用；未启用持久化时可能为 null）
        /// </summary>
        public string CachePath => _cachePath;

        private static string BuildCachePath(IList<string> roots)
        {
            if (roots == null || roots.Count == 0)
            {
                return null;
            }

            try
            {
                var key = string.Join("|", roots.Select(r => r.ToLowerInvariant()));
                using (var sha = SHA1.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                    var name = BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 16);
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "LangCode", "vsext", "index", name + ".idx.gz");
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 最多扫描的文件数（超大仓库的保护阀）
        /// </summary>
        public int MaxFiles { get; set; } = 2000;

        /// <summary>
        /// 单个片段的最大字符数
        /// </summary>
        public int MaxChunkChars { get; set; } = 1600;

        /// <summary>
        /// 文件读取上限（字符）
        /// </summary>
        public int MaxFileChars { get; set; } = 200_000;

        /// <summary>
        /// 可选向量检索：BM25 先召回候选，再用 embedding 重排；未配置时保持纯 BM25F
        /// </summary>
        public EmbeddingOptions Embedding { get; set; } = new EmbeddingOptions();

        private IEmbeddingProvider _embeddingProvider;
        private string _embeddingSignature = string.Empty;
        private DateTime _embeddingRetryAfterUtc = DateTime.MinValue;
        private readonly object _embeddingGate = new object();

        private Task _vectorIndexTask;
        private string _vectorIndexSignature = string.Empty;
        private readonly object _vectorIndexGate = new object();

        /// <summary>
        /// 片段文本哈希 → 向量（只缓存候选片段的向量，避免把整个仓库都嵌入一遍）
        /// </summary>
        private readonly ConcurrentDictionary<string, float[]> _vectorCache = new ConcurrentDictionary<string, float[]>(StringComparer.Ordinal);

        public bool IsReady => _roots != null && _roots.Any(Directory.Exists);

        /// <summary>
        /// 是否正在后台建索引（界面 / 自检可据此显示进度）
        /// </summary>
        public bool IsBuilding
        {
            get
            {
                lock (_buildGate)
                {
                    return _buildTask != null && !_buildTask.IsCompleted;
                }
            }
        }

        /// <summary>
        /// 后台预热：立即返回，建好后供后续检索使用
        /// </summary>
        /// <remarks>
        /// 打开工具窗口 / 切换解决方案时调用。首次 <c>semantic_search</c> 不必再等一次全仓库扫描，
        /// 而扫描本身也不会占用 UI 线程。
        /// </remarks>
        public void StartBackgroundBuild()
        {
            if (!IsReady)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    EnsureBuilt();
                }
                catch (Exception)
                {
                    // 建索引失败只影响检索质量：保持未就绪，下次查询会再试
                }
            });
        }

        /// <summary>
        /// 丢弃缓存
        /// </summary>
        /// <param name="rebuildInBackground">
        /// 是否在短暂防抖后自动后台重建。文件改动后应立即失效（避免读到旧代码），
        /// 但没必要让下一次检索再同步扫一遍仓库，所以默认在后台补建。
        /// </param>
        public void Invalidate(bool rebuildInBackground = true)
        {
            Interlocked.Increment(ref _generation);
            _snapshot = null;

            if (!rebuildInBackground || !IsReady)
            {
                return;
            }

            // 防抖：连续编辑只重建最后一次（旧的调度发现版本号变了就自动退出）
            var generation = Volatile.Read(ref _generation);
            Task.Delay(RebuildDelay).ContinueWith(
                _ =>
                {
                    if (Volatile.Read(ref _generation) != generation)
                    {
                        return;
                    }

                    try
                    {
                        EnsureBuilt();
                    }
                    catch (Exception)
                    {
                        // 后台重建失败：保持未就绪，下次检索时再试
                    }
                },
                TaskScheduler.Default);
        }

        /// <summary>
        /// 语义检索：返回与 <paramref name="query"/> 最相关的若干代码片段
        /// </summary>
        public ToolResult Search(string query, int limit)
        {
            if (!IsReady)
            {
                return Fail("未检测到工作区，无法做语义检索");
            }

            var terms = Tokenize(query);
            if (terms.Count == 0)
            {
                return Fail("查询词为空：请给出要查找的功能 / 符号 / 概念关键词");
            }

            // 先确保索引就绪（后台已在建则只等它，不重复扫描），之后全程读不可变快照
            var snapshot = EnsureBuilt();
            var chunks = snapshot.Chunks;
            if (chunks.Count == 0)
            {
                return Fail("索引为空：工作区里没有可索引的源码文件");
            }

            var top = limit <= 0 ? 8 : limit;
            var scores = new Dictionary<int, double>();
            var unique = terms.Distinct(StringComparer.Ordinal).ToList();
            var queryText = (query ?? string.Empty).Trim();

            // 混合检索：正文 / 符号名 / 路径三路分别做 BM25 打分后累加。
            // 只看正文时，符号名完全匹配合容易被大段注释里的高频词淹没；
            // 单独给符号名与路径更高的权重，能让「找 AgentRunner 这个类」直接命中定义。
            foreach (var term in unique)
            {
                AccumulateField(
                    snapshot.Postings, snapshot.DocumentFrequency, chunks, term,
                    weight: 1.0,
                    fieldOf: chunk => chunk.Text,
                    lengthOf: chunk => chunk.Text.Length / 4.0,
                    averageLength: snapshot.AvgChunkLength,
                    chunkCount: snapshot.ChunkCount,
                    scores: scores);

                AccumulateField(
                    snapshot.SymbolPostings, snapshot.SymbolDocumentFrequency, chunks, term,
                    weight: 3.0,
                    fieldOf: chunk => chunk.Symbol,
                    lengthOf: chunk => Math.Max(1, chunk.Symbol.Length / 4.0),
                    averageLength: snapshot.AvgSymbolLength,
                    chunkCount: snapshot.ChunkCount,
                    scores: scores);

                AccumulateField(
                    snapshot.PathPostings, snapshot.PathDocumentFrequency, chunks, term,
                    weight: 1.6,
                    fieldOf: chunk => chunk.Path,
                    lengthOf: chunk => Math.Max(1, chunk.Path.Length / 4.0),
                    averageLength: snapshot.AvgPathLength,
                    chunkCount: snapshot.ChunkCount,
                    scores: scores);
            }

            // 结构信号：整段查询命中正文 / 符号名时再加权（例如直接查 "AgentRunner"）
            if (queryText.Length >= 2)
            {
                foreach (var index in scores.Keys.ToList())
                {
                    var chunk = chunks[index];

                    if (chunk.Text.IndexOf(queryText, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        scores[index] *= 1.6;
                    }

                    if (chunk.Symbol.Length > 0)
                    {
                        if (string.Equals(chunk.Symbol, queryText, StringComparison.OrdinalIgnoreCase))
                        {
                            scores[index] *= 2.2;
                        }
                        else if (chunk.Symbol.IndexOf(queryText, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            scores[index] *= 1.5;
                        }
                    }
                }
            }

            // 兜底：分词完全没命中（标点 / 长中文短语）时按子串再扫一遍，避免直接返回空
            if (scores.Count == 0 && queryText.Length >= 2)
            {
                for (var i = 0; i < chunks.Count; i++)
                {
                    var text = chunks[i].Text;
                    foreach (var term in unique)
                    {
                        if (term.Length >= 2 && text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            scores[i] = 0.5;
                            break;
                        }
                    }
                }
            }

            if (scores.Count == 0)
            {
                return Ok("没有找到相关代码片段（可换个关键词，或先用 search_text 精确匹配）");
            }

            // BM25 先多召回一些候选，再交给可选的 embedding 重排（只对候选做向量，成本可控）
            var candidates = scores
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => chunks[kv.Key].Path, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(top * 4, 24))
                .ToList();

            var ranked = RerankWithEmbeddings(queryText, chunks, candidates, top, out var embeddingNote, out var embeddingUsed);

            var builder = new StringBuilder();
            builder.Append("混合检索结果（“").Append((query ?? string.Empty).Trim()).Append("”，共 ")
                   .Append(ranked.Count).Append(embeddingUsed
                       ? " 段，正文 / 符号 / 路径三路加权 + 向量重排）：\n\n"
                       : " 段，正文 / 符号 / 路径三路加权）：\n\n");

            if (!string.IsNullOrWhiteSpace(embeddingNote))
            {
                builder.Append(embeddingNote).Append("\n\n");
            }

            var rank = 0;
            foreach (var entry in ranked)
            {
                var chunk = chunks[entry.Key];
                builder.Append("── #").Append(++rank).Append(" · ").Append(chunk.Path);
                if (chunk.StartLine > 0)
                {
                    builder.Append(':').Append(chunk.StartLine);
                }

                if (chunk.Symbol.Length > 0)
                {
                    builder.Append(" · ").Append(chunk.Kind).Append(' ').Append(chunk.Symbol);
                }

                builder.Append("（相关度 ").Append(entry.Value.ToString("0.00")).Append("）\n");
                builder.Append(TrimSnippet(chunk.Text)).Append("\n\n");
            }

            builder.Append("提示：需要完整内容时用 read_file 读取上面对应的文件与行号");
            return Ok(builder.ToString());
        }

        /// <summary>
        /// 可选向量重排：BM25 候选 → 逐条 embedding → 余弦相似度与 BM25 归一化分融合。
        /// 未配置 / 端点不可用 / 调用失败时原样返回 BM25 结果，并给出一句可读的回退说明。
        /// </summary>
        private IList<KeyValuePair<int, double>> RerankWithEmbeddings(
            string queryText,
            IList<CodeChunk> chunks,
            IList<KeyValuePair<int, double>> candidates,
            int top,
            out string note,
            out bool used)
        {
            note = string.Empty;
            used = false;

            var options = Embedding;
            if (options == null || !options.IsEnabled || candidates.Count <= 1)
            {
                return candidates.Take(top).ToList();
            }

            if (DateTime.UtcNow < _embeddingRetryAfterUtc)
            {
                note = "（向量重排暂时冷却：上次调用失败，稍后自动重试；已回退 BM25）";
                return candidates.Take(top).ToList();
            }

            var provider = GetEmbeddingProvider();
            if (provider == null || !provider.IsAvailable)
            {
                note = "（向量检索不可用：" + (provider?.LastError ?? "未配置") + "；已回退 BM25）";
                return candidates.Take(top).ToList();
            }

            try
            {
#pragma warning disable VSTHRD002
                var queryVector = provider
                    .EmbedAsync(new[] { queryText }, CancellationToken.None)
                    .GetAwaiter().GetResult()?
                    .FirstOrDefault();
#pragma warning restore VSTHRD002

                if (queryVector == null || queryVector.Length == 0)
                {
                    throw new InvalidOperationException("查询向量为空");
                }

                // 只为还没有向量的候选补一次批量 embedding
                var missing = new List<KeyValuePair<int, double>>();
                foreach (var candidate in candidates)
                {
                    if (!_vectorCache.ContainsKey(VectorKey(chunks[candidate.Key])))
                    {
                        missing.Add(candidate);
                    }
                }

                if (missing.Count > 0)
                {
                    var texts = missing.Select(item => EmbedText(chunks[item.Key])).ToList();
#pragma warning disable VSTHRD002
                    var vectors = provider
                        .EmbedAsync(texts, CancellationToken.None)
                        .GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

                    for (var i = 0; i < missing.Count && i < vectors.Count; i++)
                    {
                        if (vectors[i] != null && vectors[i].Length > 0)
                        {
                            _vectorCache[VectorKey(chunks[missing[i].Key])] = vectors[i];
                        }
                    }

                    // 简单容量保护：向量只用于当前会话，过多时整体丢弃重建
                    if (_vectorCache.Count > 5000)
                    {
                        _vectorCache.Clear();
                    }
                }

                // 后台慢慢把更多片段也嵌入：后续查询就能做「向量召回」，
                // 不再局限于 BM25 已经召回的候选
                StartVectorIndexBuild(chunks, provider, options);

                var maxBm25 = candidates.Max(item => item.Value);
                var weight = Math.Max(0, Math.Min(1, options.Weight));
                var fused = new Dictionary<int, double>(candidates.Count);

                foreach (var candidate in candidates)
                {
                    var chunk = chunks[candidate.Key];
                    if (!_vectorCache.TryGetValue(VectorKey(chunk), out var vector))
                    {
                        continue;
                    }

                    var bm25 = maxBm25 > 0 ? candidate.Value / maxBm25 : 0;
                    var cosine = Cosine(queryVector, vector);
                    var semantic = (cosine + 1.0) / 2.0; // [-1,1] → [0,1]

                    fused[candidate.Key] = ((1 - weight) * bm25) + (weight * semantic);
                }

                // 向量召回：只补 BM25 没覆盖到的片段（余弦太低的不进结果，避免噪声）
                for (var i = 0; i < chunks.Count; i++)
                {
                    if (fused.ContainsKey(i))
                    {
                        continue;
                    }

                    if (!_vectorCache.TryGetValue(VectorKey(chunks[i]), out var vector))
                    {
                        continue;
                    }

                    var cosine = Cosine(queryVector, vector);
                    if (cosine >= 0.15)
                    {
                        fused[i] = weight * ((cosine + 1.0) / 2.0);
                    }
                }

                if (fused.Count == 0)
                {
                    return candidates.Take(top).ToList();
                }

                used = true;
                _embeddingRetryAfterUtc = DateTime.MinValue;
                return fused
                    .OrderByDescending(item => item.Value)
                    .ThenBy(item => chunks[item.Key].Path, StringComparer.OrdinalIgnoreCase)
                    .Take(top)
                    .ToList();
            }
            catch (Exception ex)
            {
                // 端点不可达 / 限流时不反复打：冷却 2 分钟，期间直接走 BM25
                _embeddingRetryAfterUtc = DateTime.UtcNow.AddMinutes(2);
                note = "（向量重排失败，已回退 BM25：" + ex.GetBaseException().Message + "）";
                return candidates.Take(top).ToList();
            }
        }

        /// <summary>
        /// 后台分批为片段建立向量（最多 <see cref="EmbeddingOptions.MaxChunks"/> 个）。
        /// 不阻塞当前检索：本次先用已有向量，之后的查询逐步获得完整的向量召回。
        /// </summary>
        private void StartVectorIndexBuild(IList<CodeChunk> chunks, IEmbeddingProvider provider, EmbeddingOptions options)
        {
            if (chunks == null || provider == null || options == null || options.MaxChunks <= 0)
            {
                return;
            }

            var signature = _embeddingSignature;
            lock (_vectorIndexGate)
            {
                if (_vectorIndexTask != null &&
                    (string.Equals(_vectorIndexSignature, signature, StringComparison.Ordinal) || !_vectorIndexTask.IsCompleted))
                {
                    return;
                }

                _vectorIndexSignature = signature;
                _vectorIndexTask = Task.Run(() => BuildVectorIndex(chunks, provider, options));
            }
        }

        private void BuildVectorIndex(IList<CodeChunk> chunks, IEmbeddingProvider provider, EmbeddingOptions options)
        {
            try
            {
                var limit = Math.Min(chunks.Count, Math.Max(1, options.MaxChunks));
                var batch = new List<CodeChunk>(16);

                for (var i = 0; i < limit; i++)
                {
                    var chunk = chunks[i];
                    if (_vectorCache.ContainsKey(VectorKey(chunk)))
                    {
                        continue;
                    }

                    batch.Add(chunk);
                    if (batch.Count >= 16)
                    {
                        FlushVectorBatch(batch, provider);
                        batch.Clear();
                    }
                }

                if (batch.Count > 0)
                {
                    FlushVectorBatch(batch, provider);
                }
            }
            catch (Exception)
            {
                // 后台建向量失败不影响检索：下次换配置或重启再试
            }
        }

        private void FlushVectorBatch(IList<CodeChunk> batch, IEmbeddingProvider provider)
        {
            try
            {
                var texts = batch.Select(EmbedText).ToList();
#pragma warning disable VSTHRD002
                var vectors = provider
                    .EmbedAsync(texts, CancellationToken.None)
                    .GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

                for (var i = 0; i < batch.Count && i < vectors.Count; i++)
                {
                    if (vectors[i] != null && vectors[i].Length > 0)
                    {
                        _vectorCache[VectorKey(batch[i])] = vectors[i];
                    }
                }
            }
            catch (Exception)
            {
                // 单批失败：停止后台建索引（由 _vectorIndexTask 的完成状态避免反复重试）
                throw;
            }
        }

        private IEmbeddingProvider GetEmbeddingProvider()
        {
            var options = Embedding;
            if (options == null || !options.IsEnabled)
            {
                return null;
            }

            var signature = string.Join("|", new[]
            {
                options.Provider ?? string.Empty,
                options.Endpoint ?? string.Empty,
                options.Model ?? string.Empty,
                options.ModelPath ?? string.Empty,
                options.VocabPath ?? string.Empty,
                options.MaxTokens.ToString(),
            });

            lock (_embeddingGate)
            {
                if (_embeddingProvider != null && string.Equals(_embeddingSignature, signature, StringComparison.Ordinal))
                {
                    return _embeddingProvider;
                }

                try
                {
                    _embeddingProvider?.Dispose();
                }
                catch (Exception)
                {
                    // 旧 provider 释放失败不影响新建
                }

                _embeddingSignature = signature;
                _embeddingProvider = EmbeddingProviderFactory.Create(options);
                _embeddingRetryAfterUtc = DateTime.MinValue;
                _vectorCache.Clear();
                return _embeddingProvider;
            }
        }

        private static string VectorKey(CodeChunk chunk)
        {
            return StableHash((chunk.Path ?? string.Empty) + "\u0001" + (chunk.Symbol ?? string.Empty) + "\u0001" + (chunk.Text ?? string.Empty))
                .ToString("x16");
        }

        private static string EmbedText(CodeChunk chunk)
        {
            var text = (chunk.Path ?? string.Empty) + "\n" + (chunk.Symbol ?? string.Empty) + "\n" + (chunk.Text ?? string.Empty);
            return text.Length <= 2000 ? text : text.Substring(0, 2000);
        }

        private static double Cosine(float[] left, float[] right)
        {
            if (left == null || right == null || left.Length == 0 || right.Length == 0)
            {
                return 0;
            }

            var length = Math.Min(left.Length, right.Length);
            double dot = 0;
            double leftNorm = 0;
            double rightNorm = 0;

            for (var i = 0; i < length; i++)
            {
                dot += left[i] * right[i];
                leftNorm += left[i] * left[i];
                rightNorm += right[i] * right[i];
            }

            if (leftNorm <= 0.0000001 || rightNorm <= 0.0000001)
            {
                return 0;
            }

            return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
        }

        /// <summary>
        /// 单字段 BM25 打分累加：正文 / 符号名 / 路径各自使用自己的平均长度与权重
        /// </summary>
        private static void AccumulateField(
            Dictionary<string, List<int>> postings,
            Dictionary<string, int> documentFrequency,
            IList<CodeChunk> chunks,
            string term,
            double weight,
            Func<CodeChunk, string> fieldOf,
            Func<CodeChunk, double> lengthOf,
            double averageLength,
            int chunkCount,
            Dictionary<int, double> scores)
        {
            if (postings == null || !postings.TryGetValue(term, out var posting) || posting.Count == 0)
            {
                return;
            }

            documentFrequency.TryGetValue(term, out var df);
            df = Math.Max(1, df > 0 ? df : posting.Count);

            var idf = Math.Log(1.0 + (chunkCount - df + 0.5) / (df + 0.5));
            const double k1 = 1.4;
            const double b = 0.72;
            var avg = Math.Max(1.0, averageLength);

            foreach (var chunkIndex in posting)
            {
                var chunk = chunks[chunkIndex];
                var tf = CountTerm(fieldOf(chunk), term);
                if (tf == 0)
                {
                    continue;
                }

                var dl = Math.Max(1.0, lengthOf(chunk));
                var denom = tf + k1 * (1 - b + b * dl / avg);
                var score = weight * idf * (tf * (k1 + 1)) / Math.Max(0.0001, denom);

                scores.TryGetValue(chunkIndex, out var existing);
                scores[chunkIndex] = existing + score;
            }
        }

        /// <summary>
        /// 查询某个类型的继承链（基类 / 实现的接口，含已知的派生类型）
        /// </summary>
        public ToolResult TypeHierarchy(string typeName)
        {
            var name = ExtractTypeName(typeName);
            if (name.Length == 0)
            {
                return Fail("请给出类型名");
            }

            if (!IsReady)
            {
                return Fail("未检测到工作区");
            }

            var snapshot = EnsureBuilt();

            if (!snapshot.SymbolsByName.TryGetValue(name, out var nodes) || nodes.Count == 0)
            {
                return Ok("没有在索引里找到类型 " + name + "（可能未被识别为 class/interface/struct）");
            }

            var builder = new StringBuilder();
            foreach (var node in nodes)
            {
                builder.Append("类型 ").Append(node.Name).Append("（").Append(node.Kind).Append("，")
                       .Append(node.Path).Append(':').Append(node.Line).Append("）\n");

                builder.Append("  基类 / 接口：")
                       .Append(node.Bases.Count == 0 ? "（无）" : string.Join(", ", node.Bases))
                       .Append('\n');

                // 派生类型：其它类型的 Bases 里出现了它
                var derived = snapshot.Symbols
                    .Where(s => s.Bases.Any(b => string.Equals(b, node.Name, StringComparison.Ordinal)))
                    .Select(s => s.Name + " (" + s.Path + ")")
                    .Distinct()
                    .ToList();

                builder.Append("  已知派生 / 实现：")
                       .Append(derived.Count == 0 ? "（无）" : string.Join(", ", derived))
                       .Append('\n');
            }

            return Ok(builder.ToString().TrimEnd());
        }

        /// <summary>
        /// 查找实现了某个接口 / 继承了某个基类的全部类型
        /// </summary>
        public ToolResult Implementations(string typeName)
        {
            var name = ExtractTypeName(typeName);
            if (name.Length == 0)
            {
                return Fail("请给出接口 / 基类名");
            }

            if (!IsReady)
            {
                return Fail("未检测到工作区");
            }

            var snapshot = EnsureBuilt();

            var matches = snapshot.Symbols
                .Where(s => s.Bases.Any(b => string.Equals(b, name, StringComparison.Ordinal)))
                .ToList();

            if (matches.Count == 0)
            {
                return Ok("没有找到实现 / 继承 " + name + " 的类型（可能名字拼写不同，或该类型只在外部程序集里）");
            }

            var builder = new StringBuilder();
            builder.Append("实现 / 继承 ").Append(name).Append(" 的类型（").Append(matches.Count).Append(" 个）：\n");
            foreach (var node in matches)
            {
                builder.Append("  - ").Append(node.Kind).Append(' ').Append(node.Name)
                       .Append("  ").Append(node.Path).Append(':').Append(node.Line).Append('\n');
            }

            return Ok(builder.ToString().TrimEnd());
        }

        /// <summary>
        /// 索引规模摘要（供界面 / 自检显示）
        /// </summary>
        public string Describe()
        {
            var snapshot = EnsureBuilt();
            return "语义索引：" + snapshot.ChunkCount + " 个片段，" + snapshot.Postings.Count + " 个词项，" +
                   snapshot.Symbols.Count + " 个类型节点";
        }

        // ==================== 建索引 ====================

        /// <summary>
        /// 取一份可用快照：已有且未过期就直接用；否则（必要时等待）建一次
        /// </summary>
        /// <remarks>
        /// 多个检索线程同时打进来时只会有一个真正建索引，其余等待同一个任务；
        /// 等待期间<b>不持有任何锁</b>，构建线程可以正常发布结果。
        /// </remarks>
        private IndexSnapshot EnsureBuilt()
        {
            while (true)
            {
                var snapshot = _snapshot;
                if (snapshot != null && DateTime.UtcNow - snapshot.BuiltAtUtc < Ttl)
                {
                    return snapshot;
                }

                Task build;
                lock (_buildGate)
                {
                    snapshot = _snapshot;
                    if (snapshot != null && DateTime.UtcNow - snapshot.BuiltAtUtc < Ttl)
                    {
                        return snapshot;
                    }

                    if (_buildTask == null || _buildTask.IsCompleted)
                    {
                        _buildTask = Task.Run((Action)BuildAndPublish);
                    }

                    build = _buildTask;
                }

                try
                {
                    // 索引查询是同步 API，且只会在后台线程调用；这里等待后台建索引完成是刻意设计
#pragma warning disable VSTHRD002
                    build.Wait();
#pragma warning restore VSTHRD002
                }
                catch (AggregateException)
                {
                    // 构建失败：返回空快照，避免把调用方卡在循环里（下次查询会重试）
                    return _snapshot ?? EmptySnapshot();
                }
            }
        }

        /// <summary>
        /// 后台建索引并发布；构建期间被失效（工作区改过）时丢弃这次结果
        /// </summary>
        private void BuildAndPublish()
        {
            var generation = Volatile.Read(ref _generation);
            var snapshot = BuildSnapshot();

            // 构建期间文件又被改过：丢弃本次结果，也**不要**写缓存
            // （否则旧快照可能覆盖并发构建刚写好的新缓存）
            if (Volatile.Read(ref _generation) != generation)
            {
                return;
            }

            if (snapshot.CacheChanged)
            {
                SaveCache(snapshot.Files);
            }

            _snapshot = snapshot;
        }

        private IndexSnapshot BuildSnapshot()
        {
            var chunks = new List<CodeChunk>();
            var symbols = new List<SymbolNode>();

            // 先尝试磁盘缓存：只对「mtime / size 变了」的文件重新读取与分块，
            // 未变化的文件直接复用上次的片段与符号（大仓库冷启动的主要收益）
            var cachedFiles = TryLoadCache();
            var cachedByKey = new Dictionary<string, CachedFileIndex>(StringComparer.OrdinalIgnoreCase);
            if (cachedFiles != null)
            {
                foreach (var entry in cachedFiles)
                {
                    cachedByKey[entry.RootIndex + "|" + entry.RelativePath] = entry;
                }
            }

            var fileEntries = new List<CachedFileIndex>();
            var changed = cachedFiles == null || cachedFiles.Count == 0;

            foreach (var file in EnumerateSourceFiles())
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(file.FullPath);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                var key = file.RootIndex + "|" + file.RelativePath;
                cachedByKey.TryGetValue(key, out var cachedEntry);

                // 版本没变：直接复用缓存里的片段与符号
                if (cachedEntry != null && cachedEntry.Size == info.Length && cachedEntry.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
                {
                    fileEntries.Add(cachedEntry);
                    chunks.AddRange(cachedEntry.Chunks);
                    symbols.AddRange(cachedEntry.Symbols);
                    continue;
                }

                string text;
                try
                {
                    text = ReadTextCapped(file.FullPath);
                }
                catch (Exception)
                {
                    continue;
                }

                var hash = StableHash(text);

                // mtime 变了但内容没变（保存时只动了时间戳）：复用旧片段，只更新时间戳
                if (cachedEntry != null && cachedEntry.Hash == hash)
                {
                    cachedEntry.Size = info.Length;
                    cachedEntry.LastWriteTicks = info.LastWriteTimeUtc.Ticks;
                    fileEntries.Add(cachedEntry);
                    chunks.AddRange(cachedEntry.Chunks);
                    symbols.AddRange(cachedEntry.Symbols);
                    changed = true;
                    continue;
                }

                var fresh = new CachedFileIndex
                {
                    RootIndex = file.RootIndex,
                    RelativePath = file.RelativePath,
                    Size = info.Length,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    Hash = hash,
                };

                CollectSymbols(file.RelativePath, text, fresh.Symbols);
                fresh.Chunks.AddRange(ChunkFile(file.RelativePath, text));

                fileEntries.Add(fresh);
                chunks.AddRange(fresh.Chunks);
                symbols.AddRange(fresh.Symbols);
                changed = true;
            }

            // 文件被删掉也会让缓存条目数量变化
            if (cachedFiles != null && cachedFiles.Count != fileEntries.Count)
            {
                changed = true;
            }

            // 正文字段倒排：词 → 片段下标
            var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var df = new Dictionary<string, int>(StringComparer.Ordinal);

            // 符号名 / 路径字段倒排：混合检索的两路结构信号
            var symbolPostings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var symbolDf = new Dictionary<string, int>(StringComparer.Ordinal);
            var pathPostings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var pathDf = new Dictionary<string, int>(StringComparer.Ordinal);
            var pathFreq = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < chunks.Count; i++)
            {
                var terms = Tokenize(chunks[i].Text).Distinct(StringComparer.Ordinal);
                foreach (var term in terms)
                {
                    if (!postings.TryGetValue(term, out var list))
                    {
                        list = [];
                        postings[term] = list;
                    }

                    list.Add(i);
                    df.TryGetValue(term, out var count);
                    df[term] = count + 1;
                }

                foreach (var term in Tokenize(chunks[i].Symbol).Distinct(StringComparer.Ordinal))
                {
                    if (!symbolPostings.TryGetValue(term, out var symbolList))
                    {
                        symbolList = [];
                        symbolPostings[term] = symbolList;
                    }

                    symbolList.Add(i);
                    symbolDf.TryGetValue(term, out var symbolCount);
                    symbolDf[term] = symbolCount + 1;
                }

                foreach (var term in Tokenize(chunks[i].Path).Distinct(StringComparer.Ordinal))
                {
                    if (!pathPostings.TryGetValue(term, out var pathList))
                    {
                        pathList = [];
                        pathPostings[term] = pathList;
                    }

                    pathList.Add(i);

                    pathDf.TryGetValue(term, out var pathCount);
                    pathDf[term] = pathCount + 1;

                    pathFreq.TryGetValue(term, out var count);
                    pathFreq[term] = count + 1;
                }
            }

            return new IndexSnapshot
            {
                Chunks = chunks,
                Files = fileEntries,
                CacheChanged = changed,
                Postings = postings,
                DocumentFrequency = df,
                SymbolPostings = symbolPostings,
                SymbolDocumentFrequency = symbolDf,
                PathPostings = pathPostings,
                PathDocumentFrequency = pathDf,
                PathFrequency = pathFreq,
                Symbols = symbols,
                SymbolsByName = symbols
                    .GroupBy(s => s.Name, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal),
                ChunkCount = Math.Max(1, chunks.Count),
                AvgChunkLength = AverageFieldLength(chunks, c => c.Text),
                AvgSymbolLength = AverageFieldLength(chunks, c => c.Symbol),
                AvgPathLength = AverageFieldLength(chunks, c => c.Path),
                BuiltAtUtc = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// 字段平均长度（按 4 字符 ≈ 1 token 折算）：BM25 的长度归一化用
        /// </summary>
        private static double AverageFieldLength(IList<CodeChunk> chunks, Func<CodeChunk, string> field)
        {
            if (chunks == null || chunks.Count == 0)
            {
                return 1;
            }

            double total = 0;
            var count = 0;
            foreach (var chunk in chunks)
            {
                var text = field(chunk) ?? string.Empty;
                if (text.Length == 0)
                {
                    continue;
                }

                total += Math.Max(1, text.Length / 4.0);
                count++;
            }

            return count == 0 ? 1 : Math.Max(1, total / count);
        }

        /// <summary>
        /// 构建失败时的占位快照：<see cref="IndexSnapshot.BuiltAtUtc"/> 保持最小值，下次调用会重试
        /// </summary>
        private static IndexSnapshot EmptySnapshot()
        {
            return new IndexSnapshot();
        }

        /// <summary>
        /// 把一个文件切成若干片段：按符号边界切，并限制单段长度
        /// </summary>
        private IEnumerable<CodeChunk> ChunkFile(string relativePath, string text)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var symbolLines = SymbolScanner.Scan(text)
                .GroupBy(s => s.Line)
                .ToDictionary(g => g.Key, g => g.First());

            var boundaries = symbolLines.Keys.OrderBy(x => x).ToList();
            if (boundaries.Count == 0 || boundaries[0] > 1)
            {
                boundaries.Insert(0, 1);
            }

            for (var b = 0; b < boundaries.Count; b++)
            {
                var start = boundaries[b];
                var end = b + 1 < boundaries.Count ? boundaries[b + 1] - 1 : lines.Length;
                if (end < start)
                {
                    end = start;
                }

                symbolLines.TryGetValue(start, out var symbol);

                // 单段过长时再按固定行数切开，避免一段就是整个文件
                const int linesPerChunk = 60;
                for (var s = start; s <= end; s += linesPerChunk)
                {
                    var e = Math.Min(end, s + linesPerChunk - 1);
                    var body = SliceLines(lines, s, e);
                    if (body.Trim().Length == 0)
                    {
                        continue;
                    }

                    if (body.Length > MaxChunkChars)
                    {
                        body = body.Substring(0, MaxChunkChars);
                    }

                    yield return new CodeChunk
                    {
                        Path = relativePath,
                        StartLine = s,
                        EndLine = e,
                        Symbol = symbol?.Name ?? string.Empty,
                        Kind = symbol?.Kind ?? "文件",
                        Text = body,
                    };
                }
            }
        }

        private static string SliceLines(string[] lines, int from, int to)
        {
            var builder = new StringBuilder();
            for (var i = from; i <= to && i <= lines.Length; i++)
            {
                builder.Append(lines[i - 1]).Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>
        /// 收集类型 → 基类 / 接口关系
        /// </summary>
        private static void CollectSymbols(string relativePath, string text, List<SymbolNode> sink)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                var match = TypeDeclRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                if (name.Length == 0)
                {
                    continue;
                }

                var node = new SymbolNode
                {
                    Name = name,
                    Kind = match.Groups["kind"].Value,
                    Path = relativePath,
                    Line = i + 1,
                };

                // 基类可能跨到下一行（`class Foo\n : Bar`）：往后看两行拼起来
                var basesText = match.Groups["bases"].Success ? match.Groups["bases"].Value : string.Empty;
                if (string.IsNullOrWhiteSpace(basesText) && i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith(":", StringComparison.Ordinal))
                {
                    basesText = lines[i + 1].TrimStart().Substring(1);
                }

                node.Bases = ParseBases(basesText);
                sink.Add(node);
            }
        }

        private static List<string> ParseBases(string basesText)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(basesText))
            {
                return result;
            }

            // 去掉泛型参数、构造函数约束与注释
            var cleaned = Regex.Replace(basesText, "<[^<>]*>", string.Empty, RegexOptions.CultureInvariant);
            cleaned = cleaned.Split('{')[0];
            var whereIndex = cleaned.IndexOf(" where ", StringComparison.OrdinalIgnoreCase);
            if (whereIndex >= 0)
            {
                cleaned = cleaned.Substring(0, whereIndex);
            }

            foreach (var part in cleaned.Split(','))
            {
                var name = ExtractTypeName(part);
                if (name.Length > 0 && !IsLikelyKeyword(name))
                {
                    result.Add(name);
                }
            }

            return result;
        }

        private static bool IsLikelyKeyword(string name)
        {
            return name == "where" || name == "new" || name == "base" || name == "object";
        }

        /// <summary>
        /// 从「IMyService<T>」这类文本里取纯类型名
        /// </summary>
        private static string ExtractTypeName(string text)
        {
            var value = (text ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return string.Empty;
            }

            // 允许传 "IFoo<int>" 或 "Foo" 或 "namespace.Foo"
            value = value.TrimEnd(';', ',', '{', '}').Trim();
            var generic = value.IndexOf('<');
            if (generic >= 0)
            {
                value = value.Substring(0, generic);
            }

            var dot = value.LastIndexOf('.');
            if (dot >= 0)
            {
                value = value.Substring(dot + 1);
            }

            var match = Regex.Match(value, "[A-Za-z_][A-Za-z0-9_]*");
            return match.Success ? match.Value : string.Empty;
        }

        // ==================== 分词 / 打分辅助 ====================

        /// <summary>
        /// 分词：按标识符切分，再拆驼峰 / 下划线，去停用词
        /// <c>GetUserById</c> → get / user / by / id
        /// </summary>
        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return tokens;
            }

            foreach (Match match in TokenSplitRegex.Matches(text))
            {
                var word = match.Value;
                if (word.Length == 0 || word[0] > 0x7f)
                {
                    // 中文没有空格：整段 + 二元组一起入索引。
                    // 只保留整段会导致「登录」查不到「登录表单校验」——二元组让子串查询也能命中
                    AddCjkTokens(word, tokens);
                    continue;
                }

                foreach (var part in CamelSplitRegex.Split(word))
                {
                    var lower = part.ToLowerInvariant();
                    if (lower.Length < 2 || StopWords.Contains(lower))
                    {
                        continue;
                    }

                    tokens.Add(lower);
                }
            }

            return tokens;
        }

        /// <summary>
        /// 中文分词：保留完整片段（精确短语），再补全部二元组（子串召回）
        /// </summary>
        private static void AddCjkTokens(string word, List<string> tokens)
        {
            if (string.IsNullOrEmpty(word))
            {
                return;
            }

            tokens.Add(word);

            if (word.Length <= 2)
            {
                return;
            }

            for (var i = 0; i + 1 < word.Length; i++)
            {
                tokens.Add(word.Substring(i, 2));
            }
        }

        /// <summary>
        /// 出现次数（不区分大小写的子串计数，够用于打分）
        /// </summary>
        private static int CountTerm(string text, string term)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
            {
                return 0;
            }

            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += term.Length;
            }

            return count;
        }

        private static string TrimSnippet(string text)
        {
            var value = (text ?? string.Empty).TrimEnd();
            return value.Length <= 1200 ? value : value.Substring(0, 1200) + "\n…（片段过长，已截断；请用 read_file 读取完整内容）";
        }

        // ==================== 文件枚举 / 位置 ====================

        /// <summary>
        /// 枚举到的一个源文件（根序号 + 完整路径 + 根内相对路径）
        /// </summary>
        private sealed class SourceFile
        {
            public int RootIndex;
            public string FullPath = string.Empty;
            public string RelativePath = string.Empty;
        }

        /// <summary>
        /// 磁盘缓存里的一个文件条目：版本信息 + 上次切好的片段与符号
        /// </summary>
        private sealed class CachedFileIndex
        {
            public int RootIndex;
            public string RelativePath = string.Empty;
            public long LastWriteTicks;
            public long Size;
            public ulong Hash;
            public List<CodeChunk> Chunks = new List<CodeChunk>();
            public List<SymbolNode> Symbols = new List<SymbolNode>();
        }

        private IEnumerable<SourceFile> EnumerateSourceFiles()
        {
            var count = 0;

            for (var rootIndex = 0; rootIndex < _roots.Count; rootIndex++)
            {
                var root = _roots[rootIndex];
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var pending = new Queue<string>();
                pending.Enqueue(root);

                while (pending.Count > 0)
                {
                    var current = pending.Dequeue();

                    string[] directories;
                    try
                    {
                        directories = Directory.GetDirectories(current);
                        Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                    }
                    catch (Exception)
                    {
                        directories = Array.Empty<string>();
                    }

                    foreach (var directory in directories)
                    {
                        var name = Path.GetFileName(directory);
                        if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        pending.Enqueue(directory);
                    }

                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(current);
                        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (var file in files)
                    {
                        if (!SourceExtensions.Contains(Path.GetExtension(file)))
                        {
                            continue;
                        }

                        if (++count > MaxFiles)
                        {
                            yield break;
                        }

                        yield return new SourceFile
                        {
                            RootIndex = rootIndex,
                            FullPath = file,
                            RelativePath = RelativeTo(file, root),
                        };
                    }
                }
            }
        }

        private static string RelativeTo(string full, string root)
        {
            var prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/')
                : full.Replace(Path.DirectorySeparatorChar, '/');
        }

        private string ReadTextCapped(string full)
        {
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                var buffer = new char[MaxFileChars];
                var read = reader.ReadBlock(buffer, 0, buffer.Length);
                return new string(buffer, 0, read);
            }
        }

        /// <summary>
        /// FNV-1a 64 位：只用于判断文件内容是否变化，不做安全用途
        /// </summary>
        private static ulong StableHash(string text)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            var hash = offset;
            var value = text ?? string.Empty;
            for (var i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= prime;
            }

            return hash;
        }

        // ==================== 索引磁盘缓存 ====================

        private List<CachedFileIndex> TryLoadCache()
        {
            if (string.IsNullOrEmpty(_cachePath) || !File.Exists(_cachePath))
            {
                return null;
            }

            try
            {
                using (var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                using (var reader = new BinaryReader(gzip, Encoding.UTF8))
                {
                    if (!string.Equals(reader.ReadString(), CacheMagic, StringComparison.Ordinal) ||
                        reader.ReadInt32() != CacheFormatVersion ||
                        reader.ReadInt32() != MaxFiles ||
                        reader.ReadInt32() != MaxChunkChars)
                    {
                        return null;
                    }

                    var rootCount = reader.ReadInt32();
                    if (rootCount != _roots.Count)
                    {
                        return null;
                    }

                    for (var i = 0; i < rootCount; i++)
                    {
                        if (!string.Equals(reader.ReadString(), _roots[i], StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }
                    }

                    var fileCount = reader.ReadInt32();
                    if (fileCount < 0 || fileCount > MaxFiles + 1000)
                    {
                        return null;
                    }

                    var result = new List<CachedFileIndex>(fileCount);
                    for (var i = 0; i < fileCount; i++)
                    {
                        var entry = new CachedFileIndex
                        {
                            RootIndex = reader.ReadInt32(),
                            RelativePath = reader.ReadString() ?? string.Empty,
                            LastWriteTicks = reader.ReadInt64(),
                            Size = reader.ReadInt64(),
                            Hash = reader.ReadUInt64(),
                        };

                        var symbolCount = reader.ReadInt32();
                        if (symbolCount < 0 || symbolCount > 100000)
                        {
                            return null;
                        }

                        for (var s = 0; s < symbolCount; s++)
                        {
                            var symbol = new SymbolNode
                            {
                                Name = reader.ReadString() ?? string.Empty,
                                Kind = reader.ReadString() ?? string.Empty,
                                Path = entry.RelativePath,
                                Line = reader.ReadInt32(),
                            };

                            var baseCount = reader.ReadInt32();
                            if (baseCount < 0 || baseCount > 1000)
                            {
                                return null;
                            }

                            for (var b = 0; b < baseCount; b++)
                            {
                                symbol.Bases.Add(reader.ReadString() ?? string.Empty);
                            }

                            entry.Symbols.Add(symbol);
                        }

                        var chunkCount = reader.ReadInt32();
                        if (chunkCount < 0 || chunkCount > 200000)
                        {
                            return null;
                        }

                        for (var c = 0; c < chunkCount; c++)
                        {
                            entry.Chunks.Add(new CodeChunk
                            {
                                Path = entry.RelativePath,
                                StartLine = reader.ReadInt32(),
                                EndLine = reader.ReadInt32(),
                                Symbol = reader.ReadString() ?? string.Empty,
                                Kind = reader.ReadString() ?? string.Empty,
                                Text = reader.ReadString() ?? string.Empty,
                            });
                        }

                        result.Add(entry);
                    }

                    return result;
                }
            }
            catch (Exception)
            {
                // 缓存损坏 / 版本不符 / 被占用：当作没有缓存，下次重建后覆盖
                return null;
            }
        }

        private void SaveCache(IList<CachedFileIndex> files)
        {
            var path = _cachePath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            string temp = null;
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gzip = new GZipStream(stream, CompressionMode.Compress))
                using (var writer = new BinaryWriter(gzip, Encoding.UTF8))
                {
                    writer.Write(CacheMagic);
                    writer.Write(CacheFormatVersion);
                    writer.Write(MaxFiles);
                    writer.Write(MaxChunkChars);

                    writer.Write(_roots.Count);
                    foreach (var root in _roots)
                    {
                        writer.Write(root);
                    }

                    var list = files ?? Array.Empty<CachedFileIndex>();
                    writer.Write(list.Count);

                    foreach (var file in list)
                    {
                        writer.Write(file.RootIndex);
                        writer.Write(file.RelativePath ?? string.Empty);
                        writer.Write(file.LastWriteTicks);
                        writer.Write(file.Size);
                        writer.Write(file.Hash);

                        writer.Write(file.Symbols.Count);
                        foreach (var symbol in file.Symbols)
                        {
                            writer.Write(symbol.Name ?? string.Empty);
                            writer.Write(symbol.Kind ?? string.Empty);
                            writer.Write(symbol.Line);
                            writer.Write(symbol.Bases.Count);
                            foreach (var baseName in symbol.Bases)
                            {
                                writer.Write(baseName ?? string.Empty);
                            }
                        }

                        writer.Write(file.Chunks.Count);
                        foreach (var chunk in file.Chunks)
                        {
                            writer.Write(chunk.StartLine);
                            writer.Write(chunk.EndLine);
                            writer.Write(chunk.Symbol ?? string.Empty);
                            writer.Write(chunk.Kind ?? string.Empty);
                            writer.Write(chunk.Text ?? string.Empty);
                        }
                    }
                }

                if (File.Exists(path))
                {
                    File.Replace(temp, path, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, path);
                }

                temp = null;
            }
            catch (Exception)
            {
                // 缓存写失败不影响检索：下次构建会再尝试
            }
            finally
            {
                if (temp != null)
                {
                    try
                    {
                        if (File.Exists(temp))
                        {
                            File.Delete(temp);
                        }
                    }
                    catch (Exception)
                    {
                        // 清理失败不影响主流程
                    }
                }
            }
        }

        private static ToolResult Ok(string output)
        {
            return new ToolResult { Ok = true, Output = output ?? string.Empty };
        }

        private static ToolResult Fail(string message)
        {
            return new ToolResult { Ok = false, Output = "✗ " + message };
        }
    }
}

