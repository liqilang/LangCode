using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LangCode.Workspace
{
    /// <summary>
    /// 一条 <c>.gitignore</c> 规则（含它所在的目录，用于嵌套 <c>.gitignore</c>）
    /// </summary>
    internal sealed class GitIgnoreRule
    {
        public GitIgnoreRule(string baseDirectory, bool negated, bool directoryOnly, bool anchored, string pattern)
        {
            // 统一用 '/'：规则来自 .gitignore，本来就是 '/' 分隔
            BaseDirectory = string.IsNullOrEmpty(baseDirectory) ? string.Empty : baseDirectory.Trim('/');
            Negated = negated;
            DirectoryOnly = directoryOnly;
            Anchored = anchored;
            Pattern = pattern;
            Regex = BuildRegex(pattern, anchored);
        }

        /// <summary>
        /// 规则所在目录（相对工作区根，'/'; 根目录为 ""）
        /// </summary>
        public string BaseDirectory { get; }

        /// <summary>
        /// <c>!</c> 前缀：把已经忽略的重新纳入
        /// </summary>
        public bool Negated { get; }

        /// <summary>
        /// 以 <c>/</c> 结尾：只匹配目录（及其内部）
        /// </summary>
        public bool DirectoryOnly { get; }

        /// <summary>
        /// 是否锚定在 <see cref="BaseDirectory"/>（模式内含非末尾的 '/' 或前导 '/'）
        /// </summary>
        public bool Anchored { get; }

        /// <summary>
        /// 去掉前导 '/' 与尾部 '/' 后的模式
        /// </summary>
        public string Pattern { get; }

        private Regex Regex { get; }

        /// <summary>
        /// 该规则是否命中相对工作区根的路径 <paramref name="path"/>
        /// </summary>
        public bool Matches(string path, bool isDirectory)
        {
            var sub = SubPath(path, out var isUnder);
            if (!isUnder || sub.Length == 0)
            {
                return false;
            }

            if (DirectoryOnly)
            {
                // 目录规则：命中路径自身（当它是目录）或其任意祖先目录
                if (isDirectory && Regex.IsMatch(sub))
                {
                    return true;
                }

                var index = sub.LastIndexOf('/');
                while (index > 0)
                {
                    if (Regex.IsMatch(sub.Substring(0, index)))
                    {
                        return true;
                    }

                    index = sub.LastIndexOf('/', index - 1);
                }

                return false;
            }

            return Regex.IsMatch(sub);
        }

        /// <summary>
        /// 把 <paramref name="path"/> 折算成相对本规则基准目录的子路径
        /// </summary>
        private string SubPath(string path, out bool isUnder)
        {
            if (BaseDirectory.Length == 0)
            {
                isUnder = true;
                return path;
            }

            if (string.Equals(path, BaseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                isUnder = true;
                return string.Empty;
            }

            var prefix = BaseDirectory + "/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                isUnder = true;
                return path.Substring(prefix.Length);
            }

            isUnder = false;
            return string.Empty;
        }

        /// <summary>
        /// 把 gitignore 的通配语法编译成正则
        /// </summary>
        private static Regex BuildRegex(string pattern, bool anchored)
        {
            var builder = new StringBuilder();
            builder.Append(anchored ? "^" : "(^|.*/)");

            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];

                if (c == '*')
                {
                    // '**' → 跨目录；单个 '*' → 不跨目录
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        // '**/' 开头表示任意层级；其余当作跨目录通配
                        if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                        {
                            builder.Append("(.*/)?");
                            i += 2;
                        }
                        else
                        {
                            builder.Append(".*");
                            i++;
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    continue;
                }

                if (c == '?')
                {
                    builder.Append("[^/]");
                    continue;
                }

                if (c == '[')
                {
                    // 字符类：原样透传（gitignore 与正则的字符类语法基本一致）
                    var close = pattern.IndexOf(']', i + 1);
                    if (close > i)
                    {
                        var cls = pattern.Substring(i, close - i + 1);
                        builder.Append(ConvertCharClass(cls));
                        i = close;
                        continue;
                    }
                }

                builder.Append(Regex.Escape(c.ToString()));
            }

            builder.Append('$');
            return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string ConvertCharClass(string cls)
        {
            // 直接把 [abc] 交给正则；把 gitignore 里 '!' 作取反的写法转成 '^'
            var body = cls.Substring(1, cls.Length - 2);
            if (body.StartsWith("!", StringComparison.Ordinal))
            {
                body = "^" + body.Substring(1);
            }

            return "[" + body + "]";
        }
    }

    /// <summary>
    /// 尊重 <c>.gitignore</c> 的忽略匹配器
    /// </summary>
    public sealed class GitIgnoreMatcher
    {
        /// <summary>
        /// 嵌套 .gitignore 时最多下探的目录层数（保护超大仓库）
        /// </summary>
        private const int MaxNestedDepth = 6;

        private static readonly string[] SkippedNames =
        {
            ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "TestResults",
        };

        private readonly IList<GitIgnoreRule> _rules;

        private GitIgnoreMatcher(IList<GitIgnoreRule> rules)
        {
            _rules = rules;
        }

        /// <summary>
        /// 规则条数（界面可据此显示「已读取 N 条忽略规则」）
        /// </summary>
        public int RuleCount => _rules.Count;

        /// <summary>
        /// 从工作区根加载忽略规则：读根 <c>.gitignore</c>、<c>.git/info/exclude</c>，
        /// 并下探有限层数收集嵌套 <c>.gitignore</c>（按「更深的规则后读、优先级更高」合并）
        /// </summary>
        public static GitIgnoreMatcher Load(string root)
        {
            var rules = new List<GitIgnoreRule>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return new GitIgnoreMatcher(rules);
            }

            try
            {
                var normalizedRoot = Path.GetFullPath(root);
                Walk(normalizedRoot, string.Empty, 0, rules);
            }
            catch (Exception)
            {
                // 任何 IO / 路径异常都不该阻断工作区：拿已有的规则继续用
            }

            return new GitIgnoreMatcher(rules);
        }

        /// <summary>
        /// 从若干行文本解析规则（纯函数，便于自检）
        /// </summary>
        public static GitIgnoreMatcher Parse(string baseDirectory, IEnumerable<string> lines)
        {
            var rules = new List<GitIgnoreRule>();
            AddRules(rules, baseDirectory, lines);
            return new GitIgnoreMatcher(rules);
        }

        /// <summary>
        /// 路径（相对工作区根，可含 '\\'）是否被忽略
        /// </summary>
        public bool IsIgnored(string relativePath, bool isDirectory)
        {
            var path = Normalize(relativePath);
            if (path.Length == 0)
            {
                return false;
            }

            // 后面出现的规则覆盖前面的（与 git 的「最后匹配者生效」一致）
            var ignored = false;
            for (var i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].Matches(path, isDirectory))
                {
                    ignored = !_rules[i].Negated;
                }
            }

            return ignored;
        }

        private static void Walk(string directory, string relativeDirectory, int depth, List<GitIgnoreRule> rules)
        {
            // ① 当前目录的 .gitignore
            try
            {
                var ignoreFile = Path.Combine(directory, ".gitignore");
                if (File.Exists(ignoreFile))
                {
                    AddRules(rules, relativeDirectory, File.ReadAllLines(ignoreFile));
                }
            }
            catch (Exception)
            {
                // 读不到就跳过
            }

            if (depth >= MaxNestedDepth)
            {
                return;
            }

            // ② 下探子目录（跳过明显的重目录）
            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(directory);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var sub in subDirectories)
            {
                var name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name) || Array.IndexOf(SkippedNames, name) >= 0)
                {
                    continue;
                }

                var childRelative = relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;
                Walk(sub, childRelative, depth + 1, rules);
            }
        }

        private static void AddRules(List<GitIgnoreRule> rules, string baseDirectory, IEnumerable<string> lines)
        {
            if (lines == null)
            {
                return;
            }

            foreach (var rawLine in lines)
            {
                var line = rawLine ?? string.Empty;

                // 去掉行尾空白（git 忽略行尾空格，除非转义）；整行注释 / 空行跳过
                line = line.TrimEnd();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var negated = false;
                if (line[0] == '!')
                {
                    negated = true;
                    line = line.Substring(1);
                }

                if (line.Length == 0)
                {
                    continue;
                }

                var directoryOnly = line.EndsWith("/", StringComparison.Ordinal);
                if (directoryOnly)
                {
                    line = line.Substring(0, line.Length - 1);
                }

                // 前导 '/' 锚定到本目录；模式中间出现 '/' 也算锚定
                var anchored = line.StartsWith("/", StringComparison.Ordinal);
                if (anchored)
                {
                    line = line.Substring(1);
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.IndexOf('/') >= 0)
                {
                    anchored = true;
                }

                try
                {
                    rules.Add(new GitIgnoreRule(baseDirectory, negated, directoryOnly, anchored, line));
                }
                catch (Exception)
                {
                    // 正则构造失败（畸形模式）：跳过该条，不影响其余规则
                }
            }
        }

        private static string Normalize(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return string.Empty;
            }

            var value = relativePath.Replace('\\', '/').Trim();
            value = value.TrimStart('/');

            // 去掉 "./" 前缀与多余斜杠
            while (value.StartsWith("./", StringComparison.Ordinal))
            {
                value = value.Substring(2);
            }

            return value.Replace("//", "/");
        }
    }
}

