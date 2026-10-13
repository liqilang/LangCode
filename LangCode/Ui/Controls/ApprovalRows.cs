using LangCode.Domain.Catalog;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LangCode.Ui.Controls
{
    /// <summary>
    /// 「按类别自动批准」的一行开关
    /// </summary>
    internal static class ApprovalRows
    {
        /// <summary>
        /// 建一行：左边名字 + 说明，右边开关
        /// </summary>
        /// <param name="owner">用于解析主题资源（资源字典挂在各控件自己的 Resources 上，不是 App 级）</param>
        /// <param name="category">类别定义</param>
        /// <param name="onToggle">开关被点击时回调</param>
        internal static FrameworkElement Create(FrameworkElement owner, ApprovalCategory category, RoutedEventHandler onToggle)
        {
            var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };

            // 用 SetResourceReference 而不是一次性取色：这样主题（深/浅）切换时颜色会自动跟随
            var title = new TextBlock
            {
                Text = category.Name,
                FontSize = 12.5,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgStrong");
            text.Children.Add(title);

            var description = category.Wired
                ? category.Description
                : category.Description;   // 未接入的说明已写在 Description 里

            var descriptionText = new TextBlock
            {
                Text = description,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            descriptionText.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
            text.Children.Add(descriptionText);

            var toggle = new ToggleButton
            {
                // Tag = AppSettings 属性名：设置页靠它回填/写回；Auto 弹层用 DataContext 传类别
                Tag = category.Property,
                DataContext = category,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = false,
            };

            var style = owner.TryFindResource("Switch") as Style;
            if (style != null)
            {
                toggle.Style = style;
            }

            if (!category.Wired)
            {
                // 还没接入的类别：开关可见但不可点，避免给出「点了会生效」的错觉
                toggle.IsEnabled = false;
                toggle.ToolTip = "该能力尚未接入，开关暂时无效（配置会被保存）";
            }

            if (onToggle != null)
            {
                toggle.Click += onToggle;
            }

            Grid.SetColumn(text, 0);
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(text);
            grid.Children.Add(toggle);
            return grid;
        }
    }
}

