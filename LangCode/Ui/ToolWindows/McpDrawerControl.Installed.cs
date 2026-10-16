using LangCode.Domain.Settings;
using LangCode.Mcp;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// McpDrawerControl 分部：「已安装」分页（服务器卡片：改名 / 改命令 / 启停 / 测试 / 删除 / 导入 / 环境变量）
    /// </summary>
    public partial class McpDrawerControl
    {
        /// <summary>
        /// 按 <see cref="AppSettings.McpServers"/> 重建服务器卡片
        /// </summary>
        private void BuildMcpRows()
        {
            if (McpServerList == null || Settings == null)
            {
                return;
            }

            McpServerList.Children.Clear();

            var servers = Settings.McpServers ?? (Settings.McpServers = []);
            var statuses = McpManager.Snapshot(servers);

            foreach (var server in servers)
            {
                var status = statuses.FirstOrDefault(s => s.Name == server.Name);
                McpServerList.Children.Add(BuildMcpRow(server, status.Status, status.ToolCount));
            }

            if (McpEmptyHint != null)
            {
                McpEmptyHint.Visibility = servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private FrameworkElement BuildMcpRow(McpServerSettings server, string status, int toolCount)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(11, 10, 11, 8),
                Margin = new Thickness(0, 0, 0, 8),
                DataContext = server,
            };
            card.SetResourceReference(Border.BackgroundProperty, "Ap.ControlBg");
            card.SetResourceReference(Border.BorderBrushProperty, "Ap.InputBorder");

            var root = new StackPanel();

            // 名称 + 启用开关
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBox
            {
                Text = server.Name ?? string.Empty,
                FontSize = 12.5,
                Height = 26,
                DataContext = server,
            };
            name.LostFocus += (_, __) =>
            {
                server.Name = name.Text ?? string.Empty;
                PersistMcp();
            };

            var enable = new ToggleButton
            {
                IsChecked = server.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                DataContext = server,
                ToolTip = "启用 / 停用这个服务器",
            };

            var switchStyle = TryFindResource("Switch") as Style;
            if (switchStyle != null)
            {
                enable.Style = switchStyle;
            }

            enable.Click += McpToggle_Click;

            Grid.SetColumn(name, 0);
            Grid.SetColumn(enable, 1);
            head.Children.Add(name);
            head.Children.Add(enable);
            root.Children.Add(head);

            // 命令 + 参数
            var commandRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });

            var command = MonoBox(server.Command, "启动命令：npx / uvx / node …", 26);
            command.TextChanged += (_, __) =>
            {
                server.Command = command.Text ?? string.Empty;
                PersistMcp();
            };

            var arguments = MonoBox(server.Arguments, "参数：-y @modelcontextprotocol/server-filesystem .", 26);
            arguments.TextChanged += (_, __) =>
            {
                server.Arguments = arguments.Text ?? string.Empty;
                PersistMcp();
            };

            Grid.SetColumn(command, 0);
            Grid.SetColumn(arguments, 2);
            commandRow.Children.Add(command);
            commandRow.Children.Add(arguments);
            root.Children.Add(commandRow);

            // 环境变量（仅当这条服务器需要时显示：市场安装会写入占位项）
            if (server.Environment != null && server.Environment.Count > 0)
            {
                root.Children.Add(BuildMcpEnvEditor(server));
            }

            // 状态 + 测试 / 删除
            var footer = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var statusText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(status) ? "未连接" : status + (toolCount > 0 ? " · " + toolCount + " 个工具" : string.Empty),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            statusText.SetResourceReference(TextBlock.ForegroundProperty, StatusKey(status, server.Enabled));

            var actions = new StackPanel { Orientation = Orientation.Horizontal };

            var test = new Button
            {
                Content = "测试连接",
                Style = TryFindResource("BtnBase") as Style,
                Height = 24,
                Padding = new Thickness(9, 0, 9, 0),
                FontSize = 11,
                DataContext = server,
                ToolTip = "启动这个 MCP 服务器并列出它的工具",
            };
            test.Click += McpTest_Click;

            var delete = new Button
            {
                Content = "删除",
                Style = TryFindResource("BtnDanger") as Style,
                Height = 24,
                Padding = new Thickness(9, 0, 9, 0),
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0),
                DataContext = server,
            };
            delete.Click += McpDelete_Click;

            actions.Children.Add(test);
            actions.Children.Add(delete);

            Grid.SetColumn(statusText, 0);
            Grid.SetColumn(actions, 1);
            footer.Children.Add(statusText);
            footer.Children.Add(actions);
            root.Children.Add(footer);

            card.Child = root;
            return card;
        }

        private TextBox MonoBox(string value, string hint, double height)
        {
            return new TextBox
            {
                Text = value ?? string.Empty,
                Height = height,
                FontSize = 11,
                Padding = new Thickness(7, 0, 7, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontFamily = TryFindResource("Ap.FontCode") as FontFamily ?? new FontFamily("Consolas"),
                ToolTip = hint,
            };
        }

        /// <summary>
        /// 环境变量编辑器：每行 KEY=VALUE（失焦写回）。仅当服务器需要环境变量时显示。
        /// </summary>
        private FrameworkElement BuildMcpEnvEditor(McpServerSettings server)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

            var label = new TextBlock
            {
                Text = "环境变量（每行一条 KEY=VALUE；值留空则不注入）",
                FontSize = 10.5,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgDim");
            panel.Children.Add(label);

            var box = new TextBox
            {
                Text = FormatEnvironment(server.Environment),
                AcceptsReturn = true,
                MinHeight = 48,
                MaxHeight = 120,
                FontSize = 11,
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = TryFindResource("Ap.FontCode") as FontFamily ?? new FontFamily("Consolas"),
                ToolTip = "例：\nGITHUB_PERSONAL_ACCESS_TOKEN=ghp_xxx",
            };

            // 失焦写回：不必每敲一字就解析
            box.LostFocus += (_, __) =>
            {
                server.Environment = ParseEnvironment(box.Text, server.Environment);
                PersistMcp();
            };

            panel.Children.Add(box);
            return panel;
        }

        /// <summary>
        /// 把环境变量字典格式化成每行 <c>KEY=VALUE</c>
        /// </summary>
        private static string FormatEnvironment(IDictionary<string, string> environment)
        {
            if (environment == null || environment.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var pair in environment)
            {
                builder.Append(pair.Key).Append('=').Append(pair.Value ?? string.Empty).Append('\n');
            }

            return builder.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// 解析「每行 KEY=VALUE」；文本里没写出来的旧键补回空值（保住市场写入的必填占位）
        /// </summary>
        private static Dictionary<string, string> ParseEnvironment(string text, IDictionary<string, string> previous)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var raw in (text ?? string.Empty).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var index = line.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, index).Trim();
                var value = line.Substring(index + 1).Trim();
                if (key.Length > 0)
                {
                    result[key] = value;
                }
            }

            if (previous != null)
            {
                foreach (var pair in previous)
                {
                    if (!result.ContainsKey(pair.Key))
                    {
                        result[pair.Key] = string.Empty;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 状态文本对应的**调色板键**（而不是取好的画刷）：调用方用 SetResourceReference 挂上去，
        /// 于是主题切换时状态色也会跟着换成新主题的值
        /// </summary>
        private static string StatusKey(string status, bool enabled)
        {
            if (!enabled)
            {
                return "Ap.FgFaint";
            }

            if (status != null && status.StartsWith("已连接", StringComparison.Ordinal))
            {
                return "Ap.Teal";
            }

            return status != null && status.Contains("失败") ? "Ap.Red" : "Ap.FgDim";
        }

        /// <summary>
        /// 服务器列表有改动：立即通知宿主落盘 + 与连接池对齐
        /// </summary>
        private void PersistMcp()
        {
            RaiseMcpChanged();
        }

        private void McpToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is McpServerSettings server)
            {
                server.Enabled = (sender as ToggleButton)?.IsChecked == true;
                PersistMcp();
                BuildMcpRows();
            }
        }

        private void McpDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is McpServerSettings server)
            {
                Settings.McpServers.Remove(server);
                PersistMcp();
                BuildMcpRows();
            }
        }

        private void McpAdd_Click(object sender, RoutedEventArgs e)
        {
            var servers = Settings.McpServers ?? (Settings.McpServers = []);

            servers.Add(new McpServerSettings
            {
                Name = "server-" + (servers.Count + 1),
                Command = "npx",
                Arguments = "-y @modelcontextprotocol/server-filesystem .",
                Enabled = false,
                Status = "unknown",
            });

            PersistMcp();
            BuildMcpRows();
        }

        /// <summary>
        /// 测试连接：真的把 MCP 进程拉起来、握手、列出工具（结果写回卡片状态）
        /// </summary>
        private async void McpTest_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.DataContext is McpServerSettings server))
            {
                return;
            }

            var manager = new McpManager();
            var report = await manager
                .CallAsync(Settings, server.Name, string.Empty, string.Empty, CancellationToken.None)
                .ConfigureAwait(true);

            // 用结果刷新状态（连接成功 / 失败原因都写进卡片）
            var line = report ?? string.Empty;
            server.Status = line.StartsWith("服务器「", StringComparison.Ordinal) && line.Contains("可用工具")
                ? "已连接"
                : "连接失败";

            server.StatusText = line;

            // 状态改用 StatusText 展示（保留完整原因，便于排查）
            UpdateMcpStatus(server, line);

            // 测试连接只更新内存里卡片的状态，不属于「设置改动」，不做落盘
        }

        private void UpdateMcpStatus(McpServerSettings server, string text)
        {
            foreach (var child in McpServerList.Children)
            {
                if (!(child is Border card) || !ReferenceEquals(card.DataContext, server))
                {
                    continue;
                }

                if (card.Child is StackPanel root)
                {
                    foreach (var node in root.Children)
                    {
                        if (node is Grid footer && footer.Children.Count > 0 && footer.Children[0] is TextBlock status)
                        {
                            status.Text = text.Replace("\n", " ");
                            status.SetResourceReference(TextBlock.ForegroundProperty, StatusKey(server.Status, server.Enabled));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 从其他工具的 <c>mcpServers</c> 配置里导入服务器
        /// 结构是 <c>"名字": { "command": "...", "args": [ ... ] }</c>，用正则就地取即可
        /// </summary>
        private async void McpImport_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入 MCP 服务器配置",
                Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var host = sender as FrameworkElement;

            string json;
            try
            {
                json = File.ReadAllText(dialog.FileName, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                await Controls.InWindowDialog.InfoAsync(host, "读取失败", ex.Message, iconName: "problem").ConfigureAwait(true);
                return;
            }

            var imported = McpConfig.Parse(json);
            if (imported.Count == 0)
            {
                await Controls.InWindowDialog.InfoAsync(
                    host,
                    "格式不匹配",
                    "没有解析出服务器期望的格式：\n{\n  \"mcpServers\": {\n    \"名字\": { \"command\": \"npx\", \"args\": [\"-y\", \"@xxx/server\"] }\n  }\n}").ConfigureAwait(true);
                return;
            }

            var servers = Settings.McpServers ?? (Settings.McpServers = []);
            var count = McpConfig.Merge(servers, imported);

            PersistMcp();
            BuildMcpRows();

            await Controls.InWindowDialog.InfoAsync(host, "导入完成", "已导入 " + count + " 个 MCP 服务器", iconName: "check").ConfigureAwait(true);
        }
    }
}
