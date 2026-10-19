using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Workspace
{
    /// <summary>
    /// 在工作区里执行 shell 命令 
    /// </summary>
    public sealed class CommandRunner
    {
        /// <summary>
        /// 用户自定义的命令黑名单（分号 / 换行分隔的子串，命中即拒绝）
        /// </summary>
        public string DenyList { get; set; } = string.Empty;

        /// <summary>
        /// 用户信任前缀（分号 / 换行分隔）：命令以它开头时跳过内置危险检查
        /// </summary>
        public string AllowPrefixes { get; set; } = string.Empty;

        /// <summary>
        /// 工作区根目录：命令在这里执行，也用它约束 <c>working_directory</c> 不越界
        /// </summary>
        public string Root { get; set; } = string.Empty;

        /// <summary>
        /// 超时（秒）超时后进程会被结束，并把已经产出的输出还给模型
        /// </summary>
        public int TimeoutSeconds { get; set; } = 90;

        /// <summary>
        /// 长命令的超时（秒）：安装 / 构建 / 还原这类动辄几分钟，用默认的 90 秒会被误杀，
        /// 用户看到的却是「命令超时」而不是「这条命令本来就要跑很久」
        /// </summary>
        public int LongRunningTimeoutSeconds { get; set; } = 600;

        /// <summary>
        /// 命中这些关键字就按长命令处理（小写比较）
        /// </summary>
        private static readonly string[] LongRunningKeywords =
        {
            "install", "restore", "npm ci", "yarn", "pnpm", "build", "publish", "pack",
            "update-package", "gradle", "mvn", "cargo build", "go mod", "dotnet test", "docker build",
        };

        /// <summary>
        /// 输出上限（字符）超出部分截断 —— 否则一条 <c>npm install</c> 能把上下文吃干
        /// </summary>
        public int MaxOutputChars { get; set; } = 20000;

        public CommandRunner(string root)
        {
            Root = (root ?? string.Empty).TrimEnd('\\', '/');
        }

        /// <summary>
        /// 这条命令是否命中危险规则（命中时返回规则说明）
        /// </summary>
        public static string MatchDangerous(string command)
        {
            return CommandPolicy.MatchDangerous(command);
        }

        /// <summary>
        /// 执行命令<paramref name="workingDirectory"/> 是相对工作区的目录（可为空 = 根目录）
        /// </summary>
        /// <param name="onOutput">
        /// 每产出一行输出就回调一次（可在后台线程触发，界面侧自行切线程）
        /// 长命令的界面因此能实时滚出进度，而不是干等到结束才知道它在动
        /// </param>
        public ToolResult Run(string command, string workingDirectory, Action<string> onOutput = null, CancellationToken cancellationToken = default)
        {
            var text = (command ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return Failure("命令为空");
            }

            var dangerous = CommandPolicy.MatchDangerous(text, DenyList, AllowPrefixes);
            if (dangerous != null)
            {
                return Failure("拒绝执行：命令命中危险规则（" + dangerous + "）请改用更精确、范围更小的命令；" +
                               "若确实需要请到设置里调整命令黑名单 / 信任前缀");
            }

            var directory = ResolveDirectory(workingDirectory);

            try
            {
                return Execute(text, directory, onOutput, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new ToolResult
                {
                    Ok = false,
                    Canceled = true,
                    ExitCode = -1,
                    Output = "命令已取消",
                };
            }
            catch (Exception ex)
            {
                return Failure("执行失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 异步执行命令（在线程池里跑同步的进程等待，便于调用方 await / 取消）
        /// </summary>
        public Task<ToolResult> RunAsync(string command, string workingDirectory, Action<string> onOutput, CancellationToken cancellationToken)
        {
            return Task.Run(() => Run(command, workingDirectory, onOutput, cancellationToken), cancellationToken);
        }

        /// <summary>
        /// 给用户看的一行预览（确认卡上显示）
        /// </summary>
        public string Describe(string command, string workingDirectory)
        {
            var directory = ResolveDirectory(workingDirectory);
            var relative = directory.Length > Root.Length ? directory.Substring(Root.Length).TrimStart('\\', '/') : string.Empty;
            return relative.Length == 0 ? command : "[" + relative + "] " + command;
        }

        /// <summary>
        /// 长命令给更长超时；其余用默认值
        /// </summary>
        private int ResolveTimeout(string command)
        {
            var text = (command ?? string.Empty).ToLowerInvariant();

            foreach (var keyword in LongRunningKeywords)
            {
                if (text.IndexOf(keyword, StringComparison.Ordinal) >= 0)
                {
                    return LongRunningTimeoutSeconds;
                }
            }

            return TimeoutSeconds;
        }

        private ToolResult Execute(string command, string directory, Action<string> onOutput, CancellationToken cancellationToken)
        {
            // chcp 65001：把控制台切到 UTF-8，中文输出才不会变成乱码
            var wrapped = "chcp 65001>nul & " + command;

            var timeout = ResolveTimeout(command);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /s /c \"" + wrapped + "\"",
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                var output = new StringBuilder();
                var error = new StringBuilder();

                // 收集输出与「等待结束」在不同线程上，需要一把锁保护两个 StringBuilder
                var gate = new object();
                {
                    process.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data == null)
                        {
                            return;
                        }

                        lock (gate)
                        {
                            output.AppendLine(e.Data);
                        }

                        // 流式回显：界面据此实时滚动命令输出
                        onOutput?.Invoke(e.Data);
                    };

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data == null)
                        {
                            return;
                        }

                        lock (gate)
                        {
                            error.AppendLine(e.Data);
                        }

                        onOutput?.Invoke(e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var finished = false;
                    var exitCode = -1;

                    // 取消 / 超时都要杀掉整棵进程树：只杀 cmd.exe 会留下 node / dotnet / MSBuild
                    using (cancellationToken.Register(() => TryKillTree(process)))
                    {
                        finished = process.WaitForExit(timeout * 1000);

                        if (finished)
                        {
                            // 给异步读取留一点时间把缓冲里的最后几行冲出来。
                            // 这里不能无限等待：子进程可能继承了 stdout 句柄并存活更久。
                            process.WaitForExit(2000);
                            try
                            {
                                exitCode = process.ExitCode;
                            }
                            catch (Exception)
                            {
                                exitCode = -1;
                            }
                        }
                        else
                        {
                            TryKillTree(process);
                        }
                    }

                    var canceled = cancellationToken.IsCancellationRequested;

                    string stdout;
                    string stderr;
                    lock (gate)
                    {
                        stdout = output.ToString().TrimEnd();
                        stderr = error.ToString().TrimEnd();
                    }

                    var body = new StringBuilder();
                    if (stdout.Length > 0)
                    {
                        body.Append(stdout);
                    }

                    if (stderr.Length > 0)
                    {
                        if (body.Length > 0)
                        {
                            body.Append('\n');
                        }

                        body.Append("[stderr]\n").Append(stderr);
                    }

                    var result = Truncate(body.ToString());

                    if (canceled)
                    {
                        return new ToolResult
                        {
                            Ok = false,
                            Canceled = true,
                            ExitCode = -1,
                            Output = "命令已取消，进程树已终止" +
                                     (result.Length == 0 ? string.Empty : "\n" + result),
                        };
                    }

                    if (!finished)
                    {
                        return new ToolResult
                        {
                            Ok = false,
                            ExitCode = -1,
                            Output = "命令超过 " + timeout + " 秒未结束，已终止（长命令可调高「长命令超时」）\n" +
                                     (result.Length == 0 ? string.Empty : result),
                        };
                    }

                    return new ToolResult
                    {
                        Ok = exitCode == 0,
                        ExitCode = exitCode,
                        Output = result.Length == 0 ? "（命令无输出）" : result,
                    };
                }
            }
        }

        private string Truncate(string text)
        {
            if (text.Length <= MaxOutputChars)
            {
                return text;
            }

            // 命令的错误信息通常出现在末尾：头尾都保留，别只留开头
            var head = MaxOutputChars * 7 / 10;
            var tail = MaxOutputChars - head;
            return text.Substring(0, head) +
                   "\n…（中间省略，输出过长已截断到 " + MaxOutputChars + " 字符）…\n" +
                   text.Substring(text.Length - tail);
        }

        /// <summary>
        /// 把相对目录夹在工作区内：越界一律退回根目录
        /// </summary>
        private string ResolveDirectory(string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(Root) || !Directory.Exists(Root))
            {
                return Root;
            }

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                return Root;
            }

            try
            {
                var combined = Path.GetFullPath(Path.Combine(Root, workingDirectory.Trim().Replace('/', '\\')));
                var normalizedRoot = Path.GetFullPath(Root);

                // 必须带目录分隔符比较：否则 C:\repo 会误放行 C:\repo-old
                return IsWithinDirectory(combined, normalizedRoot) && Directory.Exists(combined)
                    ? combined
                    : Root;
            }
            catch (Exception)
            {
                return Root;
            }
        }

        private static bool IsWithinDirectory(string full, string root)
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = normalizedRoot + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryKillTree(Process process)
        {
            int processId;
            try
            {
                if (process.HasExited)
                {
                    return;
                }

                processId = process.Id;
            }
            catch (Exception)
            {
                return;
            }

            // taskkill /T 才能把 npm → node、dotnet → MSBuild 这类子进程一并收掉
            try
            {
                var startInfo = new ProcessStartInfo("taskkill", "/T /F /PID " + processId)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var killer = Process.Start(startInfo))
                {
                    killer?.WaitForExit(3000);
                }
            }
            catch (Exception)
            {
                // taskkill 不可用 / 无权限：退回到只杀主进程
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
                // 已经退出 / 无权限：忽略
            }
        }

        private static ToolResult Failure(string message)
        {
            return new ToolResult { Ok = false, ExitCode = -1, Output = message };
        }
    }
}

