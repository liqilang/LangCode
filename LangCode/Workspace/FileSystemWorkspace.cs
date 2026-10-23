using LangCode.Domain.Chat;
using LangCode.Roslyn;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LangCode.Workspace
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

        /// <summary>
        /// 是否为「删除文件」：审批通过后删除 <see cref="RelativePath"/>，
        /// 撤销时用 <see cref="OriginalText"/> 还原
        /// </summary>
        public bool IsDelete { get; set; }

        /// <summary>
        /// 移动 / 重命名的目标相对路径（空 = 本次不是移动）
        /// </summary>
        public string MoveToPath { get; set; } = string.Empty;

        /// <summary>
        /// 原文件的编码（删除后撤销时按原编码写回；其它操作可留空）
        /// </summary>
        public Encoding OriginalEncoding { get; set; }

        /// <summary>
        /// 原文件是否带 BOM（删除后撤销时使用）
        /// </summary>
        public bool OriginalHadBom { get; set; }

        /// <summary>
        /// 原文件是否使用 CRLF（删除后撤销时使用）
        /// </summary>
        public bool OriginalUsedCrLf { get; set; }

        /// <summary>
        /// 是否是一次移动操作
        /// </summary>
        public bool IsMove => !string.IsNullOrWhiteSpace(MoveToPath);
    }

    /// <summary>
    /// 一次工具调用的结果（统一给 Agent 回灌与界面渲染用）
    /// </summary>
    public sealed class ToolResult
    {
        public bool Ok { get; set; }

        /// <summary>
        /// 命令退出码（仅命令类工具使用；-1 表示未执行 / 被取消）
        /// </summary>
        public int ExitCode { get; set; }

        /// <summary>
        /// 命令是否因为用户取消而终止
        /// </summary>
        public bool Canceled { get; set; }

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
            ".git", ".vs", ".idea", ".langcode", "bin", "obj", "node_modules", "packages", "TestResults", ".next", "dist", "build",
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
        public int MaxFileChars { get; set; } = 40_000;

        /// <summary>
        /// 列目录 / 搜索的返回上限
        /// </summary>
        public int MaxResults { get; set; } = 100;

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
            var root = RootOf(fullPath);
            if (root == null)
            {
                return false;
            }

            var relative = RelativeTo(fullPath, root);

            // 只看工作区内的相对路径：工作区本身位于 D:\build\repo 时，
            // 不能因为祖先目录叫 build 就把整个工作区忽略掉。
            if (relative.Length > 0 && IsIgnoredByName(relative))
            {
                return true;
            }

            var matcher = MatcherFor(root);
            return matcher != null && matcher.IsIgnored(relative, true);
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
            // 不传 glob = 递归列出全部文件；显式 glob 按标准语义匹配
            var pattern = string.IsNullOrWhiteSpace(glob) ? "**" : glob.Trim();
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
                    builder.Append("[已截断：文件超过 ").Append(MaxFileChars).Append(" 字符；请改用 start_line / end_line 分段读取需要的部分]\n");
                }

                return Ok(builder.ToString());
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// 批量读取多个文件：一次返回多个文件的内容（带行号），
        /// 减少模型连续发多个 read_file 的往返次数
        /// </summary>
        public ToolResult ReadMultipleFiles(string pathsText, int? maxCharsEach = null)
        {
            var paths = SplitPaths(pathsText);
            if (paths.Count == 0)
            {
                return Fail("paths 不能为空：多个路径用换行或分号分隔");
            }

            const int MaxFiles = 20;
            if (paths.Count > MaxFiles)
            {
                paths = paths.GetRange(0, MaxFiles);
            }

            // 单文件上限：默认 8000，范围 500~20000；总量再加一道上限，避免一次把上下文吃满
            var perFile = Math.Min(Math.Max(maxCharsEach ?? 8000, 500), 20000);
            const int TotalBudget = 60000;

            var builder = new StringBuilder();
            var okCount = 0;
            var failed = 0;

            foreach (var path in paths)
            {
                if (builder.Length >= TotalBudget)
                {
                    builder.Append("\n…（已达到本次批量读取的总量上限 ").Append(TotalBudget)
                        .Append(" 字符，剩余文件未读取；需要时请再单独读取）\n");
                    break;
                }

                builder.Append("=== ").Append(path).Append(" ===\n");

                if (IsLikelyBinary(path))
                {
                    builder.Append("（二进制文件，已跳过）\n\n");
                    failed++;
                    continue;
                }

                var result = ReadFile(path, 1, null);
                if (!result.Ok)
                {
                    builder.Append(result.Output).Append("\n\n");
                    failed++;
                    continue;
                }

                var text = result.Output ?? string.Empty;
                if (text.Length > perFile)
                {
                    text = text.Substring(0, perFile) +
                           "\n…（单文件超过 " + perFile + " 字符，已截断；需要完整内容请单独 read_file 并指定行范围）";
                }

                builder.Append(text.TrimEnd()).Append("\n\n");
                okCount++;
            }

            if (okCount == 0)
            {
                return Fail(builder.ToString().Trim());
            }

            return new ToolResult
            {
                Ok = true,
                Output = builder.ToString().TrimEnd() +
                         (failed == 0 ? string.Empty : "\n\n（其中 " + failed + " 个文件未能读取，原因见上）"),
            };
        }

        /// <summary>
        /// 列出目录内容（树形，目录带 / 后缀）；depth 默认 1，最大 4
        /// </summary>
        public ToolResult ListDirectory(string relativePath, int? depth = null)
        {
            try
            {
                var target = string.IsNullOrWhiteSpace(relativePath) || relativePath.Trim() == "."
                    ? _root
                    : ResolveWithinRoot(relativePath);

                if (!Directory.Exists(target))
                {
                    return File.Exists(target)
                        ? Fail("这是一个文件，不是目录：" + relativePath + "（读内容请用 read_file）")
                        : Fail("目录不存在：" + relativePath);
                }

                var levels = Math.Min(Math.Max(depth ?? 1, 1), 4);
                var builder = new StringBuilder();
                var entries = 0;
                const int MaxEntries = 1000;

                builder.Append(string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath.TrimEnd('/', '\\'))
                    .Append("/\n");

                WalkDirectory(target, 1, levels, builder, ref entries, MaxEntries);

                if (entries >= MaxEntries)
                {
                    builder.Append("…（条目超过 ").Append(MaxEntries)
                        .Append(" 个，已截断；请缩小 path 或用 list_files 精确匹配）\n");
                }

                return Ok(builder.ToString());
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// 递归列出目录（目录优先、按名称排序，跳过忽略目录 / 忽略文件）
        /// </summary>
        private void WalkDirectory(string directory, int level, int maxLevel, StringBuilder builder, ref int entries, int maxEntries)
        {
            if (level > maxLevel || entries >= maxEntries)
            {
                return;
            }

            List<string> childDirectories;
            List<string> files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory)
                    .Where(path => !IsIgnoredDirectory(path))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                files = Directory.EnumerateFiles(directory)
                    .Where(path => !IsIgnoredFile(path))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                // 无权限 / 目录被占用：跳过这一层，不影响其余结果
                return;
            }

            var indent = new string(' ', (level - 1) * 2);

            foreach (var child in childDirectories)
            {
                if (entries++ >= maxEntries)
                {
                    return;
                }

                builder.Append(indent).Append(Path.GetFileName(child)).Append("/\n");
                WalkDirectory(child, level + 1, maxLevel, builder, ref entries, maxEntries);
            }

            foreach (var file in files)
            {
                if (entries++ >= maxEntries)
                {
                    return;
                }

                builder.Append(indent).Append(Path.GetFileName(file)).Append('\n');
            }
        }

        /// <summary>
        /// 该文件是否被 .gitignore 忽略（目录忽略由 <see cref="IsIgnoredDirectory"/> 负责）
        /// </summary>
        private bool IsIgnoredFile(string fullPath)
        {
            var root = RootOf(fullPath);
            if (root == null)
            {
                return false;
            }

            var relative = RelativeTo(fullPath, root);
            if (relative.Length == 0)
            {
                return false;
            }

            var matcher = MatcherFor(root);
            return matcher != null && matcher.IsIgnored(relative, false);
        }

        /// <summary>
        /// 解析批量读取的路径列表：换行 / 分号 / 逗号分隔，并容忍模型误加的 JSON 方括号与引号
        /// </summary>
        private static List<string> SplitPaths(string pathsText)
        {
            var cleaned = (pathsText ?? string.Empty)
                .Replace("[", " ")
                .Replace("]", " ")
                .Replace("\"", " ");

            var result = new List<string>();
            foreach (var part in cleaned.Split(new[] { '\n', '\r', ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = part.Trim();
                if (path.Length == 0)
                {
                    continue;
                }

                if (!result.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(path);
                }
            }

            return result;
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
                var fileRegex = GlobToRegex(string.IsNullOrWhiteSpace(glob) ? "**" : glob.Trim());
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

                        builder.Append(relative).Append(':').Append(i + 1).Append(": ").Append(ClipLine(lines[i])).Append('\n');
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

        // ==================== 文档检索 ====================

        /// <summary>
        /// 默认参与文档检索的扩展名：只读「说明性文档」，不把源码 / 构建产物混进来
        /// </summary>
        private static readonly HashSet<string> DocumentationExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".mdx", ".markdown", ".txt", ".rst", ".adoc", ".asciidoc", ".html", ".htm",
        };

        /// <summary>
        /// 把查询拆成词（按空白 / 常见中英文标点）
        /// </summary>
        private static readonly Regex DocTermRegex = new Regex(
            "[^\\s,，。；;|、]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// 一条文档命中（保留整份行数组，便于输出上下文）
        /// </summary>
        private sealed class DocHit
        {
            public string Path = string.Empty;
            public int Line;
            public double Score;
            public string[] Lines;
        }

        /// <summary>
        /// 在工作区文档（README / docs / *.md / *.txt 等）中按关键词检索，
        /// 返回相关段落与行号。与 <see cref="SearchText"/> 的区别：
        /// 只读说明性文档、按相关度排序、并附带上下文行，适合查「这个项目怎么用 / 为什么这么设计」。
        /// </summary>
        public ToolResult SearchDocuments(string query, string glob, int limit)
        {
            try
            {
                var text = (query ?? string.Empty).Trim();
                if (text.Length == 0)
                {
                    return Fail("query 不能为空：请给出要查找的概念 / 关键词");
                }

                var terms = BuildDocTerms(text);
                if (terms.Count == 0)
                {
                    return Fail("query 里没有可检索的词");
                }

                var maxHits = limit <= 0 ? 8 : Math.Min(limit, 30);
                var fileRegex = string.IsNullOrWhiteSpace(glob) ? null : GlobToRegex(glob.Trim());
                var hits = new List<DocHit>();

                foreach (var file in EnumerateFilesCached(8))
                {
                    var relative = Relative(file);
                    if (IsLikelyBinary(relative))
                    {
                        continue;
                    }

                    if (fileRegex != null ? !fileRegex.IsMatch(relative) : !IsDocumentationFile(relative))
                    {
                        continue;
                    }

                    string content;
                    try
                    {
                        content = ReadTextCapped(file, out _);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    var pathScore = ScoreDocPath(relative, terms, text);

                    for (var i = 0; i < lines.Length; i++)
                    {
                        var score = ScoreDocLine(lines[i], terms, text);
                        if (score <= 0)
                        {
                            continue;
                        }

                        // 标题行更可能是「这一节讲什么」，给一点额外权重
                        if (lines[i].TrimStart().StartsWith("#", StringComparison.Ordinal))
                        {
                            score += 1.5;
                        }

                        hits.Add(new DocHit
                        {
                            Path = relative,
                            Line = i + 1,
                            Score = score + pathScore,
                            Lines = lines,
                        });
                    }
                }

                if (hits.Count == 0)
                {
                    return Ok("没有在工作区文档里找到「" + text + "」相关的内容" +
                              "（可换关键词，或把 glob 设为 * 扩大范围）");
                }

                // 同一文件最多保留 3 段最相关的命中，避免一个 README 把结果全占满
                var ranked = hits
                    .GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g => g.OrderByDescending(h => h.Score).Take(3))
                    .OrderByDescending(h => h.Score)
                    .ThenBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(h => h.Line)
                    .Take(maxHits)
                    .ToList();

                var builder = new StringBuilder();
                builder.Append("文档检索：「").Append(text).Append("」命中 ")
                       .Append(ranked.Count).Append(" 段");
                if (ranked.Count >= maxHits)
                {
                    builder.Append("（已达上限，可缩小关键词或指定 glob）");
                }

                builder.Append("：\n\n");

                foreach (var hit in ranked)
                {
                    builder.Append("── ").Append(hit.Path).Append(':').Append(hit.Line)
                           .Append("（相关度 ").Append(hit.Score.ToString("0.0")).Append("）\n");

                    var from = Math.Max(0, hit.Line - 2);
                    var to = Math.Min(hit.Lines.Length - 1, hit.Line);
                    for (var i = from; i <= to; i++)
                    {
                        builder.Append(i == hit.Line - 1 ? "> " : "  ")
                               .Append(ClipLine(hit.Lines[i])).Append('\n');
                    }

                    builder.Append('\n');
                }

                builder.Append("提示：需要完整上下文时用 read_file 读取对应文件与行号");
                return Ok(builder.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Fail("文档检索失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 该路径是否属于「说明性文档」
        /// </summary>
        private static bool IsDocumentationFile(string relativePath)
        {
            var path = (relativePath ?? string.Empty).Replace('\\', '/');
            var extension = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(extension) && DocumentationExtensions.Contains(extension))
            {
                return true;
            }

            var name = Path.GetFileName(path) ?? string.Empty;
            if (name.Equals("README", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CONTRIBUTING", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AGENTS", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LANGCODE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CLAUDE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CONVENTIONS", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".langcoderules", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".cursorrules", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".windsurfrules", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".clinerules", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 放在 docs/ 下的文件即使扩展名不常见，也按文档处理
            return path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 拆词：保留原词；中文长词额外补二元组，提升「上下文压缩」这类查询的召回
        /// </summary>
        private static List<string> BuildDocTerms(string query)
        {
            var result = new List<string>();

            void Add(string value)
            {
                var term = (value ?? string.Empty).Trim();
                if (term.Length == 0 || result.Contains(term, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                result.Add(term);
            }

            foreach (Match match in DocTermRegex.Matches(query ?? string.Empty))
            {
                Add(match.Value);
            }

            if (result.Count == 0)
            {
                Add(query);
            }

            foreach (var term in result.ToList())
            {
                if (term.Length < 4 || !ContainsCjk(term))
                {
                    continue;
                }

                for (var i = 0; i + 1 < term.Length && result.Count < 16; i++)
                {
                    Add(term.Substring(i, 2));
                }
            }

            return result.Take(16).ToList();
        }

        private static bool ContainsCjk(string text)
        {
            foreach (var c in text ?? string.Empty)
            {
                if (c >= '\u4e00' && c <= '\u9fff')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 一行命中的相关度：完整短语 > 多个独立词 > 单个词
        /// </summary>
        private static double ScoreDocLine(string line, IList<string> terms, string query)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return 0;
            }

            var score = 0.0;
            if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 6;
            }

            var matched = 0;
            foreach (var term in terms)
            {
                var count = CountDocOccurrences(line, term);
                if (count == 0)
                {
                    continue;
                }

                matched++;
                score += Math.Min(4, count) * 1.5;
                if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    score += 1.0;
                }
            }

            if (matched > 1)
            {
                score += matched * 0.5;
            }

            return score;
        }

        /// <summary>
        /// 路径命中加权：查「安装」时，docs/install.md 比正文里偶然出现一次更相关
        /// </summary>
        private static double ScoreDocPath(string path, IList<string> terms, string query)
        {
            var score = 0.0;
            if (path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 4;
            }

            foreach (var term in terms)
            {
                if (path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 1.5;
                }
            }

            return Math.Min(10, score);
        }

        private static int CountDocOccurrences(string text, string term)
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
                index += Math.Max(1, term.Length);
                if (count >= 10)
                {
                    break;
                }
            }

            return count;
        }

        /// <summary>
        /// 搜索命中行的最大展示字符数：一行几万字符的压缩产物 / 生成代码会把整个结果撑爆
        /// </summary>
        private const int MaxHitLineChars = 300;

        /// <summary>
        /// 裁剪搜索命中的单行（先 Trim，再按字符数截断）
        /// </summary>
        private static string ClipLine(string line)
        {
            var text = (line ?? string.Empty).Trim();
            return text.Length <= MaxHitLineChars
                ? text
                : text.Substring(0, MaxHitLineChars) + "…";
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
                var fileRegex = GlobToRegex(string.IsNullOrWhiteSpace(glob) ? "**" : glob.Trim());
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
            var fileRegex = GlobToRegex(string.IsNullOrWhiteSpace(glob) ? "**" : glob.Trim());

            try
            {
                var originals = new List<KeyValuePair<string, string>>();
                var csharpFiles = new List<KeyValuePair<string, string>>();

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
                        // 重命名后的落盘会做全文并发校验，这里也读完整文本，
                        // 避免大文件因「截断后的原文 ≠ 完整当前文本」而被误判为冲突。
                        original = ReadAllTextDetected(file);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    // 只有文本里真的出现该名字的文件才需要解析 / 改名
                    if (original.IndexOf(from, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    originals.Add(new KeyValuePair<string, string>(relative, original));
                    if (RoslynCodeService.IsAvailable && RoslynCodeService.IsCSharpFile(relative))
                    {
                        csharpFiles.Add(new KeyValuePair<string, string>(relative, original));
                    }
                }

                // C# 走语义模型：只改真正绑定到该符号的标识符，避免误改同名局部变量 / 成员
                var semanticResults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string semanticError = null;

                if (csharpFiles.Count > 0)
                {
                    var renamed = RoslynCodeService.TryRenameAcrossFiles(csharpFiles, from, to, out semanticError, out var ambiguous);
                    if (renamed == null && ambiguous)
                    {
                        error = semanticError;
                        return null;
                    }

                    if (renamed != null)
                    {
                        foreach (var item in renamed)
                        {
                            semanticResults[item.Path] = item.NewText;
                        }
                    }
                }

                var nonCsharpPattern = new Regex("\\b" + Regex.Escape(from) + "\\b", RegexOptions.CultureInvariant);

                foreach (var pair in originals)
                {
                    var relative = pair.Key;
                    var original = pair.Value;
                    string next;

                    if (RoslynCodeService.IsAvailable && RoslynCodeService.IsCSharpFile(relative))
                    {
                        // 语义重命名没有找到声明 / 编译失败时，宁可不改，也不退回容易误伤的正则
                        if (!semanticResults.TryGetValue(relative, out next))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (!nonCsharpPattern.IsMatch(original))
                        {
                            continue;
                        }

                        next = nonCsharpPattern.Replace(original, to);
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

                // 唯一候选全是 C# 且语义解析失败：把真实原因带回给模型
                if (pendings.Count == 0 && !string.IsNullOrWhiteSpace(semanticError))
                {
                    error = semanticError;
                    return null;
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
        /// 准备「删除文件」：记录原文与编码，审批通过后删除；撤销时按原编码还原
        /// </summary>
        public ToolResult PrepareDelete(string relativePath)
        {
            try
            {
                var full = ResolveWithinRoot(relativePath);
                if (Directory.Exists(full))
                {
                    return Fail("delete_file 只删除文件，不删除目录：" + relativePath +
                                "（请先清理目录内容，或改用 run_command 明确指定）");
                }

                if (!File.Exists(full))
                {
                    return Fail("文件不存在：" + relativePath);
                }

                var original = ReadAllTextDetected(full, out var encoding, out var hasBom);
                var relative = NormalizePendingPath(relativePath, full);

                return new ToolResult
                {
                    Ok = true,
                    Output = "准备删除 " + relative,
                    Pending = new PendingEdit
                    {
                        RelativePath = relative,
                        OriginalText = original,
                        NewText = string.Empty,
                        IsDelete = true,
                        Diff = SimpleDiff.Diff(original, string.Empty),
                        OriginalEncoding = encoding,
                        OriginalHadBom = hasBom,
                        OriginalUsedCrLf = UsesCrLf(original),
                    },
                };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// 准备「移动 / 重命名文件」：目标已存在时拒绝，审批通过后移动
        /// </summary>
        public ToolResult PrepareMove(string fromPath, string toPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fromPath) || string.IsNullOrWhiteSpace(toPath))
                {
                    return Fail("from 与 to 都不能为空");
                }

                var fromFull = ResolveWithinRoot(fromPath);
                var toFull = ResolveWithinRoot(toPath);

                if (Directory.Exists(fromFull))
                {
                    return Fail("move_file 只移动文件，不移动目录：" + fromPath);
                }

                if (!File.Exists(fromFull))
                {
                    return Fail("文件不存在：" + fromPath);
                }

                if (string.Equals(fromFull, toFull, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail("源路径与目标路径相同，无需移动：" + fromPath);
                }

                if (Directory.Exists(toFull))
                {
                    return Fail("目标路径是一个已存在的目录：" + toPath);
                }

                if (File.Exists(toFull))
                {
                    return Fail("目标文件已存在，已拒绝覆盖：" + toPath);
                }

                var original = ReadAllTextDetected(fromFull, out var encoding, out var hasBom);
                var fromRelative = NormalizePendingPath(fromPath, fromFull);
                var toRelative = NormalizePendingPath(toPath, toFull);

                return new ToolResult
                {
                    Ok = true,
                    Output = "准备移动 " + fromRelative + " → " + toRelative,
                    Pending = new PendingEdit
                    {
                        RelativePath = fromRelative,
                        OriginalText = original,
                        NewText = original,
                        MoveToPath = toRelative,
                        Diff = [],
                        OriginalEncoding = encoding,
                        OriginalHadBom = hasBom,
                        OriginalUsedCrLf = UsesCrLf(original),
                    },
                };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// 统一 PendingEdit 里的相对路径：模型给相对路径时原样归一化；
        /// 给绝对路径（多根工作区）时换算成所在根内的相对路径
        /// </summary>
        private string NormalizePendingPath(string given, string fullPath)
        {
            var cleaned = (given ?? string.Empty).Trim().Replace('\\', '/');
            if (!Path.IsPathRooted(given))
            {
                return cleaned;
            }

            var root = RootOf(fullPath) ?? _root;
            return RelativeTo(fullPath, root).Replace('\\', '/');
        }

        /// <summary>
        /// 准备「整文件写入」：算出 Diff，等审批后再落盘
        /// </summary>
        public ToolResult PrepareWrite(string relativePath, string content)
        {
            try
            {
                var full = ResolveWithinRoot(relativePath);
                var exists = File.Exists(full);
                var original = exists ? ReadAllTextDetected(full) : string.Empty;
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

                var original = ReadAllTextDetected(full);
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

                var original = ReadAllTextDetected(full);
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

                // 删除 / 移动不是「写文本」，走各自的落盘路径（仍然保留乐观并发校验）
                if (edit.IsDelete)
                {
                    return ApplyDelete(edit);
                }

                if (edit.IsMove)
                {
                    return ApplyMove(edit);
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
                    if (edit.IsNewFile)
                    {
                        // 审批期间目标文件可能被别人创建：此时不能静默覆盖
                        if (File.Exists(full))
                        {
                            return Fail("目标文件在审批期间已被创建，请重新读取后再试：" + edit.RelativePath);
                        }

                        WriteTextPreserving(full, edit.NewText ?? string.Empty, new UTF8Encoding(false), false, false);
                    }
                    else
                    {
                        if (!File.Exists(full))
                        {
                            return Fail("文件在审批期间已被删除，请重新读取后再试：" + edit.RelativePath);
                        }

                        var current = ReadAllTextDetected(full, out var encoding, out var hasBom);

                        // 乐观并发校验：审批期间磁盘内容变了就拒绝写入，避免覆盖别人的改动
                        if (!string.Equals(current, edit.OriginalText ?? string.Empty, StringComparison.Ordinal))
                        {
                            return Fail("文件在审批期间已被修改，请重新读取后再试：" + edit.RelativePath);
                        }

                        WriteTextPreserving(
                            full,
                            edit.NewText ?? string.Empty,
                            encoding,
                            hasBom,
                            UsesCrLf(current));
                    }
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
        /// 落盘一次删除：审批期间文件被改动过就拒绝，避免误删用户的新内容
        /// </summary>
        private ToolResult ApplyDelete(PendingEdit edit)
        {
            var full = ResolveWithinRoot(edit.RelativePath);

            lock (GetWriteLock(full))
            {
                if (!File.Exists(full))
                {
                    return Fail("文件在审批期间已被删除，请重新读取后再试：" + edit.RelativePath);
                }

                var current = ReadAllTextDetected(full);
                if (!string.Equals(current, edit.OriginalText ?? string.Empty, StringComparison.Ordinal))
                {
                    return Fail("文件在审批期间已被修改，请重新读取后再试：" + edit.RelativePath);
                }

                File.Delete(full);
            }

            edit.Applied = true;
            InvalidateFileCache();

            var deleted = SimpleDiff.CountDeleted(edit.Diff);
            return Ok("已删除 " + edit.RelativePath + "（−" + deleted + "）");
        }

        /// <summary>
        /// 落盘一次移动：源文件在审批期间被改动 / 目标被占用都拒绝
        /// </summary>
        private ToolResult ApplyMove(PendingEdit edit)
        {
            var from = ResolveWithinRoot(edit.RelativePath);
            var to = ResolveWithinRoot(edit.MoveToPath);

            ToolResult failure = null;

            WithPathLocks(from, to, () =>
            {
                if (!File.Exists(from))
                {
                    failure = Fail("文件在审批期间已被删除，请重新读取后再试：" + edit.RelativePath);
                    return;
                }

                if (File.Exists(to))
                {
                    failure = Fail("目标文件在审批期间已被创建，已拒绝覆盖：" + edit.MoveToPath);
                    return;
                }

                if (Directory.Exists(to))
                {
                    failure = Fail("目标路径是一个已存在的目录：" + edit.MoveToPath);
                    return;
                }

                var current = ReadAllTextDetected(from);
                if (!string.Equals(current, edit.OriginalText ?? string.Empty, StringComparison.Ordinal))
                {
                    failure = Fail("文件在审批期间已被修改，请重新读取后再试：" + edit.RelativePath);
                    return;
                }

                var directory = Path.GetDirectoryName(to);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.Move(from, to);
            });

            if (failure != null)
            {
                return failure;
            }

            edit.Applied = true;
            InvalidateFileCache();
            return Ok("已移动 " + edit.RelativePath + " → " + edit.MoveToPath);
        }

        /// <summary>
        /// 同时锁住两个路径：按路径排序后依次取锁，避免两个相反方向的移动互相死锁
        /// </summary>
        private void WithPathLocks(string first, string second, Action action)
        {
            var left = string.Compare(first, second, StringComparison.OrdinalIgnoreCase) <= 0 ? first : second;
            var right = ReferenceEquals(left, first) ? second : first;

            lock (GetWriteLock(left))
            {
                lock (GetWriteLock(right))
                {
                    action();
                }
            }
        }

        /// <summary>
        /// 撤销一次已落盘的改动：改文件写回「改动前」的文本，新建文件直接删掉，
        /// 删除的文件按原编码还原，移动的文件移回原位置
        /// </summary>
        public ToolResult Revert(PendingEdit edit)
        {
            if (edit == null || string.IsNullOrWhiteSpace(edit.RelativePath))
            {
                return Fail("没有可撤销的改动");
            }

            try
            {
                if (edit.IsDelete)
                {
                    return RevertDelete(edit);
                }

                if (edit.IsMove)
                {
                    return RevertMove(edit);
                }

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

                // 还原同样按当前文件的编码与换行写回，不能在这一步把编码/换行改掉
                Encoding currentEncoding;
                bool currentHasBom;
                if (File.Exists(full))
                {
                    ReadAllTextDetected(full, out currentEncoding, out currentHasBom);
                }
                else
                {
                    currentEncoding = new UTF8Encoding(false);
                    currentHasBom = false;
                }

                WriteTextPreserving(
                    full,
                    edit.OriginalText ?? string.Empty,
                    currentEncoding,
                    currentHasBom,
                    UsesCrLf(edit.OriginalText));
                edit.Applied = false;
                InvalidateFileCache();
                return Ok("已还原：" + edit.RelativePath);
            }
            catch (Exception ex)
            {
                return Fail("撤销失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 撤销一次删除：按删除前的编码与换行写回原文
        /// </summary>
        private ToolResult RevertDelete(PendingEdit edit)
        {
            var full = ResolveWithinRoot(edit.RelativePath);

            lock (GetWriteLock(full))
            {
                if (File.Exists(full))
                {
                    return Fail("撤销删除失败：文件已存在（可能已被其它操作重新创建）：" + edit.RelativePath);
                }

                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                WriteTextPreserving(
                    full,
                    edit.OriginalText ?? string.Empty,
                    edit.OriginalEncoding ?? new UTF8Encoding(false),
                    edit.OriginalHadBom,
                    edit.OriginalUsedCrLf);
            }

            edit.Applied = false;
            InvalidateFileCache();
            return Ok("已还原被删除的文件：" + edit.RelativePath);
        }

        /// <summary>
        /// 撤销一次移动：把目标文件移回原位置
        /// </summary>
        private ToolResult RevertMove(PendingEdit edit)
        {
            var from = ResolveWithinRoot(edit.RelativePath);
            var to = ResolveWithinRoot(edit.MoveToPath);

            ToolResult failure = null;

            WithPathLocks(from, to, () =>
            {
                if (!File.Exists(to))
                {
                    failure = Fail("撤销移动失败：目标文件不存在：" + edit.MoveToPath);
                    return;
                }

                if (File.Exists(from))
                {
                    failure = Fail("撤销移动失败：原位置已存在同名文件：" + edit.RelativePath);
                    return;
                }

                if (!string.Equals(ReadAllTextDetected(to), edit.NewText ?? string.Empty, StringComparison.Ordinal))
                {
                    failure = Fail("撤销移动失败：目标文件在移动后已被修改：" + edit.MoveToPath);
                    return;
                }

                var directory = Path.GetDirectoryName(from);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.Move(to, from);
            });

            if (failure != null)
            {
                return failure;
            }

            edit.Applied = false;
            InvalidateFileCache();
            return Ok("已移回：" + edit.RelativePath);
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
        private static void WriteTextPreserving(
            string fullPath,
            string text,
            Encoding encoding,
            bool hasBom,
            bool useCrLf)
        {
            var body = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (useCrLf)
            {
                body = body.Replace("\n", "\r\n");
            }

            var targetEncoding = encoding ?? new UTF8Encoding(hasBom);
            var temp = fullPath + ".langcode.tmp";

            try
            {
                File.WriteAllText(temp, body, targetEncoding);

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
            finally
            {
                // 替换失败时别把临时文件留在工作区里
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

        /// <summary>
        /// 读文件并识别常见 BOM 编码（UTF-8 / UTF-16 / UTF-32）。
        /// 无 BOM 时按 UTF-8 处理，与原有行为一致。
        /// </summary>
        private static string ReadAllTextDetected(string path)
        {
            return ReadAllTextDetected(path, out _, out _);
        }

        private static string ReadAllTextDetected(string path, out Encoding encoding, out bool hasBom)
        {
            hasBom = false;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[4];
                var read = stream.Read(head, 0, head.Length);
                stream.Position = 0;

                if (read >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0x00 && head[3] == 0x00)
                {
                    encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
                    hasBom = true;
                }
                else if (read >= 4 && head[0] == 0x00 && head[1] == 0x00 && head[2] == 0xFE && head[3] == 0xFF)
                {
                    encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
                    hasBom = true;
                }
                else if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                {
                    encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                    hasBom = true;
                }
                else if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE)
                {
                    encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                    hasBom = true;
                }
                else if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF)
                {
                    encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
                    hasBom = true;
                }
                else
                {
                    encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                }

                using (var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true))
                {
                    return reader.ReadToEnd();
                }
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
            var pattern = (glob ?? "**").Trim().Replace('\\', '/');
            var builder = new StringBuilder("^");

            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '*')
                {
                    var doubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                    if (doubleStar)
                    {
                        i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                        {
                            // **/ 匹配零层或多层目录：src/**/*.tsx 也能匹配 src/App.tsx
                            i++;
                            builder.Append("(?:.*/)?");
                        }
                        else
                        {
                            builder.Append(".*");
                        }
                    }
                    else
                    {
                        // 单星不跨目录分隔符
                        builder.Append("[^/]*");
                    }

                    continue;
                }

                if (c == '?')
                {
                    builder.Append("[^/]");
                    continue;
                }

                builder.Append(Regex.Escape(c.ToString()));
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

