using LangCode.VsExt.Domain.Chat;
using LangCode.VsExt.Roslyn;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LangCode.VsExt.Workspace
{
    /// <summary>
    /// 一次「待审批的改动」：包含改动前后文本、Diff 行与落盘动作
    /// </summary>
    public sealed class PendingEdit
    {
        public string RelativePath { get; set; } = string.Empty;

        public string OriginalText { get; set; } = string.Empty;

        public string NewText { get; set; } = string.Empty;

        public bool IsNewFile { get; set; }

        public IList<ChatBlockLine> Diff { get; set; } = [];

        /// <summary>
        /// 是否已经落盘（审批通过后置位）
        /// </summary>
        public bool Applied { get; set; }
    }

    /// <summary>
    /// 一次工具调用的结果（统一给 Agent 回灌与界面渲染用）
    /// </summary>
    public sealed class ToolResult
    {
        public bool Ok { get; set; }

        /// <summary>
        /// 回灌给模型的文本
        /// </summary>
        public string Output { get; set; } = string.Empty;

        /// <summary>
        /// 需要审批时非空
        /// </summary>
        public PendingEdit Pending { get; set; }
    }

    /// <summary>
    /// 工作区文件工具（真实读写磁盘）
    /// </summary>
    public sealed class FileSystemWorkspace
    {
        private static readonly string[] IgnoredDirectories =
        {
            ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "TestResults", ".next", "dist", "build",
        };

        private readonly string _root;

        /// <summary>
        /// 全部根目录（多根工作区；首个是主根）所有路径都必须落在其中某个根之内
        /// </summary>
        private readonly IList<string> _roots;

        // ---- .gitignore（惰性加载，按根各存一份） ----

        private readonly object _ignoreGate = new object();
        private readonly Dictionary<string, GitIgnoreMatcher> _ignoreByRoot =
            new Dictionary<string, GitIgnoreMatcher>(StringComparer.OrdinalIgnoreCase);

        // ==================== 文件清单缓存 ====================
        // 工具会在一轮任务里反复 list_files / search_text；每次都从根目录递归扫盘，
        // 会让长任务越跑越慢（同一棵目录树被扫十几遍）这里缓存一小段时间

        private readonly object _cacheGate = new object();
        private List<string> _cachedFiles;
        private int _cachedDepth = -1;
        private DateTime _cachedAtUtc = DateTime.MinValue;

        /// <summary>
        /// 缓存有效期：太短没意义，太长会让刚新建的文件「看不见」
        /// </summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 明显是二进制的扩展名：搜索它们既慢又没有意义，还会把乱码灌进上下文
        /// </summary>
        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".svgz",
            ".dll", ".exe", ".pdb", ".so", ".dylib", ".a", ".lib", ".obj", ".o", ".class", ".jar",
            ".zip", ".gz", ".tgz", ".7z", ".rar", ".bz2", ".xz", ".nupkg", ".vsix", ".whl",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".mp3", ".mp4", ".avi", ".mov", ".wav", ".ogg", ".webm",
            ".woff", ".woff2", ".ttf", ".otf", ".eot",
            ".db", ".sqlite", ".mdf", ".bin", ".dat", ".pak", ".cache",
        };

        public FileSystemWorkspace(string root)
            : this(root, null)
        {
        }

        /// <summary>
        /// 多根工作区：除主根外还可挂若干额外根（sln 多项目 / VS「多文件夹」工作区）
        /// 所有读写都可在任意根内进行，路径边界按「根集合」判定
        /// </summary>
        public FileSystemWorkspace(string root, IEnumerable<string> extraRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("工作区根目录不能为空", nameof(root));
            }

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
            _root = roots[0];
        }

        private static void AddRoot(IList<string> roots, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var existing in roots)
            {
                if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            roots.Add(full);
        }

        /// <summary>
        /// 工作区根目录（绝对路径，主根）
        /// </summary>
        public string Root => _root;

        /// <summary>
        /// 全部根目录（多根工作区；首个为主根）
        /// </summary>
        public IList<string> Roots => _roots;

        /// <summary>
        /// 是否尊重 <c>.gitignore</c>默认开启：用户在自己仓库里排除的构建产物 / 生成代码
        /// 不再被列进上下文关闭后只退回内置目录名跳过
        /// </summary>
        public bool UseGitIgnore { get; set; } = true;

        /// <summary>
        /// 加载全部根的忽略规则并返回总条数（界面在需要时调一次即可）
        /// </summary>
        public int LoadIgnoreRules()
        {
            var total = 0;
            foreach (var root in _roots)
            {
                var matcher = MatcherFor(root);
                if (matcher != null)
                {
                    total += matcher.RuleCount;
                }
            }

            return total;
        }

        /// <summary>
        /// 单文件读取上限（<b>字符数</b>），超过即截断
        /// </summary>
        public int MaxFileChars { get; set; } = 200_000;

        /// <summary>
        /// 列目录 / 搜索的返回上限
        /// </summary>
        public int MaxResults { get; set; } = 200;

        // ==================== 按路径写锁 ====================
        // 并行子任务可能同时改不同的文件；万一撞到同一个文件，必须串行落盘，
        // 否则后写者会覆盖前写者（甚至写出半个文件）这里给「同一路径」配一把锁

        private static readonly object WriteLockGate = new object();
        private static readonly Dictionary<string, object> WriteLocks =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 取某个绝对路径的写锁（按路径共享，跨实例有效）
        /// </summary>
        private static object GetWriteLock(string fullPath)
        {
            lock (WriteLockGate)
            {
                // 保护阀：条目过多时清空一次（锁对象只在写入期间被引用，清空不影响已持有的锁）
                if (WriteLocks.Count > 4096)
                {
                    WriteLocks.Clear();
                }

                if (!WriteLocks.TryGetValue(fullPath, out var gate))
                {
                    gate = new object();
                    WriteLocks[fullPath] = gate;
                }

                return gate;
            }
        }

        // ==================== 路径安全 ====================

        /// <summary>
        /// 把相对路径解析成某个根目录内的绝对路径；越界直接抛异常
        /// </summary>
        public string ResolveWithinRoot(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("路径不能为空", nameof(relativePath));
            }

            var cleaned = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);

            // 绝对路径：只接受本来就落在某个根之内的情况（多根工作区里 VS 有时给绝对路径）
            if (Path.IsPathRooted(cleaned))
            {
                var absolute = Path.GetFullPath(cleaned);
                if (RootOf(absolute) != null)
                {
                    return absolute;
                }

                throw new InvalidOperationException("路径越出了工作区范围：" + relativePath);
            }

            foreach (var root in _roots)
            {
                try
                {
                    var full = Path.GetFullPath(Path.Combine(root, cleaned));
                    if (IsWithinRoot(full, root))
                    {
                        return full;
                    }
                }
                catch (Exception)
                {
                    // 该根下解析失败（非法字符等）：继续试下一个根
                }
            }

            throw new InvalidOperationException("路径越出了工作区范围：" + relativePath);
        }

        /// <summary>
        /// 某个绝对路径是否落在指定根之内（含根本身）
        /// </summary>
        private static bool IsWithinRoot(string full, string root)
        {
            var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;

            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 返回包含该绝对路径的根；不在任何根内时返回 null
        /// </summary>
        private string RootOf(string full)
        {
            foreach (var root in _roots)
            {
                if (IsWithinRoot(full, root))
                {
                    return root;
                }
            }

            return null;
        }

        /// <summary>
        /// 按内置目录名判定是否忽略（不看 .gitignore）
        /// </summary>
        private static bool IsIgnoredByName(string fullPath)
        {
            var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Any(p => IgnoredDirectories.Contains(p, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 该目录是否应当跳过：内置目录名命中，或被该根的 .gitignore 命中
        /// </summary>
        private bool IsIgnoredDirectory(string fullPath)
        {
            if (IsIgnoredByName(fullPath))
            {
                return true;
            }

            var root = RootOf(fullPath);
            if (root == null)
            {
                return false;
            }

            var matcher = MatcherFor(root);
            return matcher != null && matcher.IsIgnored(RelativeTo(fullPath, root), true);
        }

        /// <summary>
        /// 取某个根的忽略匹配器（惰性加载并缓存）
        /// </summary>
        private GitIgnoreMatcher MatcherFor(string root)
        {
            if (!UseGitIgnore)
            {
                return null;
            }

            lock (_ignoreGate)
            {
                if (_ignoreByRoot.TryGetValue(root, out var matcher))
                {
                    return matcher;
                }

                matcher = GitIgnoreMatcher.Load(root);
                _ignoreByRoot[root] = matcher;
                return matcher;
            }
        }

        // ==================== 只读工具 ====================

        /// <summary>
        /// 列出工作区文件（支持 <c>*</c> / <c>?</c> 通配，相对路径匹配）
        /// </summary>
        public IList<string> ListFiles(string glob, int maxDepth = 6)
        {
            var pattern = string.IsNullOrWhiteSpace(glob) ? "*" : glob.Trim();
            var regex = GlobToRegex(pattern);
            var results = new List<string>();

            foreach (var file in EnumerateFilesCached(maxDepth))
            {
                var relative = Relative(file);
                if (regex.IsMatch(relative))
                {
                    results.Add(relative);
                    if (results.Count >= MaxResults)
                    {
                        break;
                    }
                }
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        /// <summary>
        /// 读取文件（可按行范围裁剪）
        /// </summary>
        public ToolResult ReadFile(string relativePath, int? startLine, int? endLine)
        {
            try
            {
                var full = ResolveWithinRoot(relativePath);
                if (!File.Exists(full))
                {
                    return Fail("文件不存在：" + relativePath);
                }

                var text = ReadTextCapped(full, out var truncated);
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

                var from = Math.Max(1, startLine ?? 1);
                var to = Math.Min(lines.Length, endLine ?? lines.Length);
                if (from > to)
                {
                    return Fail("行范围无效：" + from + "-" + to);
                }

                var builder = new StringBuilder();
                for (var i = from; i <= to; i++)
                {
                    builder.Append(i).Append(": ").Append(lines[i - 1]).Append('\n');
                }

                if (truncated)
                {
                    builder.Append("[已截断：文件超过 ").Append(MaxFileChars).Append(" 字符]\n");
                }

                return Ok(builder.ToString());
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// 按正则搜索文件内容，返回 <c>路径:行号: 内容</c>
        /// </summary>
        public ToolResult SearchText(string pattern, string glob)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    return Fail("搜索表达式不能为空");
                }

                // Compiled：一轮任务里同一模式会匹配上万行，解释执行的正则开销很明显
                var regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
                var fileRegex = GlobToRegex(string.IsNullOrWhiteSpace(glob) ? "*" : glob.Trim());
                var builder = new StringBuilder();
                var hits = 0;

                foreach (var file in EnumerateFilesCached(8))
                {
                    var relative = Relative(file);

                    // 二进制 / 压缩 / 大体积资源直接跳过：读它们既慢又只会产出乱码
                    if (!fileRegex.IsMatch(relative) || IsLikelyBinary(relative))
                    {
                        continue;
                    }

                    string text;
                    try
                    {
                        text = ReadTextCapped(file, out _);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (!regex.IsMatch(lines[i]))
                        {
                            continue;
                        }

                        builder.Append(relative).Append(':').Append(i + 1).Append(": ").Append(lines[i].Trim()).Append('\n');
                        if (++hits >= MaxResults)
                        {
                            return Ok(builder.ToString());
                        }
                    }
                }

                return Ok(hits == 0 ? "没有匹配结果" : builder.ToString());
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// 列出文件里的符号（类 / 方法 / 函数）及行号
        /// </summary>
        public ToolResult ListSymbols(string relativePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    return Fail("path 不能为空");
                }

                var full = ResolveWithinRoot(relativePath);
                if (!File.Exists(full))
                {
                    return Fail("文件不存在：" + relativePath);
                }

                var text = ReadTextCapped(full, out _);

                // C# 且 Roslyn 可用：用语法树精确提取（含完整签名与真实起止行号，
                // 且天然跳过字符串 / 注释里的伪符号）
                if (RoslynCodeService.IsAvailable && RoslynCodeService.IsCSharpFile(relativePath))
                {
                    var roslyn = RoslynCodeService.ListSymbols(text);
                    if (roslyn.Count > 0)
                    {
                        var rb = new StringBuilder();
                        rb.Append(relativePath).Append(" 共 ").Append(roslyn.Count).Append(" 个符号（Roslyn 语法级，行号: 类别 [容器] 名字 — 签名）：\n");
                        foreach (var symbol in roslyn)
                        {
                            rb.Append(symbol.StartLine);
                            if (symbol.EndLine > symbol.StartLine)
                            {
                                rb.Append('-').Append(symbol.EndLine);
                            }

                            rb.Append(": ").Append(symbol.Kind);
                            if (symbol.Container.Length > 0)
                            {
                                rb.Append(" [").Append(symbol.Container).Append(']');
                            }

                            rb.Append(' ').Append(symbol.Name);
                            if (symbol.Signature.Length > 0)
                            {
                                rb.Append("  —  ").Append(symbol.Signature);
                            }

                            rb.Append('\n');
                        }

                        return Ok(rb.ToString().TrimEnd());
                    }
                }

                // 非 C#（或 Roslyn 不可用）：回退到自研正则扫描
                var symbols = SymbolScanner.Scan(text);

                if (symbols.Count == 0)
                {
                    return Ok("（没有识别到符号：可能是该语言不在支持范围，或文件里没有类型 / 函数声明）");
                }

                var builder = new StringBuilder();
                builder.Append(relativePath).Append(" 共 ").Append(symbols.Count).Append(" 个符号（行号: 类别 名字）：\n");

                foreach (var symbol in symbols)
                {
                    builder.Append(symbol.Line).Append(": ").Append(symbol.Kind).Append(' ').Append(symbol.Name).Append('\n');
                }

                return Ok(builder.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        // ==================== Roslyn 代码智能（引用 / 继承 / 重命名） ====================

        /// <summary>
        /// Roslyn 是否可用；可用时下列查询走语法级实现（精度高于正则）
        /// </summary>
        public bool SupportsRoslyn => RoslynCodeService.IsAvailable;

        /// <summary>
        /// 标识符级查找引用：Roslyn 可用时按标识符 token 匹配（自动跳过字符串与注释），
        /// 否则回退到整词正则
        /// </summary>
        public ToolResult FindReferences(string symbol, string glob, int maxResults = 200)
        {
            var name = (symbol ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return Fail("symbol 不能为空");
            }

            try
            {
                var limit = maxResults <= 0 ? MaxResults : maxResults;
                var fileRegex = GlobToRegex(string.IsNullOrWhiteSpace(glob) ? "*" : glob.Trim());
                var builder = new StringBuilder();
                var hits = 0;
                var files = 0;
                var scanned = 0;
                const int maxScan = 1500;

                foreach (var file in EnumerateFilesCached(10))
                {
                    if (++scanned > maxScan)
                    {
                        break;
                    }

                    var relative = Relative(file);
                    if (!fileRegex.IsMatch(relative) || IsLikelyBinary(relative))
                    {
                        continue;
                    }

                    string text;
                    try
                    {
                        text = ReadTextCapped(file, out _);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    var references = RoslynCodeService.IsAvailable && RoslynCodeService.IsCSharpFile(relative)
                        ? RoslynCodeService.FindReferences(text, relative, name)
                        : FallbackReferences(text, relative, name);

                    if (references.Count == 0)
                    {
                        continue;
                    }

                    files++;
                    foreach (var reference in references)
                    {
                        builder.Append(reference.Path).Append(':').Append(reference.Line).Append(": ")
                               .Append(reference.IsDeclaration ? "[声明] " : string.Empty)
                               .Append(reference.Text).Append('\n');

                        if (++hits >= limit)
                        {
                            return Ok("引用「" + name + "」命中已达上限（前 " + limit + " 处，涉及 " + files + " 个文件）：\n" + builder);
                        }
                    }
                }

                return Ok(hits == 0
                    ? "没有找到「" + name + "」的引用"
                    : "引用「" + name + "」共 " + hits + " 处，涉及 " + files + " 个文件：\n" + builder.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// 整词回退查找（Roslyn 不可用时）
        /// </summary>
        private static IList<RoslynReference> FallbackReferences(string text, string relativePath, string symbol)
        {
            var results = new List<RoslynReference>();
            var pattern = new Regex("\\b" + Regex.Escape(symbol) + "\\b", RegexOptions.CultureInvariant);
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    results.Add(new RoslynReference
                    {
                        Path = relativePath,
                        Line = i + 1,
                        Text = lines[i].Trim(),
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// 类型继承链：基类 / 接口 / 本工作区内已知派生类型（Roslyn 语法级）
        /// </summary>
        public ToolResult FindTypeHierarchy(string typeName)
        {
            var name = (typeName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return Fail("type 不能为空");
            }

            var all = CollectTypes(out var scanned);
            if (all.Count == 0)
            {
                return Ok("没有扫描到任何类型声明（工作区里可能没有 C# 文件，或 Roslyn 不可用）");
            }

            var target = all.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                return Ok("没有找到类型「" + name + "」（已扫描 " + scanned + " 个文件）。这里只做基于文件内容的语法匹配，跨程序集类型可能不在工作区内");
            }

            var builder = new StringBuilder();
            builder.Append("类型 ").Append(target.Name).Append("（").Append(target.Kind).Append("，")
                   .Append(target.Path).Append(':').Append(target.Line).Append("）\n");

            builder.Append(target.Bases.Count > 0
                ? "基类 / 实现的接口：" + string.Join(", ", target.Bases) + "\n"
                : "没有显式基类 / 接口\n");

            var derived = all
                .Where(t => t.Bases.Any(b => string.Equals(b, target.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (derived.Count > 0)
            {
                builder.Append("已知派生 / 实现（本工作区内）：\n");
                foreach (var d in derived)
                {
                    builder.Append("· ").Append(d.Kind).Append(' ').Append(d.Name)
                           .Append("（").Append(d.Path).Append(':').Append(d.Line).Append("）\n");
                }
            }

            return Ok(builder.ToString().TrimEnd());
        }

        /// <summary>
        /// 查实现了某接口 / 继承了某基类的全部类型（Roslyn 语法级）
        /// </summary>
        public ToolResult FindImplementations(string typeName)
        {
            var name = (typeName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return Fail("type 不能为空");
            }

            var all = CollectTypes(out var scanned);
            var matches = all
                .Where(t => t.Bases.Any(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matches.Count == 0)
            {
                return Ok("没有找到实现 / 继承「" + name + "」的类型（已扫描 " + scanned + " 个文件）");
            }

            var builder = new StringBuilder();
            builder.Append("实现 / 继承「").Append(name).Append("」的类型共 ").Append(matches.Count).Append(" 个：\n");
            foreach (var match in matches)
            {
                builder.Append("· ").Append(match.Kind).Append(' ').Append(match.Name)
                       .Append("（").Append(match.Path).Append(':').Append(match.Line).Append("）\n");
            }

            return Ok(builder.ToString().TrimEnd());
        }

        /// <summary>
        /// 扫描工作区里的全部 C# 类型声明（继承 / 实现查询共用）
        /// </summary>
        private IList<RoslynTypeInfo> CollectTypes(out int scannedFiles)
        {
            var all = new List<RoslynTypeInfo>();
            scannedFiles = 0;
            if (!RoslynCodeService.IsAvailable)
            {
                return all;
            }

            const int maxScan = 1500;
            foreach (var file in EnumerateFilesCached(10))
            {
                if (scannedFiles >= maxScan)
                {
                    break;
                }

                var relative = Relative(file);
                if (!RoslynCodeService.IsCSharpFile(relative) || IsLikelyBinary(relative))
                {
                    continue;
                }

                scannedFiles++;
                string text;
                try
                {
                    text = ReadTextCapped(file, out _);
                }
                catch (Exception)
                {
                    continue;
                }

                try
                {
                    all.AddRange(RoslynCodeService.ListTypes(text, relative));
                }
                catch (Exception)
                {
                    // 单个文件解析失败不影响整体
                }
            }

            return all;
        }

        /// <summary>
        /// 准备一次跨文件重命名：返回每处待改文件的 <see cref="PendingEdit"/>（逐文件审批后落盘）
        /// </summary>
        public IList<PendingEdit> PrepareRename(string symbol, string newName, string glob, int maxFiles, out string error)
        {
            error = null;
            var from = (symbol ?? string.Empty).Trim();
            var to = (newName ?? string.Empty).Trim();

            if (from.Length == 0 || to.Length == 0)
            {
                error = "symbol 与 new_name 都不能为空";
                return null;
            }

            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                error = "新名字和原名字一样，无需重命名";
                return null;
            }

            var limit = maxFiles <= 0 ? 40 : maxFiles;
            var pendings = new List<PendingEdit>();
            var fileRegex = GlobToRegex(string.IsNullOrWhiteSpace(glob) ? "*" : glob.Trim());

            try
            {
                foreach (var file in EnumerateFilesCached(10))
                {
                    var relative = Relative(file);
                    if (!fileRegex.IsMatch(relative) || IsLikelyBinary(relative))
                    {
                        continue;
                    }

                    string original;
                    try
                    {
                        original = ReadTextCapped(file, out _);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    string next = null;
                    if (RoslynCodeService.IsAvailable && RoslynCodeService.IsCSharpFile(relative))
                    {
                        if (!RoslynCodeService.TryRename(original, from, to, out next, out _))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        var pattern = new Regex("\\b" + Regex.Escape(from) + "\\b", RegexOptions.CultureInvariant);
                        if (!pattern.IsMatch(original))
                        {
                            continue;
                        }

                        next = pattern.Replace(original, to);
                    }

                    if (next == null || SimpleDiff.AreEqual(original, next))
                    {
                        continue;
                    }

                    pendings.Add(new PendingEdit
                    {
                        RelativePath = relative,
                        OriginalText = original,
                        NewText = next,
                        IsNewFile = false,
                        Diff = SimpleDiff.Diff(original, next),
                    });

                    if (pendings.Count >= limit)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }

            return pendings;
        }

        // ==================== 写工具（需要审批） ====================

        /// <summary>
        /// 准备「整文件写入」：算出 Diff，等审批后再落盘
        /// </summary>
        public ToolResult PrepareWrite(string relativePath, string content)
        {
            try
            {
                var full = ResolveWithinRoot(relativePath);
                var exists = File.Exists(full);
                var original = exists ? File.ReadAllText(full) : string.Empty;
                var next = content ?? string.Empty;

                if (exists && SimpleDiff.AreEqual(original, next))
                {
                    return Ok("内容没有变化，无需写入：" + relativePath);
                }

                return new ToolResult
                {
                    Ok = true,
                    Output = (exists ? "准备覆盖 " : "准备新建 ") + relativePath,
                    Pending = new PendingEdit
                    {
                        RelativePath = relativePath,
                        OriginalText = original,
                        NewText = next,
                        IsNewFile = !exists,
                        Diff = SimpleDiff.Diff(original, next),
                    },
                };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// 准备「局部替换」：找到 <paramref name="oldText"/> 并替换为 <paramref name="newText"/>
        /// </summary>
        public ToolResult PrepareReplace(string relativePath, string oldText, string newText, bool replaceAll = false)
        {
            try
            {
                var full = ResolveWithinRoot(relativePath);
                if (!File.Exists(full))
                {
                    return Fail("文件不存在：" + relativePath);
                }

                var original = File.ReadAllText(full);
                if (string.IsNullOrEmpty(oldText))
                {
                    return Fail("oldText 不能为空；整文件改写请用 write_file");
                }

                var matches = CountOccurrences(original, oldText);
                if (matches == 0)
                {
                    return Fail("文件里找不到待替换的原文（可能已被改动或空白不一致）：" + relativePath);
                }

                if (matches > 1 && !replaceAll)
                {
                    return Fail("原文在文件里出现了 " + matches + " 次，无法确定要改哪一处" +
                                "请把 old_text 写得更完整（带上足够的上下文）让它唯一；" +
                                "若确实要全部替换，请带 {\"replace_all\":\"true\"}");
                }

                var next = replaceAll
                    ? original.Replace(oldText, newText ?? string.Empty)
                    : ReplaceFirst(original, oldText, newText ?? string.Empty);

                return new ToolResult
                {
                    Ok = true,
                    Output = "准备修改 " + relativePath,
                    Pending = new PendingEdit
                    {
                        RelativePath = relativePath,
                        OriginalText = original,
                        NewText = next,
                        IsNewFile = false,
                        Diff = SimpleDiff.Diff(original, next),
                    },
                };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// 准备「结构化编辑」：按符号名定位代码块，再对整块 / 仅块内 / 块前后增删改
        /// </summary>
        public ToolResult PrepareStructuredEdit(string relativePath, string symbol, string mode, string newText)
        {
            try
            {
                var full = ResolveWithinRoot(relativePath);
                if (!File.Exists(full))
                {
                    return Fail("文件不存在：" + relativePath);
                }

                var original = File.ReadAllText(full);
                if (!StructuredEditor.TryApply(original, symbol, mode, newText, out var next, out var error))
                {
                    return Fail(error);
                }

                if (SimpleDiff.AreEqual(original, next))
                {
                    return Ok("内容没有变化，无需写入：" + relativePath);
                }

                return new ToolResult
                {
                    Ok = true,
                    Output = "准备结构化编辑 " + relativePath + "（符号 " + (symbol ?? string.Empty).Trim() +
                             "，模式 " + NormalizeStructuredMode(mode) + "）",
                    Pending = new PendingEdit
                    {
                        RelativePath = relativePath,
                        OriginalText = original,
                        NewText = next,
                        IsNewFile = false,
                        Diff = SimpleDiff.Diff(original, next),
                    },
                };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// 定位一个符号块（只读，供界面预览行号 / 自检用）
        /// </summary>
        public ToolResult LocateSymbol(string relativePath, string symbol)
        {
            try
            {
                var full = ResolveWithinRoot(relativePath);
                if (!File.Exists(full))
                {
                    return Fail("文件不存在：" + relativePath);
                }

                var text = ReadTextCapped(full, out _);
                if (!StructuredEditor.TryLocate(text, symbol, out var location, out var error))
                {
                    return Fail(error);
                }

                return Ok("符号 " + location.Kind + " " + location.Symbol + " 位于 " + relativePath +
                          " 第 " + location.StartLine + "-" + location.EndLine + " 行" +
                          (location.HasBraces ? "（大括号块）" : "（缩进 / 分号块）"));
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private static string NormalizeStructuredMode(string mode)
        {
            var value = (mode ?? string.Empty).Trim();
            return value.Length == 0 ? StructuredEditor.ModeReplace : value;
        }

        /// <summary>
        /// 落盘（审批通过）
        /// </summary>
        public ToolResult Apply(PendingEdit edit)
        {
            try
            {
                if (edit == null)
                {
                    return Fail("没有待应用的改动");
                }

                var full = ResolveWithinRoot(edit.RelativePath);
                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                // 同一路径的写入串行化（并行子任务 / 同轮多次改动同一文件时保护）
                lock (GetWriteLock(full))
                {
                    WriteTextPreserving(full, edit.NewText ?? string.Empty, edit.OriginalText, edit.IsNewFile);
                }

                edit.Applied = true;
                InvalidateFileCache();

                var added = SimpleDiff.CountAdded(edit.Diff);
                var deleted = SimpleDiff.CountDeleted(edit.Diff);
                return Ok("已写入 " + edit.RelativePath + "（+" + added + " / −" + deleted + "）");
            }
            catch (Exception ex)
            {
                return Fail("写入失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 撤销一次已落盘的改动：改文件写回「改动前」的文本，新建文件直接删掉
        /// </summary>
        public ToolResult Revert(PendingEdit edit)
        {
            if (edit == null || string.IsNullOrWhiteSpace(edit.RelativePath))
            {
                return Fail("没有可撤销的改动");
            }

            try
            {
                var full = ResolveWithinRoot(edit.RelativePath);

                if (edit.IsNewFile)
                {
                    // 撤销「新建」= 把文件删掉（否则会留下一个 AI 建的僵尸文件）
                    if (File.Exists(full))
                    {
                        File.Delete(full);
                    }

                    edit.Applied = false;
                    InvalidateFileCache();
                    return Ok("已删除新建的文件：" + edit.RelativePath);
                }

                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 还原同样按原文件的编码与换行写回，不能在这一步把编码/换行改掉
                WriteTextPreserving(full, edit.OriginalText ?? string.Empty, edit.OriginalText, edit.IsNewFile);
                edit.Applied = false;
                InvalidateFileCache();
                return Ok("已还原：" + edit.RelativePath);
            }
            catch (Exception ex)
            {
                return Fail("撤销失败：" + ex.Message);
            }
        }

        // ==================== 辅助 ====================

        /// <summary>
        /// 带缓存的文件枚举（列目录 / 搜索共用同一份结果）
        /// </summary>
        private IList<string> EnumerateFilesCached(int maxDepth)
        {
            lock (_cacheGate)
            {
                if (_cachedFiles != null && _cachedDepth >= maxDepth &&
                    DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                {
                    return _cachedFiles;
                }
            }

            var files = new List<string>();
            foreach (var file in EnumerateFilesSafe(maxDepth))
            {
                files.Add(file);
            }

            lock (_cacheGate)
            {
                // 另一线程可能在我们扫盘期间放进了更深/更新的缓存：那种情况以它为准
                if (_cachedFiles != null && _cachedDepth >= maxDepth &&
                    DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                {
                    return _cachedFiles;
                }

                _cachedFiles = files;
                _cachedDepth = maxDepth;
                _cachedAtUtc = DateTime.UtcNow;
            }

            return files;
        }

        /// <summary>
        /// 写盘后让缓存失效（新建 / 删除会改变文件清单）
        /// </summary>
        private void InvalidateFileCache()
        {
            lock (_cacheGate)
            {
                _cachedFiles = null;
                _cachedDepth = -1;
                _cachedAtUtc = DateTime.MinValue;
            }
        }

        private static bool IsLikelyBinary(string relativePath)
        {
            var dot = relativePath.LastIndexOf('.');
            return dot >= 0 && BinaryExtensions.Contains(relativePath.Substring(dot));
        }

        /// <summary>
        /// 按原文件的编码与换行风格写回，并保证写入的原子性
        /// </summary>
        private static void WriteTextPreserving(string fullPath, string text, string originalText, bool isNewFile)
        {
            var hasBom = !isNewFile && HasUtf8Bom(fullPath);
            var useCrLf = !isNewFile && UsesCrLf(originalText);

            var body = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (useCrLf)
            {
                body = body.Replace("\n", "\r\n");
            }

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom);
            var temp = fullPath + ".langcode.tmp";

            File.WriteAllText(temp, body, encoding);

            if (File.Exists(fullPath))
            {
                // 原子替换：读文件的进程（含 VS 自己）不会看到「写了一半」的状态
                File.Replace(temp, fullPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, fullPath);
            }
        }

        private static bool HasUtf8Bom(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return stream.ReadByte() == 0xEF && stream.ReadByte() == 0xBB && stream.ReadByte() == 0xBF;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool UsesCrLf(string originalText)
        {
            return !string.IsNullOrEmpty(originalText) && originalText.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
        }

        private static int CountOccurrences(string text, string pattern)
        {
            var count = 0;
            var index = 0;

            while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += pattern.Length;
            }

            return count;
        }

        private static string ReplaceFirst(string text, string oldText, string newText)
        {
            var index = text.IndexOf(oldText, StringComparison.Ordinal);
            return index < 0
                ? text
                : text.Substring(0, index) + newText + text.Substring(index + oldText.Length);
        }

        private IEnumerable<string> EnumerateFilesSafe(int maxDepth)
        {
            IEnumerable<string> Walk(string directory, int depth)
            {
                if (depth > maxDepth || IsIgnoredDirectory(directory))
                {
                    yield break;
                }

                string[] files;
                try
                {
                    files = System.IO.Directory.GetFiles(directory);
                }
                catch (Exception)
                {
                    yield break;
                }

                foreach (var file in files)
                {
                    yield return file;
                }

                string[] directories;
                try
                {
                    directories = System.IO.Directory.GetDirectories(directory);
                }
                catch (Exception)
                {
                    yield break;
                }

                foreach (var sub in directories)
                {
                    foreach (var file in Walk(sub, depth + 1))
                    {
                        yield return file;
                    }
                }
            }

            // 多根：逐个根遍历，结果合并（相对路径各自相对自己的根）
            foreach (var root in _roots)
            {
                foreach (var file in Walk(root, 0))
                {
                    yield return file;
                }
            }
        }

        private string Relative(string full)
        {
            var root = RootOf(full);
            return root == null ? full : RelativeTo(full, root);
        }

        /// <summary>
        /// 把绝对路径折算成相对指定根的路径（'/' 分隔）
        /// </summary>
        private static string RelativeTo(string full, string root)
        {
            var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;

            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/')
                : string.Empty;
        }

        private string ReadTextCapped(string full, out bool truncated)
        {
            // 一次最多读 MaxFileChars 个字符：不再先 ReadToEnd 再判断，
            // 超大文件（例如压缩后的单行 JS）也不会先被整体读进内存
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                var buffer = new char[MaxFileChars];
                var read = reader.ReadBlock(buffer, 0, buffer.Length);

                truncated = !reader.EndOfStream;
                return new string(buffer, 0, read);
            }
        }

        private static Regex GlobToRegex(string glob)
        {
            var builder = new StringBuilder("^");
            foreach (var c in glob)
            {
                switch (c)
                {
                    case '*':
                        builder.Append(".*");
                        break;
                    case '?':
                        builder.Append('.');
                        break;
                    default:
                        builder.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }

            builder.Append('$');
            return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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

