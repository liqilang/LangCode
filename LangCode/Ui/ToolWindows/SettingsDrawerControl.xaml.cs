using LangCode.Chat;
using LangCode.Domain.Catalog;
using LangCode.Domain.Settings;
using LangCode.Mcp;
using LangCode.Persistence;
using LangCode.Workspace;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// 设置抽屉（带「通用 / API / 批准 / MCP / 编辑器 / 自动化 / 能力 / 上下文 / 数据」九个分页）
    /// </summary>
    public partial class SettingsDrawerControl : UserControl
    {
        /// <summary>
        /// 正在按设置回填控件（避免把程序化赋值当成用户修改）
        /// </summary>
        private bool _binding;

        /// <summary>
        /// 宿主持有的「已生效」设置：界面上编辑的是它的草稿副本，
        /// 只有点「保存」时才把草稿灌回它并通知宿主落盘（见 <see cref="SaveSettings"/>）
        /// </summary>
        private AppSettings _live;

        /// <summary>
        /// 是否有改动（只有用户改过才为真；据此决定点返回时是否提示保存）
        /// </summary>
        private bool _dirty;

        /// <summary>
        /// 当前厂商可选模型（用于「显示名 ↔ model id」互转）
        /// </summary>
        private List<ModelInfo> _models = [];

        /// <summary>
        /// 动态生成的「API Key」密文框 
        /// </summary>
        private PasswordBox _apiKeyBox;

        /// <summary>
        /// 动态生成的「API Key」明文镜像框
        /// </summary>
        private TextBox _apiKeyPlain;

        /// <summary>
        /// 动态生成的 Base URL 输入框（Plain / Toggle 两种模式共用）
        /// </summary>
        private TextBox _baseUrlBox;

        /// <summary>
        /// 「使用自定义 Base URL」勾选框（仅 Toggle 模式）
        /// </summary>
        private CheckBox _customBaseUrlToggle;

        /// <summary>
        /// 动态生成的额外字段控件（区域 / 端点 / Azure 版本…）
        /// </summary>
        private readonly List<FrameworkElement> _providerOptionControls = [];

        /// <summary>
        /// 属性名 → <see cref="AppSettings"/> 属性 的缓存（声明式绑定用）
        /// </summary>
        private static readonly Dictionary<string, PropertyInfo> PropertyCache =
            new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        /// <summary>
        /// 上下文窗口档位（token 数）；下拉显示其可读形式
        /// </summary>
        private static readonly List<int> ContextOptions =
        [
            // 这些档位覆盖了目录里各家的实际容量：
            // 32k / 128k / 200k / 256k / 400k / 1.05M(OpenAI 5.4~5.5) / 1M / 2M(Grok 4.20)
            16384, 32768, 65536, 131072, 204800, 262144, 400000, 1048576, 1050000, 2000000,
        ];

        /// <summary>
        /// 当前下拉里真正可选的档位：固定档位 + 当前模型的真实容量
        /// （模型容量不在固定档位里就补一条，见 <see cref="BindContextOptions"/>）
        /// </summary>
        private readonly List<int> _ctxOptions = new List<int>(ContextOptions);

        public SettingsDrawerControl()
        {
            InitializeComponent();

            // 调色板（Ap.*）注入：跟随 VS 主题（深/浅），并支持运行期切换
            Theme.ThemeManager.Attach(this);

            // 「批准」分页的行按类别目录生成一次：新增类别只改 ApprovalCatalog，界面自动跟上
            BuildApprovalRows();

            SelectTab("general");
        }

        /// <summary>
        /// 生成「批准」分页的类别开关（Tag = AppSettings 属性名，回填交给 BindTaggedFields）
        /// </summary>
        private void BuildApprovalRows()
        {
            if (ApprovalList == null)
            {
                return;
            }

            ApprovalList.Children.Clear();
            foreach (var category in ApprovalCatalog.All)
            {
                ApprovalList.Children.Add(
                    LangCode.Ui.Controls.ApprovalRows.Create(this, category, Switch_Click));
            }
        }

        // ==================== 设置搜索 ====================

        /// <summary>
        /// 被搜索过滤隐藏掉的元素（清空搜索时只恢复这些，不动本来就不显示的部分）
        /// </summary>
        private readonly List<UIElement> _hiddenBySearch = [];

        /// <summary>
        /// 按关键字过滤设置项
        /// </summary>
        private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = (SettingsSearch.Text ?? string.Empty).Trim();

            RestoreSearchVisibility();

            if (query.Length == 0)
            {
                return;
            }

            foreach (var panel in SettingPanels())
            {
                FilterPanel(panel, query);
            }
        }

        private IEnumerable<Panel> SettingPanels()
        {
            yield return GeneralPanel;
            yield return ApiPanel;
            yield return ApprovalPanel;
            yield return EditorPanel;
            yield return AutomationPanel;
            yield return CapabilitiesPanel;
            yield return ContextPanel;
            yield return DataPanel;
        }

        /// <summary>
        /// 恢复上一次搜索隐藏掉的元素
        /// </summary>
        private void RestoreSearchVisibility()
        {
            foreach (var element in _hiddenBySearch)
            {
                element.Visibility = Visibility.Visible;
            }

            _hiddenBySearch.Clear();
        }

        private void FilterPanel(Panel root, string query)
        {
            if (root == null)
            {
                return;
            }

            foreach (UIElement child in root.Children)
            {
                if (child is Panel group)
                {
                    foreach (UIElement row in group.Children)
                    {
                        HideIfMiss(row, query);
                    }

                    // 组内一行都没命中 → 整组收起（连标题一起）
                    var anyVisible = group.Children.OfType<UIElement>().Any(c => c.Visibility == Visibility.Visible);
                    if (!anyVisible)
                    {
                        HideIfMiss(group, query);
                    }
                }
                else
                {
                    HideIfMiss(child, query);
                }
            }
        }

        /// <summary>
        /// 未命中且当前可见 → 收起并记账（本来就不显示的元素不动）
        /// </summary>
        private void HideIfMiss(UIElement element, string query)
        {
            if (Matches(element, query) || element.Visibility != Visibility.Visible)
            {
                return;
            }

            element.Visibility = Visibility.Collapsed;
            _hiddenBySearch.Add(element);
        }

        /// <summary>
        /// 元素自身或其后代文字里是否含关键字（大小写不敏感）
        /// </summary>
        private static bool Matches(DependencyObject element, string query)
        {
            return TextOf(element).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 把一棵子树里「用户能看到的文字」拼起来：标签、输入框内容、下拉选中项、按钮文字
        /// </summary>
        private static string TextOf(DependencyObject element)
        {
            var builder = new StringBuilder();

            void Walk(DependencyObject node)
            {
                switch (node)
                {
                    case TextBlock label:
                        builder.Append(label.Text).Append('\n');
                        break;
                    case TextBox box:
                        builder.Append(box.Text).Append('\n');
                        break;
                    case PasswordBox password:
                        builder.Append(password.Password).Append('\n');
                        break;
                    case ComboBox combo:
                        builder.Append(combo.Text).Append('\n');
                        break;
                    case ContentControl content when content.Content is string text:
                        builder.Append(text).Append('\n');
                        break;
                }

                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++)
                {
                    Walk(VisualTreeHelper.GetChild(node, i));
                }
            }

            Walk(element);
            return builder.ToString();
        }

        /// <summary>
        /// 请求关闭（点返回时抛出）
        /// </summary>
        public event EventHandler CloseRequested;

        /// <summary>
        /// 设置被用户改动（宿主据此落盘并刷新界面）
        /// </summary>
        public event EventHandler SettingsChanged;

        /// <summary>
        /// 当前正在编辑的设置对象
        /// </summary>
        public AppSettings Settings { get; private set; }

        /// <summary>
        /// 绑定一份设置并刷新界面（宿主在创建抽屉后调用）
        /// </summary>
        public void Attach(AppSettings settings)
        {
            // 宿主给的是「已生效」的设置：留一份引用用于保存回写，
            // 界面自身编辑草稿副本，做到「改了不保存就不生效」
            _live = settings;
            Settings = settings?.Clone();
            _dirty = false;
            BindUi();
            BuildProfileRows();
        }

        public void Open()
        {
            Visibility = Visibility.Visible;

            // 每次打开都从「已生效」的设置重新克隆一份草稿：
            // 上一轮未保存（或已放弃）的临时改动不会残留到这一次
            Settings = _live?.Clone() ?? Settings;
            _dirty = false;

            // 每次打开都重新回填：模型也可能是在输入框的模型弹层里被改掉的
            BindUi();

            // 档案列表同理：可能在输入框的选模型弹层里刚切过，得刷新「使用中」的标记
            BuildProfileRows();

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

        /// <summary>
        /// 切到指定分页（general / api / approval / mcp / editor / automation / capabilities / context / data）
        /// </summary>
        public void SelectTab(string key)
        {
            if (key == null)
            {
                return;
            }

            foreach (var child in TabsBar.Children)
            {
                if (child is RadioButton tab && (tab.Tag as string) == key)
                {
                    tab.IsChecked = true;
                    break;
                }
            }

            ShowPanel(key);

            // 切页时把当前搜索词重新应用一次：新显示的这一页也应按关键字过滤
            if (SettingsSearch != null && SettingsSearch.Text.Length > 0)
            {
                SettingsSearch_TextChanged(SettingsSearch, null);
            }
        }

        // ============================================================
        // 设置 → 界面
        // ============================================================
        private void BindUi()
        {
            if (Settings == null || !IsInitialized)
            {
                return;
            }

            _binding = true;
            try
            {
                // 提供商：来自模型目录（含 DeepSeek / Moonshot / 智谱 …）
                var providers = new List<string>(ModelCatalog.Providers);
                if (!string.IsNullOrWhiteSpace(Settings.Provider) &&
                    !providers.Contains(Settings.Provider, StringComparer.OrdinalIgnoreCase))
                {
                    providers.Insert(0, Settings.Provider);
                }

                SetProvider.ItemsSource = providers;
                SetProvider.SelectedItem = providers.FirstOrDefault(p =>
                    string.Equals(p, Settings.Provider, StringComparison.OrdinalIgnoreCase));

                BindModelList(Settings.Provider);
                RenderProviderFields();

                // 打开设置时：只有限额还是出厂默认（= 从没配过）才按当前模型带出。
                // 已经存过的值（用户调过的 / 档案里恢复出来的）一律不动 ——
                // 否则打开一次就把这套配置的窗口改写成"模型能力"，
                // 两套配置之间也会互相串（A 的 1M ↔ B 的 128K）
                if (Settings.LimitsAreDefault())
                {
                    Settings.AlignLimitsToModel(Settings.Model);
                }

                UpdateModelInfoLine();

                // 模型高级选项（推理强度 / 详细度）
                SelectAdvanced(SetReasoningEffort, Settings.ReasoningEffort);
                SelectAdvanced(SetVerbosity, Settings.Verbosity);

                BindContextOptions(Settings.ContextWindow);

                // 通用 / 高级两个分页上的开关、下拉、文本框（按 Tag 反射对齐）
                BindTaggedFields();

                // 「配置档案」的名称默认填成当前模型名（空着才填，不覆盖用户已输入的内容）
                if (string.IsNullOrWhiteSpace(SetProfileName.Text))
                {
                    SetProfileName.Text = DefaultProfileName();
                }
            }
            finally
            {
                _binding = false;
            }
        }

        // ============================================================
        // 抽屉骨架
        // ============================================================
        private async void Back_Click(object sender, RoutedEventArgs e)
        {
            // 有未保存的改动先拦一下：避免「以为存过了」其实没存就关了
            if (HasUnsavedChanges())
            {
                var choice = await Controls.InWindowDialog.ShowAsync(
                    this,
                    "有未保存的更改",
                    "是否保存后关闭？\n\n不保存的话，这次改的设置都会丢掉。",
                    new[]
                    {
                        new Controls.InWindowDialog.DialogButton("留在设置页", "cancel", isCancel: true),
                        new Controls.InWindowDialog.DialogButton("放弃更改", "discard"),
                        new Controls.InWindowDialog.DialogButton("保存并关闭", "save", primary: true),
                    }).ConfigureAwait(true);

                if (choice == null || choice == "cancel")
                {
                    return;
                }

                if (choice == "save")
                {
                    SaveSettings();
                }
                else
                {
                    // 放弃：把草稿还原成「已生效」的设置，避免下次打开残留这次的改动
                    Settings = _live?.Clone() ?? Settings;
                }
            }

            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        // ============================================================
        // 保存（草稿 → 已生效）
        // ============================================================

        /// <summary>
        /// 保存并关闭：点一下即写回并收起设置页
        /// </summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (SaveSettings())
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 是否有未保存的改动（由 <see cref="RaiseChanged"/> 打标，回填 / 打开 / 保存时清零）
        /// </summary>
        private bool HasUnsavedChanges() => _dirty;

        /// <summary>
        /// 保存：把草稿灌回「已生效」的设置并通知宿主落盘（点保存才真正生效）
        /// </summary>
        private bool SaveSettings()
        {
            if (Settings == null || _live == null)
            {
                return false;
            }

            _live.CopyFrom(Settings);
            _dirty = false;

            // 重新对齐草稿，后续比较以「已保存」为基线
            Settings = _live.Clone();

            // MCP 服务器列表的改动在这里才真正应用到连接池
            McpManager.Sync(_live.McpServers);

            // 通知宿主：落盘 + 刷新界面（宿主对 _live 做防抖保存）
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radio)
            {
                ShowPanel(radio.Tag as string);
            }
        }

        private void ShowPanel(string key)
        {
            // 初始化阶段（分页尚未创建）直接返回；由构造函数末尾的 SelectTab 兜底
            if (GeneralPanel == null)
            {
                return;
            }

            GeneralPanel.Visibility = key == "general" ? Visibility.Visible : Visibility.Collapsed;
            ApiPanel.Visibility = key == "api" ? Visibility.Visible : Visibility.Collapsed;
            ApprovalPanel.Visibility = key == "approval" ? Visibility.Visible : Visibility.Collapsed;
            EditorPanel.Visibility = key == "editor" ? Visibility.Visible : Visibility.Collapsed;
            AutomationPanel.Visibility = key == "automation" ? Visibility.Visible : Visibility.Collapsed;
            CapabilitiesPanel.Visibility = key == "capabilities" ? Visibility.Visible : Visibility.Collapsed;
            ContextPanel.Visibility = key == "context" ? Visibility.Visible : Visibility.Collapsed;
            DataPanel.Visibility = key == "data" ? Visibility.Visible : Visibility.Collapsed;

            PanelsScroll?.ScrollToTop();
        }


        private void ContextWindow_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_binding || Settings == null)
            {
                return;
            }

            var index = SetCtxWindow.SelectedIndex;
            if (index < 0 || index >= _ctxOptions.Count)
            {
                return;
            }

            Settings.ContextWindow = _ctxOptions[index];
            RaiseChanged();
        }

        /// <summary>
        /// 重排「上下文窗口」下拉并按给定 token 数选中：固定档位 + 该值本身。
        /// 模型容量（例：Baseten 163840、Claude 200000）不在固定档位里时不补一条的话，
        /// 下拉只能选中「最接近的另一个数字」—— 界面显示的和实际生效的就对不上了
        /// </summary>
        private void BindContextOptions(int tokens)
        {
            if (SetCtxWindow == null)
            {
                return;
            }

            _ctxOptions.Clear();
            _ctxOptions.AddRange(ContextOptions);

            if (tokens > 0 && !_ctxOptions.Contains(tokens))
            {
                // 固定档位与模型容量可能"读起来"是同一个数（204800 与 200000 都读作 200K、
                // 1048576 与 1000000 都读作 1M）。这时用模型自己的真实容量顶掉固定档位：
                // 下拉里只留一条同名项，选中后生效的是厂商给的真值
                var label = ModelCatalog.FormatTokens(tokens);
                var clash = _ctxOptions.FindIndex(v =>
                    string.Equals(ModelCatalog.FormatTokens(v), label, StringComparison.Ordinal));

                if (clash >= 0)
                {
                    _ctxOptions[clash] = tokens;
                }
                else
                {
                    _ctxOptions.Add(tokens);
                }

                _ctxOptions.Sort();
            }

            SetCtxWindow.ItemsSource = _ctxOptions.Select(ModelCatalog.FormatTokens).ToList();
            SelectContextWindow(tokens);
        }

        /// <summary>
        /// 把「上下文窗口」下拉选到与给定 token 数最接近的档位
        /// </summary>
        private void SelectContextWindow(int tokens)
        {
            var best = 0;
            var bestDelta = int.MaxValue;

            for (var i = 0; i < _ctxOptions.Count; i++)
            {
                var delta = Math.Abs(_ctxOptions[i] - tokens);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = i;
                }
            }

            SetCtxWindow.SelectedIndex = best;
        }

        private void Range_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (FontSizeVal != null)
            {
                FontSizeVal.Text = ((int)SetFontSize.Value) + "px";
            }

            if (TempVal != null)
            {
                TempVal.Text = SetTemp.Value.ToString("0.00");
            }

            if (TopPVal != null)
            {
                TopPVal.Text = SetTopP.Value.ToString("0.00");
            }

            if (_binding || Settings == null)
            {
                return;
            }

            Settings.FontSize = SetFontSize.Value;
            Settings.Temperature = SetTemp.Value;
            Settings.TopP = SetTopP.Value;
            RaiseChanged();
        }

        // ============================================================
        // 声明式字段绑定（XAML 里 Tag = AppSettings 的属性名）
        // ============================================================
        /// <summary>
        /// 把「带 Tag 的开关 / 下拉 / 文本框」与 <see cref="AppSettings"/> 的同名属性对齐
        /// </summary>
        private void BindTaggedFields()
        {
            foreach (var toggle in Tagged<ToggleButton>(PanelsScroll))
            {
                toggle.IsChecked = IsTrue(ReadOption(toggle.Tag as string));
            }

            foreach (var combo in Tagged<ComboBox>(PanelsScroll))
            {
                SelectOption(combo, ReadOption(combo.Tag as string));
            }

            foreach (var box in Tagged<TextBox>(PanelsScroll))
            {
                box.Text = ReadOption(box.Tag as string) ?? string.Empty;
            }
        }

        /// <summary>
        /// 深度优先收集「带字符串 Tag」的指定类型控件
        /// </summary>
        private static IEnumerable<T> Tagged<T>(DependencyObject root) where T : FrameworkElement
        {
            if (root == null)
            {
                yield break;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);

                if (child is T element && element.Tag is string)
                {
                    yield return element;
                }

                foreach (var nested in Tagged<T>(child))
                {
                    yield return nested;
                }
            }
        }

        private string ReadOption(string propertyName)
        {
            var property = Property(propertyName);
            if (property == null)
            {
                return null;
            }

            var value = property.GetValue(Settings);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 写属性值；类型不匹配（例如把 "abc" 写进 int）时**忽略**，不把设置写坏
        /// </summary>
        private void WriteOption(string propertyName, string text)
        {
            var property = Property(propertyName);
            if (property == null)
            {
                return;
            }

            try
            {
                if (property.PropertyType == typeof(bool))
                {
                    property.SetValue(Settings, IsTrue(text));
                    return;
                }

                if (property.PropertyType == typeof(int))
                {
                    // 只接受正整数：输入框清空或打字中途（"1" → "12"）不会把设置写成 0
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0)
                    {
                        property.SetValue(Settings, number);
                    }

                    return;
                }

                if (property.PropertyType == typeof(string))
                {
                    property.SetValue(Settings, text ?? string.Empty);
                }
            }
            catch (Exception)
            {
                // 反射失败（属性只读等）：忽略 —— 设置页永远不能把界面弄崩
            }
        }

        private static PropertyInfo Property(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            lock (PropertyCache)
            {
                if (PropertyCache.TryGetValue(name, out var cached))
                {
                    return cached;
                }

                var property = typeof(AppSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                PropertyCache[name] = property;
                return property;
            }
        }

        private static bool IsTrue(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.Ordinal);
        }

        private static void SelectOption(ComboBox combo, string text)
        {
            if (combo == null)
            {
                return;
            }

            for (var i = 0; i < combo.Items.Count; i++)
            {
                var content = (combo.Items[i] as ComboBoxItem)?.Content as string ?? combo.Items[i]?.ToString();
                if (string.Equals(content, text, StringComparison.Ordinal))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private static string ComboText(ComboBox combo)
        {
            if (combo?.SelectedItem is ComboBoxItem item)
            {
                return item.Content as string;
            }

            return combo?.SelectedItem?.ToString();
        }

        private void Switch_Click(object sender, RoutedEventArgs e)
        {
            if (_binding || Settings == null)
            {
                return;
            }

            var toggle = sender as ToggleButton;
            WriteOption(toggle?.Tag as string, toggle?.IsChecked == true ? "true" : "false");
            RaiseChanged();
        }

        private void Option_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_binding || Settings == null)
            {
                return;
            }

            WriteOption((sender as FrameworkElement)?.Tag as string, ComboText(sender as ComboBox));
            RaiseChanged();
        }

        private void TextOption_Changed(object sender, TextChangedEventArgs e)
        {
            if (_binding || Settings == null)
            {
                return;
            }

            WriteOption((sender as FrameworkElement)?.Tag as string, (sender as TextBox)?.Text);
            RaiseChanged();
        }

    }
}

