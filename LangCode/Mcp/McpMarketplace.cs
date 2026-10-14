using LangCode.Domain.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LangCode.Mcp
{
    /// <summary>
    /// 市场条目声明的一个环境变量
    /// </summary>
    public sealed class McpEnvVar
    {
        public McpEnvVar(string key, string description, bool required = true)
        {
            Key = key ?? string.Empty;
            Description = description ?? string.Empty;
            Required = required;
        }

        /// <summary>变量名（如 GITHUB_PERSONAL_ACCESS_TOKEN）</summary>
        public string Key { get; }

        /// <summary>这个变量的说明（界面上作为输入框的提示）</summary>
        public string Description { get; }

        /// <summary>是否必填（缺失时连不上）</summary>
        public bool Required { get; }
    }

    /// <summary>
    /// 市场里的一个 MCP 服务器条目（精选目录的一项）
    /// </summary>
    public sealed class McpMarketEntry
    {
        public McpMarketEntry(
            string name,
            string displayName,
            string category,
            string description,
            string command,
            string arguments,
            string homepage = null,
            IList<McpEnvVar> env = null)
        {
            Name = name ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Name : displayName;
            Category = string.IsNullOrWhiteSpace(category) ? "其他" : category;
            Description = description ?? string.Empty;
            Command = command ?? string.Empty;
            Arguments = arguments ?? string.Empty;
            Homepage = homepage ?? string.Empty;
            Env = env ?? [];
        }

        /// <summary>安装到设置里的服务器名，也是去重键</summary>
        public string Name { get; }

        /// <summary>卡片标题（中文名）</summary>
        public string DisplayName { get; }

        /// <summary>分类（用于分组展示 / 检索）</summary>
        public string Category { get; }

        /// <summary>一句话说明</summary>
        public string Description { get; }

        /// <summary>启动命令（npx / uvx / node …）</summary>
        public string Command { get; }

        /// <summary>启动参数</summary>
        public string Arguments { get; }

        /// <summary>项目主页（可为空）</summary>
        public string Homepage { get; }

        /// <summary>需要的环境变量（API Key / Token 等；空表示无需配置）</summary>
        public IList<McpEnvVar> Env { get; }

        /// <summary>是否需要填环境变量</summary>
        public bool NeedsEnv => Env.Count > 0;

        /// <summary>卡片上的一行命令预览</summary>
        public string CommandLine => string.IsNullOrWhiteSpace(Arguments) ? Command : Command + " " + Arguments;
    }

    /// <summary>
    /// MCP「市场」 
    /// </summary>  
    public static class McpMarketplace
    {
        /// <summary>
        /// 精选目录（顺序即展示顺序，同分类会聚在一起）
        /// </summary>
        private static readonly McpMarketEntry[] Catalog =
        [
            // ==================== 文件与代码 ====================
            new McpMarketEntry(
                "filesystem", "文件系统", "文件与代码",
                "读取、写入、搜索工作区文件（官方参考实现；路径已默认指向当前工作区，可按需修改）",
                "npx", "-y @modelcontextprotocol/server-filesystem .",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem"),

            new McpMarketEntry(
                "git", "Git 仓库", "文件与代码",
                "查看提交历史、分支、差异与文件变更（官方参考实现，需本机已装 Python/uvx）",
                "uvx", "mcp-server-git --repository .",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/git"),

            new McpMarketEntry(
                "github", "GitHub", "文件与代码",
                "仓库、Issue、PR、代码搜索等 GitHub 操作",
                "npx", "-y @modelcontextprotocol/server-github",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/github",
                [
                    new McpEnvVar("GITHUB_PERSONAL_ACCESS_TOKEN", "GitHub 个人访问令牌（需 repo 权限）"),
                ]),

            new McpMarketEntry(
                "gitlab", "GitLab", "文件与代码",
                "GitLab 仓库、合并请求与流水线查询",
                "npx", "-y @modelcontextprotocol/server-gitlab",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/gitlab",
                [
                    new McpEnvVar("GITLAB_PERSONAL_ACCESS_TOKEN", "GitLab 个人访问令牌"),
                    new McpEnvVar("GITLAB_API_URL", "自建 GitLab 的 API 地址（gitlab.com 可留空）", required: false),
                ]),

            // ==================== 搜索与 Web ====================
            new McpMarketEntry(
                "fetch", "网页抓取", "搜索与 Web",
                "抓取网页并转成 Markdown 交给模型（官方参考实现，需 Python/uvx）",
                "uvx", "mcp-server-fetch",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/fetch"),

            new McpMarketEntry(
                "context7", "Context7 文档", "搜索与 Web",
                "按库名实时检索最新官方文档与代码示例，减少模型「凭记忆写错 API」",
                "npx", "-y @upstash/context7-mcp",
                "https://github.com/upstash/context7"),

            new McpMarketEntry(
                "duckduckgo", "DuckDuckGo 搜索", "搜索与 Web",
                "免密钥的网页搜索（需 Python/uvx）",
                "uvx", "duckduckgo-mcp-server",
                "https://github.com/nickclyde/duckduckgo-mcp-server"),

            new McpMarketEntry(
                "brave-search", "Brave 搜索", "搜索与 Web",
                "Brave Search 的网页与本地搜索",
                "npx", "-y @modelcontextprotocol/server-brave-search",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/brave-search",
                [
                    new McpEnvVar("BRAVE_API_KEY", "Brave Search API Key"),
                ]),

            new McpMarketEntry(
                "exa", "Exa 搜索", "搜索与 Web",
                "面向 AI 的语义化网页搜索与内容抓取",
                "npx", "-y exa-mcp-server",
                "https://github.com/exa-labs/exa-mcp-server",
                [
                    new McpEnvVar("EXA_API_KEY", "Exa API Key"),
                ]),

            new McpMarketEntry(
                "tavily", "Tavily 搜索", "搜索与 Web",
                "为 LLM 设计的联网搜索与摘要",
                "npx", "-y tavily-mcp",
                "https://github.com/tavily-ai/tavily-mcp",
                [
                    new McpEnvVar("TAVILY_API_KEY", "Tavily API Key"),
                ]),

            new McpMarketEntry(
                "firecrawl", "Firecrawl 抓取", "搜索与 Web",
                "整站爬取与结构化抓取，供模型做资料收集",
                "npx", "-y firecrawl-mcp",
                "https://github.com/mendableai/firecrawl-mcp-server",
                [
                    new McpEnvVar("FIRECRAWL_API_KEY", "Firecrawl API Key"),
                ]),

            new McpMarketEntry(
                "perplexity", "Perplexity 问答", "搜索与 Web",
                "让 Perplexity 联网检索并给出带引用的回答",
                "npx", "-y server-perplexity-ask",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/perplexity-ask",
                [
                    new McpEnvVar("PERPLEXITY_API_KEY", "Perplexity API Key"),
                ]),

            // ==================== 浏览器自动化 ====================
            new McpMarketEntry(
                "playwright", "Playwright 浏览器", "浏览器自动化",
                "真实浏览器自动化：导航、点击、填表、截图，适合前端自测（首次运行会下载浏览器）",
                "npx", "-y @playwright/mcp@latest",
                "https://github.com/microsoft/playwright-mcp"),

            new McpMarketEntry(
                "puppeteer", "Puppeteer 浏览器", "浏览器自动化",
                "基于 Puppeteer 的浏览器自动化（官方参考实现，首次运行会下载 Chromium）",
                "npx", "-y @modelcontextprotocol/server-puppeteer",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/puppeteer"),

            // ==================== 数据库 ====================
            new McpMarketEntry(
                "sqlite", "SQLite", "数据库",
                "查询本地 SQLite 数据库并执行 SQL（把 --db-path 改成你的数据库文件，需 Python/uvx）",
                "uvx", "mcp-server-sqlite --db-path ./data.db",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/sqlite"),

            new McpMarketEntry(
                "postgres", "PostgreSQL", "数据库",
                "以只读方式查询 PostgreSQL（把结尾的连接串改成你的数据库地址）",
                "npx", "-y @modelcontextprotocol/server-postgres postgresql://user:password@localhost:5432/dbname",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/postgres"),

            new McpMarketEntry(
                "mongodb", "MongoDB", "数据库",
                "查询 MongoDB 集合与文档、查看索引与统计",
                "npx", "-y mongodb-mcp-server",
                "https://github.com/mongodb-js/mongodb-mcp-server",
                [
                    new McpEnvVar("MDB_MCP_CONNECTION_STRING", "MongoDB 连接串（mongodb://… 或 mongodb+srv://…）"),
                ]),

            new McpMarketEntry(
                "elasticsearch", "Elasticsearch", "数据库",
                "查询 Elasticsearch 索引、映射与搜索",
                "npx", "-y @elastic/mcp-server-elasticsearch",
                "https://github.com/elastic/mcp-server-elasticsearch",
                [
                    new McpEnvVar("ES_URL", "Elasticsearch 地址（如 https://localhost:9200）"),
                    new McpEnvVar("ES_API_KEY", "Elasticsearch API Key（可选）", required: false),
                ]),

            // ==================== 推理与记忆 ====================
            new McpMarketEntry(
                "memory", "记忆（知识图谱）", "推理与记忆",
                "跨会话持久化的实体 / 关系记忆，让助手记住长期上下文",
                "npx", "-y @modelcontextprotocol/server-memory",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/memory"),

            new McpMarketEntry(
                "sequential-thinking", "分步推理", "推理与记忆",
                "让模型把复杂问题拆成显式的多步思考再作答，适合难题排查",
                "npx", "-y @modelcontextprotocol/server-sequential-thinking",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/sequentialthinking"),

            new McpMarketEntry(
                "time", "时间与时区", "推理与记忆",
                "获取当前时间、做时区换算（需 Python/uvx）",
                "uvx", "mcp-server-time",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/time"),

            // ==================== 协作与办公 ====================
            new McpMarketEntry(
                "slack", "Slack", "协作与办公",
                "读取频道消息、发送消息与查询成员",
                "npx", "-y @modelcontextprotocol/server-slack",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/slack",
                [
                    new McpEnvVar("SLACK_BOT_TOKEN", "Slack Bot Token（xoxb-…）"),
                    new McpEnvVar("SLACK_TEAM_ID", "Slack 工作区 ID（T…）"),
                ]),

            new McpMarketEntry(
                "notion", "Notion", "协作与办公",
                "读写 Notion 页面与数据库",
                "npx", "-y @notionhq/notion-mcp-server",
                "https://github.com/makenotion/notion-mcp-server",
                [
                    new McpEnvVar("NOTION_TOKEN", "Notion 集成 Token（ntn_… 或 secret_…）"),
                ]),

            new McpMarketEntry(
                "gdrive", "Google Drive", "协作与办公",
                "搜索与读取 Google Drive 中的文件（需要 OAuth 凭据 JSON）",
                "npx", "-y @modelcontextprotocol/server-gdrive",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/gdrive",
                [
                    new McpEnvVar("GDRIVE_CREDENTIALS_PATH", "OAuth 凭据 JSON 文件路径"),
                ]),

            new McpMarketEntry(
                "airtable", "Airtable", "协作与办公",
                "读写 Airtable 基表记录",
                "npx", "-y airtable-mcp-server",
                "https://github.com/domdomegg/airtable-mcp-server",
                [
                    new McpEnvVar("AIRTABLE_API_KEY", "Airtable 个人访问令牌"),
                ]),

            // ==================== 设计与监控 ====================
            new McpMarketEntry(
                "figma", "Figma", "设计与监控",
                "读取 Figma 设计稿的结构、样式与文本，便于按设计稿还原界面",
                "npx", "-y figma-developer-mcp --stdio",
                "https://github.com/GLips/Figma-Context-MCP",
                [
                    new McpEnvVar("FIGMA_API_KEY", "Figma 个人访问令牌"),
                ]),

            new McpMarketEntry(
                "sentry", "Sentry", "设计与监控",
                "查询线上错误、Issue 与堆栈，便于定位真实报错",
                "npx", "-y @sentry/mcp-server",
                "https://github.com/getsentry/sentry-mcp",
                [
                    new McpEnvVar("SENTRY_ACCESS_TOKEN", "Sentry User Auth Token"),
                    new McpEnvVar("SENTRY_HOST", "Sentry 自建域名（sentry.io 可留空）", required: false),
                ]),

            new McpMarketEntry(
                "stripe", "Stripe", "设计与监控",
                "查询客户、支付、订阅等 Stripe 对象",
                "npx", "-y @stripe/mcp --tools=all",
                "https://github.com/stripe/agent-toolkit",
                [
                    new McpEnvVar("STRIPE_SECRET_KEY", "Stripe Secret Key（sk_…）"),
                ]),

            new McpMarketEntry(
                "grafana", "Grafana", "设计与监控",
                "查询仪表盘、数据源与告警（需 Python/uvx）",
                "uvx", "mcp-grafana",
                "https://github.com/grafana/mcp-grafana",
                [
                    new McpEnvVar("GRAFANA_URL", "Grafana 地址（如 http://localhost:3000）"),
                    new McpEnvVar("GRAFANA_API_KEY", "Grafana 服务账号 Token"),
                ]),

            // ==================== 开发工具 ====================
            new McpMarketEntry(
                "everything", "Everything 示例", "开发工具",
                "官方示例服务器，内置各类测试工具：用于验证「MCP 连接是否打通」",
                "npx", "-y @modelcontextprotocol/server-everything",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/everything"),

            new McpMarketEntry(
                "kubernetes", "Kubernetes", "开发工具",
                "查看集群资源、Pod 日志与事件（沿用本机 kubeconfig）",
                "npx", "-y mcp-server-kubernetes",
                "https://github.com/Flux159/mcp-server-kubernetes"),

            new McpMarketEntry(
                "docker", "Docker", "开发工具",
                "查看容器、镜像与执行容器内命令（需本机 Docker 与 Python/uvx）",
                "uvx", "mcp-server-docker",
                "https://github.com/ckreiling/mcp-server-docker"),
        ];

        /// <summary>全部条目</summary>
        public static IList<McpMarketEntry> All => Catalog;

        /// <summary>目录里出现过的分类（按首次出现顺序）</summary>
        public static IList<string> Categories
        {
            get
            {
                var seen = new List<string>();
                foreach (var entry in Catalog)
                {
                    if (!seen.Contains(entry.Category))
                    {
                        seen.Add(entry.Category);
                    }
                }

                return seen;
            }
        }

        /// <summary>
        /// 按关键词过滤（名称 / 分类 / 说明，大小写不敏感）；空关键词返回全部
        /// </summary>
        public static IList<McpMarketEntry> Search(string query)
        {
            var text = (query ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return Catalog;
            }

            return Catalog.Where(entry =>
                    Contains(entry.DisplayName, text) ||
                    Contains(entry.Name, text) ||
                    Contains(entry.Category, text) ||
                    Contains(entry.Description, text))
                .ToList();
        }

        private static bool Contains(string source, string value)
        {
            return !string.IsNullOrEmpty(source) &&
                   source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 把市场条目转成一条设置项（默认启用；需要的环境变量先写入空占位，等用户填）。
        /// </summary>
        public static McpServerSettings ToServer(McpMarketEntry entry)
        {
            var server = new McpServerSettings
            {
                Name = entry.Name,
                Command = entry.Command,
                Arguments = entry.Arguments,
                Enabled = true,
                Status = "unknown",
                Environment = [],
            };

            foreach (var env in entry.Env)
            {
                if (!string.IsNullOrWhiteSpace(env.Key))
                {
                    server.Environment[env.Key] = string.Empty;
                }
            }

            return server;
        }

        /// <summary>该条目是否已经装过（按名字判断）</summary>
        public static bool IsInstalled(IEnumerable<McpServerSettings> servers, McpMarketEntry entry)
        {
            return servers != null && entry != null &&
                   servers.Any(s => string.Equals(s.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 该条目「必填的环境变量」是否都已填写（已安装的前提下；未安装返回 false）
        /// </summary>
        public static bool HasRequiredEnv(IEnumerable<McpServerSettings> servers, McpMarketEntry entry)
        {
            var server = servers?.FirstOrDefault(s =>
                string.Equals(s.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

            if (server == null || entry.Env.Count == 0)
            {
                return true;
            }

            foreach (var env in entry.Env)
            {
                if (!env.Required)
                {
                    continue;
                }

                if (server.Environment == null ||
                    !server.Environment.TryGetValue(env.Key, out var value) ||
                    string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 安装一个条目的命令 / 参数 / 环境变量占位：已存在同名服务器时**就地更新**（保留已填的变量值）。
        /// </summary>
        /// <returns>true = 新增了一条；false = 已存在（已就地更新）</returns>
        public static bool Install(IList<McpServerSettings> servers, McpMarketEntry entry)
        {
            if (servers == null || entry == null)
            {
                return false;
            }

            var existing = servers.FirstOrDefault(s =>
                string.Equals(s.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Command = entry.Command;
                existing.Arguments = entry.Arguments;

                // 补齐新增变量（不动用户已填的值）
                existing.Environment ??= [];
                foreach (var env in entry.Env)
                {
                    if (!string.IsNullOrWhiteSpace(env.Key) && !existing.Environment.ContainsKey(env.Key))
                    {
                        existing.Environment[env.Key] = string.Empty;
                    }
                }

                return false;
            }

            servers.Add(ToServer(entry));
            return true;
        }

        /// <summary>
        /// 命令是否能在本机 PATH 上找到（安装前提示「先装 uvx / npx」用）。
        /// </summary>
        public static bool IsCommandAvailable(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            var name = command.Trim();

            // 直接给了路径的：判断该文件是否存在
            if (name.IndexOf(Path.DirectorySeparatorChar) >= 0 || name.IndexOf('/') >= 0)
            {
                return File.Exists(name);
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var extensions = new List<string> { string.Empty };

            // Windows 下要连带 PATHEXT 一起试（uvx.exe / npx.cmd 等）
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
                foreach (var ext in pathext.Split(';'))
                {
                    if (ext.Length > 0)
                    {
                        extensions.Add(ext);
                    }
                }
            }

            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                foreach (var extension in extensions)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(directory.Trim(), name + extension)))
                        {
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                        // 非法路径片段：跳过
                    }
                }
            }

            return false;
        }
    }
}
