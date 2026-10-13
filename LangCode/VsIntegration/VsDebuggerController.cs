using EnvDTE;
using EnvDTE80;
using LangCode.Agent;
using System;
using System.IO;
using System.Text;

namespace LangCode.VsIntegration
{
    /// <summary>
    /// 调试器控制：断点增删 / 单步 / 继续 / 停止，以及只读的调用栈、局部变量与表达式求值。
    ///
    /// 基于 Visual Studio 的 DTE 调试器对象（EnvDTE），因此<b>只能在 UI 线程调用</b>。
    /// 与只读的 <see cref="VsDebuggerReader"/> 互补：那个给系统提示词做环境速览，
    /// 这个把调试器变成 Agent 可驱动的工具。
    /// </summary>
    internal static class VsDebuggerController
    {
        /// <summary>
        /// 执行一条调试命令；返回给模型看的文本（失败以 ✗ 开头）
        /// </summary>
        /// <param name="command">命令</param>
        /// <param name="root">工作区根（把相对文件路径解析成绝对路径用；可为空）</param>
        public static string Execute(DebugCommand command, string root)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (command == null)
            {
                return "✗ 空的调试命令";
            }

            var action = (command.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (action.Length == 0)
            {
                return "✗ action 不能为空";
            }

            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE)) as DTE2;
                var debugger = dte?.Debugger;
                if (debugger == null)
                {
                    return "✗ 当前不可用调试器（未在 Visual Studio 宿主中运行？）";
                }

