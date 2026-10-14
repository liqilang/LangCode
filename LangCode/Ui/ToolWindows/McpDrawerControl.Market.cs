using LangCode.Domain.Settings;
using LangCode.Mcp;
using LangCode.Ui.Primitive;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// McpDrawerControl 分部：「市场」分页 
    /// </summary>
    public partial class McpDrawerControl
    {
        /// <summary>
        /// 当前选中的分类筛选（null / 空 = 全部）
        /// </summary>
        private string _marketCategory;

        /// <summary>
        /// 搜索框内容变化：按关键词重建市场列表
        /// </summary>
        private void McpMarketSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            BuildMcpMarketList();
        }

        /// <summary>
        /// 点分类筛选芯片
        /// </summary>
        private void McpMarketCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                _marketCategory = element.Tag as string;
                BuildMcpMarketCategories();
                BuildMcpMarketList();
            }
        }

        /// <summary>
        /// 重建分类筛选芯片（「全部」+ 目录里出现过的分类）
        /// </summary>
        private void BuildMcpMarketCategories()
        {
            if (McpMarketCategories == null)
            {
                return;
            }

            McpMarketCategories.Children.Clear();
            McpMarketCategories.Children.Add(BuildMarketChip("全部", null));

            foreach (var category in McpMarketplace.Categories)
            {
                McpMarketCategories.Children.Add(BuildMarketChip(category, category));
            }
        }

        private FrameworkElement BuildMarketChip(string label, string category)
        {
            var active = string.Equals(_marketCategory ?? string.Empty, category ?? string.Empty, StringComparison.Ordinal);

            var chip = new Button
            {
                Content = label,
                Style = TryFindResource(active ? "BtnPrimary" : "BtnBase") as Style,
                Height = 22,
                Padding = new Thickness(9, 0, 9, 0),
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 6, 6),
                Tag = category,
                VerticalAlignment = VerticalAlignment.Center,
            };

            chip.Click += McpMarketCategory_Click;
            return chip;
        }

        /// <summary>
        /// 按当前搜索词 + 分类重建市场卡片
        /// </summary>
        private void BuildMcpMarketList()
        {
            if (McpMarketList == null || Settings == null)
            {
                return;
            }

            var entries = McpMarketplace.Search(McpMarketSearch?.Text);

            if (!string.IsNullOrEmpty(_marketCategory))
            {
                entries = entries
                    .Where(e => string.Equals(e.Category, _marketCategory, StringComparison.Ordinal))
                    .ToList();
            }

            McpMarketList.Children.Clear();

            if (McpMarketCount != null)
            {
                McpMarketCount.Text = "共 " + entries.Count + " 个";
            }

            if (entries.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "没有匹配的服务器，换个关键词或分类试试",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
                McpMarketList.Children.Add(empty);
                return;
            }

            foreach (var entry in entries)
            {
                McpMarketList.Children.Add(BuildMcpMarketCard(entry));
            }
        }

        /// <summary>
        /// 一张市场卡片：左侧图标头像，中间名称 + 分类 + 说明 + 命令 + 环境变量，右侧安装 / 更新 / 主页。
        /// </summary>
        private FrameworkElement BuildMcpMarketCard(McpMarketEntry entry)
        {
            var installed = McpMarketplace.IsInstalled(Settings.McpServers, entry);
            var installedServer = Settings.McpServers?
                .FirstOrDefault(s => string.Equals(s.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 0, 0, 8),
                DataContext = entry,
            };
            card.SetResourceReference(Border.BackgroundProperty, "Ap.BgElevated");
            card.SetResourceReference(Border.BorderBrushProperty, "Ap.Divider");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 图标
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 文字
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 操作

            // 图标头像（分类图标 → 一眼能看出这是干什么的服务器）
            var avatar = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Top,
            };
            avatar.SetResourceReference(Border.BackgroundProperty, "Ap.BgHover");

            var icon = new Icon
            {
                IconName = IconForCategory(entry.Category),
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(Icon.ForegroundProperty, "Ap.Teal");

            avatar.Child = icon;
            Grid.SetColumn(avatar, 0);
            grid.Children.Add(avatar);

            // 文字区
            var text = new StackPanel
            {
                Margin = new Thickness(10, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            var name = new TextBlock
            {
                Text = entry.DisplayName,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgBright");
            titleRow.Children.Add(name);
            titleRow.Children.Add(BuildBadge(entry.Category, "Ap.BgHover", "Ap.FgDim"));

            if (installed)
            {
                titleRow.Children.Add(BuildBadge("已添加", "Ap.BgHover", "Ap.Teal"));
            }

            text.Children.Add(titleRow);

            var description = new TextBlock
            {
                Text = entry.Description,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            };
            description.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgDim");
            text.Children.Add(description);

            var command = new TextBlock
            {
                Text = entry.CommandLine,
                FontSize = 10.5,
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = TryFindResource("Ap.FontCode") as FontFamily ?? new FontFamily("Consolas"),
                ToolTip = entry.CommandLine,
            };
            command.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
            text.Children.Add(command);

            // 环境变量输入 
            var envBoxes = new List<(McpEnvVar Var, TextBox Box)>();
            if (entry.NeedsEnv)
            {
                foreach (var env in entry.Env)
                {
                    text.Children.Add(BuildContextEnvRow(env, installedServer, envBoxes));
                }
            }

            // 启动命令不在本机：直接给出警告，避免「装了却连不上」还看不出原因
            if (!McpMarketplace.IsCommandAvailable(entry.Command))
            {
                text.Children.Add(BuildWarn(
                    "⚠ 本机未检测到「" + entry.Command + "」，需先安装（见主页 / 条目说明）", "Ap.Red"));
            }

            // 已安装但必填变量还没填
            if (installed && entry.NeedsEnv && !McpMarketplace.HasRequiredEnv(Settings.McpServers, entry))
            {
                text.Children.Add(BuildWarn("⚠ 还有必填的环境变量没填，补上后再「测试连接」", "Ap.Red"));
            }

            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            // 操作区：安装 / 更新 + （可选）主页
            var actions = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 0, 0, 0),
            };
            actions.Children.Add(BuildMarketActionButton(entry, installed, envBoxes));

            if (!string.IsNullOrWhiteSpace(entry.Homepage))
            {
                var home = new Button
                {
                    Content = "主页",
                    Style = TryFindResource("BtnBase") as Style,
                    Height = 22,
                    Padding = new Thickness(8, 0, 8, 0),
                    FontSize = 10.5,
                    Margin = new Thickness(0, 6, 0, 0),
                    DataContext = entry,
                    ToolTip = entry.Homepage,
                };
                home.Click += McpMarketHome_Click;
                actions.Children.Add(home);
            }

            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);

            card.Child = grid;
            return card;
        }

        /// <summary>
        /// 一行环境变量输入：变量名 + 说明，下面一个输入框
        /// </summary>
        private FrameworkElement BuildContextEnvRow(
            McpEnvVar env,
            McpServerSettings installedServer,
            IList<(McpEnvVar Var, TextBox Box)> collector)
        {
            var row = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };

            var label = new TextBlock
            {
                Text = env.Key + (env.Required ? " *" : string.Empty),
                FontSize = 10,
                FontFamily = TryFindResource("Ap.FontCode") as FontFamily ?? new FontFamily("Consolas"),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgDim");

            var hint = new TextBlock
            {
                Text = env.Description,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 0, 0, 0),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");

            var labelRow = new StackPanel { Orientation = Orientation.Horizontal };
            labelRow.Children.Add(label);
            labelRow.Children.Add(hint);
            row.Children.Add(labelRow);

            var existing = string.Empty;
            installedServer?.Environment?.TryGetValue(env.Key, out existing);

            var box = new TextBox
            {
                Text = existing ?? string.Empty,
                Height = 24,
                FontSize = 11,
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(0, 2, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontFamily = TryFindResource("Ap.FontCode") as FontFamily ?? new FontFamily("Consolas"),
                ToolTip = env.Description,
            };
            row.Children.Add(box);

            collector.Add((env, box));
            return row;
        }

        /// <summary>
        /// 安装 / 更新按钮
        /// </summary>
        private Button BuildMarketActionButton(
            McpMarketEntry entry,
            bool installed,
            IList<(McpEnvVar Var, TextBox Box)> envBoxes)
        {
            var button = new Button
            {
                Height = 26,
                MinWidth = 54,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11,
                DataContext = entry,
            };

            if (installed)
            {
                button.Content = "更新";
                button.Style = TryFindResource("BtnBase") as Style;
                button.ToolTip = "用市场里的推荐配置覆盖命令 / 参数，并写回你填的环境变量";
            }
            else
            {
                button.Content = "安装";
                button.Style = TryFindResource("BtnPrimary") as Style;
                button.ToolTip = "把这条服务器加入「已连接的服务器」，立即生效";
            }

            button.Click += (_, __) => InstallFromMarket(entry, envBoxes);
            return button;
        }

        /// <summary>
        /// 小徽标（分类 / 已添加）
        /// </summary>
        private FrameworkElement BuildBadge(string label, string backgroundKey, string foregroundKey)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.SetResourceReference(Border.BackgroundProperty, backgroundKey);

            var text = new TextBlock
            {
                Text = label ?? string.Empty,
                FontSize = 10,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);

            badge.Child = text;
            return badge;
        }

        /// <summary>
        /// 一行提示文本
        /// </summary>
        private FrameworkElement BuildWarn(string message, string foregroundKey)
        {
            var warn = new TextBlock
            {
                Text = message,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            };
            warn.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
            return warn;
        }

        /// <summary>
        /// 分类 → 图标名（与 <c>Ui/Assets/Icons</c> 下的 svg 对应）
        /// </summary>
        private static string IconForCategory(string category)
        {
            switch (category)
            {
                case "文件与代码":
                    return "code";
                case "搜索与 Web":
                    return "search";
                case "浏览器自动化":
                    return "external";
                case "数据库":
                    return "server";
                case "推理与记忆":
                    return "zap";
                case "协作与办公":
                    return "at";
                case "设计与监控":
                    return "sliders";
                case "开发工具":
                    return "terminal";
                default:
                    return "server";
            }
        }

        /// <summary>
        /// 安装 / 更新一个市场条目（含把卡片上填的环境变量写回去）
        /// </summary>
        private void InstallFromMarket(McpMarketEntry entry, IList<(McpEnvVar Var, TextBox Box)> envBoxes)
        {
            if (entry == null)
            {
                return;
            }

            var servers = Settings.McpServers ?? (Settings.McpServers = []);
            var added = McpMarketplace.Install(servers, entry);

            // 把卡片上填的环境变量写进对应服务器
            var server = servers.FirstOrDefault(s =>
                string.Equals(s.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

            if (server != null && envBoxes != null)
            {
                server.Environment ??= [];

                foreach (var pair in envBoxes)
                {
                    if (pair.Var == null || string.IsNullOrWhiteSpace(pair.Var.Key))
                    {
                        continue;
                    }

                    server.Environment[pair.Var.Key] = pair.Box?.Text?.Trim() ?? string.Empty;
                }
            }

            PersistMcp();
            BuildMcpRows();

            // 市场卡片的「已添加 / 环境变量」状态要跟着变
            BuildMcpMarketList();

            var hint = added
                ? "已添加「" + entry.DisplayName + "」到服务器列表，已生效"
                : "已更新「" + entry.DisplayName + "」的配置，已生效";

            if (!McpMarketplace.IsCommandAvailable(entry.Command))
            {
                hint += "。注意：本机 PATH 里没找到「" + entry.Command + "」，请先安装并重启 VS（否则连接会失败）";
            }
            else if (entry.NeedsEnv && !McpMarketplace.HasRequiredEnv(servers, entry))
            {
                hint += "。提示：还有必填的环境变量没填，补上后再点「测试连接」";
            }

            ShowMarketHint(hint);
        }

        /// <summary>
        /// 打开条目主页（浏览器）
        /// </summary>
        private void McpMarketHome_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.DataContext is McpMarketEntry entry) ||
                string.IsNullOrWhiteSpace(entry.Homepage))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(entry.Homepage) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowMarketHint("打开主页失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 市场面板底部的一行提示
        /// </summary>
        private void ShowMarketHint(string text)
        {
            if (McpMarketHint == null)
            {
                return;
            }

            McpMarketHint.Text = text ?? string.Empty;
            McpMarketHint.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
