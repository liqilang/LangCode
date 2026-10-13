using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LangCode.Workspace
{
    /// <summary>
    /// 一个检查点（影子仓库里的一次提交）
    /// </summary>
    public sealed class CheckpointInfo
    {
        /// <summary>
        /// 完整提交 id（40 位 sha）
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// 短 id（7 位，界面上显示）
        /// </summary>
        public string ShortId
        {
            get { return Id.Length > 7 ? Id.Substring(0, 7) : Id; }
        }

        /// <summary>
        /// 标签（用户轮次描述 / 时间）
        /// </summary>
        public string Label { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 本地时间的短文本（列表右侧显示）
        /// </summary>
        public string CreatedText
        {
            get
            {
                var local = CreatedUtc.ToLocalTime();
                return local.Date == DateTime.Today
                    ? local.ToString("HH:mm")
                    : local.ToString("M月d日 HH:mm");
            }
        }

        /// <summary>
        /// 列表里的一行说明
        /// </summary>
        public string Display => (string.IsNullOrWhiteSpace(Label) ? "检查点" : Label) + " · " + ShortId + " · " + CreatedText;
    }

    /// <summary>
    /// 检查点 / 「时间旅行」 
    /// </summary>
    public sealed class CheckpointStore
    {
        /// <summary>
        /// 影子仓库里要排除的目录（避免把构建产物 / 版本库存进检查点）
        /// </summary>
        private static readonly string[] ExcludedDirectories =
        {
            ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "TestResults", ".next", "dist", "build",
        };

        private readonly string _shadow;
        private readonly object _gate = new object();
        private bool _initialized;
        private bool _available = true;

        public CheckpointStore(string root)
        {
            Root = string.IsNullOrWhiteSpace(root) ? string.Empty : Path.GetFullPath(root);
            _shadow = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LangCode", "vsext", "checkpoints", HashRoot(Root));
        }

        /// <summary>
        /// 工作区根（绝对路径）
        /// </summary>
        public string Root { get; }

        /// <summary>
        /// 影子仓库位置（诊断用）
        /// </summary>
        public string ShadowDirectory => _shadow;

        /// <summary>
        /// 是否启用（关闭后不写任何检查点）
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// git 是否可用、影子仓库是否可用（不可用时所有操作优雅失败）
        /// </summary>
        public bool IsAvailable => Enabled && _available && !string.IsNullOrWhiteSpace(Root);

        /// <summary>
        /// 最近一次失败原因（诊断用）
        /// </summary>
        public string LastError { get; private set; } = string.Empty;

        /// <summary>
        /// 创建一个检查点；失败返回 null（原因见 <see cref="LastError"/>）
        /// </summary>
        public CheckpointInfo Create(string label)
        {
            if (!IsAvailable || !EnsureInitialized())
            {
                return null;
            }

            lock (_gate)
            {
                var add = RunGit("add -A", withWorkTree: true);
                if (!add.Ok)
                {
                    LastError = add.Output;
                    return null;
                }

                var message = Sanitize(string.IsNullOrWhiteSpace(label) ? "checkpoint" : label.Trim());
                var commit = RunGit(
                    "commit --quiet --allow-empty -m \"" + message + "\"",
                    withWorkTree: true);

                if (!commit.Ok)
                {
                    LastError = commit.Output;
                    return null;
                }

                var head = RunGit("rev-parse HEAD", withWorkTree: false);
                var id = head.Ok ? head.Output.Trim() : string.Empty;
                if (id.Length == 0)
                {
                    LastError = head.Output;
                    return null;
                }

                return new CheckpointInfo
                {
                    Id = id,
                    Label = message,
                    CreatedUtc = DateTime.UtcNow,
                };
            }
        }

        /// <summary>
        /// 按时间倒序列出最近的检查点
        /// </summary>
        public IList<CheckpointInfo> List(int max = 30)
        {
            var result = new List<CheckpointInfo>();
            if (!IsAvailable || !EnsureInitialized())
            {
                return result;
            }

            lock (_gate)
            {
                // 用 0x1f（单元分隔符）当字段分隔，避免标签里的空格 / 竖线干扰
                var log = RunGit(
                    "log --format=%H%x1f%cI%x1f%s -n " + Math.Max(1, max),
                    withWorkTree: false);

                if (!log.Ok)
                {
                    LastError = log.Output;
                    return result;
                }

                foreach (var line in log.Output.Replace("\r\n", "\n").Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var parts = line.Split('\u001f');
                    if (parts.Length < 2)
                    {
                        continue;
                    }

                    result.Add(new CheckpointInfo
                    {
                        Id = parts[0].Trim(),
                        CreatedUtc = ParseDate(parts.Length > 1 ? parts[1] : null),
                        Label = parts.Length > 2 ? parts[2] : string.Empty,
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// 恢复到某个检查点：把被跟踪的文件还原到该状态
        /// </summary>
        public ToolResult Restore(string id)
        {
            if (!IsAvailable || !EnsureInitialized())
            {
                return Fail("检查点不可用：" + (string.IsNullOrWhiteSpace(LastError) ? "未启用或找不到 git" : LastError));
            }

            var safeId = SanitizeId(id);
            if (safeId.Length == 0)
            {
                return Fail("检查点 id 非法");
            }

            lock (_gate)
            {
                var checkout = RunGit("checkout \"" + safeId + "\" -- .", withWorkTree: true);
                if (!checkout.Ok)
                {
                    LastError = checkout.Output;
                    return Fail("恢复失败：" + checkout.Output);
                }

                return new ToolResult
                {
                    Ok = true,
                    Output = "已恢复到检查点 " + (safeId.Length > 7 ? safeId.Substring(0, 7) : safeId) +
                             "（被跟踪的文件已还原；未跟踪 / 新建文件保留原样）",
                };
            }
        }

        /// <summary>
        /// 预览「相对该检查点，当前有哪些改动」（只读，不改任何文件）
        /// </summary>
        public string DiffAgainst(string id)
        {
            if (!IsAvailable || !EnsureInitialized())
            {
                return string.Empty;
            }

            var safeId = SanitizeId(id);
            if (safeId.Length == 0)
            {
                return string.Empty;
            }

            lock (_gate)
            {
                var diff = RunGit("diff \"" + safeId + "\" -- .", withWorkTree: true);
                if (!diff.Ok)
                {
                    LastError = diff.Output;
                    return string.Empty;
                }

                var text = diff.Output.Trim();
                return text.Length > 4000 ? text.Substring(0, 4000) + "\n…（差异过长，已截断）" : text;
            }
        }

        // ==================== 内部 ====================

        private bool EnsureInitialized()
        {
            if (_initialized)
            {
                return true;
            }

            lock (_gate)
            {
                if (_initialized)
                {
                    return true;
                }

                try
                {
                    Directory.CreateDirectory(_shadow);

                    if (!File.Exists(Path.Combine(_shadow, "HEAD")))
                    {
                        var init = RunGit("init --quiet", withWorkTree: false);
                        if (!init.Ok)
                        {
                            _available = false;
                            LastError = "git init 失败：" + init.Output;
                            return false;
                        }

                        RunGit("config core.worktree \"" + Root + "\"", withWorkTree: false);
                        RunGit("config core.bare false", withWorkTree: false);
                        RunGit("config user.name LangCode", withWorkTree: false);
                        RunGit("config user.email langcode@local", withWorkTree: false);

                        WriteExcludeFile();
                    }

                    _initialized = true;
                    return true;
                }
                catch (Exception ex)
                {
                    _available = false;
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// 把忽略目录与工作区 .gitignore 写进影子仓库的 info/exclude
        /// </summary>
        private void WriteExcludeFile()
        {
            try
            {
                var builder = new StringBuilder();
                foreach (var name in ExcludedDirectories)
                {
                    builder.Append(name).Append('/').Append('\n');
                }

                var gitignore = Path.Combine(Root, ".gitignore");
                if (File.Exists(gitignore))
                {
                    builder.Append('\n').Append(File.ReadAllText(gitignore));
                }

                var infoDir = Path.Combine(_shadow, "info");
                Directory.CreateDirectory(infoDir);
                File.WriteAllText(Path.Combine(infoDir, "exclude"), builder.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 排除文件写失败只影响体积，不影响正确性
            }
        }

        private ToolResult RunGit(string arguments, bool withWorkTree)
        {
            var command = new StringBuilder();
            command.Append("git --git-dir=\"").Append(_shadow).Append('"');
            if (withWorkTree)
            {
                command.Append(" --work-tree=\"").Append(Root).Append('"');
            }

            command.Append(' ').Append(arguments);

            var runner = new CommandRunner(_shadow)
            {
                TimeoutSeconds = 60,
                MaxOutputChars = 200000,
            };

            return runner.Run(command.ToString(), string.Empty);
        }

        private static string Sanitize(string text)
        {
            return text.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        /// <summary>
        /// 只接受十六进制 id（防止把任意字符串拼进命令）
        /// </summary>
        private static string SanitizeId(string id)
        {
            var value = (id ?? string.Empty).Trim();
            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                {
                    builder.Append(c);
                }
                else
                {
                    return string.Empty;
                }
            }

            return builder.ToString();
        }

        private static DateTime ParseDate(string text)
        {
            return DateTime.TryParse(text, out var parsed) ? parsed.ToUniversalTime() : DateTime.UtcNow;
        }

        private static string HashRoot(string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                return "default";
            }

            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString(0, 16);
            }
        }

        private static ToolResult Fail(string message)
        {
            return new ToolResult { Ok = false, Output = "✗ " + message };
        }
    }
}

