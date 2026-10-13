using LangCode.Ui.Primitive;
using LangCode.Workspace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：@ 提及芯片
    /// </summary>
    public partial class AgentToolWindowControl
    {
        /// <summary>
        /// 待发送的文件 / 文件夹提及（发送后清空）
        /// </summary>
        private readonly List<MentionItem> _pendingMentions = [];

        /// <summary>
        /// 挂一枚提及芯片（同一个路径不重复挂）
        /// </summary>
        private void AddMention(MentionItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.RelativePath))
            {
                return;
            }

            if (_pendingMentions.Any(m =>
                    m.IsDirectory == item.IsDirectory &&
                    string.Equals(m.RelativePath, item.RelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _pendingMentions.Add(item);
            RenderPendingMentions();
            UpdateSendState();
        }

        /// <summary>
        /// 取走待发送提及并清空芯片条（发送时调用）
        /// </summary>
        private void TakePendingMentions()
        {
            if (_pendingMentions.Count == 0)
            {
                return;
            }

            _pendingMentions.Clear();
            RenderPendingMentions();
        }

        /// <summary>
        /// 把「待发送的提及芯片」拼进正文：芯片在界面上只显示图标 + 名称，
        /// 真正发给模型的仍然是 <c>@相对路径</c>（与原来「插入 @路径 文本」的行为一致）
        /// </summary>
        private string ComposeInputText()
        {
            var text = (Input.Text ?? string.Empty).Trim();

            if (_pendingMentions.Count == 0)
            {
                return text;
            }

            var builder = new StringBuilder();
            foreach (var item in _pendingMentions)
            {
                builder.Append('@').Append(item.InsertText).Append(' ');
            }

            var mentions = builder.ToString().TrimEnd();
            return text.Length == 0 ? mentions : mentions + "\n" + text;
        }

        /// <summary>
        /// 重绘输入框上方的提及芯片条（没有芯片就整条收起）
        /// </summary>
        private void RenderPendingMentions()
        {
            if (MentionBar == null)
            {
                return;
            }

            MentionBar.Children.Clear();

            if (_pendingMentions.Count == 0)
            {
                MentionBar.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var item in _pendingMentions)
            {
                MentionBar.Children.Add(PendingMentionChip(item));
            }

            MentionBar.Visibility = Visibility.Visible;
        }

        private void RemoveMention_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is MentionItem item))
            {
                return;
            }

            _pendingMentions.RemoveAll(m =>
                m.IsDirectory == item.IsDirectory &&
                string.Equals(m.RelativePath, item.RelativePath, StringComparison.OrdinalIgnoreCase));

            RenderPendingMentions();
            UpdateSendState();

            Input.Focus();
        }

        /// <summary>
        /// 一枚提及芯片：图标 + 名称 + 移除按钮（完整路径放 ToolTip）
        /// </summary>
        private FrameworkElement PendingMentionChip(MentionItem item)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 3, 2),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = item.RelativePath,
            };
            border.SetResourceReference(Border.BackgroundProperty, "Ap.BgHover");
            border.SetResourceReference(Border.BorderBrushProperty, "Ap.Border");

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var glyph = new Icon
            {
                IconName = item.IsDirectory ? "folder" : "file",
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.SetResourceReference(Icon.ForegroundProperty, item.IsDirectory ? "Ap.Teal" : "Ap.Accent");
            row.Children.Add(glyph);

            var name = new TextBlock
            {
                Text = item.Name,
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgMenu");
            row.Children.Add(name);

            // 移除按钮：扁平无边框（悬停不换底，避免在芯片里显得脏）
            var close = new Button
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                Tag = item,
                ToolTip = "移除",
                Content = CloseGlyph(),
            };

            close.Click += RemoveMention_Click;
            row.Children.Add(close);

            border.Child = row;
            return border;
        }

        /// <summary>
        /// 芯片上的「×」图标（颜色走主题资源引用，随 VS 主题刷新）
        /// </summary>
        private static Icon CloseGlyph()
        {
            var glyph = new Icon
            {
                IconName = "close",
                Width = 9,
                Height = 9,
            };
            glyph.SetResourceReference(Icon.ForegroundProperty, "Ap.FgFaint");
            return glyph;
        }
    }
}

