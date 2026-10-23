using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LangCode.Workspace
{
    /// <summary>
    /// 「仓库地图」：给系统提示注入一份轻量大纲（目录 + 入口文件 + 关键符号），
    /// 让模型第一轮就知道项目怎么分层，少做几次 list_files / 全仓库搜索。
    ///
    /// 设计取舍：
    /// <list type="bullet">
    /// <item>只读少量文件（默认最多 120 个、单文件 150KB），不抢语义索引的活；</item>
    /// <item>结果缓存 45 秒，连续对话不会每轮都重新扫盘；</item>
    /// <item>任何异常都退回空串，绝不因为「地图」失败而影响对话。</item>
    /// </list>
    /// </summary>
    public static class RepoMap
    {
        private static readonly string[] IgnoredDirectories =
        {
            ".git", ".vs", ".idea", ".vscode", ".langcode", "bin", "obj", "node_modules", "packages",
            "TestResults", ".next", "dist", "build", "out", "target", "coverage", "__pycache__",
            ".venv", "venv", "vendor", "Debug", "Release",
        };

        private static readonly HashSet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".vb", ".fs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte",
            ".java", ".kt", ".kts", ".scala", ".go", ".rs", ".rb", ".php", ".py", ".swift",
            ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".m", ".mm", ".cshtml", ".razor",
            ".sql", ".sh", ".ps1", ".psm1", ".yml", ".yaml", ".toml",
        };

        private static readonly string[] EntryNames =
        {
            "program.cs", "startup.cs", "main.cs", "main.go", "main.rs", "main.py", "main.ts", "main.js",
            "index.ts", "index.tsx", "index.js", "index.jsx", "app.tsx", "app.vue", "app.jsx",
            "langcodepackage.cs", "package.json", "pyproject.toml", "cargo.toml", "go.mod",
            "dockerfile", "readme.md", "agents.md",
        };

        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);

        private sealed class CacheEntry
        {
            public DateTime AtUtc;
            public string Text;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 生成仓库地图；没有可用根 / 没有源码时返回 null
        /// </summary>
        public static string Build(IEnumerable<string> roots, int maxChars = 1800)
        {
            var list = (roots ?? Enumerable.Empty<string>()).Where(root => !string.IsNullOrWhiteSpace(root)).Select(root =>
                {
                    try
                    {
                        return Path.GetFullPath(root).TrimEnd('\\', '/');
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }).Where(root => root != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (list.Count == 0)
            {
                return null;
            }

            var key = string.Join("|", list.Select(root => root.ToLowerInvariant())) + "#" + maxChars;

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.AtUtc < Ttl)
                {
                    return cached.Text;
                }
            }

            string text;
            try
            {
                text = BuildCore(list, Math.Max(600, maxChars));
            }
            catch (Exception)
            {
                text = null;
            }

            lock (Gate)
            {
                Cache[key] = new CacheEntry { AtUtc = DateTime.UtcNow, Text = text };
            }

            return text;
        }

        /// <summary>
        /// 文件改动后让缓存立刻失效（下一轮重新生成）
        /// </summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                Cache.Clear();
            }
        }

        private static string BuildCore(IList<string> roots, int maxChars)
        {
            var files = new List<string>();
            const int maxFiles = 2500;

            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var pending = new Queue<string>();
                pending.Enqueue(root);

                while (pending.Count > 0 && files.Count < maxFiles)
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
                        if (!IgnoredDirectories.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
                        {
                            pending.Enqueue(directory);
                        }
                    }

                    string[] children;
                    try
                    {
                        children = Directory.GetFiles(current);
                        Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (var file in children)
                    {
                        if (SourceExtensions.Contains(Path.GetExtension(file)) ||
                            IsEntryFile(file) ||
                            string.Equals(Path.GetExtension(file), ".sln", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetExtension(file), ".csproj", StringComparison.OrdinalIgnoreCase))
                        {
                            files.Add(file);
                            if (files.Count >= maxFiles)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            if (files.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            builder.AppendLine("【仓库地图（自动生成，可能不完整；具体实现仍以 read_file / semantic_search 为准）】");

            // 目录规模：取相对路径的前两层，给出「哪个目录是主战场」
            var directoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var relativeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var relative = MakeRelative(file, roots);
                if (relative.Length == 0)
                {
                    continue;
                }

                relativeFiles[file] = relative;
                var directory = DirectoryKey(relative);
                directoryCounts.TryGetValue(directory, out var count);
                directoryCounts[directory] = count + 1;
            }

            var topDirectories = directoryCounts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .Select(pair => pair.Key + " (" + pair.Value + ")");

            builder.Append("目录：").AppendLine(string.Join("、", topDirectories));

            // 入口文件：按名称 / 项目文件挑选，最多 8 个
            var entryFiles = files
                .Where(IsEntryFile)
                .Select(file => relativeFiles.TryGetValue(file, out var relative) ? relative : null)
                .Where(relative => !string.IsNullOrWhiteSpace(relative))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            if (entryFiles.Count > 0)
            {
                builder.Append("入口：").AppendLine(string.Join("、", entryFiles));
            }

            // 关键符号：只读少量「最像主代码」的文件，避免地图生成本身变成一次全仓库扫描
            var candidates = files
                .Where(file => SourceExtensions.Contains(Path.GetExtension(file)))
                .OrderByDescending(file => IsEntryFile(file) ? 2 : 1)
                .ThenBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
                .Take(120)
                .ToList();

            var symbolLines = new List<string>();

            foreach (var file in candidates)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists || info.Length > 150 * 1024)
                    {
                        continue;
                    }

                    var text = File.ReadAllText(file);
                    var symbols = SymbolScanner.Scan(text, 12);
                    if (symbols.Count == 0)
                    {
                        continue;
                    }

                    var relative = relativeFiles.TryGetValue(file, out var value) ? value : file;
                    var picked = symbols.Take(3).Select(symbol => symbol.Kind + " " + symbol.Name);
                    symbolLines.Add(relative + "：" + string.Join("、", picked));

                    if (symbolLines.Count >= 12)
                    {
                        break;
                    }
                }
                catch (Exception)
                {
                    // 单个文件读不了就跳过
                }
            }

            if (symbolLines.Count > 0)
            {
                builder.AppendLine("关键符号：");
                foreach (var line in symbolLines)
                {
                    builder.Append("  ").AppendLine(line);
                }
            }

            var result = builder.ToString().TrimEnd();
            if (result.Length > maxChars)
            {
                result = result.Substring(0, maxChars) + "\n…（地图过长，已截断）";
            }

            return result;
        }

        private static bool IsEntryFile(string path)
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (EntryNames.Contains(name.ToLowerInvariant()))
            {
                return true;
            }

            var extension = Path.GetExtension(name);
            return string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase);
        }

        private static string DirectoryKey(string relative)
        {
            var normalized = (relative ?? string.Empty).Replace('\\', '/');
            var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
            {
                return ".";
            }

            return parts.Length <= 3 ? string.Join("/", parts.Take(parts.Length - 1)) : string.Join("/", parts.Take(2)) + "/…";
        }

        private static string MakeRelative(string full, IList<string> roots)
        {
            foreach (var root in roots)
            {
                var prefix = root + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return full.Substring(prefix.Length).Replace('\\', '/');
                }
            }

            return full.Replace('\\', '/');
        }
    }
}
