using EnvDTE80;
using System;
using System.Text;

namespace LangCode.VsIntegration
{
    /// <summary>
    /// 读取 Visual Studio「错误列表」里的编译诊断，供 Agent 做自动修复
    /// </summary>
    internal static class VsDiagnosticsReader
    {
        /// <summary>
        /// 读当前错误列表，返回「每行一条错误」的文本；没有错误 / 拿不到时返回 <c>null</c>
        /// </summary>
        /// <param name="maxItems">最多返回多少条（超出截断并标注）</param>
        public static string TryRead(int maxItems = 50)
        {
            // 非 UI 线程访问 DTE 会抛 InvalidCastException / COMException，也会污染分析器：
            // 这里直接退化为「没有诊断」，让自动修复安全跳过
            if (!Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
            {
                return null;
            }

            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as DTE2;
                if (dte == null)
                {
                    return null;
                }

                // 错误列表窗口未创建时该属性可能抛异常：视为「暂无诊断」
                ErrorItems items;
                try
                {
                    // EnvDTE80.ToolWindows 才有 ErrorList（DTE.ToolWindows 是老接口）：
                    // 显式按 EnvDTE80 形态取，避免宿主版本差异导致取不到
                    var toolWindows = dte.ToolWindows;
                    items = toolWindows?.ErrorList?.ErrorItems;
                }
                catch (Exception)
                {
                    return null;
                }

                if (items == null || items.Count == 0)
                {
                    return null;
                }

                var builder = new StringBuilder();
                var count = 0;

                for (var i = 1; i <= items.Count && count < maxItems; i++)
                {
                    EnvDTE80.ErrorItem item;
                    try
                    {
                        item = items.Item(i);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (item == null || !IsError(item))
                    {
                        continue;
                    }

                    var file = string.Empty;
                    var line = 0;
                    var text = string.Empty;

                    try
                    {
                        file = item.FileName ?? string.Empty;
                        line = item.Line;
                        text = (item.Description ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                    }
                    catch (Exception)
                    {
                        // 单条读取失败：跳过它，不影响其余诊断
                        continue;
                    }

                    if (text.Length > 300)
                    {
                        text = text.Substring(0, 300) + "…";
                    }

                    builder.Append("- ").Append(file.Length == 0 ? "(无文件)" : file);
                    if (line > 0)
                    {
                        builder.Append(':').Append(line);
                    }

                    builder.Append(": ").Append(text).Append('\n');
                    count++;
                }

                if (count == 0)
                {
                    return null;
                }

                if (count >= maxItems)
                {
                    builder.Append("…（诊断过多，仅列出前 ").Append(maxItems).Append(" 条错误）\n");
                }

                return builder.ToString().TrimEnd();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 是否「错误」级别（拿不到级别时按错误处理 —— 宁可多报一条，也别漏过）
        /// </summary>
        private static bool IsError(EnvDTE80.ErrorItem item)
        {
            try
            {
                return item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}

