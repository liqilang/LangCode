using LangCode.VsExt.Domain.Settings;
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LangCode.VsExt.Persistence
{
    /// <summary>
    /// 设置的读写
    /// </summary>
    public static class SettingsStore
    {
        private static readonly object Gate = new object();

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
                        return settings ?? CreateDefault();
                    }
                }
                catch (Exception)
                {
                    // 损坏 / 无权限：不抛，回退默认值（调用方可自行提示）
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
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);

                    var temp = FilePath + ".tmp";
                    File.WriteAllText(temp, Serialize(settings), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                    if (File.Exists(FilePath))
                    {
                        File.Delete(FilePath);
                    }

                    File.Move(temp, FilePath);
                }
                catch (Exception)
                {
                    // 写失败不阻断交互（例如 %APPDATA% 只读）
                }
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

                    return settings.Normalize();
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

