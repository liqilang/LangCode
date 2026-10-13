using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LangCode.VsExt.Workspace
{
    /// <summary>
    /// 命令安全策略：判断一条 shell 命令是否属于「绝不执行」的那一类 
    /// </summary>
    public static class CommandPolicy
    {
        /// <summary>
        /// 高风险命令（按「首词」精确匹配，路径里的同名文字不会误伤）
        /// </summary>
        private static readonly HashSet<string> DeniedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "format", "format.com", "diskpart", "mkfs", "mkfs.ext4", "mkfs.ntfs", "fdisk",
            "shutdown", "restart-computer", "stop-computer", "bcdedit", "vssadmin", "wbadmin",
        };

        /// <summary>
        /// 危险参数组合（对整条命令按小写做正则匹配）
        /// </summary>
        private static readonly List<KeyValuePair<string, Regex>> DeniedPatterns = BuildPatterns();

        private static List<KeyValuePair<string, Regex>> BuildPatterns()
        {
            var patterns = new List<KeyValuePair<string, Regex>>();

            void Add(string description, string pattern)
            {
                patterns.Add(new KeyValuePair<string, Regex>(description, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)));
            }

            // 递归删除根目录的各种写法（拆开写也不再漏过）
            Add("rm -rf /", @"\brm\s+-[a-z]*r[a-z]*f[a-z]*\s+/\s*(\*|$)");
            Add("rm -fr /", @"\brm\s+-[a-z]*f[a-z]*r[a-z]*\s+/\s*(\*|$)");

            // Windows 上整盘删除
            Add("del /f /s /q c:\\", @"\bdel\s+/[fsq][^\r\n]*\bc:\\");
            Add("rd /s /q c:\\", @"\brd\s+/s\s+/q\s+c:\\");

            // 注册表 / 引导 / 备份的破坏性操作
            Add("reg delete hklm", @"\breg(\.exe)?\s+delete\s+hklm");
            Add("reg delete hkcr", @"\breg(\.exe)?\s+delete\s+hkcr");
            Add("cipher /w", @"\bcipher\s+/w\b");
            Add("takeown /f c:\\", @"\btakeown\s+/f\s+c:\\");
            Add("icacls c:\\", @"\bicacls\s+c:\\");

            // 直接写块设备 / 磁盘镜像
            Add("dd 写块设备", @"\bdd\s+if=/dev/\w+\s+of=/dev/");
            Add("> /dev/sdX", @">\s*/dev/sd[a-z]");

            // fork 炸弹
            Add("fork 炸弹", @":\s*\(\s*\)\s*\{.*\|.*&.*\}\s*;\s*:");

            // 磁盘格式化（带盘符参数才算，避免把 --format 之类的选项算进来）
            Add("format C:", @"\bformat(\.com)?\s+[a-z]:");

            return patterns;
        }

        /// <summary>
        /// 只做内置规则检查（无用户配置）
        /// </summary>
        public static string MatchDangerous(string command)
        {
            return MatchDangerous(command, null, null);
        }

        /// <summary>
        /// 检查一条命令
        /// </summary>
        /// <param name="command">原始命令</param>
        /// <param name="userDenyList">用户黑名单（分号 / 换行分隔的子串；命中即拒绝）</param>
        /// <param name="userAllowPrefixes">用户信任前缀（分号 / 换行分隔；命中则跳过内置检查，黑名单仍生效）</param>
        /// <returns>命中的规则说明；未命中返回 <c>null</c></returns>
        public static string MatchDangerous(string command, string userDenyList, string userAllowPrefixes)
        {
            var text = (command ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            var lower = text.ToLowerInvariant();

            // ① 用户黑名单永远优先：它代表「我明确不想让它跑」
            var denied = MatchUserEntry(lower, userDenyList);
            if (denied != null)
            {
                return "你自定义的禁用规则：" + denied;
            }

            // ② 用户信任前缀：显式放行时跳过内置规则（用户自己写的命令，他自己担责）
            if (MatchUserEntry(lower, userAllowPrefixes, prefixOnly: true) != null)
            {
                return null;
            }

            // ③ 首词精确匹配高风险命令名
            var first = FirstWord(lower);
            if (first.Length > 0 && DeniedCommands.Contains(first))
            {
                return "高风险命令：" + first;
            }

            // ④ 危险参数组合
            foreach (var pair in DeniedPatterns)
            {
                if (pair.Value.IsMatch(lower))
                {
                    return pair.Key;
                }
            }

            return null;
        }

        /// <summary>
        /// 取命令的首词（去掉路径与扩展名之后）
        /// </summary>
        private static string FirstWord(string command)
        {
            var text = (command ?? string.Empty).TrimStart();
            var end = text.IndexOfAny(new[] { ' ', '\t', '&', '|', ';', '\r', '\n' });
            var word = end < 0 ? text : text.Substring(0, end);
            var slash = Math.Max(word.LastIndexOf('\\'), word.LastIndexOf('/'));
            return (slash >= 0 ? word.Substring(slash + 1) : word).Trim('"').ToLowerInvariant();
        }

        /// <summary>
        /// 在用户配置里找命中的条目（黑名单用「包含」，信任前缀用「开头」）
        /// </summary>
        private static string MatchUserEntry(string lowerCommand, string entries, bool prefixOnly = false)
        {
            if (string.IsNullOrWhiteSpace(entries))
            {
                return null;
            }

            foreach (var raw in entries.Split(new[] { ';', '\r', '\n' }))
            {
                var entry = raw.Trim().ToLowerInvariant();
                if (entry.Length == 0)
                {
                    continue;
                }

                var hit = prefixOnly ? lowerCommand.StartsWith(entry, StringComparison.Ordinal) : lowerCommand.IndexOf(entry, StringComparison.Ordinal) >= 0;

                if (hit)
                {
                    return entry;
                }
            }

            return null;
        }
    }
}

