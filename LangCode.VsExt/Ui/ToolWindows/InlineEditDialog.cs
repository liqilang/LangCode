using System.Windows;
using System.Windows.Controls;

namespace LangCode.VsExt.Ui.ToolWindows
{
    /// <summary>
    /// 内联编辑（Ctrl+K）的极简输入框：让用户用一句话描述「要对选中的代码做什么」
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
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "要对选中的代码做什么？（回车确认，Esc 取消）",
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            _input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 10, 0, 10),
                MinHeight = 80,
            };
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
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
            };

            var confirm = new Button
            {
                Content = "生成",
                Width = 84,
                IsDefault = true,
            };
            confirm.Click += (_, __) => DialogResult = true;

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
    }
}

