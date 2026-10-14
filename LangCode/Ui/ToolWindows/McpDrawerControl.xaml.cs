using LangCode.Domain.Settings;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// MCP 抽屉：独立的 MCP 服务器视图 
    /// </summary>  
    public partial class McpDrawerControl : UserControl
    {
        /// <summary>
        /// 已生效的设置（宿主注入；本抽屉直接改它，不再走草稿 / 保存按钮）
        /// </summary>
        public AppSettings Settings { get; private set; }

        /// <summary>
        /// 请求关闭（点返回）
        /// </summary>
        public event EventHandler CloseRequested;

        /// <summary>
        /// MCP 配置有改动（宿主据此落盘 + 与连接池对齐）
        /// </summary>
        public event EventHandler SettingsChanged;

        public McpDrawerControl()
        {
            InitializeComponent();

            // 调色板（Ap.*）注入：跟随 VS 主题（深/浅），并支持运行期切换
            Theme.ThemeManager.Attach(this);

            SelectTab("installed");
        }

        /// <summary>
        /// 绑定一份设置并刷新界面（宿主在创建抽屉后调用）
        /// </summary>
        public void Attach(AppSettings settings)
        {
            Settings = settings;
            BuildGlobalOptions();
            BuildMcpRows();
        }

        public void Open()
        {
            Visibility = Visibility.Visible;

            // 每次打开都重建：服务器可能在别处被改过（市场安装 / 导入 / 测试连接改了状态）
            BuildGlobalOptions();
            BuildMcpRows();
            BuildMcpMarketCategories();
            BuildMcpMarketList();

            var distance = Math.Max(ActualWidth, 420);
            Slide.BeginAnimation(TranslateTransform.XProperty, null);
            Slide.X = distance;
            Slide.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }

        public void Close()
        {
            if (Visibility != Visibility.Visible)
            {
                return;
            }

            var animation = new DoubleAnimation(Math.Max(ActualWidth, 420), TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            animation.Completed += (_, __) => Visibility = Visibility.Collapsed;
            Slide.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radio)
            {
                SelectTab(radio.Tag as string);
            }
        }

        /// <summary>
        /// 切到指定分页（installed / market）
        /// </summary>
        public void SelectTab(string key)
        {
            key ??= "installed";

            // 注意：TabInstalled 在 XAML 里带 IsChecked="True"，它的 Checked 事件会在
            // InitializeComponent 解析过程中就触发 —— 此时后面声明的 MarketPanel 等还没创建。
            // 因此这里必须逐个判空（与 SettingsDrawer.ShowPanel 的处理一致）。
            if (TabInstalled != null)
            {
                TabInstalled.IsChecked = key == "installed";
            }

            if (TabMarket != null)
            {
                TabMarket.IsChecked = key == "market";
            }

            if (InstalledPanel != null)
            {
                InstalledPanel.Visibility = key == "market" ? Visibility.Collapsed : Visibility.Visible;
            }

            if (MarketPanel != null)
            {
                MarketPanel.Visibility = key == "market" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (key == "market")
            {
                BuildMcpMarketCategories();
                BuildMcpMarketList();
            }
        }

        /// <summary>
        /// 「浏览市场」快捷键：切到市场分页
        /// </summary>
        private void McpMarketShortcut_Click(object sender, RoutedEventArgs e)
        {
            SelectTab("market");
        }

        /// <summary>
        /// 通知宿主：MCP 配置改了
        /// </summary>
        private void RaiseMcpChanged()
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        // ============================================================
        // 全局选项（启动时自动连接 / 工具调用需确认 / 记录日志）
        // ============================================================
        private void BuildGlobalOptions()
        {
            if (McpGlobalOptions == null || Settings == null)
            {
                return;
            }

            McpGlobalOptions.Children.Clear();
            McpGlobalOptions.Children.Add(BuildOptionRow(
                "启动时自动连接", "打开 LangCode 时自动连接所有已启用的 MCP 服务器",
                () => Settings.AutoConnectMcp, value => Settings.AutoConnectMcp = value));
            McpGlobalOptions.Children.Add(BuildOptionRow(
                "工具调用需确认", "每次调用 MCP 工具前弹出确认提示",
                () => Settings.ConfirmMcpTools, value => Settings.ConfirmMcpTools = value));
            McpGlobalOptions.Children.Add(BuildOptionRow(
                "记录工具调用日志", "在输出面板中保留完整的 MCP 通信记录",
                () => Settings.LogMcpCalls, value => Settings.LogMcpCalls = value));
        }

        private FrameworkElement BuildOptionRow(string name, string description, Func<bool> get, Action<bool> set)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };

            var title = new TextBlock { Text = name, FontSize = 12 };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgBright");

            var desc = new TextBlock { Text = description, FontSize = 10.5, TextWrapping = TextWrapping.Wrap };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgDim");

            text.Children.Add(title);
            text.Children.Add(desc);

            var toggle = new ToggleButton
            {
                IsChecked = get(),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var style = TryFindResource("Switch") as Style;
            if (style != null)
            {
                toggle.Style = style;
            }

            toggle.Click += (_, __) =>
            {
                set(toggle.IsChecked == true);
                RaiseMcpChanged();
            };

            Grid.SetColumn(text, 0);
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(text);
            grid.Children.Add(toggle);
            return grid;
        }
    }
}
