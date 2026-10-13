using System;

namespace LangCode.VsExt.Domain.Chat
{
    /// <summary>
    /// 历史列表条目的视图模型（对应设计稿「历史记录」抽屉里的一行）
    /// </summary>
    public sealed class ConversationSummary : ObservableObject
    {
        private bool _isActive;
        private bool _isSelected;

        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Preview { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public int MessageCount { get; set; }

        /// <summary>
        /// 是否是当前正在查看的会话（列表里高亮）
        /// </summary>
        public bool IsActive
        {
            get { return _isActive; }
            set { Set(ref _isActive, value); }
        }

        /// <summary>
        /// 多选模式下是否被勾选（历史抽屉的批量删除用；退出多选时清零）
        /// </summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set { Set(ref _isSelected, value); }
        }

        /// <summary>
        /// 分组标题：今天 / 昨天 / 更早 
        /// </summary>
        public string GroupLabel
        {
            get
            {
                var day = UpdatedUtc.ToLocalTime().Date;
                var today = DateTime.Today;

                if (day == today)
                {
                    return "今天";
                }

                return day == today.AddDays(-1) ? "昨天" : "更早";
            }
        }

        /// <summary> 
        /// 右侧的模型徽标（设计稿里的 <c>claude-sonnet-4.5</c>） 
        /// </summary>
        public string ModelBadge => string.IsNullOrWhiteSpace(Model) ? "未指定模型" : Model;

        /// <summary> 
        /// 「· N 条消息」 
        /// </summary>
        public string CountLabel => "· " + MessageCount + " 条消息";

        /// <summary> 
        /// 「· 14:32」/「· 昨天 18:47」/「· 10月3日」 
        /// </summary>
        public string TimeLabel => "· " + FormatTime(UpdatedUtc.ToLocalTime());

        private static string FormatTime(DateTime local)
        {
            var today = DateTime.Today;
            if (local.Date == today)
            {
                return local.ToString("HH:mm");
            }

            if (local.Date == today.AddDays(-1))
            {
                return "昨天 " + local.ToString("HH:mm");
            }

            return local.ToString("M月d日");
        }
    }
}

