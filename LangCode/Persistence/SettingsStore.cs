using LangCode.Domain.Settings;
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace LangCode.Persistence
{
    /// <summary>
    /// 设置的读写
    /// </summary>
    public static class SettingsStore
    {
        private static readonly object Gate = new object();

        /// <summary>
        /// 最近一次读 / 写失败的原因（成功时为空）
        /// </summary>
        public static string LastError { get; private set; } = string.Empty;

        /// <summary>
        /// DPAPI 加密后的字符串前缀，用来区分旧的明文配置
        /// </summary>
        private const string DpapiPrefix = "dpapi:";

        /// <summary>
        /// 配置目录：<c>%APPDATA%\LangCode\vsext</c> 
        /// </summary>
        public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LangCode", "vsext");

        /// <summary>
        /// 设置文件路径
        /// </summary>
        public static string FilePath => Path.Combine(Directory, "settings.json");

        /// <summary>
        /// 读取设置；文件不存在或损坏时返回默认值
        /// </summary>
        public static AppSettings Load()
        {
            lock (Gate)
            {
                LastError = string.Empty;

                try
                {
                    if (!File.Exists(FilePath))
                    {
                        return CreateDefault();
                    }

                    var json = File.ReadAllText(FilePath, Encoding.UTF8);
                    var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        var settings = serializer.ReadObject(stream) as AppSettings;
                        if (settings == null)
                        {
                            return CreateDefault();
                        }

                        // 旧版本设置可能缺字段：必须先 Normalize，再用默认值补齐；
                        // 同时把旧文件里的明文 Key 透明解密（新写入会自动加密）。
                        return UnprotectSecrets(settings).Normalize();
                    }
                }
                catch (Exception ex)
                {
                    // 损坏 / 无权限：不抛，回退默认值（调用方可自行提示）
                    LastError = "读取设置失败：" + ex.Message;
                    return CreateDefault();
                }
            }
        }

        /// <summary>
        /// 写入设置（原子替换）
        /// </summary>
        public static void Save(AppSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            lock (Gate)
            {
                LastError = string.Empty;

                try
                {
                    System.IO.Directory.CreateDirectory(Directory);

                    // 只在序列化副本上加密，不修改调用方正在编辑的对象
                    var payload = Serialize(ProtectSecrets(settings.Clone()));
                    var temp = FilePath + ".tmp";
                    File.WriteAllText(temp, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                    if (File.Exists(FilePath))
                    {
                        // 原子替换：避免「先删后移」在崩溃窗口里把设置文件弄丢
                        File.Replace(temp, FilePath, null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(temp, FilePath);
                    }
                }
                catch (Exception ex)
                {
                    // 写失败不阻断交互（例如 %APPDATA% 只读），但把原因留给界面
                    LastError = "保存设置失败：" + ex.Message;
                    try
                    {
                        var temp = FilePath + ".tmp";
                        if (File.Exists(temp))
                        {
                            File.Delete(temp);
                        }
                    }
                    catch (Exception)
                    {
                        // 清理失败忽略
                    }
                }
            }
        }

        /// <summary>
        /// 用 Windows DPAPI 加密当前用户的 API Key（只保护落盘文件，不影响内存中的对象）
        /// </summary>
        private static AppSettings ProtectSecrets(AppSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            settings.ApiKey = ProtectSecret(settings.ApiKey);
            settings.WebSearchApiKey = ProtectSecret(settings.WebSearchApiKey);

            foreach (var profile in settings.Profiles ?? [])
            {
                if (profile != null)
                {
                    profile.ApiKey = ProtectSecret(profile.ApiKey);
                }
            }

            foreach (var profile in settings.NamedProfiles ?? [])
            {
                if (profile != null)
                {
                    profile.ApiKey = ProtectSecret(profile.ApiKey);
                }
            }

            foreach (var server in settings.McpServers ?? [])
            {
                if (server?.Environment == null)
                {
                    continue;
                }

                foreach (var key in new System.Collections.Generic.List<string>(server.Environment.Keys))
                {
                    server.Environment[key] = ProtectSecret(server.Environment[key]);
                }
            }

            return settings;
        }

        /// <summary>
        /// 解密旧文件 / 新文件里的 DPAPI 字段；明文值原样返回
        /// </summary>
        private static AppSettings UnprotectSecrets(AppSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            settings.ApiKey = UnprotectSecret(settings.ApiKey);
            settings.WebSearchApiKey = UnprotectSecret(settings.WebSearchApiKey);

            foreach (var profile in settings.Profiles ?? [])
            {
                if (profile != null)
                {
                    profile.ApiKey = UnprotectSecret(profile.ApiKey);
                }
            }

            foreach (var profile in settings.NamedProfiles ?? [])
            {
                if (profile != null)
                {
                    profile.ApiKey = UnprotectSecret(profile.ApiKey);
                }
            }

            foreach (var server in settings.McpServers ?? [])
            {
                if (server?.Environment == null)
                {
                    continue;
                }

                foreach (var key in new System.Collections.Generic.List<string>(server.Environment.Keys))
                {
                    server.Environment[key] = UnprotectSecret(server.Environment[key]);
                }
            }

            return settings;
        }

        private static string ProtectSecret(string value)
        {
            if (string.IsNullOrEmpty(value) || value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                return value;
            }

            try
            {
                var protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(value),
                    null,
                    DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(protectedBytes);
            }
            catch (Exception)
            {
                // DPAPI 不可用（极少数环境）：退回明文，至少不丢配置
                return value;
            }
        }

        private static string UnprotectSecret(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                return value;
            }

            try
            {
                var bytes = Convert.FromBase64String(value.Substring(DpapiPrefix.Length));
                var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception)
            {
                // 换机器 / 用户后 DPAPI 解不开：当作没有配置 Key，提示用户重新填写
                return string.Empty;
            }
        }

        /// <summary>
        /// 序列化成 JSON（导出与落盘共用同一份格式）
        /// </summary>
        public static string Serialize(AppSettings settings)
        {
            var serializer = new DataContractJsonSerializer(typeof(AppSettings));
            var buffer = new MemoryStream();
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(buffer, Encoding.UTF8, ownsStream: false, indent: true))
            {
                serializer.WriteObject(writer, settings);
                writer.Flush();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>
        /// 把设置导出到任意路径；成功返回 null，失败返回给用户看的原因
        /// </summary>
        public static string Export(AppSettings settings, string path)
        {
            if (settings == null || string.IsNullOrWhiteSpace(path))
            {
                return "没有可导出的内容";
            }

            try
            {
                File.WriteAllText(path, Serialize(settings), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// 从文件导入设置；成功返回对象（已 Normalize），失败返回 null 并给出原因
        /// </summary>
        public static AppSettings Import(string path, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "文件不存在";
                return null;
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var settings = serializer.ReadObject(stream) as AppSettings;
                    if (settings == null)
                    {
                        error = "文件内容不是有效的设置";
                        return null;
                    }

                    // 如果导入的是新版本导出的加密配置，这里同样透明解密
                    return UnprotectSecrets(settings).Normalize();
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 重置为默认值并落盘，返回新对象
        /// </summary>
        public static AppSettings Reset()
        {
            var settings = CreateDefault();
            Save(settings);
            return settings;
        }

        /// <summary>
        /// 内置默认值（与设置页出厂状态一致）
        /// </summary>
        public static AppSettings CreateDefault()
        {
            var settings = new AppSettings();
            settings.McpServers.Add(new McpServerSettings
            {
                Name = "filesystem",
                Command = "npx -y @modelcontextprotocol/server-filesystem",
                Enabled = true,
                ToolCount = 8,
                Status = "on",
            });
            settings.McpServers.Add(new McpServerSettings
            {
                Name = "github",
                Command = "npx -y @modelcontextprotocol/server-github",
                Enabled = true,
                ToolCount = 12,
                Status = "on",
            });
            settings.McpServers.Add(new McpServerSettings
            {
                Name = "puppeteer",
                Command = "npx -y @modelcontextprotocol/server-puppeteer",
                Enabled = false,
                ToolCount = 0,
                Status = "off",
            });
            settings.CustomInstructions = "";
            return settings;
        }
    }
}

