using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LangCode.Workspace
{
    /// <summary>
    /// 输入框 <c>@</c> 提及里的一条候选（文件或目录）
    /// </summary>
    public sealed class MentionItem
    {
        public MentionItem(string relativePath, bool isDirectory)
        {
            RelativePath = (relativePath ?? string.Empty).Replace('\\', '/');
            IsDirectory = isDirectory;
        }

        /// <summary>
        /// 相对工作区根的路径（始终用 <c>/</c>，与提及文本一致）
        /// </summary>
        public string RelativePath { get; }

        public bool IsDirectory { get; }

        /// <summary>
        /// 列表里显示的名字（最后一段）
        /// </summary>
        public string Name
        {
            get
            {
                var trimmed = RelativePath.TrimEnd('/');
                var slash = trimmed.LastIndexOf('/');
                return slash < 0 ? trimmed : trimmed.Substring(slash + 1);
            }
        }

        /// <summary>
        /// 目录显示带尾斜杠，让人一眼看出是文件夹
        /// </summary>
        public string InsertText => RelativePath + (IsDirectory ? "/" : string.Empty);
    }

    /// <summary>
    /// 工作区文件索引：给输入框的 <c>@</c> 提及提供**本项目的真实文件与目录**
    /// </summary>
    public sealed class WorkspaceIndex
    {
        /// <summary>
        /// 这些目录名一律跳过（依赖 / 构建产物 / 工具缓存）：扫它们既慢又没意义
        /// </summary>
        private static readonly HashSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode",
            "packages", "dist", "build", "out", "target", ".next", "coverage",
            "__pycache__", ".venv", "venv", "vendor", "Debug", "Release",
        };

        private readonly string _root;
        private readonly object _gate = new object();

        private List<MentionItem> _items;

        public WorkspaceIndex(string root)
        {
            _root = (root ?? string.Empty).TrimEnd('\\', '/');
        }

        /// <summary>
        /// 可以直接用于 <c>@</c> 提及吗（有根目录）
        /// </summary>
        public bool IsReady => _root.Length > 0 && Directory.Exists(_root);

        /// <summary>
        /// 索引里的全部条目（首次访问时扫描并缓存）
        /// </summary>
        public IList<MentionItem> All
        {
            get
            {
                lock (_gate)
                {
                    _items ??= Scan();

                    return _items;
                }
            }
        }

        /// <summary>
        /// 丢弃缓存（工作区文件变动后调用）
        /// </summary>
        public void Invalidate()
        {
            lock (_gate)
            {
                _items = null;
            }
        }

        /// <summary>
        /// 按关键词过滤：先匹配「路径包含」，再优先短路径与顶层条目
        /// </summary>
        public IList<MentionItem> Search(string query, int limit)
        {
            var all = All;
            if (all.Count == 0)
            {
                return [];
            }

            IEnumerable<MentionItem> matched = all;

            var text = (query ?? string.Empty).Trim();
            if (text.Length > 0)
            {
                matched = all.Where(item =>
                    item.RelativePath.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return matched
                .OrderBy(item => item.IsDirectory ? 0 : 1)
                .ThenBy(item => item.RelativePath.Count(c => c == '/'))
                .ThenBy(item => item.RelativePath.Length)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(limit <= 0 ? 20 : limit)
                .ToList();
        }

        private List<MentionItem> Scan()
        {
            var items = new List<MentionItem>();
            if (!IsReady)
            {
                return items;
            }

            try
            {
                // 目录：全扫（数量有限，且是最常用的提及目标）
                foreach (var directory in EnumerateSafely(_root, searchOption: SearchOption.AllDirectories, files: false))
                {
                    var relative = MakeRelative(directory);
                    if (relative.Length == 0)
                    {
                        continue;
                    }

                    items.Add(new MentionItem(relative, isDirectory: true));
                }

                // 文件：设上限，避免超大仓库把内存与界面拖死
                var fileCount = 0;
                foreach (var file in EnumerateSafely(_root, searchOption: SearchOption.AllDirectories, files: true))
                {
                    if (++fileCount > 3000)
                    {
                        break;
                    }

                    items.Add(new MentionItem(MakeRelative(file), isDirectory: false));
                }
            }
            catch (Exception)
            {
                // 权限 / 路径异常：返回已经扫到的部分，不抛
            }

            return items;
        }

        /// <summary>
        /// 自己递归而不是用 <c>EnumerateFiles(AllDirectories)</c>：后者遇到一个无权限的目录会整体抛异常，
        /// 这里逐层 try 掉，能扫多少算多少
        /// </summary>
        private IEnumerable<string> EnumerateSafely(string directory, SearchOption searchOption, bool files)
        {
            var pending = new Queue<string>();
            pending.Enqueue(directory);

            while (pending.Count > 0)
            {
                var current = pending.Dequeue();

                string[] subDirectories;
                try
                {
                    subDirectories = Directory.GetDirectories(current);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var sub in subDirectories)
                {
                    var name = Path.GetFileName(sub);
                    if (IgnoredDirectories.Contains(name))
                    {
                        continue;
                    }

                    if (!files)
                    {
                        yield return sub;
                    }

                    pending.Enqueue(sub);
                }

                if (!files)
                {
                    continue;
                }

                string[] filePaths;
                try
                {
                    filePaths = Directory.GetFiles(current);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var file in filePaths)
                {
                    yield return file;
                }
            }
        }

        private string MakeRelative(string fullPath)
        {
            var relative = fullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(_root.Length)
                : fullPath;

            return relative.TrimStart('\\', '/').Replace('\\', '/');
        }
    }
}

