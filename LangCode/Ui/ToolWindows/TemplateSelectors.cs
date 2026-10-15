using LangCode.Domain.Chat;
using System.Windows;
using System.Windows.Controls;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// 按消息角色挑模板（用户气泡 / 助手消息）
    /// </summary>
    public sealed class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate UserTemplate { get; set; }

        public DataTemplate AssistantTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            var message = item as ChatMessage;
            if (message != null && message.IsUser)
            {
                return UserTemplate ?? AssistantTemplate;
            }

            return AssistantTemplate;
        }
    }

    /// <summary>
    /// 按块类型挑模板（正文 / Thinking / 代码卡 / Diff 卡 / 完成条 / 待办 / 提问 / 子任务）
    /// </summary>
    public sealed class ChatBlockTemplateSelector : DataTemplateSelector
    {
        public DataTemplate TextTemplate { get; set; }

        public DataTemplate ThinkingTemplate { get; set; }

        public DataTemplate CodeTemplate { get; set; }

        /// <summary>
        /// 文件卡（读文件 / 新增文件）：内容不在对话里展开，点卡片直接在 VS 里打开
        /// </summary>
        public DataTemplate FileTemplate { get; set; }

        public DataTemplate DiffTemplate { get; set; }

        public DataTemplate DoneTemplate { get; set; }

        /// <summary>
        /// 任务进度卡（长任务编排的待办清单）
        /// </summary>
        public DataTemplate TodoTemplate { get; set; }

        /// <summary>
        /// 提问卡（模型问、用户答）
        /// </summary>
        public DataTemplate QuestionTemplate { get; set; }

        /// <summary>
        /// 子任务卡（拆出去独立完成的活）
        /// </summary>
        public DataTemplate SubTaskTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            var block = item as ChatBlock;
            if (block == null)
            {
                return TextTemplate;
            }

            switch (block.Kind)
            {
                case ChatBlockKind.Thinking:
                    return ThinkingTemplate ?? TextTemplate;
                case ChatBlockKind.CodeCard:
                    return CodeTemplate ?? TextTemplate;
                case ChatBlockKind.FileCard:
                    return FileTemplate ?? CodeTemplate ?? TextTemplate;

                // 操作确认卡复用差异卡模板（同样的标题 / 目标 / 接受·拒绝·批注），
                // 靠 ShowsDiffStats 把 +N/−N 统计藏掉
                case ChatBlockKind.Approval:
                    return DiffTemplate ?? TextTemplate;
                case ChatBlockKind.DiffCard:
                    return DiffTemplate ?? TextTemplate;
                case ChatBlockKind.Done:
                    return DoneTemplate ?? TextTemplate;

                case ChatBlockKind.Todo:
                    return TodoTemplate ?? TextTemplate;

                case ChatBlockKind.Question:
                    return QuestionTemplate ?? TextTemplate;

                case ChatBlockKind.SubTask:
                    return SubTaskTemplate ?? TextTemplate;

                default:
                    return TextTemplate;
            }
        }
    }
}

