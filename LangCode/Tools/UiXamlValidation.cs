using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LangCode.Tools
{
    /// <summary>
    /// 一条校验问题（缺失的资源键 / 缺失的图标资产 / 损坏的 XML…）
    /// </summary>
    public sealed class UiXamlIssue
    {
        public UiXamlIssue(string kind, string key, string message)
        {
            Kind = kind;
            Key = key;
            Message = message;
        }

        /// <summary>
        /// 问题类别：missing-key / icon-asset / xml / assets
        /// </summary>
        public string Kind { get; }

        /// <summary>
        /// 相关的键名（资源键 / 图标名 / 文件路径）
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// 可读的说明
        /// </summary>
        public string Message { get; }
    }

    /// <summary>
    /// 校验报告
    /// </summary>
    public sealed class UiXamlReport
    {
        public UiXamlReport(bool ok, string summary, IReadOnlyList<UiXamlIssue> issues, IReadOnlyList<UiXamlIssue> warnings, IReadOnlyDictionary<string, int> styleDebt)
        {
            Ok = ok;
            Summary = summary;
            Issues = issues;
            Warnings = warnings;
            StyleDebt = styleDebt;
        }

        /// <summary>
        /// 是否通过（无阻断级问题）
        /// </summary>
        public bool Ok { get; }

        /// <summary>
        /// 一句话摘要
        /// </summary>
        public string Summary { get; }

        /// <summary>
        /// 阻断级问题
        /// </summary>
        public IReadOnlyList<UiXamlIssue> Issues { get; }

        /// <summary>
        /// 提示级问题（不阻断）
        /// </summary>
        public IReadOnlyList<UiXamlIssue> Warnings { get; }

        /// <summary>
        /// 设计令牌债务（片段 → 数量）；本形态尚未启用棘轮，恒为空
        /// </summary>
        public IReadOnlyDictionary<string, int> StyleDebt { get; }
    }

    /// <summary>
    /// 界面 XAML 管线的公开校验入口（给 <c>tools/XamlGuard</c> 用）
    /// </summary>
    public static class UiXamlValidation
    {
        private static readonly Regex KeyDefinitionRegex = new Regex("x:Key\\s*=\\s*\"(?<key>[^\"]+)\"", RegexOptions.CultureInvariant);

        private static readonly Regex StaticResourceRegex = new Regex("\\{StaticResource\\s+(?<key>[^}]+)\\}", RegexOptions.CultureInvariant);

        /// <summary>
        /// <c>{DynamicResource Xxx}</c>：主题令牌随主题切换全靠它，同样需要校验键是否存在
        /// </summary>
        private static readonly Regex DynamicResourceRegex = new Regex("\\{DynamicResource\\s+(?<key>[^}]+)\\}", RegexOptions.CultureInvariant);

        /// <summary>
        /// <c>IconName="xxx"</c>：XAML 与 C# 里的写法完全相同，一个正则同时扫两边
        /// </summary>
        private static readonly Regex IconNameRegex = new Regex("IconName\\s*=\\s*\"(?<name>[^\"]+)\"", RegexOptions.CultureInvariant);

        // 注释里可能写着示例（例如 “{StaticResource Ap.*}”），扫描引用前必须先把注释剥掉，否则误报
        private static readonly Regex CommentRegex = new Regex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        /// <summary>
        /// 跑一遍校验
        /// </summary>
        public static Task<UiXamlReport> ValidateAsync()
        {
            return Task.Run(Validate);
        }

        /// <summary>
        /// 导出「最终 XAML」供离线排查
        /// </summary>
        public static Task<string> RenderXamlAsync(string theme, bool vectorIcons)
        {
            return Task.Run(() =>
            {
                var root = FindRepositoryRoot();
                var extensionDir = Path.Combine(root, "LangCode");
                var builder = new StringBuilder();

                builder.AppendLine("<!-- LangCode XAML dump -->");
                builder.AppendLine("<!-- theme=" + (theme ?? "dark") + " vectorIcons=" + (vectorIcons ? "true" : "false") + " -->");
                builder.AppendLine("<!-- 说明：本形态界面为编译期 XAML，这里导出的是资源字典原文的拼接 -->");
                builder.AppendLine();

                foreach (var relative in new[]
                {
                    Path.Combine("Ui", "Theme", "Palette.xaml"),
                    Path.Combine("Ui", "Theme", "PaletteLight.xaml"),
                    Path.Combine("Ui", "Theme", "Controls.xaml"),
                    Path.Combine("Ui", "ToolWindows", "AgentToolWindowControl.xaml"),
                    Path.Combine("Ui", "ToolWindows", "HistoryDrawerControl.xaml"),
                    Path.Combine("Ui", "ToolWindows", "SettingsDrawerControl.xaml"),
                })
                {
                    var full = Path.Combine(extensionDir, relative);
                    builder.AppendLine("<!-- ===== " + relative + " ===== -->");
                    builder.AppendLine(File.Exists(full) ? File.ReadAllText(full) : "<!-- 缺失：" + relative + " -->");
                    builder.AppendLine();
                }

                return builder.ToString();
            });
        }

        private static UiXamlReport Validate()
        {
            var issues = new List<UiXamlIssue>();
            var warnings = new List<UiXamlIssue>();

            var root = FindRepositoryRoot();
            var extensionDir = Path.Combine(root, "LangCode");
            var uiDir = Path.Combine(extensionDir, "Ui");
            var themeDir = Path.Combine(uiDir, "Theme");
            var toolWindowDir = Path.Combine(uiDir, "ToolWindows");
            var controlsDir = uiDir;
            var iconsDir = Path.Combine(uiDir, "Assets", "Icons");

            if (!Directory.Exists(extensionDir))
            {
                issues.Add(new UiXamlIssue("assets", extensionDir, "找不到扩展源码目录：" + extensionDir));
                return BuildReport(issues, warnings, 0, 0, 0);
            }

            // 1) 收集 XAML 文件并检查良构性
            var xamlFiles = new List<string>();
            CollectXaml(themeDir, xamlFiles);
            CollectXaml(toolWindowDir, xamlFiles);

            var keyDefinitions = new HashSet<string>(StringComparer.Ordinal);
            var referencedKeys = new List<(string Key, string File)>();

            foreach (var file in xamlFiles)
            {
                string content;
                try
                {
                    content = File.ReadAllText(file);
                }
                catch (Exception ex)
                {
                    issues.Add(new UiXamlIssue("xml", file, "读取失败：" + ex.Message));
                    continue;
                }

                try
                {
                    System.Xml.Linq.XDocument.Parse(content);
                }
                catch (Exception ex)
                {
                    issues.Add(new UiXamlIssue("xml", Path.GetFileName(file), "不是良构 XML：" + ex.Message));
                }

                var scannable = CommentRegex.Replace(content, string.Empty);

                foreach (Match match in KeyDefinitionRegex.Matches(scannable))
                {
                    keyDefinitions.Add(match.Groups["key"].Value);
                }

                foreach (Match match in StaticResourceRegex.Matches(scannable))
                {
                    AddKeyReference(referencedKeys, file, match.Groups["key"].Value);
                }

                foreach (Match match in DynamicResourceRegex.Matches(scannable))
                {
                    AddKeyReference(referencedKeys, file, match.Groups["key"].Value);
                }
            }

            // 2) 缺失的资源键
            foreach (var (key, file) in referencedKeys)
            {
                if (!keyDefinitions.Contains(key))
                {
                    issues.Add(new UiXamlIssue("missing-key", key, file + " 引用了未定义的资源键：" + key));
                }
            }

            // 3) 图标资产：XAML 与 C# 里的 IconName 都必须有对应 svg
            var iconNames = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var file in xamlFiles)
            {
                var scannable = CommentRegex.Replace(File.ReadAllText(file), string.Empty);
                foreach (Match match in IconNameRegex.Matches(scannable))
                {
                    iconNames.Add(match.Groups["name"].Value);
                }
            }

            // C# 侧的 IconName：整个 Ui/ 目录都扫（Icon 资产名在任意表现层文件都可能出现）

            if (Directory.Exists(controlsDir))
            {
                foreach (var file in Directory.GetFiles(controlsDir, "*.cs", SearchOption.AllDirectories))
                {
                    foreach (Match match in IconNameRegex.Matches(File.ReadAllText(file)))
                    {
                        iconNames.Add(match.Groups["name"].Value);
                    }
                }
            }

            foreach (var file in Directory.GetFiles(toolWindowDir, "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in IconNameRegex.Matches(File.ReadAllText(file)))
                {
                    iconNames.Add(match.Groups["name"].Value);
                }
            }

            foreach (var name in iconNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var svg = Path.Combine(iconsDir, name + ".svg");
                if (!File.Exists(svg))
                {
                    issues.Add(new UiXamlIssue("icon-asset", name, "缺少图标资产：Ui/Assets/Icons/" + name + ".svg"));
                    continue;
                }

                var content = File.ReadAllText(svg);
                if (content.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    issues.Add(new UiXamlIssue("icon-asset", name, "图标文件不是 SVG：Ui/Assets/Icons/" + name + ".svg"));
                }
                else if (!Regex.IsMatch(content, "<(path|circle|rect|line|polyline|polygon)\\b", RegexOptions.IgnoreCase))
                {
                    issues.Add(new UiXamlIssue("icon-asset", name, "图标里没有任何可绘制的形状：Ui/Assets/Icons/" + name + ".svg"));
                }
            }

            return BuildReport(issues, warnings, xamlFiles.Count, referencedKeys.Count, iconNames.Count);
        }

        /// <summary>
        /// 登记一个资源键引用；类型键（<c>{x:Type ...}</c>）与空键忽略
        /// </summary>
        private static void AddKeyReference(List<(string Key, string File)> into, string file, string rawKey)
        {
            var key = (rawKey ?? string.Empty).Trim();

            // {StaticResource {x:Type TextBox}} 这类是类型键，不是我们自己定义的令牌键
            if (key.Length == 0 || key.IndexOf("x:Type", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            into.Add((key, Path.GetFileName(file)));
        }

        private static UiXamlReport BuildReport(List<UiXamlIssue> issues, List<UiXamlIssue> warnings, int xamlCount, int keyReferenceCount, int iconCount)
        {
            var summary =
                "检查 " + xamlCount + " 个 XAML 文件" +
                " · " + keyReferenceCount + " 个资源键引用" +
                " · " + iconCount + " 个图标引用" +
                " · 问题 " + issues.Count + " 个";

            return new UiXamlReport(
                issues.Count == 0,
                summary,
                issues,
                warnings,
                new Dictionary<string, int>(StringComparer.Ordinal));
        }

        private static void CollectXaml(string directory, List<string> into)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            into.AddRange(Directory.GetFiles(directory, "*.xaml", SearchOption.AllDirectories));
        }

        /// <summary>
        /// 从可执行文件目录向上找到含 LangCode.sln 的仓库根
        /// </summary>
        private static string FindRepositoryRoot()
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "LangCode.sln")))
                {
                    return current.FullName;
                }
            }

            // 退而求其次：从当前工作目录向上找
            for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current != null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "LangCode.sln")))
                {
                    return current.FullName;
                }
            }

            return Directory.GetCurrentDirectory();
        }
    }
}

