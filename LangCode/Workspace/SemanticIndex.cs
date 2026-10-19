using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

        private readonly string _root;
        private readonly object _gate = new object();

        private List<CodeChunk> _chunks;
        private Dictionary<string, List<int>> _postings;   // 词 → 片段下标
        private Dictionary<string, int> _documentFrequency; // 词 → 出现该词的片段数
        private Dictionary<string, int> _pathFrequency;     // 词 → 出现该词的路径数（路径加权）
        private List<SymbolNode> _symbols;
        private Dictionary<string, List<SymbolNode>> _symbolsByName;
        private DateTime _builtAtUtc = DateTime.MinValue;
        private int _chunkCount;
        private double _avgChunkLength = 1;

        /// <summary>
        /// 索引有效期：太长会让刚改的代码检索不到；太短则每次检索都重扫
        /// </summary>
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

        public SemanticIndex(string root)
        {
            _root = (root ?? string.Empty).TrimEnd('\\', '/');
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

        public bool IsReady => _root.Length > 0 && Directory.Exists(_root);

        /// <summary>
        /// 丢弃缓存（下一次查询时重建）
        /// </summary>
        public void Invalidate()
        {
            lock (_gate)
            {
                _chunks = null;
                _postings = null;
                _documentFrequency = null;
                _pathFrequency = null;
                _symbols = null;
                _symbolsByName = null;
                _builtAtUtc = DateTime.MinValue;
            }
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

            lock (_gate)
            {
                EnsureBuilt();
                if (_chunks.Count == 0)
                {
                    return Fail("索引为空：工作区里没有可索引的源码文件");
                }

                var top = limit <= 0 ? 8 : limit;
                var scores = new Dictionary<int, double>();
                var unique = terms.Distinct(StringComparer.Ordinal).ToList();

                foreach (var term in unique)
                {
                    if (!_postings.TryGetValue(term, out var posting))
                    {
                        continue;
                    }

                    var df = Math.Max(1, _documentFrequency[term]);
                    var idf = Math.Log(1.0 + (_chunkCount - df + 0.5) / (df + 0.5));

                    foreach (var chunkIndex in posting)
                    {
                        var chunk = _chunks[chunkIndex];
                        var tf = CountTerm(chunk.Text, term);
                        if (tf == 0)
                        {
                            continue;
                        }

                        var dl = Math.Max(1, chunk.Text.Length / 4.0);
                        const double k1 = 1.4;
                        const double b = 0.72;
                        var denom = tf + k1 * (1 - b + b * dl / _avgChunkLength);
                        var score = idf * (tf * (k1 + 1)) / Math.Max(0.0001, denom);

                        // 路径命中加权：查 "AgentRunner" 时，文件名叫 AgentRunner.cs 的片段更相关
                        score *= 1.0 + 0.35 * PathBoost(chunk.Path, term);

                        scores.TryGetValue(chunkIndex, out var existing);
                        scores[chunkIndex] = existing + score;
                    }
                }

                if (scores.Count == 0)
                {
                    return Ok("没有找到相关代码片段（可换个关键词，或先用 search_text 精确匹配）");
                }

                var ranked = scores
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => _chunks[kv.Key].Path, StringComparer.OrdinalIgnoreCase)
                    .Take(top)
                    .ToList();

                var builder = new StringBuilder();
                builder.Append("语义检索结果（“").Append((query ?? string.Empty).Trim()).Append("”，共 ")
                       .Append(ranked.Count).Append(" 段，按相关度排序）：\n\n");

                var rank = 0;
                foreach (var entry in ranked)
                {
                    var chunk = _chunks[entry.Key];
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

            lock (_gate)
            {
                EnsureBuilt();

                if (!_symbolsByName.TryGetValue(name, out var nodes) || nodes.Count == 0)
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
                    var derived = _symbols
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

            lock (_gate)
            {
                EnsureBuilt();

                var matches = _symbols
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
        }

        /// <summary>
        /// 索引规模摘要（供界面 / 自检显示）
        /// </summary>
        public string Describe()
        {
            lock (_gate)
            {
                EnsureBuilt();
                return "语义索引：" + _chunkCount + " 个片段，" + (_postings?.Count ?? 0) + " 个词项，" +
                       (_symbols?.Count ?? 0) + " 个类型节点";
            }
        }

        // ==================== 建索引 ====================

        private void EnsureBuilt()
        {
            if (_chunks != null && DateTime.UtcNow - _builtAtUtc < Ttl)
            {
                return;
            }

            Build();
        }

        private void Build()
        {
            var chunks = new List<CodeChunk>();
            var symbols = new List<SymbolNode>();

            foreach (var file in EnumerateSourceFiles())
            {
                string text;
                try
                {
                    text = ReadTextCapped(file);
                }
                catch (Exception)
                {
                    continue;
                }

                var relative = Relative(file);
                CollectSymbols(relative, text, symbols);
                chunks.AddRange(ChunkFile(relative, text));
            }

            // 倒排索引：词 → 片段下标
            var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
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

                foreach (var term in Tokenize(chunks[i].Path).Distinct(StringComparer.Ordinal))
                {
                    pathFreq.TryGetValue(term, out var count);
                    pathFreq[term] = count + 1;
                }
            }

            _chunks = chunks;
            _postings = postings;
            _documentFrequency = df;
            _pathFrequency = pathFreq;
            _symbols = symbols;
            _symbolsByName = symbols
                .GroupBy(s => s.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            _chunkCount = Math.Max(1, chunks.Count);
            _avgChunkLength = chunks.Count == 0
                ? 1
                : Math.Max(1, chunks.Average(c => c.Text.Length / 4.0));
            _builtAtUtc = DateTime.UtcNow;
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
                    // 中文按整段作为一个词（够用，且不必引入分词器）
                    if (word.Length > 0)
                    {
                        tokens.Add(word);
                    }

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

        private double PathBoost(string path, string term)
        {
            if (_pathFrequency == null || string.IsNullOrEmpty(path))
            {
                return 0;
            }

            return path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ? 1.0 : 0.0;
        }

        private static string TrimSnippet(string text)
        {
            var value = (text ?? string.Empty).TrimEnd();
            return value.Length <= 1200 ? value : value.Substring(0, 1200) + "\n…（片段过长，已截断；请用 read_file 读取完整内容）";
        }

        // ==================== 文件枚举 ====================

        private IEnumerable<string> EnumerateSourceFiles()
        {
            var count = 0;
            var pending = new Queue<string>();
            pending.Enqueue(_root);

            while (pending.Count > 0)
            {
                var current = pending.Dequeue();

                string[] directories;
                try
                {
                    directories = Directory.GetDirectories(current);
                }
                catch (Exception)
                {
                    directories = new string[0];
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

                    yield return file;
                }
            }
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

        private string Relative(string full)
        {
            var prefix = _root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/')
                : full.Replace(Path.DirectorySeparatorChar, '/');
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

