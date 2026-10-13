using EnvDTE;
using System;
using System.IO;
using System.Text;

namespace LangCode.VsIntegration
{
    /// <summary>
    /// 采集「编辑器当前状态」作为自动上下文：正在编辑哪个文件、光标在第几行
    /// </summary>
    internal static class VsContextProvider
    {
        /// <summary>
        /// 描述当前活动文档：形如 <c>【当前文件】src/App.tsx（光标在第 42 行）</c>
        /// 没有打开文档 / 未在 UI 线程时返回 <c>null</c>
        /// </summary>
        public static string DescribeActiveDocument(string root)
        {
            if (!Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
            {
                return null;
            }

            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE)) as DTE;
                var document = dte?.ActiveDocument;
                var path = document?.FullName;

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                var builder = new StringBuilder();
                builder.Append("【当前文件】").Append(MakeRelative(root, path));

                var line = 0;
                try
                {
                    if (document.Selection is TextSelection selection)
                    {
                        line = selection.CurrentLine;
                    }
                }
                catch (Exception)
                {
                    // 拿不到光标位置不影响「是哪个文件」这个关键信息
                }

                if (line > 0)
                {
                    builder.Append("（光标在第 ").Append(line).Append(" 行）");
                }

                return builder.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 把绝对路径变成相对工作区根的路径（统一 <c>/</c>）；不在根内则原样返回
        /// </summary>
        private static string MakeRelative(string root, string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return absolutePath;
            }

            try
            {
                var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                                     + Path.DirectorySeparatorChar;

                if (absolutePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return absolutePath.Substring(normalizedRoot.Length).Replace('\\', '/');
                }
            }
            catch (Exception)
            {
                // 路径异常：退回绝对路径
            }

            return absolutePath.Replace('\\', '/');
        }
    }
}

