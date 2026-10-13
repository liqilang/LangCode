namespace LangCode.Domain.Chat
{
    /// <summary>
    /// 本次会话里被改动文件的类型（「本次变更」摘要区左侧标记）
    /// </summary>
    public enum SessionChangeKind
    {
        /// <summary>
        /// 修改已有文件（M）
        /// </summary>
        Modify,

        /// <summary>
        /// 新建文件（A）
        /// </summary>
        Add,

        /// <summary>
        /// 删除文件（D）
        /// </summary>
        Delete,
    }

    /// <summary>
    /// 本次会话改动过的一个文件（「本次会话更改的文件」摘要区的一行）
    /// </summary>
    public sealed class SessionChange : ObservableObject
    {
        private SessionChangeKind _kind;
        private string _path = string.Empty;
        private int _added;
        private int _deleted;

        /// <summary>
        /// 改动类型（新增 / 修改 / 删除）
        /// </summary>
        public SessionChangeKind Kind
        {
            get { return _kind; }
            set
            {
                if (Set(ref _kind, value))
                {
                    Raise(nameof(Marker));
                    Raise(nameof(TypeKey));
                }
            }
        }

        /// <summary>
        /// 工作区内的相对路径（摘要行主文案，点击可打开）
        /// </summary>
        public string Path
        {
            get { return _path; }
            set
            {
                if (Set(ref _path, value ?? string.Empty))
                {
                    Raise(nameof(Name));
                }
            }
        }

        /// <summary>
        /// 新增行数（累计）
        /// </summary>
        public int Added
        {
            get { return _added; }
            set
            {
                if (Set(ref _added, value))
                {
                    Raise(nameof(StatLabel));
                }
            }
        }

        /// <summary>
        /// 删除行数（累计）
        /// </summary>
        public int Deleted
        {
            get { return _deleted; }
            set
            {
                if (Set(ref _deleted, value))
                {
                    Raise(nameof(StatLabel));
                }
            }
        }

        /// <summary>
        /// 文件名（路径的最后一段）
        /// </summary>
        public string Name
        {
            get
            {
                var path = _path ?? string.Empty;
                var index = path.LastIndexOfAny(new[] { '/', '\\' });
                return index >= 0 && index < path.Length - 1 ? path.Substring(index + 1) : path;
            }
        }

        /// <summary>
        /// 摘要行左侧的类型标记（A / M / D）
        /// </summary>
        public string Marker
        {
            get
            {
                switch (_kind)
                {
                    case SessionChangeKind.Add: return "A";
                    case SessionChangeKind.Delete: return "D";
                    default: return "M";
                }
            }
        }

        /// <summary>
        /// 类型的短名（add / del / mod，供样式 / 自检使用）
        /// </summary>
        public string TypeKey
        {
            get
            {
                switch (_kind)
                {
                    case SessionChangeKind.Add: return "add";
                    case SessionChangeKind.Delete: return "del";
                    default: return "mod";
                }
            }
        }

        /// <summary>
        /// 右侧统计文案（「+18 −6」/「+24」）
        /// </summary>
        public string StatLabel
        {
            get
            {
                if (_added <= 0 && _deleted <= 0)
                {
                    return string.Empty;
                }

                return _deleted > 0 ? "+" + _added + " −" + _deleted : "+" + _added;
            }
        }
    }
}
