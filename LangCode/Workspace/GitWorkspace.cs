using System;
using System.Collections.Generic;
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
    /// Git 相关的上下文来源（@ 提及用）
    /// </summary>
    public static class GitWorkspace
    {
        /// <summary>
        /// 读取改动列表（不是 git 仓库 / 没有 git 命令时返回空表）
        /// </summary>
        public static IList<GitChange> LoadChanges(string root)
        {
            return ParsePorcelain(RunGit(root, "status --porcelain"));
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

        private static string RunGit(string root, string arguments)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            try
            {
                // 复用命令执行器：它已经处理了 UTF-8 编码、超时与输出上限
                var runner = new CommandRunner(root) { TimeoutSeconds = 20, MaxOutputChars = 8000 };
                var result = runner.Run("git " + arguments, string.Empty);
                return result.Ok ? result.Output : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

