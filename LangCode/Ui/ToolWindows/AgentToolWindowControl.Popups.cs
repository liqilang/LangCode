using LangCode.Domain.Catalog;
using LangCode.Persistence;
using LangCode.Workspace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：弹层（模型 / 档案 / 提及 / 自动批准）类定义与字段见 AgentToolWindowControl.xaml.cs
    /// </summary>
    public partial class AgentToolWindowControl
    {
        // ============================================================
        // 自动批准开关
        // ============================================================
        /// <summary>
        /// 输入框里的「Auto」= 自动应用文件修改 
        /// </summary>
        /// <summary>
        /// 规划模式开关 ：打开后只调研与出计划，不改任何文件
        /// </summary>
        private void PlanChip_Click(object sender, RoutedEventArgs e)
        {
            SwitchMode(plan: true);
        }

        /// <summary>
        /// 切到「执行」：按计划动手改文件
        /// </summary>
        private void ActChip_Click(object sender, RoutedEventArgs e)
        {
            SwitchMode(plan: false);
        }

        /// <summary>
        /// 切换「计划 / 执行」两个模式（两个芯片互斥）
        /// </summary>
        private void SwitchMode(bool plan)
        {
            if (_settings.PlanMode == plan)
            {
                // 点的是已经选中的那个：保持选中态即可，不必打扰用户
                ApplySettingsToUi();
                return;
            }

            _settings.PlanMode = plan;
            SettingsStore.Save(_settings);
            ApplySettingsToUi();

            AddNote(plan
                ? "已切换到「计划」：我只做调研并给出分步计划，不会修改任何文件确认后点「执行」开始动手"
                : "已切换到「执行」：现在可以按计划改动文件了");
        }

        private void AutoChip_Click(object sender, RoutedEventArgs e)
        {
            // 点 Auto 不再直接改设置，而是弹出类别清单让用户挑（与「选模型」同样的交互）
            var open = !AutoPopup.IsOpen;
            ClosePopups();

            if (open)
            {
                // 打开时把当前值刷一遍（可能刚在设置页改过）
                RefreshAutoPopup();
                AutoPopup.IsOpen = true;
                AutoChip.IsChecked = true;
            }
            else
            {
                AutoChip.IsChecked = ApprovalCatalog.AllWiredApproved(_settings);
                AutoChipText.Text = ApprovalCatalog.DescribeAutoApproval(_settings);
            }
        }

        /// <summary>
        /// 弹层里的类别开关被点：写进设置、落盘、并同步 Auto 按钮的高亮
        /// </summary>
        private void AutoToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.DataContext is ApprovalCategory category))
            {
                return;
            }

            ApprovalCatalog.Set(_settings, category, (sender as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true);

            AutoChip.IsChecked = ApprovalCatalog.AllWiredApproved(_settings);

            // 文案也要跟着变：芯片上直接写「N 项批准」，不用点开弹层才知道自己放开了什么
            AutoChipText.Text = ApprovalCatalog.DescribeAutoApproval(_settings);

            SettingsStore.Save(_settings);
        }

        /// <summary>
        /// 把弹层里的开关刷成设置里的当前值
        /// </summary>
        private void RefreshAutoPopup()
        {
            foreach (var child in AutoList.Children)
            {
                if (child is System.Windows.Controls.Panel panel)
                {
                    foreach (var nested in panel.Children)
                    {
                        if (nested is System.Windows.Controls.Primitives.ToggleButton toggle &&
                            toggle.DataContext is ApprovalCategory category)
                        {
                            toggle.IsChecked = ApprovalCatalog.IsOn(_settings, category);
                        }
                    }
                }
            }
        }

        // ============================================================
        // 模型 / 提及 弹层
        // ============================================================
        private void ModelBtn_Click(object sender, RoutedEventArgs e)
        {
            var open = !ModelPopup.IsOpen;
            ClosePopups();

            if (open)
            {
                // 先把弹层打开 —— 视觉上立刻响应；内容放到后台优先级再填。
                // 列表可能上百条且不做虚拟化，同步构建 + 布局会让「点下去半天没反应」。
                ModelPopup.IsOpen = true;
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        // 重建时按「有无档案」切标题与内置模型表的显隐
                        BuildProfileList();

                        // 只列「设置里已配置好」的模型；拉取 / 新增模型一律在设置页完成
                        RefreshModelList();
                    }),
                    DispatcherPriority.Background);
            }
            else
            {
                ModelPopup.IsOpen = false;
            }
        }

        /// <summary>
        /// 填充弹层顶部的「配置档案」列表
        /// </summary>
        private void BuildProfileList()
        {
            if (ProfileList == null || ProfileSection == null)
            {
                return;
            }

            ProfileList.Items.Clear();

            var profiles = (_settings.NamedProfiles ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            // 有「配置好的」命名档案时：弹层只列档案，内置模型表收起；
            // 一个档案都没有（首次使用）才回退到内置模型表，免得没得选
            var hasProfiles = profiles.Count > 0;
            ProfileSection.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;

            var host = (LogicalTreeHelper.GetParent(ProfileSection) as Panel)
                ?? (VisualTreeHelper.GetParent(ProfileSection) as Panel);
            if (host != null)
            {
                foreach (var child in host.Children)
                {
                    if (child is TextBlock title && (title.Text == "选择模型" || title.Text == "选择配置"))
                    {
                        title.Text = hasProfiles ? "选择配置" : "选择模型";
                    }
                    else if (child is ScrollViewer catalog)
                    {
                        catalog.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
                    }
                }
            }

            if (!hasProfiles)
            {
                return;
            }

            foreach (var profile in profiles)
            {
                var active = string.Equals(profile.Name, _settings.ActiveProfileName, StringComparison.OrdinalIgnoreCase);

                var button = new Button
                {
                    Style = TryFindResource("MenuItemButton") as Style,
                    Tag = profile.Name,
                    ToolTip = active ? "当前正在使用这套配置" : "点击切换到这套配置",
                };

                button.Click += ModelProfile_Click;

                var row = new StackPanel { Orientation = Orientation.Horizontal };

                var glyph = new Ui.Primitive.Icon
                {
                    IconName = active ? "check" : "server",
                    Width = 12,
                    Height = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                glyph.SetResourceReference(Ui.Primitive.Icon.ForegroundProperty, active ? "Ap.Accent" : "Ap.IconDim");
                row.Children.Add(glyph);

                // 只显示自定义的名称，不带厂商 / 模型等细项
                var profileName = new TextBlock
                {
                    Text = profile.Name,
                    Margin = new Thickness(8, 0, 0, 0),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                profileName.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgMenu");
                row.Children.Add(profileName);

                button.Content = row;
                ProfileList.Items.Add(button);
            }
        }

        /// <summary>
        /// 点配置档案：整套切过去（厂商 / Base URL / Key / 模型一起换）
        /// </summary>
        private void ModelProfile_Click(object sender, RoutedEventArgs e)
        {
            var name = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrWhiteSpace(name) || !_settings.ApplyNamedProfile(name))
            {
                return;
            }

            _session.CurrentModel = _settings.Model;

            // 限额随档案一起恢复（ApplyNamedProfile 已处理：档案里存过就用档案的，
            // 老档案才按模型能力带出）—— 这里不要再按模型对齐一次，否则会把档案里的值改掉
            _session.ContextWindow = _settings.ContextWindow;
            SettingsStore.Save(_settings);

            UpdateTokens();
            ApplySettingsToUi();

            ModelPopup.IsOpen = false;
        }

        /// <summary>
        /// 重绑「选择模型」列表：<b>只列设置里已配置好的厂商的模型</b>
        /// （当前厂商 + 已保存过档案的厂商 + 命名档案里的厂商）。
        /// 这里只负责「挑一个已配置好的模型」，拉取 / 新增模型请到设置页完成。
        /// 万一都匹配不上（例如自定义网关厂商不在目录里），退回完整目录，保证至少有得选。
        /// </summary>
        /// <summary>
        /// 最近一次构建出来的弹层分组（供点击时反查模型属于哪个厂商，避免再去查模型目录）
        /// </summary>
        private List<ModelGroup> _modelListGroups = [];

        /// <summary>
        /// 重绑「选择模型」列表：<b>只列设置里已经配置好的模型</b>（当前选择 + 各档案里的默认模型）。
        ///
        /// 刻意<b>不读模型目录</b>：输入框旁的选择器只负责「在已配置的模型之间切换」，
        /// 没有任何配置就显示空。要浏览 / 新增模型请到设置页（那里才读目录）。
        /// 这样弹层既不读盘、也不涉及目录锁，天然不会被几百条模型拖慢。
        /// </summary>
        private void RefreshModelList()
        {
            if (ModelList == null || _settings == null)
            {
                return;
            }

            var groups = new List<ModelGroup>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 当前生效的厂商 + 模型
            AppendConfiguredModel(groups, seen, _settings.Provider, _settings.Model);

            // 2) 各厂商档案（切过厂商就会在这里留一份配置）
            foreach (var profile in _settings.Profiles ?? [])
            {
                AppendConfiguredModel(groups, seen, profile.Provider, profile.DefaultModel);
            }

            // 3) 用户命名的多套配置
            foreach (var named in _settings.NamedProfiles ?? [])
            {
                AppendConfiguredModel(groups, seen, named.Provider, named.Model);
            }

            _modelListGroups = groups;
            ModelList.ItemsSource = groups;

            Chat.ChatDiagnostics.Log("模型弹层列表（仅已配置）：分组 " + groups.Count
                + "，条目 " + groups.Sum(g => g.Models.Count));
        }

        /// <summary>
        /// 往弹层列表追加「某厂商的一个已配置模型」；同厂商归到同一个分组，重复项跳过
        /// </summary>
        private static void AppendConfiguredModel(
            List<ModelGroup> groups, HashSet<string> seen, string provider, string model)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            {
                return;
            }

            var providerKey = provider.Trim();
            var modelKey = model.Trim();
            if (!seen.Add(providerKey + "\u0000" + modelKey))
            {
                return;
            }

            // 只在内存里已有目录时取友好名 / 颜色（TryFind 不触发任何加载），拿不到就用 id + 中性色
            var info = ModelCatalog.TryFind(modelKey);

            var existing = groups.FirstOrDefault(g =>
                string.Equals(g.Provider, providerKey, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new ModelGroup(providerKey, providerKey, new List<ModelInfo>());
                groups.Add(existing);
            }

            existing.Models.Add(new ModelInfo(
                modelKey,
                info?.DisplayName ?? modelKey,
                providerKey,
                info?.ColorHex ?? "#4EC9B0",
                string.Empty));
        }

        private void ModelItem_Click(object sender, RoutedEventArgs e)
        {
            // Tag 就是 ModelCatalog 里的模型 id（即发给 API 的 model 值）
            var modelId = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            if (modelId.Length == 0)
            {
                return;
            }

            // 切到该模型所属厂商：会把旧厂商的 Key / Base URL 存回它的档案，
            // 再装入新厂商自己的配置（这样多厂商配置互不覆盖）。
            // 厂商信息直接从弹层列表反查 —— 不去查模型目录（那会触发目录加载）
            var provider = _modelListGroups?
                .FirstOrDefault(g => g.Models.Any(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase)))
                ?.Provider;

            // 点的是当前这个模型时不改限额（等价于"再看一眼"），只有真的换了模型才按模型能力带出
            var modelChanged = !string.Equals(modelId, _settings.Model, StringComparison.OrdinalIgnoreCase);

            _settings.SwitchProvider(string.IsNullOrWhiteSpace(provider) ? _settings.Provider : provider, modelId);

            // 选了具体模型 = 离开命名档案：档案代表「一整套配置」，
            // 只换模型就不再等同于原来那套，标记留着反而误导
            _settings.ActiveProfileName = string.Empty;

            _session.CurrentModel = _settings.Model;

            // 上下文窗口 / 最大输出跟随模型真实能力（令牌条分母与单次输出上限一起带出）
            if (modelChanged)
            {
                _settings.AlignLimitsToModel(modelId);
            }

            _session.ContextWindow = _settings.ContextWindow;
            UpdateTokens();
            SettingsStore.Save(_settings);
            ApplySettingsToUi();

            ModelPopup.IsOpen = false;
        }

        private void MentionBtn_Click(object sender, RoutedEventArgs e)
        {
            // 重扫 @ 索引时会读取 VS 工作区信息，必须处于 UI 线程
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var open = !MentionPopup.IsOpen;
            ClosePopups();

            if (open)
            {
                // 点 @ 按钮：先按当前打开的项目重扫一次文件 / 文件夹，再从分类根菜单开始
                RefreshMentionIndex(forceRescan: true);
                _mentionView = MentionView.Root;
                RefreshMentions(string.Empty);
                MentionPopup.IsOpen = true;
                MentionBtn.IsChecked = true;
            }
        }

        private void MentionItem_Click(object sender, RoutedEventArgs e)
        {
            // 上下文芯片可能要去读 DTE（@selection），统一在这里声明 UI 线程
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var element = sender as FrameworkElement;

            // 文件 / 目录提及：Tag 是 MentionItem 本体 → 挂成一枚「图标 + 名称」的芯片，
            // 不再往输入框塞一长串路径
            if (element?.Tag is MentionItem mention)
            {
                AddMention(mention);
                MentionPopup.IsOpen = false;
                MentionBtn.IsChecked = false;
                Input.Focus();
                return;
            }

            var tag = element?.Tag as string ?? string.Empty;
            if (tag.Length == 0)
            {
                return;
            }

            // 分类行 / 返回行：只切换视图（根菜单 ⇄ 全部文件夹 / 全部文件），不插入文本、不关弹层
            if (tag.StartsWith(MentionViewTagPrefix, StringComparison.Ordinal))
            {
                var view = tag.Substring(MentionViewTagPrefix.Length);

                // 延后到下一次消息循环再重刷：此刻正在处理这颗按钮的 Click，
                // 立即 Clear() 会把它从可视树上摘掉
                Dispatcher.BeginInvoke(
                    new Action(() => SwitchMentionView(view)),
                    DispatcherPriority.Background);
                return;
            }

            // 斜杠命令：插入提示词 / 执行动作（两者都要先抹掉用户已经敲下的 /query）
            if (tag.StartsWith(SlashInsertPrefix, StringComparison.Ordinal))
            {
                InsertSlashCommand(tag.Substring(SlashInsertPrefix.Length));
            }
            else if (tag.StartsWith(SlashActionPrefix, StringComparison.Ordinal))
            {
                RunSlashAction(tag.Substring(SlashActionPrefix.Length));
            }

            // 上下文芯片：现算文本再插入（多行也照插，输入框本来就是多行的）
            else if (tag.StartsWith(ContextTagPrefix, StringComparison.Ordinal))
            {
                var key = tag.Substring(ContextTagPrefix.Length);

                // @web 要发网络请求，走异步路径（其余芯片都是同步现算）
                if (string.Equals(key, "web", StringComparison.OrdinalIgnoreCase))
                {
                    BeginWebContext();
                }
                else
                {
                    var context = BuildContextText(key);
                    InsertAtCursor(context.Length == 0 ? string.Empty : "\n" + context + "\n");
                }
            }
            else
            {
                InsertAtCursor(tag);
            }

            MentionPopup.IsOpen = false;
            MentionBtn.IsChecked = false;
            Input.Focus();
        }

        /// <summary>
        /// 切换 @ 提及弹层的视图（根菜单 / 全部文件夹 / 全部文件）并用当前筛词重刷
        /// </summary>
        private void SwitchMentionView(string view)
        {
            _mentionView = string.Equals(view, "folders", StringComparison.OrdinalIgnoreCase) ? MentionView.Folders
                : string.Equals(view, "files", StringComparison.OrdinalIgnoreCase) ? MentionView.Files
                : MentionView.Root;

            RefreshMentions(MentionQuery() ?? string.Empty);
        }

        private void ClosePopups()
        {
            ModelPopup.IsOpen = false;
            AutoPopup.IsOpen = false;
            MentionPopup.IsOpen = false;
            MentionBtn.IsChecked = false;
        }

    }
}

