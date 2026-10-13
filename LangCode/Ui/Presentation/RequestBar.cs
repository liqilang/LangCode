using System.Collections.Generic;

namespace LangCode.Ui.Presentation
{
    /// <summary>
    /// 输入框上方「请求条」上的一个动作
    /// </summary>
    public enum RequestAction
    {
        /// <summary>
        /// 拒绝待批动作
        /// </summary>
        Reject,

        /// <summary>
        /// 允许待批动作
        /// </summary>
        Approve,

        /// <summary>
        /// 取消当前生成（横向铺满的那一条）
        /// </summary>
        Cancel,

        /// <summary>
        /// 继续被步数上限打断的任务
        /// </summary>
        Continue,

        /// <summary>
        /// 恢复被用户中断的任务（跟「继续」是同一件事，只是起因不同）
        /// </summary>
        Resume,

        /// <summary>
        /// 重试上一轮（真正出错时）
        /// </summary>
        Retry,

        /// <summary>
        /// 撤销本轮真正落盘的改动
        /// </summary>
        Undo,

        /// <summary>
        /// 跳过模型的提问（不回答，让它自己按最合理的默认方案继续）
        /// </summary>
        SkipQuestion,

        /// <summary>
        /// 只撤销<b>上一步</b>落盘的改动（检查点）
        /// </summary>
        UndoStep,
    }

    /// <summary>
    /// 输入框上方那条请求条的<b>纯逻辑</b>：什么状态下该出现哪些动作
    /// </summary>
    public static class RequestBar
    {
        /// <summary>
        /// 按当前状态算出要显示的动作（顺序即显示顺序）
        /// </summary>
        public static IList<RequestAction> Actions(
            bool pendingApproval,
            bool busy,
            bool canContinue,
            bool interrupted,
            bool canRetry,
            bool canUndo,
            bool pendingQuestion = false,
            bool canUndoStep = false)
        {
            var actions = new List<RequestAction>();

            // ① 待批：阻塞式，只显示「拒绝 / 允许」，点完才往下走
            if (pendingApproval)
            {
                actions.Add(RequestAction.Reject);
                actions.Add(RequestAction.Approve);
            }

            // ①' 待答：同样是阻塞式给一个「跳过回答」的出口，否则用户在不想答时只能取消整个任务
            //     回答本身在输入框里打字回车（或在卡片上点备选答案）
            if (pendingQuestion)
            {
                actions.Add(RequestAction.SkipQuestion);
            }

            // ② 生成中：一条横向铺满的「取消」，就它一个
            if (busy)
            {
                actions.Add(RequestAction.Cancel);
                return actions;
            }

            // ③ 空闲：按需给「撤销上一步 / 撤销本轮 / 继续 / 恢复 / 重试」；任务正常跑完什么都不显示
            //     逐步撤销受「检查点」设置控制：关掉它时只留「撤销本轮」这一个粗粒度出口
            if (canUndoStep)
            {
                actions.Add(RequestAction.UndoStep);
            }

            if (canUndo)
            {
                actions.Add(RequestAction.Undo);
            }

            if (canContinue)
            {
                // 撞到步数上限：任务的下一步是明确的，所以叫「继续任务」
                actions.Add(RequestAction.Continue);
            }
            else if (interrupted)
            {
                // 用户自己按的取消：不是出错，所以叫「恢复任务」而不是「重试」
                actions.Add(RequestAction.Resume);
            }
            else if (canRetry)
            {
                // 真正抛异常了才叫「重试」
                actions.Add(RequestAction.Retry);
            }

            return actions;
        }

        /// <summary>
        /// 按钮文案
        /// </summary>
        public static string Label(RequestAction action)
        {
            switch (action)
            {
                case RequestAction.Reject: return "拒绝";
                case RequestAction.Approve: return "允许";
                case RequestAction.Cancel: return "取消";
                case RequestAction.Continue: return "继续任务";
                case RequestAction.Resume: return "恢复任务";
                case RequestAction.Retry: return "重试";
                case RequestAction.Undo: return "撤销修改";
                case RequestAction.SkipQuestion: return "跳过回答";
                case RequestAction.UndoStep: return "撤销上一步";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// 按钮图标名（对应 Icons/*.svg）
        /// </summary>
        public static string Icon(RequestAction action)
        {
            switch (action)
            {
                case RequestAction.Reject: return "close";
                case RequestAction.Approve: return "check";
                case RequestAction.Cancel: return "stop";
                case RequestAction.Continue: return "play-fill";
                case RequestAction.Resume: return "play-fill";
                case RequestAction.Retry: return "retry";
                case RequestAction.Undo: return "undo";
                case RequestAction.SkipQuestion: return "chevron-right";
                case RequestAction.UndoStep: return "chevron-left";
                default: return "check";
            }
        }

        /// <summary>
        /// 主按钮（允许）—— 用强调色
        /// </summary>
        public static bool IsPrimary(RequestAction action) => action == RequestAction.Approve;

        /// <summary>
        /// 危险动作（拒绝）—— 用红色
        /// </summary>
        public static bool IsDanger(RequestAction action) => action == RequestAction.Reject;

        /// <summary>
        /// 是否要横向铺满整条宽度只有「取消」是 —— 它是生成中唯一的出口，
        /// 做成一条通栏按钮，闭着眼也知道点哪
        /// </summary>
        public static bool IsWide(RequestAction action) => action == RequestAction.Cancel;

        /// <summary>
        /// 请求条的标题
        /// </summary>
        public static string Title(bool pendingApproval, bool pendingQuestion = false)
        {
            if (pendingQuestion)
            {
                return "需要你的回答才能继续";
            }

            return pendingApproval ? "有一个操作在等你确认" : string.Empty;
        }
    }
}

