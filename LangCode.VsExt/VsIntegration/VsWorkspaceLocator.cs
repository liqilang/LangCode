using System;
using System.IO;

namespace LangCode.VsExt.VsIntegration
{
    /// <summary>
    /// 定位「当前工作区根目录」= VS 已打开解决方案 / 文件夹所在目录 
    /// </summary>
    internal static class VsWorkspaceLocator
    {
        /// <summary>
        /// 解决方案文件夹（Solution Folder）的 Kind GUID —— 它不是真正的项目，要跳过
        /// </summary>
        private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

        /// <summary>
        /// 返回工作区根目录；取不到返回 null
        /// </summary>
        public static string TryGetSolutionDirectory()
        {
            // DTE 只能在 UI 线程访问（VSTHRD010）：不在 UI 线程就直接退化为「无工作区」
            if (!Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
            {
                return null;
            }

            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null)
                {
                    return null;
                }

                // 1) 打开解决方案 / 打开文件夹：FullName 既可能是 .sln 也可能是目录
                var solution = dte.Solution;
                var root = FromFileOrDirectory(solution?.FullName);

                // 2) 某些 VS 版本在「打开文件夹」时只把目录放在 Solution.FileName 上
                root ??= FromFileOrDirectory(solution?.FileName);

                // 3) 临时解决方案（未存盘）：用第一个真实项目所在目录（跳过解决方案文件夹）
                if (root == null && solution != null)
                {
                    var projects = solution.Projects;
                    for (var i = 1; projects != null && i <= projects.Count && root == null; i++)
                    {
                        var project = projects.Item(i);
                        if (project == null ||
                            string.Equals(project.Kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        root = FromFileOrDirectory(project.FullName);
                    }
                }

                // 4) 最后退一步：活动文档所在目录（单文件 / 杂项文件场景）
                if (root == null)
                {
                    var documentPath = dte.ActiveDocument?.FullName;
                    if (!string.IsNullOrWhiteSpace(documentPath) && File.Exists(documentPath))
                    {
                        root = Path.GetDirectoryName(documentPath);
                    }
                }

                return root;
            }
            catch (Exception)
            {
                // 非 VS 宿主 / 服务不可用 / COM 取属性抛异常：退化为无工作区
                return null;
            }
        }

        /// <summary>
        /// 同一条路径既当文件（.sln）又当目录（打开文件夹）来判断
        /// </summary>
        internal static string FromFileOrDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                if (File.Exists(path))
                {
                    return Path.GetDirectoryName(path);
                }

                if (Directory.Exists(path))
                {
                    return path;
                }
            }
            catch (Exception)
            {
                // 路径非法（例如 DTE 返回了占位串）：当作没取到
            }

            return null;
        }
    }
}

