using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace LangCode.Workspace
{
    /// <summary>
    /// 敏感文件识别：默认不把这类文件送进模型 / 搜索索引，读或改之前必须额外确认 
    /// </summary>
    public static class SensitiveFiles
    {
        private static readonly HashSetLike Names = new HashSetLike(
            ".env", ".npmrc", ".pypirc", ".netrc", ".git-credentials",
            "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
            "credentials", "credentials.json", "secrets.json", "secret.json");

        private static readonly HashSetLike Extensions = new HashSetLike(
            ".pem", ".key", ".pfx", ".p12", ".keystore", ".jks", ".kdbx", ".ppk");

        /// <summary>
        /// 该相对路径是否属于敏感文件
        /// </summary>
        public static bool IsSensitive(string relativePath)
        {
            var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim();
            if (normalized.Length == 0)
            {
                return false;
            }

            var name = Path.GetFileName(normalized).ToLowerInvariant();
            if (Names.Contains(name))
            {
                return true;
            }

            if (name.StartsWith(".env.", StringComparison.Ordinal))
            {
                return true;
            }

            if (Extensions.Contains(Path.GetExtension(name)))
            {
                return true;
            }

            // ASP.NET Core 的 appsettings.<Environment>.json 经常放连接串 / 密钥；
            // 基础 appsettings.json 通常只放非机密配置，不强制拦截
            if (name.StartsWith("appsettings.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal) && name.Length > "appsettings..json".Length)
            {
                return true;
            }

            // git 的本地配置 / 凭据目录
            if (normalized.IndexOf(".git/config", StringComparison.OrdinalIgnoreCase) >= 0 || normalized.IndexOf(".git-credentials", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 轻量大小写不敏感集合（避免额外分配 HashSet 的阈值逻辑）
        /// </summary>
        private sealed class HashSetLike
        {
            private readonly string[] _values;

            public HashSetLike(params string[] values)
            {
                _values = values ?? Array.Empty<string>();
            }

            public bool Contains(string value)
            {
                foreach (var item in _values)
                {
                    if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// 网络目标守卫：拦截本机 / 私网 / 链路本地 / 云元数据地址，避免 Agent 被诱导访问内网。
    /// localhost 可单独放行（前端自测需要），元数据地址永远拦截。
    /// </summary>
    public static class NetworkGuard
    {
        /// <summary>
        /// 检查 URL；返回 null 表示允许，否则返回拒绝原因
        /// </summary>
        public static string Check(string url, bool allowPrivateNetwork, bool allowLocalhost)
        {
            var text = (url ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                // 相对路径由浏览器工具内部解析为工作区文件，不在这里判定
                return null;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                // file: / javascript: 等交给调用方拒绝
                return null;
            }

            var host = (uri.DnsSafeHost ?? string.Empty).Trim('[', ']').ToLowerInvariant();
            if (host.Length == 0)
            {
                return null;
            }

            if (string.Equals(host, "metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            {
                return "目标地址是云元数据端点，已被安全策略拦截";
            }

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                return allowLocalhost ? null : "目标地址是本机（localhost），已被安全策略拦截";
            }

            if (IPAddress.TryParse(host, out var ip))
            {
                return CheckAddress(ip, allowPrivateNetwork, allowLocalhost);
            }

            // 域名形式的私网 / 容器内域名：做一次保守拦截（不主动 DNS 解析，避免额外网络请求）
            if (host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "host.docker.internal", StringComparison.OrdinalIgnoreCase))
            {
                return allowPrivateNetwork ? null : "目标地址属于内网域名，已被安全策略拦截";
            }

            return null;
        }

        private static string CheckAddress(IPAddress ip, bool allowPrivateNetwork, bool allowLocalhost)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (IPAddress.IsLoopback(ip))
            {
                return allowLocalhost ? null : "目标地址是本机回环地址，已被安全策略拦截";
            }

            var bytes = ip.GetAddressBytes();

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                // 云元数据 / 链路本地：永远拦截
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    return "目标地址是链路本地 / 云元数据地址，已被安全策略拦截";
                }

                if (IsPrivateV4(bytes))
                {
                    return allowPrivateNetwork ? null : "目标地址属于私网，已被安全策略拦截";
                }
            }
            else
            {
                // IPv6 唯一本地地址 / 链路本地
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (bytes.Length > 0 && (bytes[0] & 0xFE) == 0xFC))
                {
                    return allowPrivateNetwork ? null : "目标地址属于 IPv6 私网 / 链路本地，已被安全策略拦截";
                }
            }

            return null;
        }

        private static bool IsPrivateV4(byte[] bytes)
        {
            if (bytes.Length != 4)
            {
                return false;
            }

            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   bytes[0] == 127 ||
                   bytes[0] == 0 ||
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                   (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19));
        }
    }
}
