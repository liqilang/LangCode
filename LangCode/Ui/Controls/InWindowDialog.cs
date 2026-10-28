using LangCode.Ui.Primitive;
using LangCode.Ui.Theme;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LangCode.Ui.Controls
{
    /// <summary>
    /// LangCode <b>窗口内</b>的自定义提示框（替代 <c>MessageBox</c>）。
    /// <para>
    /// 为什么不用系统弹窗：<c>MessageBox</c> 是独立的系统窗口，位置不受工具窗控制
    /// （常见"飘到 VS 之外 / 另一个显示器上"），配色也不跟主题走。
    /// 这里改成"盖在宿主控件上的覆盖层"：
    /// </para>
    /// <list type="bullet">
    /// <item>显示范围严格限制在 LangCode 自己的窗口里（覆盖它所属的那个界面）；</item>
    /// <item>配色跟随 VS 主题（标题 / 正文 / 卡片 / 按钮都用 <c>Ap.*</c> 令牌）；</item>
    /// <item>标题、正文、按钮文字与按钮外观全部由调用方指定，可自由组合。</item>
    /// </list>
    /// </summary>
    internal static class InWindowDialog
    {
        /// <summary>
        /// 一个按钮：文字 + 返回值 + 外观
        /// </summary>
        internal sealed class DialogButton
        {
            public DialogButton(string text, string value = null, bool primary = false, bool danger = false, bool isCancel = false)
            {
                Text = text ?? string.Empty;
                Value = value;
                IsPrimary = primary;
                IsDanger = danger;
                IsCancel = isCancel;
            }

            /// <summary>按钮文字</summary>
            public string Text { get; }

            /// <summary>被点中时的返回值（点背景 / Esc 关掉时为 null）</summary>
            public string Value { get; }

            /// <summary>主按钮：强调色</summary>
            public bool IsPrimary { get; }

            /// <summary>危险动作：红色</summary>
            public bool IsDanger { get; }

            /// <summary>Esc / 点空白处 = 点了这个按钮</summary>
            public bool IsCancel { get; }
        }

        /// <summary>
        /// 确定 / 取消：返回是否点了确定。
        /// <paramref name="host"/> 传界面里任意一个元素即可（会用它所属的那层界面当容器）
        /// </summary>
        public static async Task<bool> ConfirmAsync(
            FrameworkElement host,
            string title,
            string message,
            string confirmText = "确定",
            string cancelText = "取消",
            bool danger = true,
            string iconName = "problem")
        {
            var buttons = new List<DialogButton>
            {
                new DialogButton(cancelText, "cancel", isCancel: true),
                new DialogButton(confirmText, "ok", primary: true, danger: danger),
            };

            var value = await ShowAsync(host, title, message, buttons, iconName).ConfigureAwait(true);
            return value == "ok";
        }

        /// <summary>
        /// 只有一枚「知道了」按钮的提示（成功 / 失败 / 说明类）
        /// </summary>
        public static Task InfoAsync(
            FrameworkElement host,
            string title,
            string message,
            string closeText = "知道了",
            string iconName = "info")
        {
            var buttons = new List<DialogButton>
            {
                new DialogButton(closeText, "ok", primary: true, isCancel: true),
            };

            return ShowAsync(host, title, message, buttons, iconName);
        }

        /// <summary>
        /// 自定义按钮组：返回被点中按钮的 <see cref="DialogButton.Value"/>
        /// （点背景 / Esc / 没有取消按钮时关闭，返回 null）
        /// </summary>
        public static Task<string> ShowAsync(
            FrameworkElement host,
            string title,
            string message,
            IList<DialogButton> buttons,
            string iconName = null)
        {
            var panel = RootPanelOf(host);
            if (panel == null || buttons == null || buttons.Count == 0)
            {
                // 兜底：没有可用的宿主容器时退回系统弹窗，至少别把提示吞掉
                MessageBox.Show(message ?? string.Empty, string.IsNullOrWhiteSpace(title) ? "LangCode" : title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return Task.FromResult(buttons == null ? null : FirstValue(buttons));
            }

            var completion = new TaskCompletionSource<string>();

            var danger = buttons.Any(b => b.IsDanger);

            // 背板：盖满宿主（= LangCode 的这块界面），点空白处等于取消
            var overlay = new Border
            {
                Background = new SolidColorBrush(ScrimColor()),
                Focusable = true,
                SnapsToDevicePixels = true,
            };
            SetFullSpan(overlay, panel);

            // 注意：卡片上的按钮样式要从**已在树上的**宿主去解析
            // （卡片此刻还没挂到树上，自己去 TryFindResource 是找不到 BtnBase 这些样式的）
            var card = BuildCard(panel, title, message, buttons, iconName, danger, value => Close(completion, panel, overlay, value));

            overlay.Child = card;

            // Esc = 取消，Enter = 主按钮（焦点在覆盖层里，所以按键不会漏到后面的界面）
            overlay.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    Close(completion, panel, overlay, CancelValue(buttons));
                }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Close(completion, panel, overlay, PrimaryValue(buttons));
                }
            };

            // 只有点在背板本身（空白处）才算取消；点卡片内部不算
            overlay.MouseLeftButtonUp += (_, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, overlay))
                {
                    Close(completion, panel, overlay, CancelValue(buttons));
                }
            };

            panel.Children.Add(overlay);

            // 焦点交给"取消"（没有就交给主按钮）：Esc 立刻可用，Tab 也不会跑到背后的界面上
            overlay.Loaded += (_, __) =>
            {
                var target = FirstButton(card, true) ?? FirstButton(card, false);
                if (target != null)
                {
                    target.Focus();
                }
                else
                {
                    overlay.Focus();
                }
            };

            return completion.Task;
        }

        // ============================================================
        // 内部：搭建
        // ============================================================

        private static FrameworkElement BuildCard(
            FrameworkElement resources,
            string title,
            string message,
            IList<DialogButton> buttons,
            string iconName,
            bool danger,
            Action<string> close)
        {
            var card = new Border
            {
                MinWidth = 260,
                MaxWidth = 420,
                Margin = new Thickness(18),
                Padding = new Thickness(16, 14, 16, 14),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 2,
                    Opacity = 0.35,
                    Color = Colors.Black,
                },
            };
            card.SetResourceReference(Border.BackgroundProperty, "Ap.BgElevated");
            card.SetResourceReference(Border.BorderBrushProperty, "Ap.Border");

            var stack = new StackPanel();

            // 标题（可选图标 + 文字）
            var header = new StackPanel { Orientation = Orientation.Horizontal };

            if (!string.IsNullOrWhiteSpace(iconName))
            {
                var glyph = new Icon
                {
                    IconName = iconName,
                    Width = 14,
                    Height = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                glyph.SetResourceReference(Icon.ForegroundProperty, danger ? "Ap.RedText" : "Ap.Accent");
                header.Children.Add(glyph);
            }

            var heading = new TextBlock
            {
                Text = title ?? string.Empty,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(string.IsNullOrWhiteSpace(iconName) ? 0 : 7, 0, 0, 0),
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgBright");
            header.Children.Add(heading);

            stack.Children.Add(header);

            // 正文（原样多行，自动换行）
            var body = new TextBlock
            {
                Text = message ?? string.Empty,
                FontSize = 12.5,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 9, 0, 0),
            };
            body.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgBody");
            stack.Children.Add(body);

            // 按钮行：右对齐，最后一个（主按钮）放最右
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0),
            };

            foreach (var spec in buttons)
            {
                var button = new Button
                {
                    Content = spec.Text,
                    MinWidth = 78,
                    Height = 28,
                    Padding = new Thickness(12, 0, 12, 0),
                    Margin = new Thickness(8, 0, 0, 0),

                    // 只当"这枚按钮是什么角色"的标记用：键的处理由覆盖层统一做
                    // （这两个属性在非 Window 宿主里不会自己生效）
                    IsDefault = spec.IsPrimary,
                    IsCancel = spec.IsCancel,
                };

                var styleKey = spec.IsDanger ? "BtnDanger" : (spec.IsPrimary ? "BtnPrimary" : "BtnBase");
                if (resources.TryFindResource(styleKey) is Style style)
                {
                    button.Style = style;
                }

                var value = spec.Value;
                button.Click += (_, __) => close(value);

                row.Children.Add(button);
            }

            stack.Children.Add(row);
            card.Child = stack;

            return card;
        }

        /// <summary>
        /// 在卡片里找取消按钮（<c>IsCancel</c>）/ 主按钮（<c>IsDefault</c>）
        /// </summary>
        private static Button FirstButton(DependencyObject card, bool cancel)
        {
            if (card == null)
            {
                return null;
            }

            var count = VisualTreeHelper.GetChildrenCount(card);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(card, i);
                if (child is Button button && (cancel ? button.IsCancel : button.IsDefault))
                {
                    return button;
                }

                var nested = FirstButton(child, cancel);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        // ============================================================
        // 内部：容器 / 关闭 / 取值
        // ============================================================

        /// <summary>
        /// 找宿主所在的"那层界面"的根 Grid：从 <paramref name="host"/> 往上找最近的 UserControl，
        /// 它的 Content 是 Grid 就用它（历史 / 设置 / MCP 抽屉与主窗口都是这种结构）。
        /// 找不到就返回 null（调用方退回系统弹窗）
        /// </summary>
        private static Grid RootPanelOf(DependencyObject host)
        {
            var node = host;
            while (node != null)
            {
                if (node is UserControl control)
                {
                    if (control.Content is Grid grid)
                    {
                        return grid;
                    }

                    // Content 不是 Grid（少见）：继续往上找外层界面（例如主窗口的根 Grid）
                    return RootPanelOf(VisualTreeHelper.GetParent(control));
                }

                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return null;
        }

        /// <summary>
        /// 覆盖整块宿主：Grid 里没设 Row/Column 的话只会盖住第一格，
        /// 所以显式指定行/列，并把跨度拉到最大（超出会被 Grid 自动裁剪到实际格数）
        /// </summary>
        private static void SetFullSpan(FrameworkElement element, Grid panel)
        {
            Grid.SetRow(element, 0);
            Grid.SetColumn(element, 0);

            if (panel.RowDefinitions.Count > 0)
            {
                Grid.SetRowSpan(element, panel.RowDefinitions.Count);
            }

            if (panel.ColumnDefinitions.Count > 0)
            {
                Grid.SetColumnSpan(element, panel.ColumnDefinitions.Count);
            }
        }

        private static void Close(TaskCompletionSource<string> completion, Grid panel, Border overlay, string value)
        {
            if (!ReferenceEquals(overlay.Parent, panel))
            {
                return;
            }

            overlay.Child = null;
            panel.Children.Remove(overlay);
            completion.TrySetResult(value);
        }

        private static string CancelValue(IList<DialogButton> buttons)
        {
            var cancel = buttons.FirstOrDefault(b => b.IsCancel);
            return cancel?.Value;
        }

        private static string PrimaryValue(IList<DialogButton> buttons)
        {
            var primary = buttons.FirstOrDefault(b => b.IsPrimary);
            return (primary ?? buttons.FirstOrDefault())?.Value;
        }

        private static string FirstValue(IList<DialogButton> buttons)
        {
            return buttons.FirstOrDefault()?.Value;
        }

        /// <summary>
        /// 背板颜色：深色主题下重一点，浅色主题下轻一点（够"压住背景"又看得清卡片）
        /// </summary>
        private static Color ScrimColor()
        {
            return ThemeManager.Current == VsThemeKind.Dark ? Color.FromArgb(0x8C, 0x00, 0x00, 0x00) : Color.FromArgb(0x40, 0x10, 0x10, 0x10);
        }
    }
}
