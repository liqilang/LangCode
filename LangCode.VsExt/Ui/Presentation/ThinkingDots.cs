using System;

namespace LangCode.VsExt.Ui.Presentation
{
    /// <summary>
    /// 「思考中...」那串循环点的<b>纯逻辑</b> 
    /// </summary>
    public static class ThinkingDots
    {
        /// <summary>
        /// 正在思考时的前缀
        /// </summary>
        public const string Thinking = "思考中";

        /// <summary>
        /// 这一步想完了的文案
        /// </summary>
        public const string Done = "思考完成";

        /// <summary>
        /// 循环的点数：1 → 2 → 3 → 回到 1
        /// </summary>
        public const int Phases = 3;

        /// <summary>
        /// 第 <paramref name="phase"/> 帧的文案（点数在 1..3 之间循环）
        /// </summary>
        public static string Label(int phase)
        {
            var dots = Math.Abs(phase) % Phases + 1;
            return Thinking + new string('.', dots);
        }

        /// <summary>
        /// 下一帧
        /// </summary>
        public static int Next(int phase)
        {
            return (Math.Abs(phase) + 1) % Phases;
        }
    }
}

