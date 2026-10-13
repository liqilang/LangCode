using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LangCode.VsExt.Workspace
{
    /// <summary>
    /// 
    /// </summary>
    public sealed class SymbolItem
    {
        public SymbolItem(int line, string kind, string name)
        {
            Line = line;
            Kind = kind ?? string.Empty;
            Name = name ?? string.Empty;
        }
        /// <summary>
        /// 
        /// </summary>
        public int Line { get; }
        /// <summary>
        /// 
        /// </summary>
        public string Kind { get; }

        public string Name { get; }
    }

    /// <summary>
    /// 轻量符号提取：用一组按语言常见写法的正则，从源码里挑出「类 / 接口 / 方法 / 函数」
    /// </summary>
    public static class SymbolScanner
    {
        /// <summary>
        /// 
        /// </summary>
        private static readonly List<KeyValuePair<string, Regex>> Rules = BuildRules();

        private static List<KeyValuePair<string, Regex>> BuildRules()
        {
            var rules = new List<KeyValuePair<string, Regex>>
            {
                // 类型声明：C# / Java / TS / Kotlin / Swift 等大多数语言都长这样
                new KeyValuePair<string, Regex>(
                "类型",
                new Regex(@"(?<![\w])(?:class|interface|struct|enum|record|trait)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)),

                // Python / JS 的关键字函数
                new KeyValuePair<string, Regex>(
                "函数",
                new Regex(@"(?<![\w])(?:def|function)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)),

                // TS / JS 的箭头函数常量：const foo = () => / async () =>
                new KeyValuePair<string, Regex>(
                "函数",
                new Regex(@"^\s*(?:export\s+)?(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?\(", RegexOptions.CultureInvariant)),

                // C# / Java 的方法：带访问修饰符 + 返回类型 + 名字(…
                new KeyValuePair<string, Regex>(
                "方法",
                new Regex(
                    @"^\s*(?:public|private|protected|internal)\s+(?:static\s+|virtual\s+|override\s+|async\s+|sealed\s+|partial\s+|new\s+)*" +
                    @"[\w<>\[\],\.\?]+\s+(?<name>[A-Za-z_]\w*)\s*\(",
                    RegexOptions.CultureInvariant))
            };

            return rules;
        }
        /// <summary>
        /// 
        /// </summary>
        public static IList<SymbolItem> Scan(string text, int maxResults = 300)
        {
            var symbols = new List<SymbolItem>();
            if (string.IsNullOrEmpty(text))
            {
                return symbols;
            }

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length && symbols.Count < maxResults; i++)
            {
                var line = lines[i];
                if (IsCommentLine(line))
                {
                    continue;
                }

                foreach (var rule in Rules)
                {
                    var match = rule.Value.Match(line);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var name = match.Groups["name"].Value;
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    // 关键字误命中（例如 `class = "x"` 这类赋值）：名字是保留字就跳过
                    if (IsKeyword(name))
                    {
                        break;
                    }

                    symbols.Add(new SymbolItem(i + 1, rule.Key, name));
                    break;
                }
            }

            return symbols;
        }

        private static bool IsCommentLine(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("#", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal);
        }

        private static bool IsKeyword(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "if":
                case "for":
                case "while":
                case "switch":
                case "catch":
                case "using":
                case "return":
                case "new":
                case "get":
                case "set":
                    return true;
                default:
                    return false;
            }
        }
    }
}

