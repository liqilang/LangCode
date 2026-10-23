using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LangCode.Domain.Settings
{
    /// <summary>
    /// 工作区级权限策略（放在仓库里的 <c>.langcode/policy.json</c>，可随代码一起审查） 
    /// </summary>
    [DataContract(Name = "workspacePolicy")]
    public sealed class WorkspacePolicy
    {
        public const string RelativePath = ".langcode/policy.json";

        /// <summary>strict / balanced / trusted</summary>
        [DataMember(Name = "mode", Order = 1)]
        public string Mode { get; set; } = "balanced";

        /// <summary>允许执行的命令前缀（非空时，命令必须命中其中之一）</summary>
        [DataMember(Name = "allowedCommandPrefixes", Order = 2)]
        public List<string> AllowedCommandPrefixes { get; set; } = [];

        /// <summary>禁止执行的命令前缀（命中即拒绝）</summary>
        [DataMember(Name = "deniedCommandPrefixes", Order = 3)]
        public List<string> DeniedCommandPrefixes { get; set; } = [];

        /// <summary>禁止读写的路径 / glob（命中即拒绝，优先级最高）</summary>
        [DataMember(Name = "deniedPaths", Order = 4)]
        public List<string> DeniedPaths { get; set; } = [];

        /// <summary>显式允许的工具（非空时，破坏性 / 外部工具只能从这里选）</summary>
        [DataMember(Name = "allowedTools", Order = 5)]
        public List<string> AllowedTools { get; set; } = [];

        /// <summary>禁止的工具（优先级最高）</summary>
        [DataMember(Name = "deniedTools", Order = 6)]
        public List<string> DeniedTools { get; set; } = [];

        /// <summary>敏感文件（.env / 密钥 / 证书等）是否需要额外确认</summary>
        [DataMember(Name = "blockSensitiveFiles", Order = 7)]
        public bool BlockSensitiveFiles { get; set; } = true;

        /// <summary>策略级放行敏感文件（团队明确接受风险时才打开）</summary>
        [DataMember(Name = "allowSensitiveFiles", Order = 8)]
        public bool AllowSensitiveFiles { get; set; }

        /// <summary>是否拦截私网 / 链路本地 / 云元数据地址</summary>
        [DataMember(Name = "blockPrivateNetwork", Order = 9)]
        public bool BlockPrivateNetwork { get; set; } = true;

        /// <summary>是否允许 localhost（本地开发服务器通常需要）</summary>
        [DataMember(Name = "allowLocalhost", Order = 10)]
        public bool AllowLocalhost { get; set; } = true;

        /// <summary>是否允许 browser_action 的 evaluate / eval（任意 JS 执行）</summary>
        [DataMember(Name = "allowBrowserEvaluate", Order = 11)]
        public bool AllowBrowserEvaluate { get; set; } = true;

        /// <summary>命令解释器：cmd / powershell / bash（空 = 跟随用户设置）</summary>
        [DataMember(Name = "shell", Order = 12)]
        public string Shell { get; set; } = string.Empty;

        /// <summary>策略文件是否真的存在并被加载</summary>
        [IgnoreDataMember]
        public bool Loaded { get; private set; }

        /// <summary>加载失败原因（文件存在但 JSON 损坏等）</summary>
        [IgnoreDataMember]
        public string LoadError { get; private set; } = string.Empty;

        /// <summary>
        /// DataContract 反序列化会跳过属性初始化器：先把安全默认值填好，
        /// 再让 policy.json 里显式写的字段覆盖。否则漏写一个 bool 就等于关掉保护。
        /// </summary>
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            Mode = "balanced";
            AllowedCommandPrefixes = [];
            DeniedCommandPrefixes = [];
            DeniedPaths = [];
            AllowedTools = [];
            DeniedTools = [];
            BlockSensitiveFiles = true;
            AllowSensitiveFiles = false;
            BlockPrivateNetwork = true;
            AllowLocalhost = true;
            AllowBrowserEvaluate = true;
            Shell = string.Empty;
        }

        /// <summary>
        /// 从工作区根加载；没有策略文件时返回一份默认策略（Loaded=false）
        /// </summary>
        public static WorkspacePolicy Load(string root)
        {
            var policy = new WorkspacePolicy();
            if (string.IsNullOrWhiteSpace(root))
            {
                return policy;
            }

            try
            {
                var path = Path.Combine(root, ".langcode", "policy.json");
                if (!File.Exists(path))
                {
                    return policy;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var serializer = new DataContractJsonSerializer(typeof(WorkspacePolicy));
                    var loaded = serializer.ReadObject(stream) as WorkspacePolicy;
                    if (loaded == null)
                    {
                        policy.LoadError = "策略文件内容为空";
                        return policy;
                    }

                    loaded.Loaded = true;
                    loaded.Normalize();
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                policy.LoadError = "策略文件加载失败：" + ex.Message;
                return policy;
            }
        }

        /// <summary>
        /// 归一化：列表非空、模式 / Shell 只用受支持的取值
        /// </summary>
        public WorkspacePolicy Normalize()
        {
            AllowedCommandPrefixes ??= [];
            DeniedCommandPrefixes ??= [];
            DeniedPaths ??= [];
            AllowedTools ??= [];
            DeniedTools ??= [];

            Mode = NormalizeChoice(Mode, "balanced", "strict", "trusted", "balanced");
            Shell = NormalizeChoice(Shell, string.Empty, "cmd", "powershell", "bash", string.Empty);

            return this;
        }

        private static string NormalizeChoice(string value, string emptyValue, string first, string second, string third, string fallback)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (text.Length == 0)
            {
                return emptyValue;
            }

            if (text == first || text == second || text == third)
            {
                return text;
            }

            return fallback;
        }

        private static string NormalizeChoice(string value, string emptyValue, string first, string second, string fallback)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (text.Length == 0)
            {
                return emptyValue;
            }

            return text == first || text == second ? text : fallback;
        }

        /// <summary>
        /// 相对路径是否命中 deniedPaths（支持 * / ? glob 与目录前缀）
        /// </summary>
        public bool IsPathDenied(string relativePath)
        {
            var path = NormalizePath(relativePath);
            if (path.Length == 0)
            {
                return false;
            }

            foreach (var pattern in DeniedPaths)
            {
                if (MatchesPath(path, pattern))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        }

        private static bool MatchesPath(string normalizedPath, string pattern)
        {
            var value = NormalizePath(pattern);
            if (value.Length == 0)
            {
                return false;
            }

            if (value.IndexOf('*') >= 0 || value.IndexOf('?') >= 0)
            {
                var regex = "^" + System.Text.RegularExpressions.Regex.Escape(value)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                return System.Text.RegularExpressions.Regex.IsMatch(
                    normalizedPath,
                    regex,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            }

            return normalizedPath == value ||
                   normalizedPath.StartsWith(value + "/", StringComparison.Ordinal) ||
                   normalizedPath.StartsWith(value, StringComparison.Ordinal);
        }

        /// <summary>
        /// 策略内容摘要（给系统提示 / 诊断用）
        /// </summary>
        public string Describe()
        {
            var builder = new StringBuilder();
            builder.Append("模式=").Append(Mode);
            if (BlockSensitiveFiles && !AllowSensitiveFiles)
            {
                builder.Append("，敏感文件需确认");
            }

            if (BlockPrivateNetwork)
            {
                builder.Append("，拦截私网");
            }

            if (DeniedPaths.Count > 0)
            {
                builder.Append("，禁读 ").Append(DeniedPaths.Count).Append(" 条路径");
            }

            if (DeniedTools.Count > 0)
            {
                builder.Append("，禁用 ").Append(DeniedTools.Count).Append(" 个工具");
            }

            return builder.ToString();
        }
    }
}
