using LangCode.Ui.Theme;
using System;
using System.Windows;
using System.Windows.Controls;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// 内联编辑（Ctrl+K）的极简输入框：让用户用一句话描述「要对选中的代码做什么」。
    /// <para>
    /// 它是编辑器命令直接拉起来的独立小窗（此时 LangCode 工具窗可能根本没开），所以不塞进工具窗；
    /// 但同样跟随 VS 主题、并**挂在 VS 主窗口上居中显示**（而不是飘到屏幕中间 / 另一个显示器上）
    /// </para>
    /// </summary>
    internal sealed class InlineEditDialog : Window
    {
        private readonly TextBox _input;

        public InlineEditDialog()
        {
            Title = "LangCode · 内联编辑";
            Width = 480;
            Height = 220;
            MinWidth = 360;
            MinHeight = 170;

            // 跟随主题：调色板（Ap.*）注入窗口自己的资源，再并入控件样式
            ThemeManager.Attach(this);
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/LangCode;component/Ui/Theme/Controls.xaml", UriKind.Absolute),
            });

            SetResourceReference(BackgroundProperty, "Ap.BgElevated");
            SetResourceReference(ForegroundProperty, "Ap.FgBright");

            // 贴在 VS 主窗口上（取不到就退回屏幕居中）
            TryCenterOverVs();

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "要对选中的代码做什么？（回车确认，Esc 取消）",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgBody");
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            _input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 10, 0, 10),
                MinHeight = 80,
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 12.5,
            };
            _input.SetResourceReference(BackgroundProperty, "Ap.BgInput");
            _input.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgBright");
            _input.SetResourceReference(Control.BorderBrushProperty, "Ap.InputBorder");
            _input.SetResourceReference(TextBox.CaretBrushProperty, "Ap.FgBright");
            Grid.SetRow(_input, 1);
            grid.Children.Add(_input);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var cancel = new Button
            {
                Content = "取消",
                Width = 84,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
            };

            var confirm = new Button
            {
                Content = "生成",
                Width = 84,
                Height = 28,
                IsDefault = true,
            };
            confirm.Click += (_, __) => DialogResult = true;

            if (TryFindResource("BtnBase") is Style plain)
            {
                cancel.Style = plain;
            }

            if (TryFindResource("BtnPrimary") is Style primary)
            {
                confirm.Style = primary;
            }

            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            Content = grid;
            Loaded += (_, __) => _input.Focus();
        }

        /// <summary>
        /// 用户输入的修改要求（确认时非空）
        /// </summary>
        public string Instruction => _input.Text ?? string.Empty;

        /// <summary>
        /// 居中对齐到 VS 主窗口：这样小窗被约束在 VS 的范围内，
        /// 不会像默认的「屏幕居中」那样跑到 VS 之外或另一个显示器上
        /// </summary>
        private void TryCenterOverVs()
        {
            try
            {
                var owner = Application.Current?.MainWindow;
                if (owner != null && !ReferenceEquals(owner, this) && owner.IsLoaded)
                {
                    Owner = owner;
                    WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    return;
                }
            }
            catch (Exception)
            {
                // 取不到主窗口（非 VS 宿主 / 时序不对）：下面退回屏幕居中
            }

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}
