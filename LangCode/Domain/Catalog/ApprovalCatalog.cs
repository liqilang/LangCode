using LangCode.Agent;
using LangCode.Domain.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LangCode.Domain.Catalog
{

    /// <summary>
    /// 一个「可自动批准」的操作类别
    /// </summary>
    public sealed class ApprovalCategory
    {
        public ApprovalCategory(string key, string name, string description, string property, bool wired)
        {
            Key = key;
            Name = name;
            Description = description;
            Property = property;
            Wired = wired;
        }

        /// <summary>
        /// 类别键（read / edit / create / command / browser / mcp）
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// 界面上的名字
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// 界面上的说明
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// <see cref="AppSettings"/> 上的同名布尔属性（设置页按 Tag 反射读写它）
        /// </summary>
        public string Property { get; }

        /// <summary>
        /// 是否已经有对应的工具（false 表示界面要标注「尚未接入」）
        /// </summary>
        public bool Wired { get; }
    }

    /// <summary>
    /// 按类别的自动批准目录
    /// </summary>
    public static class ApprovalCatalog
    {
        public const string Read = "read";
        public const string Edit = "edit";
        public const string Create = "create";
        public const string Command = "command";
        public const string Browser = "browser";
        public const string Mcp = "mcp";

        /// <summary>browser_action 的 evaluate / eval：在页面里执行任意 JS，单独一类审批</summary>
        public const string Script = "browser-script";

        /// <summary>
        /// 没有任何类别匹配时的返回值（永远需要用户确认）
        /// </summary>
        public const string Unknown = "";

        private static readonly IList<ApprovalCategory> Categories =
        [
            new ApprovalCategory(Read, "读取文件", "读文件 / 列目录 / 搜索代码 / 语义检索，不修改任何内容", nameof(AppSettings.AutoApproveReads), true),
            new ApprovalCategory(Edit, "编辑已有文件", "局部替换 / 结构化编辑或覆盖已有文件", nameof(AppSettings.AutoApproveEdits), true),
            new ApprovalCategory(Create, "新增文件", "创建新文件", nameof(AppSettings.AutoApproveCreates), true),
            new ApprovalCategory(Command, "执行终端命令", "在工作区里运行 shell 命令（危险命令会被直接拒绝）", nameof(AppSettings.AutoApproveCommands), true),
            new ApprovalCategory(Browser, "浏览器 / 联网操作", "联网搜索、打开网页 / 抓正文，或用 DevTools 协议做导航、点击、填表、截图", nameof(AppSettings.AutoApproveBrowser), true),
            new ApprovalCategory(Script, "浏览器脚本执行", "browser_action 的 evaluate / eval：会在页面里执行任意 JS，风险高于普通浏览器操作", nameof(AppSettings.AutoApproveBrowserScript), true),
            new ApprovalCategory(Mcp, "MCP 工具", "调用「设置 → MCP」里已启用的服务器上的工具", nameof(AppSettings.AutoApproveMcp), true),
        ];

        /// <summary>
        /// 全部类别（界面按此顺序展示）
        /// </summary>
        public static IList<ApprovalCategory> All => Categories;

        /// <summary>
        /// 把工具名归到某个类别；无法归类返回 <see cref="Unknown"/>。
        /// 映射的**单一来源**在工具目录（<see cref="ToolCatalog"/>），这里只做转发 ——
        /// 新增工具时无需在两个 switch 里各改一遍。
        /// </summary>
        public static string CategoryOf(string toolName)
        {
            return ToolCatalog.ApprovalCategoryOf(toolName);
        }

        /// <summary>
        /// 按动作（而不是工具名）归类：browser_action 的 evaluate 单独归到 <see cref="Script"/>
        /// </summary>
        public static string CategoryOf(ToolAction action)
        {
            if (action != null && string.Equals(action.Name, "browser_action", StringComparison.OrdinalIgnoreCase))
            {
                var verb = (action.Get("action") ?? string.Empty).Trim();
                if (string.Equals(verb, "evaluate", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(verb, "eval", StringComparison.OrdinalIgnoreCase))
                {
                    return Script;
                }
            }

            return CategoryOf(action?.Name);
        }

        /// <summary>
        /// 类别 → <see cref="AppSettings"/> 属性 的映射
        /// </summary>
        private static readonly Dictionary<string, PropertyInfo> PropertyByCategory = BuildPropertyMap();

        private static Dictionary<string, PropertyInfo> BuildPropertyMap()
        {
            var map = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (var category in Categories)
            {
                var property = typeof(AppSettings).GetProperty(category.Property);
                if (property != null)
                {
                    map[category.Key] = property;
                }
            }

            return map;
        }

        /// <summary>
        /// 该类别是否已放开（不用再问用户）未知类别一律不放开
        /// </summary>
        public static bool IsAutoApproved(AppSettings settings, string category)
        {
            if (settings == null || string.IsNullOrEmpty(category))
            {
                return false;
            }

            if (!PropertyByCategory.TryGetValue(category, out var property))
            {
                return false;
            }

            return property.GetValue(settings) is bool value && value;
        }

        /// <summary>
        /// Auto 芯片上的摘要文案：把「当前放开了几类」直接写在按钮上
        /// </summary>
        public static string DescribeAutoApproval(AppSettings settings)
        {
            var wired = new List<ApprovalCategory>();
            foreach (var category in Categories)
            {
                if (category.Wired)
                {
                    wired.Add(category);
                }
            }

            if (wired.Count == 0)
            {
                return "自动";
            }

            var approved = 0;
            foreach (var category in wired)
            {
                if (IsAutoApproved(settings, category.Key))
                {
                    approved++;
                }
            }

            if (approved == 0)
            {
                return "不自动批准";
            }

            return approved == wired.Count ? "全部批准" : approved + " 项批准";
        }

        /// <summary>
        /// 某个工具是否已放开
        /// </summary>
        public static bool IsAutoApprovedForTool(AppSettings settings, string toolName)
        {
            return IsAutoApproved(settings, CategoryOf(toolName));
        }

        /// <summary>
        /// 按具体动作判定（会识别 browser_action 的 evaluate）
        /// </summary>
        public static bool IsAutoApprovedForTool(AppSettings settings, ToolAction action)
        {
            return IsAutoApproved(settings, CategoryOf(action));
        }

        /// <summary>
        /// 是否所有「已接入」的类别都放开了（决定 Auto 按钮的高亮态）
        /// </summary>
        public static bool AllWiredApproved(AppSettings settings)
        {
            return settings != null && Categories.Where(c => c.Wired).All(c => IsAutoApproved(settings, c.Key));
        }

        /// <summary>
        /// 读 / 写某一类别的开关值（界面用）
        /// </summary>
        public static bool IsOn(AppSettings settings, ApprovalCategory category)
        {
            return category != null && IsAutoApproved(settings, category.Key);
        }

        /// <summary>
        /// 设置某一类别的开关（界面用）
        /// </summary>
        public static void Set(AppSettings settings, ApprovalCategory category, bool value)
        {
            if (settings == null || category == null)
            {
                return;
            }

            var property = typeof(AppSettings).GetProperty(category.Property);
            if (property != null && property.PropertyType == typeof(bool))
            {
                property.SetValue(settings, value);
            }
        }
    }
}

