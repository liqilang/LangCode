using System;
using System.Collections.Generic;

namespace LangCode.Agent
{
    /// <summary>
    /// 「本次会话内始终允许」的临时授权 
    /// </summary>
    public sealed class SessionGrantStore
    {
        private readonly object _gate = new object();
        private readonly HashSet<string> _tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 这个动作是否命中本次会话的授权
        /// </summary>
        public bool IsGranted(ToolAction action)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.Name))
            {
                return false;
            }

            lock (_gate)
            {
                if (_tools.Contains(action.Name))
                {
                    return true;
                }

                if (string.Equals(action.Name, "run_command", StringComparison.OrdinalIgnoreCase))
                {
                    var command = (action.Get("command") ?? string.Empty).Trim();
                    return command.Length > 0 && _commands.Contains(command);
                }

                return false;
            }
        }

        /// <summary>
        /// 按工具名判断授权（供 PendingEdit 使用；不依赖 ToolAction）
        /// </summary>
        public bool IsGrantedByName(string toolName)
        {
            var value = (toolName ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return false;
            }

            lock (_gate)
            {
                return _tools.Contains(value);
            }
        }

        /// <summary>
        /// 按工具名授权（移动 / 删除 / 编辑等）
        /// </summary>
        public void GrantTool(string toolName)
        {
            var value = (toolName ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                _tools.Add(value);
            }
        }

        /// <summary>
        /// 按命令全文授权（不扩大到同工具的其他命令）
        /// </summary>
        public void GrantCommand(string command)
        {
            var value = (command ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                _commands.Add(value);
            }
        }

        /// <summary>
        /// 清空（新建 / 切换会话时调用）
        /// </summary>
        public void Clear()
        {
            lock (_gate)
            {
                _tools.Clear();
                _commands.Clear();
            }
        }

        /// <summary>
        /// 当前授权数量（诊断用）
        /// </summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _tools.Count + _commands.Count;
                }
            }
        }
    }
}
