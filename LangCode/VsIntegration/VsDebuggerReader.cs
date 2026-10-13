using EnvDTE;
using EnvDTE80;
using System;
using System.Text;

namespace LangCode.VsIntegration
{
    /// <summary>
    /// 读取 Visual Studio 调试器的<b>只读</b>状态：断点、当前栈帧、局部变量 / 参数
    /// </summary>
    internal static class VsDebuggerReader
    {
        /// <summary>
        /// 读调试器状态，返回可读文本；不在调试 / 拿不到时返回 <c>null</c>
        /// </summary>
        public static string TryRead(int maxBreakpoints = 20, int maxVariables = 40)
        {
            if (!Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
            {
                return null;
            }

            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE)) as DTE2;
                var debugger = dte?.Debugger;
                if (debugger == null)
                {
                    return null;
                }

                var builder = new StringBuilder();

                AppendBreakpoints(builder, debugger, maxBreakpoints);
                AppendCurrentFrame(builder, debugger, maxVariables);

                return builder.Length == 0 ? null : builder.ToString().TrimEnd();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AppendBreakpoints(StringBuilder builder, Debugger debugger, int max)
        {
            try
            {
                var breakpoints = debugger.Breakpoints;
                if (breakpoints == null || breakpoints.Count == 0)
                {
                    return;
                }

                builder.Append("断点（").Append(breakpoints.Count).Append("）：\n");
                var shown = 0;
                foreach (Breakpoint breakpoint in breakpoints)
                {
                    if (shown >= max)
                    {
                        builder.Append("· …（其余断点省略）\n");
                        break;
                    }

                    string file = string.Empty;
                    var line = 0;
                    var enabled = true;

                    try
                    {
                        file = breakpoint.File ?? string.Empty;
                        line = breakpoint.FileLine;
                        enabled = breakpoint.Enabled;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    builder.Append(enabled ? "· [已启用] " : "· [已禁用] ")
                           .Append(file.Length == 0 ? "(无文件)" : file);
                    if (line > 0)
                    {
                        builder.Append(':').Append(line);
                    }

                    builder.Append('\n');
                    shown++;
                }
            }
            catch (Exception)
            {
                // 断点集合不可用：跳过
            }
        }

        private static void AppendCurrentFrame(StringBuilder builder, Debugger debugger, int maxVariables)
        {
            try
            {
                // 设计模式（未调试）时 CurrentStackFrame 通常为 null，这里自然跳过
                var frame = debugger.CurrentStackFrame;
                if (frame == null)
                {
                    return;
                }

                builder.Append("当前栈帧：").Append(frame.FunctionName ?? "(未知函数)");
                try
                {
                    if (!string.IsNullOrWhiteSpace(frame.Module))
                    {
                        builder.Append(" @ ").Append(frame.Module);
                    }
                }
                catch (Exception)
                {
                    // 模块名拿不到：忽略
                }

                builder.Append('\n');

                AppendExpressions(builder, frame.Arguments, "参数", maxVariables);
                AppendExpressions(builder, frame.Locals, "局部变量", maxVariables);
            }
            catch (Exception)
            {
                // 未调试 / 栈帧已失效：忽略
            }
        }

        private static void AppendExpressions(StringBuilder builder, Expressions expressions, string title, int max)
        {
            try
            {
                if (expressions == null || expressions.Count == 0)
                {
                    return;
                }

                builder.Append(title).Append("：\n");
                var shown = 0;
                foreach (Expression expression in expressions)
                {
                    if (shown >= max)
                    {
                        builder.Append("· …（其余省略）\n");
                        break;
                    }

                    try
                    {
                        builder.Append("· ").Append(expression.Name).Append(" = ").Append(expression.Value).Append('\n');
                        shown++;
                    }
                    catch (Exception)
                    {
                        // 单个表达式求值失败（已释放 / 副作用）：跳过它
                    }
                }
            }
            catch (Exception)
            {
                // 表达式集合不可用：跳过
            }
        }
    }
}
