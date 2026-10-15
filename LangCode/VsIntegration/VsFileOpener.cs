using System;
using System.IO;
using System.Text;

namespace LangCode.VsIntegration
{
    /// <summary>
    /// 「跟随 AI 打开文件」的 VS 集成层：把文件的读取 / 编辑 / 新增反映到编辑器里 
    /// </summary>
    internal static class VsFileOpener
    {
        /// <summary>
        /// 差异对比用的临时目录（放"修改前"的快照）
        /// </summary>
        private static string DiffDirectory => Path.Combine(Path.GetTempPath(), "LangCode-diff");

        /// <summary>
        /// 取 DTE（非 UI 线程返回 null）
        /// </summary>
        private static EnvDTE.DTE GetDte()
        {
            if (!Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
            {
                return null;
            }

            try
            {
                Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                return Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 在编辑器里打开文件（可选跳到某一行）
        /// </summary>
        public static bool TryOpenFile(string absolutePath, int line = 0)
        {
            // DTE 只能在 UI 线程访问：先断言，后台线程误调会立刻暴露成异常，
            // 而不是让 VSTHRD010 这条隐患一直挂在分析器的警告列表里
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            {
                return false;
            }

            var dte = GetDte();
            if (dte == null)
            {
                return false;
            }

            try
            {
                var window = dte.ItemOperations.OpenFile(absolutePath, EnvDTE.Constants.vsViewKindTextView);
                window?.Activate();

                if (line > 0 && window?.Document?.Selection is EnvDTE.TextSelection selection)
                {
                    selection.GotoLine(line, true);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 打开 VS 自带的差异对比：两侧内容都来自内存（「改动前」vs「改动后（提议）」）。
        /// 待审批的改动还没落盘，磁盘上的文件还是旧版 —— 这时必须用这个方法才能看到真正的差异
        /// </summary>
        public static bool TryOpenDiffContents(string beforeContent, string afterContent, string currentAbsolutePath)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(currentAbsolutePath))
            {
                return false;
            }

            var dte = GetDte();
            if (dte == null)
            {
                return false;
            }

            try
            {
                var before = WriteSnapshot(beforeContent, currentAbsolutePath);
                var after = WriteSnapshot(afterContent, currentAbsolutePath, "after");

                dte.ExecuteCommand("Tools.DiffFiles", "\"" + before + "\" \"" + after + "\"");
                return true;
            }
            catch (Exception)
            {
                // 快照或差异窗口起不来：退化成「打开磁盘上的文件」，至少让用户看到当前内容
                return TryOpenFile(currentAbsolutePath);
            }
        }

        /// <summary>
        /// 打开 VS 自带的差异对比：把"修改前"的内容写到临时文件，再和磁盘上的当前文件比较
        /// </summary>
        public static bool TryOpenDiff(string beforeContent, string currentAbsolutePath)
        {
            // 同 TryOpenFile：ExecuteCommand / ItemOperations 都是只能在 UI 线程碰的 DTE API
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(currentAbsolutePath) || !File.Exists(currentAbsolutePath))
            {
                return false;
            }

            var dte = GetDte();
            if (dte == null)
            {
                return false;
            }

            string snapshot;
            try
            {
                snapshot = WriteSnapshot(beforeContent, currentAbsolutePath);
            }
            catch (Exception)
            {
                // 快照写不出来就退化成「只打开文件」，至少让用户看到结果
                return TryOpenFile(currentAbsolutePath);
            }

            try
            {
                // VS 自带命令：对两个文件开差异窗口（参数是两个带引号的路径）
                dte.ExecuteCommand("Tools.DiffFiles", "\"" + snapshot + "\" \"" + currentAbsolutePath + "\"");
                return true;
            }
            catch (Exception)
            {
                return TryOpenFile(currentAbsolutePath);
            }
        }

        /// <summary>
        /// 打开文件并请求 VS 执行「转到定义」
        /// </summary>
        public static bool GoToDefinition(string absolutePath, int line)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (!TryOpenFile(absolutePath, line))
            {
                return false;
            }

            try
            {
                var dte = GetDte();
                if (dte == null)
                {
                    return false;
                }

                dte.ExecuteCommand("Edit.GoToDefinition");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 取编辑器里当前选中的文本（没有选中 / 拿不到 DTE 时返回 null）
        /// </summary>
        public static string TryGetSelection()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var dte = GetDte();
            if (dte == null)
            {
                return null;
            }

            try
            {
                var selection = dte.ActiveWindow?.Document?.Selection as EnvDTE.TextSelection;
                var text = selection?.Text;
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 把工作区内的相对路径解析成绝对路径（越界 / 不存在返回 null）
        /// </summary>
        public static string ResolveInRoot(string root, string relativePath)
        {
            var full = ResolveInRootAllowMissing(root, relativePath);
            return full != null && File.Exists(full) ? full : null;
        }

        /// <summary>
        /// 同 <see cref="ResolveInRoot"/>，但**不要求文件已存在** ——
        /// 待审批的「新增文件」此时还没有落盘，但差异对比仍然要能开起来
        /// </summary>
        public static string ResolveInRootAllowMissing(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            try
            {
                var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', '\\')));
                var normalizedRoot = Path.GetFullPath(root);

                if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return full;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 写"修改前"快照名字保留可识别的扩展名（<c>App.before.tsx</c>），
        /// 这样差异窗口里两侧都有正确的语法高亮
        /// </summary>
        private static string WriteSnapshot(string content, string currentAbsolutePath, string suffix = "before")
        {
            Directory.CreateDirectory(DiffDirectory);

            var name = Path.GetFileNameWithoutExtension(currentAbsolutePath);
            var extension = Path.GetExtension(currentAbsolutePath);
            var snapshot = Path.Combine(DiffDirectory, name + "." + suffix + extension);

            File.WriteAllText(snapshot, content ?? string.Empty, new UTF8Encoding(false));
            return snapshot;
        }
    }
}

