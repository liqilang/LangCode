using LangCode.Domain.Chat;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LangCode.Persistence
{
    /// <summary>
    /// 落盘的内容块
    /// </summary>
    /// <remarks>
    /// Diff 行不单独建表：统一编码进 <c>text</c>，每行以符号开头（"+ " / "- " / "  "），
    /// 读取时再解回来 —— 少一层嵌套契约，JSON 也更易读
    /// </remarks>
    [DataContract(Name = "block")]
    public sealed class StoredBlock
    {
        /// <summary>
        /// text / thinking / code / diff / done
        /// </summary>
        [DataMember(Name = "kind", Order = 1)]
        public string Kind { get; set; } = "text";

        [DataMember(Name = "title", Order = 2)]
        public string Title { get; set; } = string.Empty;

        [DataMember(Name = "path", Order = 3)]
        public string Path { get; set; } = string.Empty;

        [DataMember(Name = "text", Order = 4)]
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// 落盘的消息记录
    /// </summary>
    [DataContract(Name = "message")]
    public sealed class StoredMessage
    {
        [DataMember(Name = "role", Order = 1)]
        public string Role { get; set; } = "assistant";

        /// <summary>
        /// 用户消息的正文（助手消息用 blocks）
        /// </summary>
        [DataMember(Name = "text", Order = 2)]
        public string Text { get; set; } = string.Empty;

        [DataMember(Name = "blocks", Order = 3)]
        public List<StoredBlock> Blocks { get; set; } = [];
    }

    /// <summary>
    /// 落盘的一条「本次会话改动过的文件」（「本次变更」摘要的一行）
    /// </summary>
    [DataContract(Name = "change")]
    public sealed class StoredChange
    {
        /// <summary>add / mod / del</summary>
        [DataMember(Name = "kind", Order = 1)]
        public string Kind { get; set; } = "mod";

        [DataMember(Name = "path", Order = 2)]
        public string Path { get; set; } = string.Empty;

        [DataMember(Name = "added", Order = 3)]
        public int Added { get; set; }

        [DataMember(Name = "deleted", Order = 4)]
        public int Deleted { get; set; }
    }

    /// <summary>
    /// 落盘的会话记录
    /// </summary>
    [DataContract(Name = "conversation")]
    public sealed class StoredConversation
    {
        [DataMember(Name = "id", Order = 1)]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [DataMember(Name = "title", Order = 2)]
        public string Title { get; set; } = string.Empty;

        [DataMember(Name = "preview", Order = 3)]
        public string Preview { get; set; } = string.Empty;

        [DataMember(Name = "model", Order = 4)]
        public string Model { get; set; } = string.Empty;

        [DataMember(Name = "updatedUtc", Order = 5)]
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 消息条数。索引文件里只保留元数据（<see cref="Messages"/> 为空），
        /// 历史列表要靠它显示「N 条消息」，因此单独存一份，避免为了数条数去读每个会话文件
        /// </summary>
        [DataMember(Name = "messageCount", Order = 6)]
        public int MessageCount { get; set; }

        [DataMember(Name = "messages", Order = 7)]
        public List<StoredMessage> Messages { get; set; } = [];

        /// <summary>
        /// 该会话的任务处于「可继续」状态（步数上限暂停，或运行中被强杀）。
        /// 重开 VS 载入会话时据此恢复请求条上的「继续任务」。
        /// </summary>
        [DataMember(Name = "paused", Order = 8)]
        public bool Paused { get; set; }

        /// <summary>任务被用户中断（恢复时显示「恢复任务」）</summary>
        [DataMember(Name = "interrupted", Order = 9)]
        public bool Interrupted { get; set; }

        /// <summary>任务真的出错（恢复时显示「重试」）</summary>
        [DataMember(Name = "canRetry", Order = 10)]
        public bool CanRetry { get; set; }

        /// <summary>本次会话改动过的文件（「本次变更」摘要）</summary>
        [DataMember(Name = "changes", Order = 11)]
        public List<StoredChange> Changes { get; set; } = [];
    }

    /// <summary>
    /// 会话索引文件（<c>conversations/index.json</c>）：只存元数据，供历史列表快速加载
    /// </summary>
    [DataContract(Name = "conversationIndex")]
    internal sealed class ConversationIndex
    {
        [DataMember(Name = "version", Order = 1)]
        public int Version { get; set; } = 2;

        [DataMember(Name = "conversations", Order = 2)]
        public List<StoredConversation> Conversations { get; set; } = [];
    }

    /// <summary>
    /// 旧版单文件格式（<c>conversations.json</c>）：仅用于一次性迁移
    /// </summary>
    [DataContract(Name = "conversationFile")]
    internal sealed class ConversationFile
    {
        [DataMember(Name = "version", Order = 1)]
        public int Version { get; set; } = 1;

        [DataMember(Name = "conversations", Order = 2)]
        public List<StoredConversation> Conversations { get; set; } = [];
    }

    /// <summary>
    /// 会话持久化。每个会话单独落一个文件（<c>%APPDATA%\LangCode\vsext\conversations\{id}.json</c>），
    /// 另有一份索引（<c>index.json</c>）只存元数据。
    /// <para>
    /// 相比旧的「所有会话挤在一个 conversations.json」：保存单个会话不再全量重写整份文件，
    /// 历史列表也只读索引（不解析每条会话的全部消息），会话越多优势越明显。
    /// 旧文件会在首次访问时自动迁移到新结构（原文件改名 <c>.migrated</c> 备份）。
    /// </para>
    /// 读失败回退空表，写失败静默（不阻断对话）。
    /// </summary>
    public static class ConversationStore
    {
        private static readonly object Gate = new object();

        /// <summary>索引文件名（不会与会话 id 冲突：id 是 32 位十六进制 guid）</summary>
        private const string IndexFileName = "index.json";

        /// <summary>会话文件目录</summary>
        public static string Directory => Path.Combine(SettingsStore.Directory, "conversations");

        private static string IndexPath => Path.Combine(Directory, IndexFileName);

        /// <summary>旧版单文件路径（迁移来源）</summary>
        private static string LegacyFilePath => Path.Combine(SettingsStore.Directory, "conversations.json");

        private static bool _migrated;

        // ==================== 对外 API ====================

        /// <summary>
        /// 读取全部会话（含消息）。会触发一次性迁移。
        /// </summary>
        public static List<StoredConversation> LoadAll()
        {
            lock (Gate)
            {
                EnsureMigratedLocked();

                var result = new List<StoredConversation>();
                try
                {
                    if (!System.IO.Directory.Exists(Directory))
                    {
                        return result;
                    }

                    foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        if (string.Equals(Path.GetFileName(file), IndexFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        try
                        {
                            var conversation = Deserialize<StoredConversation>(File.ReadAllText(file, Encoding.UTF8));
                            if (conversation != null)
                            {
                                result.Add(conversation);
                            }
                        }
                        catch (Exception)
                        {
                            // 单个文件损坏：跳过，不影响其余会话
                        }
                    }
                }
                catch (Exception)
                {
                    return result;
                }

                return result;
            }
        }

        /// <summary>
        /// 只读索引：返回全部会话的元数据（<see cref="StoredConversation.Messages"/> 为空）
        /// —— 历史列表用它，不必解析每条会话的完整消息
        /// </summary>
        public static List<StoredConversation> LoadSummaries()
        {
            lock (Gate)
            {
                EnsureMigratedLocked();
                return ReadIndexLocked();
            }
        }

        /// <summary>
        /// 按 id 读单个会话（含消息）；不存在返回 null
        /// </summary>
        public static StoredConversation LoadConversationById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            lock (Gate)
            {
                EnsureMigratedLocked();

                try
                {
                    var path = PathFor(id);
                    if (!File.Exists(path))
                    {
                        return null;
                    }

                    return Deserialize<StoredConversation>(File.ReadAllText(path, Encoding.UTF8));
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 保存单个会话：写会话文件 + 更新索引；索引超过 <paramref name="maxItems"/> 时
        /// 顺带删掉最旧的多余会话文件（0 / 负数表示不限制）
        /// </summary>
        public static void SaveConversation(StoredConversation conversation, int maxItems = 0)
        {
            if (conversation == null || string.IsNullOrWhiteSpace(conversation.Id))
            {
                return;
            }

            lock (Gate)
            {
                EnsureMigratedLocked();
                SaveConversationLocked(conversation, maxItems);
            }
        }

        /// <summary>
        /// 批量保存（迁移 / 兼容旧调用）：逐个写文件并重建索引
        /// </summary>
        public static void SaveAll(IEnumerable<StoredConversation> conversations, int maxItems = 0)
        {
            var list = (conversations ?? Enumerable.Empty<StoredConversation>()).ToList();

            lock (Gate)
            {
                EnsureMigratedLocked();

                foreach (var conversation in list)
                {
                    if (conversation == null || string.IsNullOrWhiteSpace(conversation.Id))
                    {
                        continue;
                    }

                    WriteConversationFileLocked(conversation);
                }

                var entries = list
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id))
                    .Select(ToIndexEntry)
                    .OrderByDescending(c => c.UpdatedUtc)
                    .ToList();

                if (maxItems > 0 && entries.Count > maxItems)
                {
                    var removed = entries.Skip(maxItems).ToList();
                    entries = entries.Take(maxItems).ToList();
                    foreach (var stale in removed)
                    {
                        TryDeleteConversationFileLocked(stale.Id);
                    }
                }

                WriteIndexLocked(entries);
            }
        }

        /// <summary>
        /// 删除单个会话（文件 + 索引条目）
        /// </summary>
        public static void DeleteConversation(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            lock (Gate)
            {
                EnsureMigratedLocked();

                TryDeleteConversationFileLocked(id);

                var index = ReadIndexLocked();
                index.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
                WriteIndexLocked(index);
            }
        }

        /// <summary>
        /// 清空全部会话（删掉所有会话文件与索引）
        /// </summary>
        public static void ClearAll()
        {
            lock (Gate)
            {
                EnsureMigratedLocked();

                try
                {
                    if (System.IO.Directory.Exists(Directory))
                    {
                        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json", SearchOption.TopDirectoryOnly))
                        {
                            try
                            {
                                File.Delete(file);
                            }
                            catch (Exception)
                            {
                                // 单个删不掉就算了，继续删其余
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // 目录不可写等：忽略
                }

                WriteIndexLocked([]);
            }
        }

        /// <summary>
        /// 把运行时消息序列化成可落盘的会话（标题 / 摘要取首条用户消息）
        /// </summary>
        public static StoredConversation Snapshot(
            string id,
            string model,
            IEnumerable<ChatMessage> messages,
            IEnumerable<SessionChange> changes = null,
            bool paused = false,
            bool interrupted = false,
            bool canRetry = false)
        {
            var list = (messages ?? Enumerable.Empty<ChatMessage>()).ToList();
            var firstUser = list.FirstOrDefault(m => m.IsUser);

            return new StoredConversation
            {
                Id = id,
                Title = BuildTitle(firstUser?.Text),
                Preview = BuildPreview(firstUser?.Text),
                Model = model ?? string.Empty,
                UpdatedUtc = DateTime.UtcNow,
                MessageCount = list.Count,
                Messages = list.Select(ToStoredMessage).ToList(),

                // 任务状态 + 本次变更：重开 VS 后据此恢复「继续任务」与「本次变更」摘要
                Paused = paused,
                Interrupted = interrupted,
                CanRetry = canRetry,
                Changes = (changes ?? Enumerable.Empty<SessionChange>())
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path))
                    .Select(ToStoredChange)
                    .ToList(),
            };
        }

        private static StoredChange ToStoredChange(SessionChange change)
        {
            return new StoredChange
            {
                Kind = change.Kind == SessionChangeKind.Add ? "add"
                    : change.Kind == SessionChangeKind.Delete ? "del"
                    : "mod",
                Path = change.Path ?? string.Empty,
                Added = change.Added,
                Deleted = change.Deleted,
            };
        }

        /// <summary>
        /// 把落盘会话里的「本次变更」还原成运行时集合
        /// </summary>
        public static List<SessionChange> ToSessionChanges(StoredConversation conversation)
        {
            var result = new List<SessionChange>();
            if (conversation?.Changes == null)
            {
                return result;
            }

            foreach (var stored in conversation.Changes)
            {
                if (stored == null || string.IsNullOrWhiteSpace(stored.Path))
                {
                    continue;
                }

                result.Add(new SessionChange
                {
                    Kind = string.Equals(stored.Kind, "add", StringComparison.OrdinalIgnoreCase) ? SessionChangeKind.Add
                        : string.Equals(stored.Kind, "del", StringComparison.OrdinalIgnoreCase) ? SessionChangeKind.Delete
                        : SessionChangeKind.Modify,
                    Path = stored.Path,
                    Added = stored.Added,
                    Deleted = stored.Deleted,
                });
            }

            return result;
        }

        /// <summary>
        /// 把落盘会话还原成运行时消息
        /// </summary>
        public static List<ChatMessage> ToMessages(StoredConversation conversation)
        {
            var result = new List<ChatMessage>();
            if (conversation?.Messages == null)
            {
                return result;
            }

            foreach (var stored in conversation.Messages)
            {
                result.Add(FromStoredMessage(stored, conversation.Model));
            }

            return result;
        }

        /// <summary>
        /// 跨会话全文搜索：在全部历史会话的消息正文（用户正文 + 助手各内容块）里找关键字
        /// </summary>
        public static IList<ConversationSearchHit> Search(string query, int max = 50)
        {
            var hits = new List<ConversationSearchHit>();
            var text = (query ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return hits;
            }

            foreach (var conversation in LoadAll().OrderByDescending(c => c.UpdatedUtc))
            {
                if (hits.Count >= Math.Max(1, max))
                {
                    break;
                }

                var count = 0;
                string snippet = null;

                foreach (var message in conversation.Messages ?? [])
                {
                    if (Matches(message.Text, text, ref snippet))
                    {
                        count++;
                    }

                    foreach (var block in message.Blocks ?? [])
                    {
                        if (Matches(block.Text, text, ref snippet))
                        {
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    hits.Add(new ConversationSearchHit
                    {
                        Id = conversation.Id,
                        Title = conversation.Title,
                        Model = conversation.Model,
                        UpdatedUtc = conversation.UpdatedUtc,
                        MatchCount = count,
                        Snippet = snippet ?? string.Empty,
                    });
                }
            }

            return hits;
        }

        // ==================== 索引 / 文件读写（调用方须持 <see cref="Gate"/>） ====================

        /// <summary>
        /// 写单个会话文件并更新索引（须在锁内调用）
        /// </summary>
        private static void SaveConversationLocked(StoredConversation conversation, int maxItems)
        {
            WriteConversationFileLocked(conversation);

            var index = ReadIndexLocked();
            index.RemoveAll(c => string.Equals(c.Id, conversation.Id, StringComparison.OrdinalIgnoreCase));
            index.Insert(0, ToIndexEntry(conversation));

            index = index.OrderByDescending(c => c.UpdatedUtc).ToList();

            if (maxItems > 0 && index.Count > maxItems)
            {
                var removed = index.Skip(maxItems).ToList();
                index = index.Take(maxItems).ToList();
                foreach (var stale in removed)
                {
                    TryDeleteConversationFileLocked(stale.Id);
                }
            }

            WriteIndexLocked(index);
        }

        /// <summary>
        /// 只取元数据（消息体置空），用于索引条目
        /// </summary>
        private static StoredConversation ToIndexEntry(StoredConversation source)
        {
            return new StoredConversation
            {
                Id = source.Id,
                Title = source.Title,
                Preview = source.Preview,
                Model = source.Model,
                UpdatedUtc = source.UpdatedUtc,
                MessageCount = source.MessageCount > 0
                    ? source.MessageCount
                    : (source.Messages?.Count ?? 0),
                Messages = [],

                // 索引只留元数据，但「可继续」值得带上：历史列表将来可据此标记未完成任务
                Paused = source.Paused,
            };
        }

        private static void WriteConversationFileLocked(StoredConversation conversation)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                var path = PathFor(conversation.Id);
                var temp = path + ".tmp";
                File.WriteAllText(temp, Serialize(conversation), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temp, path);
            }
            catch (Exception)
            {
                // 落盘失败不影响当前会话继续
            }
        }

        private static void TryDeleteConversationFileLocked(string id)
        {
            try
            {
                var path = PathFor(id);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        private static List<StoredConversation> ReadIndexLocked()
        {
            try
            {
                if (!File.Exists(IndexPath))
                {
                    // 没有索引（首次运行 / 索引丢失）：按会话文件重建一份
                    return RebuildIndexLocked();
                }

                var index = Deserialize<ConversationIndex>(File.ReadAllText(IndexPath, Encoding.UTF8));
                return index?.Conversations ?? [];
            }
            catch (Exception)
            {
                return RebuildIndexLocked();
            }
        }

        private static void WriteIndexLocked(List<StoredConversation> entries)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                var payload = new ConversationIndex { Conversations = entries ?? [] };
                var temp = IndexPath + ".tmp";
                File.WriteAllText(temp, Serialize(payload), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                if (File.Exists(IndexPath))
                {
                    File.Delete(IndexPath);
                }

                File.Move(temp, IndexPath);
            }
            catch (Exception)
            {
                // 索引写失败：下次读取时由 RebuildIndexLocked 兜底重建
            }
        }

        /// <summary>
        /// 没有索引时，扫描会话文件重建（只取元数据，不保留消息体）
        /// </summary>
        private static List<StoredConversation> RebuildIndexLocked()
        {
            var entries = new List<StoredConversation>();

            try
            {
                if (!System.IO.Directory.Exists(Directory))
                {
                    return entries;
                }

                foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(Path.GetFileName(file), IndexFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var conversation = Deserialize<StoredConversation>(File.ReadAllText(file, Encoding.UTF8));
                        if (conversation != null)
                        {
                            entries.Add(ToIndexEntry(conversation));
                        }
                    }
                    catch (Exception)
                    {
                        // 单个损坏：跳过
                    }
                }
            }
            catch (Exception)
            {
                return entries;
            }

            entries = entries.OrderByDescending(c => c.UpdatedUtc).ToList();
            WriteIndexLocked(entries);
            return entries;
        }

        /// <summary>
        /// 首次访问时把旧的单文件 <c>conversations.json</c> 迁移到新结构（须在锁内调用）
        /// </summary>
        private static void EnsureMigratedLocked()
        {
            if (_migrated)
            {
                return;
            }

            _migrated = true;

            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                if (!File.Exists(LegacyFilePath))
                {
                    return;
                }

                var legacy = Deserialize<ConversationFile>(File.ReadAllText(LegacyFilePath, Encoding.UTF8));
                var conversations = legacy?.Conversations ?? [];

                foreach (var conversation in conversations)
                {
                    if (conversation == null || string.IsNullOrWhiteSpace(conversation.Id))
                    {
                        continue;
                    }

                    WriteConversationFileLocked(conversation);
                }

                var entries = conversations
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id))
                    .Select(ToIndexEntry)
                    .OrderByDescending(c => c.UpdatedUtc)
                    .ToList();

                WriteIndexLocked(entries);

                // 迁移成功：把旧文件改名备份，避免下次重复迁移
                try
                {
                    File.Move(LegacyFilePath, LegacyFilePath + ".migrated");
                }
                catch (Exception)
                {
                    // 改名失败无妨：下次会重新迁移（幂等，最坏多写一遍）
                }
            }
            catch (Exception)
            {
                // 迁移失败：保持旧文件不动，下次再试
            }
        }

        // ==================== 文件路径 / 序列化 ====================

        private static string PathFor(string id)
        {
            var safe = new StringBuilder();
            foreach (var c in id ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                {
                    safe.Append(c);
                }
            }

            var name = safe.Length == 0 ? "conversation" : safe.ToString();
            return Path.Combine(Directory, name + ".json");
        }

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var buffer = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(buffer, Encoding.UTF8, ownsStream: false, indent: true))
                {
                    serializer.WriteObject(writer, value);
                    writer.Flush();
                }

                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static T Deserialize<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        // ==================== 映射 ====================

        private static StoredMessage ToStoredMessage(ChatMessage message)
        {
            var stored = new StoredMessage
            {
                Role = message.Role,
                Text = message.IsUser ? message.Text : string.Empty,
            };

            if (!message.IsUser)
            {
                stored.Blocks = message.Blocks.Select(ToStoredBlock).ToList();
            }

            return stored;
        }

        private static ChatMessage FromStoredMessage(StoredMessage stored, string model)
        {
            var message = new ChatMessage
            {
                Role = stored.Role,
                Model = model,
                Text = stored.Text ?? string.Empty,
            };

            foreach (var block in stored.Blocks ?? [])
            {
                message.Blocks.Add(FromStoredBlock(block));
            }

            return message;
        }

        private static StoredBlock ToStoredBlock(ChatBlock block)
        {
            var stored = new StoredBlock
            {
                Kind = KindToTag(block.Kind),
                Title = block.Title,
                Path = block.Path,
            };

            if (block.Kind == ChatBlockKind.DiffCard)
            {
                stored.Text = string.Join("\n", block.Lines.Select(EncodeLine));
            }
            else if (block.Kind == ChatBlockKind.Todo)
            {
                // 待办清单编码成 [ ]/[-]/[x] 文本：复用现成的解析器就能还原，
                // 不必为它再加一层数据契约
                stored.Text = TodoListParser.Render(block.Todos);
            }
            else if (block.Kind == ChatBlockKind.Question)
            {
                // 提问卡没有文件路径，正好把「用户的回答」寄存在 Path 上
                stored.Text = block.Text;
                stored.Path = block.AnswerText ?? string.Empty;
            }
            else
            {
                stored.Text = block.Text;
            }

            return stored;
        }

        private static ChatBlock FromStoredBlock(StoredBlock stored)
        {
            var kind = TagToKind(stored.Kind);

            var block = new ChatBlock
            {
                Kind = kind,
                Title = stored.Title ?? string.Empty,
                Path = stored.Path ?? string.Empty,

                // 载入历史时**一律折叠**：一次长会话可能有几十张读文件 / 编辑 / 列表卡片，
                // 全部展开会把正文彻底淹没 —— 用户得滚很久才能看到 AI 到底说了什么
                IsBodyVisible = false,
            };

            if (block.Kind == ChatBlockKind.DiffCard)
            {
                foreach (var line in (stored.Text ?? string.Empty).Split('\n'))
                {
                    if (line.Length > 0)
                    {
                        block.Lines.Add(DecodeLine(line));
                    }
                }
            }
            else if (block.Kind == ChatBlockKind.Todo)
            {
                foreach (var item in TodoListParser.Parse(stored.Text))
                {
                    block.Todos.Add(item);
                }

                block.HasTodos = block.Todos.Count > 0;
                block.ProgressText = TodoListParser.Describe(TodoListParser.Summarize(block.Todos));
                block.IsBodyVisible = false;
            }
            else if (block.Kind == ChatBlockKind.Question)
            {
                block.Text = stored.Text ?? string.Empty;
                block.AnswerText = stored.Path ?? string.Empty;
                block.IsAnswered = block.AnswerText.Length > 0;
                block.IsPendingQuestion = false;    // 历史里的问题一律不算「正在等回答」
                block.StatusText = block.IsAnswered ? "已回答" : string.Empty;
                block.Path = string.Empty;          // 上面借 Path 存过答案，别让它看起来像个文件
                block.IsBodyVisible = false;
            }
            else if (block.Kind == ChatBlockKind.SubTask)
            {
                block.Text = stored.Text ?? string.Empty;
                block.StatusText = "已完成";
                block.IsBodyVisible = false;
            }
            else
            {
                block.Text = stored.Text ?? string.Empty;
            }

            return block;
        }

        private static string EncodeLine(ChatBlockLine line)
        {
            var sign = line.Kind == DiffLineKind.Add ? "+ " : line.Kind == DiffLineKind.Delete ? "- " : "  ";
            return sign + line.Text;
        }

        private static ChatBlockLine DecodeLine(string line)
        {
            if (line.StartsWith("+ ", StringComparison.Ordinal))
            {
                return new ChatBlockLine(DiffLineKind.Add, "+ ", line.Substring(2));
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                return new ChatBlockLine(DiffLineKind.Delete, "− ", line.Substring(2));
            }

            var text = line.StartsWith("  ", StringComparison.Ordinal) ? line.Substring(2) : line;
            return new ChatBlockLine(DiffLineKind.Context, "  ", text);
        }

        private static string KindToTag(ChatBlockKind kind)
        {
            switch (kind)
            {
                case ChatBlockKind.Thinking: return "thinking";
                case ChatBlockKind.CodeCard: return "code";
                case ChatBlockKind.FileCard: return "file";
                case ChatBlockKind.DiffCard: return "diff";
                case ChatBlockKind.Note: return "note";
                case ChatBlockKind.Done: return "done";
                case ChatBlockKind.Todo: return "todo";
                case ChatBlockKind.Question: return "question";
                case ChatBlockKind.SubTask: return "subtask";
                default: return "text";
            }
        }

        private static ChatBlockKind TagToKind(string tag)
        {
            switch (tag)
            {
                case "thinking": return ChatBlockKind.Thinking;
                case "code": return ChatBlockKind.CodeCard;
                case "file": return ChatBlockKind.FileCard;
                case "diff": return ChatBlockKind.DiffCard;
                case "note": return ChatBlockKind.Note;
                case "done": return ChatBlockKind.Done;
                case "todo": return ChatBlockKind.Todo;
                case "question": return ChatBlockKind.Question;
                case "subtask": return ChatBlockKind.SubTask;
                default: return ChatBlockKind.Text;
            }
        }

        private static string BuildTitle(string firstUserText)
        {
            var text = (firstUserText ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length == 0)
            {
                return "新会话";
            }

            return text.Length <= 24 ? text : text.Substring(0, 24) + "…";
        }

        private static string BuildPreview(string firstUserText)
        {
            var text = (firstUserText ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 60 ? text : text.Substring(0, 60) + "…";
        }

        /// <summary>
        /// 命中则返回 true，并在 snippet 为空时填入一小段上下文
        /// </summary>
        private static bool Matches(string source, string query, ref string snippet)
        {
            if (string.IsNullOrEmpty(source))
            {
                return false;
            }

            var index = source.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            snippet ??= SnippetOf(source, query);

            return true;
        }

        /// <summary>
        /// 截取关键字周围的一小段（压平换行，便于列表显示）
        /// </summary>
        private static string SnippetOf(string source, string query)
        {
            var flat = source.Replace("\r", " ").Replace("\n", " ");
            var index = flat.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return flat.Length <= 80 ? flat : flat.Substring(0, 80) + "…";
            }

            var start = Math.Max(0, index - 30);
            var length = Math.Min(flat.Length - start, query.Length + 60);
            var snippet = flat.Substring(start, length).Trim();
            return (start > 0 ? "…" : string.Empty) + snippet + (start + length < flat.Length ? "…" : string.Empty);
        }
    }

    /// <summary>
    /// 历史全文搜索命中的一条会话
    /// </summary>
    public sealed class ConversationSearchHit
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 命中次数
        /// </summary>
        public int MatchCount { get; set; }

        /// <summary>
        /// 包含关键字的片段
        /// </summary>
        public string Snippet { get; set; } = string.Empty;

        /// <summary>
        /// 列表里的摘要行
        /// </summary>
        public string Summary => MatchCount + " 处匹配 · " + Snippet;
    }
}
