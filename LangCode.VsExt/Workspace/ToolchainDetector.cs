using LangCode.VsExt.Agent;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LangCode.VsExt.Workspace
{
    /// <summary>
    /// 从工作区「猜」出来的工具链信息（全部为可选，猜不到就是空）
    /// </summary>
    public sealed class ToolchainInfo
    {
        /// <summary>
        /// 技术栈名（.NET / Node.js / Python / Rust / Go / Java …）
        /// </summary>
        public string Kind = string.Empty;

        /// <summary>
        /// 建议的验证命令（<c>dotnet build</c> / <c>npm test</c> …）；猜不到为 null
        /// </summary>
        public string VerifyCommand;

        /// <summary>
        /// 本地开发服务器地址（如 <c>http://localhost:5173</c>）；猜不到为 null
        /// </summary>
        public string DevUrl;

        /// <summary>
        /// 包管理器（Node 项目：npm / pnpm / yarn）
        /// </summary>
        public string PackageManager = string.Empty;

        /// <summary>
        /// 一句话摘要，供系统提示词的「环境速览」使用；无信息时为空
        /// </summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Kind))
            {
                parts.Add("技术栈：" + Kind);
            }

            if (!string.IsNullOrWhiteSpace(PackageManager))
            {
                parts.Add("包管理器：" + PackageManager);
            }

            if (!string.IsNullOrWhiteSpace(VerifyCommand))
            {
                parts.Add("建议验证命令：`" + VerifyCommand + "`");
            }

            if (!string.IsNullOrWhiteSpace(DevUrl))
            {
                parts.Add("本地开发地址：" + DevUrl);
            }

            return string.Join("；", parts);
        }
    }

    /// <summary>
    /// 技术栈自动探测：让「验证循环 / 浏览器自动化」在用户<b>什么都不配</b>的情况下也能工作
    /// </summary>
    public static class ToolchainDetector
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

        private sealed class CacheEntry
        {
            public ToolchainInfo Info;
            public DateTime AtUtc;
        }

        /// <summary>
        /// 探测工作区技术栈（带短缓存，避免每个工具调用都扫盘）
        /// </summary>
        public static ToolchainInfo Detect(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return new ToolchainInfo();
            }

            lock (Gate)
            {
                if (Cache.TryGetValue(root, out var cached) && DateTime.UtcNow - cached.AtUtc < Ttl)
                {
                    return cached.Info;
                }
            }

            var info = DetectCore(root);

            lock (Gate)
            {
                Cache[root] = new CacheEntry { Info = info, AtUtc = DateTime.UtcNow };
            }

            return info;
        }

        /// <summary>
        /// 丢弃缓存（工作区文件变动后可调用）
        /// </summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                Cache.Clear();
            }
        }

        /// <summary>
        /// 只取「建议的验证命令」
        /// </summary>
        public static string DetectVerifyCommand(string root)
        {
            return Detect(root).VerifyCommand;
        }

        private static ToolchainInfo DetectCore(string root)
        {
            // 顺序即优先级：多语言混装时，以「构建入口」最明确的那个为准
            if (HasAny(root, "*.sln", "*.slnx") || HasAny(root, "*.csproj", "*.fsproj", "*.vbproj"))
            {
                return DetectDotNet(root);
            }

            if (File.Exists(Path.Combine(root, "package.json")))
            {
                return DetectNode(root);
            }

            if (File.Exists(Path.Combine(root, "go.mod")))
            {
                return new ToolchainInfo { Kind = "Go", VerifyCommand = "go build ./..." };
            }

            if (File.Exists(Path.Combine(root, "Cargo.toml")))
            {
                return new ToolchainInfo { Kind = "Rust", VerifyCommand = "cargo check" };
            }

            if (File.Exists(Path.Combine(root, "pom.xml")))
            {
                return new ToolchainInfo { Kind = "Java (Maven)", VerifyCommand = "mvn -q -DskipTests compile" };
            }

            if (HasAny(root, "build.gradle", "build.gradle.kts") || File.Exists(Path.Combine(root, "settings.gradle")))
            {
                var wrapper = File.Exists(Path.Combine(root, "gradlew.bat")) || File.Exists(Path.Combine(root, "gradlew"));
                return new ToolchainInfo
                {
                    Kind = "Java (Gradle)",
                    VerifyCommand = wrapper ? "gradlew build -x test" : "gradle build -x test",
                };
            }

            if (HasAny(root, "pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "pytest.ini", "tox.ini", "manage.py"))
            {
                return DetectPython(root);
            }

            return new ToolchainInfo();
        }

        private static ToolchainInfo DetectDotNet(string root)
        {
            var info = new ToolchainInfo { Kind = ".NET", VerifyCommand = "dotnet build" };

            // 从 launchSettings.json 里找本地开发地址（ASP.NET 项目常见）
            var launch = FindFile(root, "launchSettings.json", maxDepth: 4);
            if (launch != null)
            {
                var url = ParseApplicationUrl(launch);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    info.DevUrl = url;
                    info.Kind = ".NET（Web）";
                }
            }

            return info;
        }

        private static ToolchainInfo DetectNode(string root)
        {
            var info = new ToolchainInfo { Kind = "Node.js" };

            if (File.Exists(Path.Combine(root, "pnpm-lock.yaml")))
            {
                info.PackageManager = "pnpm";
            }
            else if (File.Exists(Path.Combine(root, "yarn.lock")))
            {
                info.PackageManager = "yarn";
            }
            else
            {
                info.PackageManager = "npm";
            }

            var packageJson = TryRead(Path.Combine(root, "package.json"));
            var scripts = ReadScripts(packageJson);

            // 验证命令优先级：test > build > lint > typecheck用户不用自己挑
            if (scripts.ContainsKey("test"))
            {
                info.VerifyCommand = RunScript(info.PackageManager, "test");
            }
            else if (scripts.ContainsKey("build"))
            {
                info.VerifyCommand = RunScript(info.PackageManager, "build");
            }
            else if (scripts.ContainsKey("lint"))
            {
                info.VerifyCommand = RunScript(info.PackageManager, "lint");
            }
            else if (scripts.ContainsKey("typecheck"))
            {
                info.VerifyCommand = RunScript(info.PackageManager, "typecheck");
            }
            else if (File.Exists(Path.Combine(root, "tsconfig.json")))
            {
                info.VerifyCommand = "npx tsc --noEmit";
            }

            // 开发地址：先从 dev / start 脚本里抠端口，再退化到框架默认端口
            var devScript = scripts.ContainsKey("dev") ? scripts["dev"]
                : scripts.ContainsKey("start") ? scripts["start"]
                : string.Empty;

            info.DevUrl = GuessDevUrl(root, devScript, packageJson);
            return info;
        }

        private static ToolchainInfo DetectPython(string root)
        {
            var info = new ToolchainInfo { Kind = "Python" };

            var hasPytest = File.Exists(Path.Combine(root, "pytest.ini")) ||
                            File.Exists(Path.Combine(root, "tox.ini")) ||
                            ContainsText(Path.Combine(root, "pyproject.toml"), "pytest");

            info.VerifyCommand = hasPytest ? "python -m pytest -q" : "python -m compileall -q .";

            if (File.Exists(Path.Combine(root, "manage.py")))
            {
                info.Kind = "Python (Django)";
                info.DevUrl = "http://localhost:8000";
            }

            return info;
        }

        private static string GuessDevUrl(string root, string devScript, string packageJson)
        {
            // ① 脚本里显式写的端口：--port 5173 / -p 3000 / PORT=3000 等
            if (!string.IsNullOrWhiteSpace(devScript))
            {
                var match = Regex.Match(devScript, @"(?:--port[=\s]+|-p\s+|PORT[=\s]+)(?<port>\d{2,5})",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success && int.TryParse(match.Groups["port"].Value, out var port) && port > 0 && port <= 65535)
                {
                    return "http://localhost:" + port;
                }
            }

            // ② 框架默认端口
            var blob = (packageJson ?? string.Empty).ToLowerInvariant();
            if (blob.IndexOf("\"vite\"", StringComparison.Ordinal) >= 0 || File.Exists(Path.Combine(root, "vite.config.ts")) || File.Exists(Path.Combine(root, "vite.config.js")))
            {
                return "http://localhost:5173";
            }

            if (blob.IndexOf("\"next\"", StringComparison.Ordinal) >= 0)
            {
                return "http://localhost:3000";
            }

            if (blob.IndexOf("@angular/core", StringComparison.Ordinal) >= 0)
            {
                return "http://localhost:4200";
            }

            if (blob.IndexOf("react-scripts", StringComparison.Ordinal) >= 0 || blob.IndexOf("\"react\"", StringComparison.Ordinal) >= 0)
            {
                return "http://localhost:3000";
            }

            if (blob.IndexOf("\"http-server\"", StringComparison.Ordinal) >= 0 ||
                blob.IndexOf("\"serve\"", StringComparison.Ordinal) >= 0)
            {
                return "http://localhost:8080";
            }

            return null;
        }

        private static Dictionary<string, string> ReadScripts(string packageJson)
        {
            var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(packageJson))
            {
                return scripts;
            }

            try
            {
                var root = LiteJson.ParseObject(packageJson);
                var scriptsNode = LiteJson.GetObject(root, "scripts");
                if (scriptsNode == null)
                {
                    return scripts;
                }

                foreach (var pair in scriptsNode)
                {
                    if (pair.Value is string text)
                    {
                        scripts[pair.Key] = text;
                    }
                }
            }
            catch (Exception)
            {
                // package.json 不合法：忽略，退回后续探测
            }

            return scripts;
        }

        private static string ParseApplicationUrl(string launchSettingsPath)
        {
            try
            {
                var json = File.ReadAllText(launchSettingsPath);
                // 取第一个出现的 applicationUrl 里的第一个 http 地址
                var match = Regex.Match(json, "\"applicationUrl\"\\s*:\\s*\"(?<url>[^\"]+)\"", RegexOptions.CultureInvariant);
                if (!match.Success)
                {
                    return null;
                }

                foreach (var candidate in match.Groups["url"].Value.Split(';'))
                {
                    var url = candidate.Trim();
                    if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return url;
                    }
                }
            }
            catch (Exception)
            {
                // 读不了就算了
            }

            return null;
        }

        private static string RunScript(string packageManager, string script)
        {
            switch ((packageManager ?? "npm").ToLowerInvariant())
            {
                case "yarn":
                    return "yarn " + script;
                case "pnpm":
                    return "pnpm " + script;
                default:
                    return script == "test" ? "npm test" : "npm run " + script;
            }
        }

        private static bool HasAny(string root, params string[] patterns)
        {
            foreach (var pattern in patterns)
            {
                try
                {
                    if (Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly).Length > 0)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // 目录不可读：忽略
                }
            }

            return false;
        }

        private static string FindFile(string root, string fileName, int maxDepth)
        {
            try
            {
                var found = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (found == null)
                {
                    return null;
                }

                // 粗略限制深度，避免在超大仓库里穿太深
                var depth = found.Split(Path.DirectorySeparatorChar).Length - root.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar).Length;
                return depth <= maxDepth ? found : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool ContainsText(string path, string needle)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string TryRead(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

