using System.Collections.Generic;

namespace LangCode.VsExt.Agent
{
    /// <summary>
    /// 一条调试器命令（由 <c>debug_inspect</c> / <c>debug_control</c> 工具翻译而来）。
    ///
    /// 放在 Agent 命名空间：它是「工具协议」的一部分，由 AgentRunner 构造、
    /// 由 UI 层回调解到 VS 集成层（<c>VsDebuggerController</c>）执行。
    /// </summary>
    public sealed class DebugCommand
    {
        /// <summary>
        /// 动作：list_breakpoints / set_breakpoint / remove_breakpoint / clear_breakpoints /
        /// get_stack / get_locals / evaluate / status / continue / step_over / step_into /
        /// step_out / pause / stop
        /// </summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>
        /// 目标文件（相对工作区根，或绝对路径）；断点操作使用
        /// </summary>
        public string File { get; set; } = string.Empty;

        /// <summary>
        /// 行号（1 起）；断点操作使用
        /// </summary>
        public int Line { get; set; }

        /// <summary>
        /// 断点条件表达式（可空）
        /// </summary>
        public string Condition { get; set; } = string.Empty;

        /// <summary>
        /// 求值表达式；evaluate 使用
        /// </summary>
        public string Expression { get; set; } = string.Empty;

        /// <summary>
        /// 列表类结果的条数上限
        /// </summary>
        public int MaxItems { get; set; } = 30;

        /// <summary>
        /// 只读动作（不改变调试状态 / 不写断点）：这些归类为「读取」，默认放开
        /// </summary>
        public static readonly HashSet<string> ReadOnlyActions = new HashSet<string>
        {
            "list_breakpoints",
            "get_stack",
            "get_locals",
            "evaluate",
            "status",
        };

        /// <summary>
        /// 当前动作是否只读
        /// </summary>
        public bool IsReadOnly => ReadOnlyActions.Contains((Action ?? string.Empty).Trim().ToLowerInvariant());
    }
}
