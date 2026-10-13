namespace LangCode.VsExt.Domain.Catalog
{
    /// <summary>
    /// 粗粒度 token 估算
    /// </summary>
    public static class TokenEstimator
    {
        /// <summary>
        /// 单条消息的角色 / 分隔开销（粗估）
        /// </summary>
        public const double PerMessageOverhead = 4.0;

        /// <summary>
        /// 估算一段文本的 token 数
        /// </summary> 
        public static double Estimate(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            double tokens = 0;
            foreach (var c in text)
            {
                tokens += IsCjk(c) ? 1.0 : 0.25;
            }

            return tokens;
        }

        /// <summary>
        /// 估算单条消息（含角色开销）
        /// </summary>
        public static double EstimateMessage(string content)
        {
            return Estimate(content) + PerMessageOverhead;
        }

        /// <summary>
        /// 是否 CJK 字符（中日韩文字与全角标点）
        /// </summary>
        public static bool IsCjk(char c)
        {
            return (c >= 0x3000 && c <= 0x303F)     // CJK 标点
                || (c >= 0x3040 && c <= 0x30FF)     // 日文假名
                || (c >= 0x3400 && c <= 0x4DBF)     // CJK 扩展 A
                || (c >= 0x4E00 && c <= 0x9FFF)     // CJK 统一表意
                || (c >= 0xAC00 && c <= 0xD7AF)     // 韩文
                || (c >= 0xF900 && c <= 0xFAFF);    // CJK 兼容表意
        }
    }
}
