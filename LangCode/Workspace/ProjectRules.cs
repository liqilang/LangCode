using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LangCode.Workspace
{
    /// <summary>
    /// 项目规则文件加载器：把工作区里的「项目级约定」读成一段提示词
    /// <list type="bullet">
    /// <item>工作区根目录：<c>AGENTS.md</c> / <c>LANGCODE.md</c> / <c>.langcoderules</c>；</item>
    /// <item>Copilot 约定：<c>.github/copilot-instructions.md</c>（复用同一份，不必重写）；</item>
    /// <item>规则目录：<c>.langcode/rules/*.md</c>（按文件名排序，支持多条独立规则）</item>
    /// </list>
    /// </summary>
    public static class ProjectRules
    {
        /// <summary>
        /// 规则目录（相对工作区根）
        /// </summary>
        public const string RulesDirectory = ".langcode/rules";

        /// <summary>
        /// 项目根目录下按优先级识别的规则文件（相对路径，允许含子目录）
        /// </summary>
        private static readonly string[] RootRuleFiles =
        {
            "AGENTS.md",
            "LANGCODE.md",
            "CLAUDE.md",
            ".langcoderules",
            ".cursorrules",
            ".windsurfrules",
            ".clinerules",
            ".github/copilot-instructions.md",
            "CONVENTIONS.md",
        };

        /// <summary>
        /// 规则目录 → 识别的扩展名覆盖各家插件的规则目录约定
        /// </summary>
        private static readonly KeyValuePair<string, string[]>[] RuleDirectories =
        {
            new KeyValuePair<string, string[]>(".langcode/rules", new[] { "*.md" }),
            new KeyValuePair<string, string[]>(".cursor/rules", new[] { "*.mdc", "*.md" }),
            new KeyValuePair<string, string[]>(".clinerules", new[] { "*.md", "*.txt" }),
            new KeyValuePair<string, string[]>(".github/instructions", new[] { "*.md" }),
        };

        /// <summary>
        /// 全部规则拼起来的字符上限：规则再多也不能把系统提示词吃光
        /// （超长规则会稀释模型对「当前任务」的注意力）
        /// </summary>
        private const int MaxTotalChars = 20000;

        /// <summary>
        /// 单条规则文件的字符上限（截断时明确标注，避免模型以为读到了全文）
        /// </summary>
        private const int MaxFileChars = 8000;

        /// <summary>
        /// 把所有规则拼成一段文本；没有任何规则时返回 <c>null</c>
        /// </summary>
        public static string Load(string root)
        {
            return Load(root, out _);
        }

        /// <summary>
        /// 把所有规则拼成一段文本，并通过 <paramref name="loaded"/> 返回「实际读到了哪些文件」
        /// </summary>
        public static string Load(string root, out IList<string> loaded)
        {
            loaded = [];
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return null;
            }

            var builder = new StringBuilder();
            var files = Discover(root);

            foreach (var relative in files)
            {
                if (builder.Length >= MaxTotalChars)
                {
                    break;
                }

                var text = TryRead(root, relative);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (text.Length > MaxFileChars)
                {
                    text = text.Substring(0, MaxFileChars) + "\n…（规则过长，已截断）";
                }

                builder.Append("### ").Append(relative).Append('\n');
                builder.Append(text.Trim()).Append('\n');
                builder.Append('\n');
                loaded.Add(relative);
            }

            if (loaded.Count == 0)
            {
                return null;
            }

            var result = builder.ToString().Trim();
            if (result.Length > MaxTotalChars)
            {
                result = result.Substring(0, MaxTotalChars) + "\n…（项目规则总量过大，后续内容已省略）";
            }

            return result;
        }

        /// <summary>
        /// 按确定顺序列出「存在且可读」的规则文件（相对工作区根的路径，统一用 <c>/</c>）
        /// </summary>
        public static IList<string> Discover(string root)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return result;
            }

            // ① 根目录的约定文件（按固定优先级）
            foreach (var relative in RootRuleFiles)
            {
                try
                {
                    var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full))
                    {
                        result.Add(relative);
                    }
                }
                catch (Exception)
                {
                    // 路径非法：跳过该条，不影响其余规则
                }
            }

            // ② 各家插件的规则目录（按文件名排序，保证拼接顺序稳定）
            foreach (var entry in RuleDirectories)
            {
                try
                {
                    var directory = Path.Combine(root, entry.Key.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pattern in entry.Value)
                    {
                        var files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
                        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                        foreach (var file in files)
                        {
                            // 同一目录下同时匹配 *.md 与 *.txt 之类时去重
                            if (seen.Add(file))
                            {
                                result.Add(entry.Key + "/" + Path.GetFileName(file));
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // 规则目录不可读：跳过该目录，不影响其它规则
                }
            }

            return result;
        }

        private static string TryRead(string root, string relative)
        {
            try
            {
                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                return File.ReadAllText(full);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

