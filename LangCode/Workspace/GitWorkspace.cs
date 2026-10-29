using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LangCode.Workspace
{
    /// <summary>
    /// 工作区里一个有改动的文件
    /// </summary>
    public sealed class GitChange
    {
        public GitChange(string status, string path)
        {
            Status = (status ?? string.Empty).Trim();
            Path = (path ?? string.Empty).Replace('\\', '/');
        }

        /// <summary>
        /// git 的两字符状态（<c>M</c> / <c>A</c> / <c>D</c> / <c>??</c> …）
        /// </summary>
        public string Status { get; }

        public string Path { get; }

        /// <summary>
        /// 给人看的一字标记
        /// </summary>
        public string Marker
        {
            get
            {
                if (Status == "??")
                {
                    return "U";     // untracked
                }

                if (Status.IndexOf('D') >= 0)
                {
                    return "D";
                }

                if (Status.IndexOf('A') >= 0)
                {
                    return "A";
                }

                if (Status.IndexOf('R') >= 0)
                {
                    return "R";
                }

                return "M";
            }
        }
    }

    /// <summary>
    /// Git 相关的上下文来源与工具实现
    /// </summary>
    public static class GitWorkspace
    {
        /// <summary>
        /// 读取改动列表（不是 git 仓库 / 没有 git 命令时返回空表）
        /// </summary>
        public static IList<GitChange> LoadChanges(string root)
        {
            // -- . ：工作区可能是仓库子目录，只看当前工作区范围内的改动
            var result = RunGitResult(root, "status --porcelain -- .", 100000);
            if (!result.Ok || result.Output == "（命令无输出）")
            {
                return new List<GitChange>();
            }

            return ParsePorcelain(result.Output);
        }

        /// <summary>
        /// 解析 <c>git status --porcelain</c> 的输出（纯函数，方便自检）
        /// </summary>
        public static IList<GitChange> ParsePorcelain(string output)
        {
            var result = new List<GitChange>();
            if (string.IsNullOrWhiteSpace(output))
            {
                return result;
            }

            foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
            {
                // 格式：`XY path`（XY 占两列，第 3 列是空格）
                if (raw.Length < 4)
                {
                    continue;
                }

                var status = raw.Substring(0, 2);
                var path = raw.Substring(3).Trim();

                // 重命名 / 复制会写成 `old -> new`：取新路径
                var arrow = path.IndexOf("->", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    path = path.Substring(arrow + 2).Trim();
                }

                // git 在路径含特殊字符时会加引号，去掉即可
                path = path.Trim('"');

                if (path.Length > 0)
                {
                    result.Add(new GitChange(status, path));
                }
            }

            return result;
        }

        /// <summary>
        /// 拼一段「改动清单」上下文（插入到输入框里给模型看）
        /// </summary>
        public static string BuildChangesContext(string root)
        {
            var changes = LoadChanges(root);
            if (changes.Count == 0)
            {
                return "（当前工作区没有未提交的改动，或这里不是 git 仓库）";
            }

            var builder = new StringBuilder();
            builder.Append("当前工作区的改动（git status）：").Append('\n');
            foreach (var change in changes)
            {
                builder.Append("- [").Append(change.Marker).Append("] ").Append(change.Path).Append('\n');
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 拼一段「最近提交」上下文
        /// </summary>
        public static string BuildRecentCommitsContext(string root, int count)
        {
            var output = RunGit(root, "log --oneline -n " + Math.Max(1, count));
            return string.IsNullOrWhiteSpace(output)
                ? "（读不到提交历史，或这里不是 git 仓库）"
                : "最近的提交（git log）：\n" + output.TrimEnd();
        }

        /// <summary>
        /// 读取某文件在 HEAD 提交里的内容（用于「与改动前对比」）。
        /// 不是 git 仓库 / 该文件未跟踪时返回 null。
        /// </summary>
        public static string TryReadHeadContent(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            var path = relativePath.Replace('\\', '/').TrimStart('/');

            // 整个 "HEAD:path" 作为一个带引号的参数，路径含空格也能正确解析
            return RunGit(root, "show \"HEAD:" + path + "\"");
        }

        // ==================== 给 Agent 的只读工具 ====================

        /// <summary>
        /// <c>git_status</c>：工作区状态（含当前分支）
        /// </summary>
        public static ToolResult Status(string root)
        {
            var result = RunGitResult(root, "status --porcelain=v1 --branch -- .", 12000);
            if (result.Ok && result.Output == "（命令无输出）")
            {
                result.Output = "（工作区干净，没有未提交的改动）";
            }

            return result;
        }

        /// <summary>
        /// <c>git_diff</c>：查看工作区或暂存区差异
        /// </summary>
        /// <param name="staged">true = 已暂存（git diff --cached）</param>
        /// <param name="path">可选：只查看某个文件 / 目录</param>
        /// <param name="stat">true = 只看统计，不输出完整 diff</param>
        public static ToolResult Diff(string root, bool staged, string path, bool stat)
        {
            var args = new StringBuilder("diff --no-ext-diff --no-color --unified=3");
            if (staged)
            {
                args.Append(" --cached");
            }

            if (stat)
            {
                args.Append(" --stat");
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!TryNormalizePath(root, path, out var normalized))
                {
                    return Failed("路径不在工作区内或包含非法字符：" + path);
                }

                args.Append(" -- ").Append(Quote(normalized));
            }
            else
            {
                // 未指定路径时只看工作区范围，避免把仓库里工作区之外的改动带进上下文
                args.Append(" -- .");
            }

            var result = RunGitResult(root, args.ToString(), 24000);
            if (result.Ok && result.Output == "（命令无输出）")
            {
                result.Output = staged ? "（暂存区没有差异）" : "（工作区没有差异）";
            }

            return result;
        }

        /// <summary>
        /// <c>git_log</c>：最近提交
        /// </summary>
        public static ToolResult Log(string root, int limit, string path)
        {
            var count = Math.Max(1, Math.Min(limit <= 0 ? 20 : limit, 100));
            var args = new StringBuilder("log --no-color --oneline --decorate -n ").Append(count);

            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!TryNormalizePath(root, path, out var normalized))
                {
                    return Failed("路径不在工作区内或包含非法字符：" + path);
                }

                args.Append(" -- ").Append(Quote(normalized));
            }

            var result = RunGitResult(root, args.ToString(), 12000);
            if (result.Ok && result.Output == "（命令无输出）")
            {
                result.Output = "（没有提交记录）";
            }

            return result;
        }

        /// <summary>
        /// <c>git_show</c>：查看某个提交的详情
        /// </summary>
        public static ToolResult Show(string root, string revision, bool stat)
        {
            var rev = (revision ?? string.Empty).Trim();
            if (!IsSafeRevision(rev))
            {
                return Failed("提交引用非法：请使用 HEAD / HEAD~1 / 分支名 / 提交哈希");
            }

            var args = "show --no-ext-diff --no-color --format=fuller " + (stat ? "--stat " : "--patch ") + rev;
            var result = RunGitResult(root, args, 24000);
            if (result.Ok && result.Output == "（命令无输出）")
            {
                result.Output = "（该提交没有可显示的改动）";
            }

            return result;
        }

        // ==================== 暂存 / 提交 ====================

        /// <summary>
        /// <c>git_stage</c>：暂存指定路径，或 <paramref name="all"/> = true 时暂存全部改动
        /// </summary>
        /// <param name="isBlockedPath">敏感文件判定：返回 true 的路径拒绝暂存</param>
        public static ToolResult Stage(string root, string pathsText, bool all, Func<string, bool> isBlockedPath)
        {
            IList<string> paths;
            string arguments;

            if (all)
            {
                paths = LoadChanges(root).Select(change => change.Path).ToList();

                // 只暂存当前工作区目录下的改动：工作区可能是仓库的子目录，
                // 裸 `git add -A` 会把工作区之外的文件也带进来
                arguments = "add -A -- .";
            }
            else
            {
                paths = ParsePaths(pathsText);
                if (paths.Count == 0)
                {
                    return Failed("没有要暂存的文件：请提供 paths，或把 all 设为 true");
                }

                var safe = new List<string>();
                foreach (var path in paths)
                {
                    if (!TryNormalizePath(root, path, out var normalized))
                    {
                        return Failed("路径不在工作区内或包含非法字符：" + path);
                    }

                    safe.Add(normalized);
                }

                arguments = "add -- " + string.Join(" ", safe.Select(Quote));
            }

            var blocked = FindBlockedPath(paths, isBlockedPath);
            if (blocked != null)
            {
                return Failed("检测到敏感文件，已拒绝暂存：" + blocked +
                              "\n如确需提交，请在工作区策略里显式放行敏感文件，或先把它加入 .gitignore");
            }

            var result = RunGitResult(root, arguments, 12000);
            if (result.Ok)
            {
                var staged = StagedFiles(root);
                result.Output = "已暂存" + (all ? "全部改动" : "指定文件") +
                                (staged.Count == 0 ? string.Empty : "：\n" + string.Join("\n", staged.Select(p => "- " + p)));
            }

            return result;
        }

        /// <summary>
        /// <c>git_commit</c>：提交已暂存改动。
        /// <paramref name="addAll"/> = true 时先执行一次 <see cref="Stage"/>。
        /// </summary>
        public static ToolResult Commit(string root, string message, bool addAll, Func<string, bool> isBlockedPath)
        {
            var text = (message ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return Failed("提交信息不能为空：请先根据改动生成一条清晰的提交信息");
            }

            if (addAll)
            {
                var staged = Stage(root, null, true, isBlockedPath);
                if (!staged.Ok)
                {
                    return staged;
                }
            }

            var files = StagedFiles(root);
            if (files.Count == 0)
            {
                return Failed("没有已暂存的改动：请先调用 git_stage，或把 add_all 设为 true");
            }

            // 工作区是仓库子目录时，暂存区里可能混有工作区之外的改动：不替用户提交它们
            var prefix = StatusPrefix(root);
            if (prefix.Length > 0)
            {
                var outside = files.FirstOrDefault(file => !file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (outside != null)
                {
                    return Failed("暂存区包含工作区之外的改动：" + outside +
                                  "\n请先提交或取消暂存这些文件，再重试");
                }
            }

            var blocked = FindBlockedPath(files, isBlockedPath);
            if (blocked != null)
            {
                return Failed("暂存区里包含敏感文件，已拒绝提交：" + blocked +
                              "\n请先取消暂存（git reset <path>）或把该文件加入 .gitignore");
            }

            var messageFile = Path.Combine(
                Path.GetTempPath(),
                "LangCode-git-" + Guid.NewGuid().ToString("N") + ".txt");

            try
            {
                File.WriteAllText(messageFile, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                var result = RunGitResult(root, "commit -F " + Quote(messageFile), 16000);
                if (!result.Ok)
                {
                    return result;
                }

                var head = RunGitResult(root, "log -1 --oneline", 4000);
                result.Output = "提交成功：" + (head.Ok ? "\n" + head.Output.Trim() : string.Empty) +
                                "\n\n" + result.Output;
                return result;
            }
            catch (Exception ex)
            {
                return Failed("提交失败：" + ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(messageFile))
                    {
                        File.Delete(messageFile);
                    }
                }
                catch (Exception)
                {
                    // 临时文件清理失败不影响提交结果
                }
            }
        }

        /// <summary>
        /// 已暂存的文件列表（<c>git diff --cached --name-only</c>）
        /// </summary>
        public static IList<string> StagedFiles(string root)
        {
            var result = RunGitResult(root, "diff --cached --name-only", 100000);
            if (!result.Ok || result.Output == "（命令无输出）")
            {
                return new List<string>();
            }

            return result.Output
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }

        /// <summary>
        /// 当前工作区相对仓库根的前缀（仓库根为空串，子目录形如 <c>src/</c>）。
        /// 用于阻止「工作区是仓库子目录」时误提交工作区之外的暂存内容。
        /// </summary>
        private static string StatusPrefix(string root)
        {
            var result = RunGitResult(root, "rev-parse --show-prefix", 4000);
            if (!result.Ok || result.Output == "（命令无输出）")
            {
                return string.Empty;
            }

            return result.Output.Trim().Replace('\\', '/');
        }

        // ==================== 内部工具 ====================

        private static ToolResult RunGitResult(string root, string arguments, int maxOutputChars)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return Failed("未检测到工作区，无法执行 git 操作");
            }

            try
            {
                // 复用命令执行器：它已经处理了 UTF-8 编码、超时与输出上限
                var runner = new CommandRunner(root)
                {
                    TimeoutSeconds = 20,
                    MaxOutputChars = Math.Max(4000, maxOutputChars),
                };

                var result = runner.Run("git " + arguments, string.Empty);
                if (!result.Ok)
                {
                    result.Output = "git " + arguments + "\n" + result.Output;
                }

                return result;
            }
            catch (Exception ex)
            {
                return Failed("git 执行失败：" + ex.Message);
            }
        }

        private static string RunGit(string root, string arguments)
        {
            var result = RunGitResult(root, arguments, 8000);
            return result.Ok ? result.Output : null;
        }

        private static ToolResult Failed(string message)
        {
            return new ToolResult
            {
                Ok = false,
                ExitCode = -1,
                Output = message,
            };
        }

        private static IList<string> ParsePaths(string text)
        {
            return (text ?? string.Empty)
                .Split(new[] { '\n', '\r', ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FindBlockedPath(IEnumerable<string> paths, Func<string, bool> isBlockedPath)
        {
            if (isBlockedPath == null || paths == null)
            {
                return null;
            }

            foreach (var path in paths)
            {
                try
                {
                    if (isBlockedPath(path))
                    {
                        return path;
                    }
                }
                catch (Exception)
                {
                    // 判定函数异常时按「不阻断」处理，避免把正常提交卡死
                }
            }

            return null;
        }

        private static bool TryNormalizePath(string root, string relativePath, out string normalized)
        {
            normalized = (relativePath ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
            if (normalized.Length == 0 ||
                normalized.IndexOf('"') >= 0 ||
                normalized.IndexOf('\n') >= 0 ||
                normalized.IndexOf('\r') >= 0 ||
                Path.IsPathRooted(normalized))
            {
                return false;
            }

            try
            {
                var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
                var full = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', '\\')));
                var prefix = rootFull + Path.DirectorySeparatorChar;
                return string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase) ||
                       full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 提交引用只允许常见形式：HEAD / HEAD~1 / 分支名 / 短哈希 / 标签
        /// </summary>
        private static bool IsSafeRevision(string revision)
        {
            if (string.IsNullOrWhiteSpace(revision) || revision[0] == '-')
            {
                return false;
            }

            foreach (var c in revision)
            {
                // 不允许 ^：cmd.exe 会把 ^ 当转义符，HEAD^ 可能被悄悄改成 HEAD
                var allowed = char.IsLetterOrDigit(c) ||
                              c == '.' || c == '_' || c == '-' || c == '/' ||
                              c == '~' || c == '@' || c == '{' || c == '}';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }

        private static string Quote(string value)
        {
            // 路径 / 临时文件路径都在工作区或系统临时目录内，且已拒绝引号与换行
            return "\"" + (value ?? string.Empty) + "\"";
        }
    }
}
