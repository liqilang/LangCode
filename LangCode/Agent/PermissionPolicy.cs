using LangCode.Domain.Catalog;
using LangCode.Domain.Settings;
using LangCode.Workspace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LangCode.Agent
{
    /// <summary>
    /// 统一权限判定：把「用户设置 / 工作区策略 / 会话临时授权」合成一个入口 
    /// </summary>
    public sealed class PermissionPolicy
    {
        public PermissionPolicy(AppSettings settings, WorkspacePolicy workspace, SessionGrantStore grants)
        {
            Settings = settings ?? new AppSettings();
            Workspace = (workspace ?? new WorkspacePolicy()).Normalize();
            Grants = grants ?? new SessionGrantStore();
        }

        public AppSettings Settings { get; }

        public WorkspacePolicy Workspace { get; }

        public SessionGrantStore Grants { get; }

        /// <summary>strict / balanced / trusted（工作区策略优先，其次用户设置）</summary>
        public string Mode
        {
            get
            {
                var mode = (Workspace.Mode ?? string.Empty).Trim().ToLowerInvariant();
                if (mode == "strict" || mode == "trusted" || mode == "balanced")
                {
                    return mode;
                }

                var fallback = (Settings.ApprovalMode ?? string.Empty).Trim().ToLowerInvariant();
                return fallback == "strict" || fallback == "trusted" ? fallback : "balanced";
            }
        }

        /// <summary>命令解释器：policy 覆盖用户设置</summary>
        public string Shell
        {
            get
            {
                var shell = (Workspace.Shell ?? string.Empty).Trim().ToLowerInvariant();
                if (shell == "cmd" || shell == "powershell" || shell == "bash")
                {
                    return shell;
                }

                var fallback = (Settings.CommandShell ?? string.Empty).Trim().ToLowerInvariant();
                return fallback == "powershell" || fallback == "bash" ? fallback : "cmd";
            }
        }

        /// <summary>
        /// 工具的硬拒绝原因（命中即不执行、不弹审批）
        /// </summary>
        public string BlockReason(ToolAction action)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.Name))
            {
                return null;
            }

            if (Workspace.DeniedTools.Any(item => string.Equals(item, action.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return "工作区策略禁止使用工具：" + action.Name;
            }

            // allowedTools 是「破坏性 / 外部工具」的白名单；只读与编排工具不受它限制
            if (Workspace.AllowedTools.Count > 0 && IsRiskyTool(action.Name) && !Workspace.AllowedTools.Any(item => string.Equals(item, action.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return "工作区策略只允许以下工具：" + string.Join("、", Workspace.AllowedTools) + "（" + action.Name + " 不在白名单内）";
            }

            if (IsBrowserEvaluate(action) && !Workspace.AllowBrowserEvaluate)
            {
                return "工作区策略禁止 browser_action 的 evaluate（任意 JS 执行）";
            }

            foreach (var path in PathsOf(action))
            {
                var blocked = DeniedPathReason(path);
                if (blocked != null)
                {
                    return blocked;
                }
            }

            if (string.Equals(action.Name, "run_command", StringComparison.OrdinalIgnoreCase))
            {
                var commandBlocked = CommandBlockReason(action.Get("command"));
                if (commandBlocked != null)
                {
                    return commandBlocked;
                }
            }

            var urlBlocked = UrlBlockReason(action);
            if (urlBlocked != null)
            {
                return urlBlocked;
            }

            return null;
        }

        /// <summary>
        /// 该动作是否自动批准（不弹确认卡）
        /// </summary>
        public bool IsAutoApproved(ToolAction action)
        {
            if (action == null || BlockReason(action) != null)
            {
                return false;
            }

            // 敏感文件不接受「本次会话始终允许」：每次都必须看到具体路径再确认
            if (IsSensitiveAction(action))
            {
                return false;
            }

            if (Grants.IsGranted(action))
            {
                return true;
            }

            var mode = Mode;
            if (mode == "strict")
            {
                return false;
            }

            if (mode == "trusted")
            {
                return !IsSensitiveAction(action);
            }

            // balanced：读设置页的类别开关；敏感文件强制回到「需要确认」
            return ApprovalCatalog.IsAutoApprovedForTool(Settings, action) && !IsSensitiveAction(action);
        }

        /// <summary>
        /// 一次待审批改动的自动批准判定
        /// </summary>
        public bool IsAutoApprovedEdit(PendingEdit edit)
        {
            if (edit == null)
            {
                return false;
            }

            if (DeniedPathReason(edit.RelativePath) != null ||
                (!string.IsNullOrWhiteSpace(edit.MoveToPath) && DeniedPathReason(edit.MoveToPath) != null))
            {
                return false;
            }

            if (IsSensitiveEdit(edit))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(edit.ToolName) && Grants.IsGrantedByName(edit.ToolName))
            {
                return true;
            }

            var mode = Mode;
            if (mode == "strict")
            {
                return false;
            }

            if (mode == "trusted")
            {
                return true;
            }

            var category = edit.IsNewFile ? ApprovalCatalog.Create : ApprovalCatalog.Edit;
            return ApprovalCatalog.IsAutoApproved(Settings, category);
        }

        /// <summary>
        /// 显式路径拒绝（敏感文件不属于硬拒绝，走审批）
        /// </summary>
        public string DeniedPathReason(string relativePath)
        {
            return Workspace.IsPathDenied(relativePath)
                ? "路径被工作区策略禁止访问：" + relativePath
                : null;
        }

        /// <summary>
        /// 该路径是否是敏感文件（.env / 密钥 / 证书 / appsettings.Production.json 等）
        /// </summary>
        public bool IsSensitivePath(string relativePath)
        {
            if (!Workspace.BlockSensitiveFiles || Workspace.AllowSensitiveFiles)
            {
                return false;
            }

            return SensitiveFiles.IsSensitive(relativePath);
        }

        /// <summary>
        /// 该动作是否涉及敏感文件（用于强制确认）
        /// </summary>
        public bool IsSensitiveAction(ToolAction action)
        {
            return action != null && PathsOf(action).Any(IsSensitivePath);
        }

        /// <summary>
        /// 命令是否被工作区策略放行
        /// </summary>
        public string CommandBlockReason(string command)
        {
            var text = (command ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            foreach (var prefix in Workspace.DeniedCommandPrefixes)
            {
                var value = (prefix ?? string.Empty).Trim();
                if (value.Length > 0 && text.StartsWith(value, StringComparison.OrdinalIgnoreCase))
                {
                    return "命令命中工作区禁用前缀：" + value;
                }
            }

            var allowed = Workspace.AllowedCommandPrefixes
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToList();

            if (allowed.Count > 0 &&
                !allowed.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return "命令不在工作区允许前缀内；允许的前缀：" + string.Join("、", allowed);
            }

            return null;
        }

        /// <summary>
        /// 网络目标守卫：open_url / browser navigate
        /// </summary>
        public string UrlBlockReason(ToolAction action)
        {
            if (action == null)
            {
                return null;
            }

            string url = null;
            if (string.Equals(action.Name, "open_url", StringComparison.OrdinalIgnoreCase))
            {
                url = action.Get("url");
            }
            else if (string.Equals(action.Name, "browser_action", StringComparison.OrdinalIgnoreCase) && string.Equals(action.Get("action"), "navigate", StringComparison.OrdinalIgnoreCase))
            {
                url = action.Get("url");
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            // 相对路径 = 工作区文件；敏感文件要额外确认，而不是直接打开
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out _))
            {
                if (IsSensitivePath(url) && !AllowSensitiveByPolicy())
                {
                    return "该操作会打开敏感文件，需要先在策略里放行：" + url;
                }

                return null;
            }

            return CheckUrl(url);
        }

        /// <summary>
        /// 直接检查一个 URL（供 BrowserTool 重定向逐跳校验）
        /// </summary>
        public string CheckUrl(string url)
        {
            return NetworkGuard.Check(url, !Workspace.BlockPrivateNetwork, Workspace.AllowLocalhost);
        }

        /// <summary>
        /// 当前策略摘要（系统提示 / 诊断用）
        /// </summary>
        public string Describe()
        {
            var parts = new List<string> { "审批模式=" + Mode };
            if (Workspace.Loaded)
            {
                parts.Add("工作区策略=" + Workspace.Describe());
            }
            else if (!string.IsNullOrWhiteSpace(Workspace.LoadError))
            {
                parts.Add("策略文件异常=" + Workspace.LoadError);
            }

            return string.Join("；", parts);
        }

        private bool AllowSensitiveByPolicy()
        {
            return !Workspace.BlockSensitiveFiles || Workspace.AllowSensitiveFiles;
        }

        private bool IsSensitiveEdit(PendingEdit edit)
        {
            if (!Workspace.BlockSensitiveFiles || Workspace.AllowSensitiveFiles)
            {
                return false;
            }

            return SensitiveFiles.IsSensitive(edit.RelativePath) || (!string.IsNullOrWhiteSpace(edit.MoveToPath) && SensitiveFiles.IsSensitive(edit.MoveToPath));
        }

        private static bool IsRiskyTool(string toolName)
        {
            return ToolCatalog.IsDestructive(toolName) || ToolCatalog.DispatchOf(toolName) == ToolDispatch.External;
        }

        private static bool IsBrowserEvaluate(ToolAction action)
        {
            if (action == null || !string.Equals(action.Name, "browser_action", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var verb = (action.Get("action") ?? string.Empty).Trim();
            return string.Equals(verb, "evaluate", StringComparison.OrdinalIgnoreCase) || string.Equals(verb, "eval", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> PathsOf(ToolAction action)
        {
            if (action == null)
            {
                yield break;
            }

            foreach (var key in new[] { "path", "from", "to" })
            {
                var value = action.Get(key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }

            var paths = action.Get("paths");
            if (!string.IsNullOrWhiteSpace(paths))
            {
                foreach (var part in paths.Split(new[] { '\n', '\r', ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var value = part.Trim();
                    if (value.Length > 0)
                    {
                        yield return value;
                    }
                }
            }
        }

    }
}