                switch (action)
                {
                    case "list_breakpoints":
                        return ListBreakpoints(debugger, command);

                    case "set_breakpoint":
                        return SetBreakpoint(debugger, command, root);

                    case "remove_breakpoint":
                        return RemoveBreakpoint(debugger, command, root);

                    case "clear_breakpoints":
                        return ClearBreakpoints(debugger);

                    case "get_stack":
                        return GetStack(debugger, command);

                    case "get_locals":
                        return GetLocals(debugger, command);

                    case "evaluate":
                        return Evaluate(debugger, command);

                    case "status":
                        return Status(debugger);

                    case "continue":
                        debugger.Go(false);
                        return "已继续执行（等价 F5）";

                    case "step_over":
                        debugger.StepOver(false);
                        return "已单步跳过（等价 F10）";

                    case "step_into":
                        debugger.StepInto(false);
                        return "已单步进入（等价 F11）";

                    case "step_out":
                        debugger.StepOut(false);
                        return "已跳出当前函数（等价 Shift+F11）";

                    case "pause":
                        debugger.Break(false);
                        return "已请求中断（全部中断）";

                    case "stop":
                        debugger.Stop(false);
                        return "已停止调试";

                    default:
                        return "✗ 未知的调试动作：" + command.Action +
                               "（可用：list_breakpoints / set_breakpoint / remove_breakpoint / clear_breakpoints / " +
                               "get_stack / get_locals / evaluate / status / continue / step_over / step_into / step_out / pause / stop）";
                }
            }
            catch (Exception ex)
            {
                return "✗ 调试操作失败：" + ex.GetBaseException().Message;
            }
        }

        // ==================== 断点 ====================

        private static string ListBreakpoints(Debugger debugger, DebugCommand command)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var breakpoints = debugger.Breakpoints;
            if (breakpoints == null || breakpoints.Count == 0)
            {
                return "当前没有断点";
            }

            var max = Math.Max(1, command.MaxItems);
            var builder = new StringBuilder();
            builder.Append("断点共 ").Append(breakpoints.Count).Append(" 个：\n");

            var shown = 0;
            foreach (Breakpoint breakpoint in breakpoints)
            {
                if (shown >= max)
                {
                    builder.Append("· …（其余省略）\n");
                    break;
                }

                try
                {
                    builder.Append(breakpoint.Enabled ? "· [已启用] " : "· [已禁用] ")
                           .Append(RelativeOrFull(breakpoint.File)).Append(':').Append(breakpoint.FileLine);
                    var condition = breakpoint.Condition;
                    if (!string.IsNullOrWhiteSpace(condition))
                    {
                        builder.Append("  条件：").Append(condition);
                    }

                    builder.Append('\n');
                    shown++;
                }
                catch (Exception)
                {
                    // 单个断点属性不可读：跳过
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static string SetBreakpoint(Debugger debugger, DebugCommand command, string root)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var file = ResolveFile(root, command.File);
            if (file == null)
            {
                return "✗ 找不到要设断点的文件：" + command.File;
            }

            if (command.Line <= 0)
            {
                return "✗ line 必须大于 0";
            }

            var condition = command.Condition ?? string.Empty;

            // Breakpoints.Add 返回的是「断点集合」本身，不代表单个断点位置，因此这里直接用请求的位置回执
            debugger.Breakpoints.Add(string.Empty, file, command.Line, 1, condition);

            return "已设置断点：" + RelativeOrFull(file) + ":" + command.Line +
                   (condition.Length > 0 ? "（条件：" + condition + "）" : string.Empty);
        }

        private static string RemoveBreakpoint(Debugger debugger, DebugCommand command, string root)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var file = ResolveFile(root, command.File);
            if (file == null)
            {
                return "✗ 找不到文件：" + command.File;
            }

            var breakpoints = debugger.Breakpoints;
            if (breakpoints == null || breakpoints.Count == 0)
            {
                return "当前没有断点可删除";
            }

            var removed = 0;
            // 倒序删除，避免边遍历边改集合
            for (var i = breakpoints.Count; i >= 1; i--)
            {
                try
                {
                    var breakpoint = breakpoints.Item(i);
                    var sameFile = string.Equals(Normalize(breakpoint.File), Normalize(file), StringComparison.OrdinalIgnoreCase);
                    var sameLine = command.Line <= 0 || breakpoint.FileLine == command.Line;
                    if (sameFile && sameLine)
                    {
                        breakpoint.Delete();
                        removed++;
                    }
                }
                catch (Exception)
                {
                    // 单个删除失败：继续
                }
            }

            return removed == 0
                ? "没有匹配的断点：" + RelativeOrFull(file) + (command.Line > 0 ? ":" + command.Line : string.Empty)
                : "已删除 " + removed + " 个断点：" + RelativeOrFull(file);
        }

        private static string ClearBreakpoints(Debugger debugger)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var breakpoints = debugger.Breakpoints;
            if (breakpoints == null || breakpoints.Count == 0)
            {
                return "当前没有断点";
            }

            var count = breakpoints.Count;
            for (var i = breakpoints.Count; i >= 1; i--)
            {
                try
                {
                    breakpoints.Item(i).Delete();
                }
                catch (Exception)
                {
                    // 忽略单个失败
                }
            }

            return "已清空全部断点（共 " + count + " 个）";
        }

        // ==================== 调用栈 / 变量 ====================

        private static string GetStack(Debugger debugger, DebugCommand command)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var thread = debugger.CurrentThread;
            if (thread == null)
            {
                return "✗ 没有活动线程（未在调试中）";
            }

            var frames = thread.StackFrames;
            if (frames == null || frames.Count == 0)
            {
                return "当前没有调用栈（未在断点处停顿）";
            }

            var max = Math.Max(1, command.MaxItems);
            var builder = new StringBuilder();
            builder.Append("调用栈（自顶向下，共 ").Append(frames.Count).Append(" 帧）：\n");

            var current = debugger.CurrentStackFrame;
            var shown = 0;
            foreach (StackFrame frame in frames)
            {
                if (shown >= max)
                {
                    builder.Append("· …（其余省略）\n");
                    break;
                }

                try
                {
                    builder.Append("· ").Append(frame.FunctionName ?? "(未知函数)");
                    var module = frame.Module;
                    if (!string.IsNullOrWhiteSpace(module))
                    {
                        builder.Append(" @ ").Append(module);
                    }

                    if (ReferenceEquals(frame, current))
                    {
                        builder.Append("   ← 当前帧");
                    }

                    builder.Append('\n');
                    shown++;
                }
                catch (Exception)
                {
                    // 单帧属性不可读：跳过
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static string GetLocals(Debugger debugger, DebugCommand command)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var frame = debugger.CurrentStackFrame;
            if (frame == null)
            {
                return "✗ 当前没有活动栈帧（未在断点处停顿）";
            }

            var max = Math.Max(1, command.MaxItems);
            var builder = new StringBuilder();

            AppendExpressions(builder, "局部变量", frame.Locals, max);
            AppendExpressions(builder, "参数", frame.Arguments, max);

            return builder.Length == 0 ? "当前帧没有可读的局部变量 / 参数" : builder.ToString().TrimEnd();
        }

        private static void AppendExpressions(StringBuilder builder, string title, Expressions expressions, int max)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            if (expressions == null || expressions.Count == 0)
            {
                return;
            }

            builder.Append(title).Append("（").Append(expressions.Count).Append("）：\n");
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
                    builder.Append("· ").Append(expression.Name);
                    var type = expression.Type;
                    if (!string.IsNullOrWhiteSpace(type))
                    {
                        builder.Append(" : ").Append(type);
                    }

                    builder.Append(" = ").Append(expression.Value ?? "(无值)").Append('\n');
                    shown++;
                }
                catch (Exception)
                {
                    // 单个表达式不可读：跳过
                }
            }
        }

        private static string Evaluate(Debugger debugger, DebugCommand command)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var expression = (command.Expression ?? string.Empty).Trim();
            if (expression.Length == 0)
            {
                return "✗ expression 不能为空";
            }

            try
            {
                var result = debugger.GetExpression(expression, false, 5000);
                if (result == null || !result.IsValidValue)
                {
                    return "✗ 无法求值「" + expression + "」" +
                           (result == null ? string.Empty : "：" + result.Value);
                }

                var type = result.Type;
                return expression + " = " + (result.Value ?? "(无值)") +
                       (string.IsNullOrWhiteSpace(type) ? string.Empty : "  [" + type + "]");
            }
            catch (Exception ex)
            {
                return "✗ 求值失败：" + ex.GetBaseException().Message;
            }
        }

        private static string Status(Debugger debugger)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            string mode;
            switch (debugger.CurrentMode)
            {
                case dbgDebugMode.dbgRunMode:
                    mode = "运行中";
                    break;
                case dbgDebugMode.dbgBreakMode:
                    mode = "断点处暂停";
                    break;
                case dbgDebugMode.dbgDesignMode:
                    mode = "设计模式（未调试）";
                    break;
                default:
                    mode = "未知";
                    break;
            }

            var breakpoints = debugger.Breakpoints;
            return "调试器状态：" + mode + "；断点数：" + (breakpoints?.Count ?? 0);
        }

        // ==================== 辅助 ====================

        private static string ResolveFile(string root, string file)
        {
            var value = (file ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return null;
            }

            try
            {
                var full = Path.IsPathRooted(value) || string.IsNullOrWhiteSpace(root)
                    ? Path.GetFullPath(value)
                    : Path.GetFullPath(Path.Combine(root, value.Replace('/', '\\')));

                return File.Exists(full) ? full : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        private static string RelativeOrFull(string path)
        {
            return string.IsNullOrEmpty(path) ? "(无文件)" : path.Replace('\\', '/');
        }
    }
}
