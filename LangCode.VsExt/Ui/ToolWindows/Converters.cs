using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LangCode.VsExt.Ui.ToolWindows
{
    /// <summary>
    /// 字符串非空 → Visible（用于「有内容才显示」的元素，如状态文案、Thinking）
    /// </summary>
    public sealed class EmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var text = value as string;
            return string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// 十六进制颜色字符串（如 <c>#4D6BFE</c>）→ 画刷
    /// 用于模型列表的小圆点：颜色定义在 ModelCatalog（不引 WPF），这里只做转换
    /// </summary>
    public sealed class HexToBrushConverter : IValueConverter
    {
        private static readonly Brush Fallback = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hex = value as string;
            if (string.IsNullOrWhiteSpace(hex))
            {
                return Fallback;
            }

            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch (Exception)
            {
                return Fallback;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}

