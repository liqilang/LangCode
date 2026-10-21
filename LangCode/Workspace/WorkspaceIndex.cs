using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
            "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode", ".langcode",
            "packages", "dist", "build", "out", "target", ".next", "coverage",
            "__pycache__", ".venv", "venv", "vendor", "Debug", "Release",
        };

        private readonly string _root;
        private readonly object _gate = new object();

        private List<MentionItem> _items;

        /// <summary>
        /// 当前正在跑的扫描任务：后台预热与用户打开弹层同时发生时只扫一遍
        /// </summary>
        private Task<List<MentionItem>> _scanTask;

        /// <summary>
        /// 任务对应的版本号：<see cref="Invalidate"/> 后旧任务的结果会被丢弃
        /// </summary>
        private int _scanGeneration;

        /// <summary>
        /// <see cref="_scanTask"/> 启动时的版本号（与当前版本不一致就要重扫）
        /// </summary>
        private int _scanTaskGeneration;

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
                while (true)
                {
                    int generation;
                    Task<List<MentionItem>> scan;
                    lock (_gate)
                    {
                        if (_items != null)
                        {
                            return _items;
                        }

                        generation = _scanGeneration;
                        if (_scanTask == null || _scanTask.IsCompleted || _scanTaskGeneration != generation)
                        {
                            _scanTask = Task.Run(Scan);
                            _scanTaskGeneration = generation;
                        }

                        scan = _scanTask;
                    }

                    // 扫描在读盘，别握着锁；其它调用者会等同一个任务
                    List<MentionItem> items;
                    try
                    {
                        // 这个属性本身就是同步 API（调用方都不是 VS UI 线程），
                        // 这里同步等待后台扫描完成是有意为之，不会造成 UI 死锁
#pragma warning disable VSTHRD002
                        items = scan.Result;
#pragma warning restore VSTHRD002
                    }
                    catch (AggregateException)
                    {
                        items = new List<MentionItem>();
                    }

                    lock (_gate)
                    {
                        if (_scanGeneration != generation)
                        {
                            continue; // 扫描期间被失效：结果已过期，重扫
                        }

                        _items = items;
                        return _items;
                    }
                }
            }
        }

        /// <summary>
        /// 非阻塞取快照：还没扫完时返回 <c>false</c>（同时确保后台扫描已启动），界面可以据此显示「扫描中」
        /// </summary>
        public bool TryGetAll(out IList<MentionItem> items)
        {
            lock (_gate)
            {
                if (_items != null)
                {
                    items = _items;
                    return true;
                }

                if (_scanTask != null && _scanTask.IsCompleted && _scanTaskGeneration == _scanGeneration)
                {
                    _items = Complete(_scanTask);
                    items = _items;
                    return true;
                }

                if (_scanTask == null || _scanTask.IsCompleted || _scanTaskGeneration != _scanGeneration)
                {
                    _scanTask = Task.Run(Scan);
                    _scanTaskGeneration = _scanGeneration;
                }

                items = Array.Empty<MentionItem>();
                return false;
            }
        }

        /// <summary>
        /// 扫描完成时在后台线程回调一次（用于界面在「扫描中」之后自动刷新）
        /// </summary>
        public void WhenScanCompleted(Action callback)
        {
            if (callback == null)
            {
                return;
            }

            Task<List<MentionItem>> scan;
            lock (_gate)
            {
                if (_items != null)
                {
                    scan = null;
                }
                else
                {
                    if (_scanTask == null || _scanTask.IsCompleted || _scanTaskGeneration != _scanGeneration)
                    {
                        _scanTask = Task.Run(Scan);
                        _scanTaskGeneration = _scanGeneration;
                    }

                    scan = _scanTask;
                }
            }

            if (scan == null)
            {
                callback();
                return;
            }

            scan.ContinueWith(_ => callback(), TaskScheduler.Default);
        }

        private static List<MentionItem> Complete(Task<List<MentionItem>> scan)
        {
            try
            {
                // 只在任务已完成时调用（见 TryGetAll），取结果不会真正阻塞
#pragma warning disable VSTHRD002
                return scan.Result ?? new List<MentionItem>();
#pragma warning restore VSTHRD002
            }
            catch (AggregateException)
            {
                return new List<MentionItem>();
            }
        }

        /// <summary>
        /// 后台预热：打开工具窗口 / 切换工作区时先把文件表扫出来，用户点 @ 时不用等
        /// </summary>
        public void StartBackgroundScan()
        {
            if (!IsReady)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    _ = All;
                }
                catch (Exception)
                {
                    // 扫描失败时保持空表，用户点 @ 时会再试一次
                }
            });
        }

        /// <summary>
        /// 丢弃缓存（工作区文件变动后调用）
        /// </summary>
        public void Invalidate()
        {
            lock (_gate)
            {
                _items = null;
                _scanGeneration++;
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

