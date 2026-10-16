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
    /// SettingsDrawerControl 分部：模型信息 / 命名配置档案 / 厂商与模型切换事件 / 高级选项
    /// </summary>
    public partial class SettingsDrawerControl
    {
        /// <summary>
        /// 模型信息行（上下文 / 最大输出 / 单价 / 能力） 
        /// </summary>
        private void UpdateModelInfoLine()
        {
            if (ModelInfoLine == null || Settings == null)
            {
                return;
            }

            var info = ModelCatalog.Find(Settings.Model);
            if (info == null)
            {
                ModelInfoLine.Text = string.Empty;
                return;
            }

            var parts = new List<string>();
            if (info.ContextTokens > 0)
            {
                parts.Add("上下文 " + ModelCatalog.FormatTokens(info.ContextTokens));
            }

            if (info.MaxOutputTokens > 0)
            {
                parts.Add("最大输出 " + info.MaxOutputTokens);
            }

            if (info.HasPricing)
            {
                parts.Add("$" + info.InputPricePerMillion.ToString("0.##", CultureInfo.InvariantCulture)
                    + " / $" + info.OutputPricePerMillion.ToString("0.##", CultureInfo.InvariantCulture) + " 每百万 token");
            }

            if (!string.IsNullOrWhiteSpace(info.CapabilityLabel))
            {
                parts.Add(info.CapabilityLabel);
            }

            ModelInfoLine.Text = string.Join(" · ", parts);
        }

        /// <summary>
        /// 切厂商时：当前模型若对新厂商非法，则重置为该厂商的默认（首个）模型
        /// </summary>
        private void ResetModelForProvider(string provider)
        {
            if (Settings == null)
            {
                return;
            }

            var models = ModelCatalog.ForProvider(provider);
            if (models.Count == 0)
            {
                return;
            }

            if (models.Any(m => string.Equals(m.Id, Settings.Model, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Settings.Model = models[0].Id;
            Settings.StoreActiveProfile();

            // 换厂商顺带换了默认模型：限额也跟着这个模型走
            Settings.AlignLimitsToModel(Settings.Model);
        }

        /// <summary>
        /// 有改动：这里不再即时生效，真正写回在 <see cref="SaveSettings"/>（点保存才落盘）
        /// </summary>
        private void RaiseChanged()
        {
            if (_binding)
            {
                return;
            }

            _dirty = true;
        }

        // ============================================================
        // 配置档案（用户命名的多套连接）
        // ============================================================

        /// <summary>
        /// 按 Settings.NamedProfiles 重建档案卡片
        /// </summary>
        private void BuildProfileRows()
        {
            if (ProfileRows == null)
            {
                return;
            }

            ProfileRows.Children.Clear();

            var profiles = (Settings?.NamedProfiles ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            ProfileEmptyHint.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 「名称」下拉：已有档案名 + 可自由输入 
            if (SetProfileName != null)
            {
                SetProfileName.ItemsSource = profiles.Select(p => p.Name).ToList();
            }

            foreach (var profile in profiles)
            {
                ProfileRows.Children.Add(BuildProfileRow(profile));
            }
        }

        private FrameworkElement BuildProfileRow(NamedProfile profile)
        {
            var active = string.Equals(profile.Name, Settings?.ActiveProfileName, StringComparison.OrdinalIgnoreCase);

            // 整张卡就是「选中应用」：不再单独放「应用」按钮，点一下即成当前配置
            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 7, 6, 7),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = profile.Name,
                ToolTip = active ? "当前正在使用这套配置" : "点击切换到这套配置（厂商 / Base URL / Key / 模型一起换）",
            };

            // 常态 / 悬停都用资源引用，主题切换时自动跟随
            border.SetResourceReference(Border.BackgroundProperty, "Ap.BgCard");
            border.SetResourceReference(Border.BorderBrushProperty, "Ap.ToolBorder");

            border.MouseLeftButtonUp += ProfileApply_Click;
            border.MouseEnter += (_, __) => border.SetResourceReference(Border.BackgroundProperty, "Ap.BgHover");
            border.MouseLeave += (_, __) => border.SetResourceReference(Border.BackgroundProperty, "Ap.BgCard");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (active)
            {
                var check = new Ui.Primitive.Icon
                {
                    IconName = "check",
                    Width = 12,
                    Height = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                check.SetResourceReference(Ui.Primitive.Icon.ForegroundProperty, "Ap.Accent");
                Grid.SetColumn(check, 0);
                grid.Children.Add(check);
            }

            var info = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(active ? 8 : 0, 0, 8, 0),
            };

            var nameText = new TextBlock
            {
                Text = active ? profile.Name + "（使用中）" : profile.Name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgStrong");
            info.Children.Add(nameText);

            var summaryText = new TextBlock
            {
                Text = profile.Summary,
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0),
            };
            summaryText.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
            info.Children.Add(summaryText);

            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            // 删除：改成图标按钮（垃圾桶）
            var remove = new Button
            {
                Style = TryFindResource("IconButton") as Style,
                Width = 24,
                Height = 24,
                Tag = profile.Name,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "删除这套档案（只删记录，不动当前正在使用的配置）",
                Content = new Ui.Primitive.Icon { IconName = "trash", Width = 13, Height = 13 },
            };

            remove.Click += ProfileDelete_Click;
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);

            border.Child = grid;
            return border;
        }

        /// <summary>
        /// 把当前配置存成命名档案（同名覆盖）；名称留空时默认用当前模型名
        /// </summary>
        private void ProfileSave_Click(object sender, RoutedEventArgs e)
        {
            if (Settings == null)
            {
                return;
            }

            var name = (SetProfileName.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                // 名称默认为模型名称：多数人一套配置就对应一个模型
                name = DefaultProfileName();
            }

            if (name.Length == 0)
            {
                ProfileEmptyHint.Text = "请先选好模型，或给这套配置起个名字（例如 AA），再点 + 保存";
                ProfileEmptyHint.Visibility = Visibility.Visible;
                return;
            }

            Settings.SaveNamedProfile(name);
            SetProfileName.Text = string.Empty;

            BuildProfileRows();
            RaiseChanged();
        }

        /// <summary>
        /// 命名档案的默认名称：当前模型名（没有则退回厂商名）
        /// </summary>
        private string DefaultProfileName()
        {
            var model = Settings?.Model ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(model))
            {
                return ModelCatalog.DisplayOf(model);
            }

            return Settings?.Provider ?? string.Empty;
        }

        /// <summary>
        /// 切换到某个档案：整套换，然后把界面重新回填一遍
        /// </summary>
        private void ProfileApply_Click(object sender, RoutedEventArgs e)
        {
            var name = (sender as FrameworkElement)?.Tag as string;
            if (Settings == null || string.IsNullOrWhiteSpace(name) || !Settings.ApplyNamedProfile(name))
            {
                return;
            }

            _binding = true;
            try
            {
                BindUi();
            }
            finally
            {
                _binding = false;
            }

            BuildProfileRows();
            RaiseChanged();
        }

        /// <summary>
        /// 删除某个档案（不动当前正在使用的配置）
        /// </summary>
        private void ProfileDelete_Click(object sender, RoutedEventArgs e)
        {
            var name = (sender as FrameworkElement)?.Tag as string;
            if (Settings == null || string.IsNullOrWhiteSpace(name) || !Settings.RemoveNamedProfile(name))
            {
                return;
            }

            BuildProfileRows();
            RaiseChanged();
        }

        // ============================================================
        // 用户改动
        // ============================================================
        private void Provider_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_binding || Settings == null)
            {
                return;
            }

            var provider = SetProvider.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(provider))
            {
                return;
            }

            // 先把旧厂商的 Key / Base URL / 限额存回它的档案，再装入新厂商的档案
            // 返回值 = 新厂商档案里存过自己的限额（各家互不串）
            var limitsFromProfile = Settings.SwitchProvider(provider);

            _binding = true;
            try
            {
                // 1:1 对齐 Zoo：切厂商时，若当前模型对新厂商非法，则重置为该厂商默认模型
                ResetModelForProvider(Settings.Provider);

                // 档案里没存过限额（第一次用这个厂商 / 老档案）才按新模型的能力带出；
                // 存过就用档案自己的 —— 否则 A 的 1M 会串到 B，B 改 128K 又会串回 A
                if (!limitsFromProfile)
                {
                    Settings.AlignLimitsToModel(Settings.Model);
                }

                BindModelList(Settings.Provider);
                RenderProviderFields();
                UpdateModelInfoLine();

                // 上下文档位跟着模型容量重排（模型容量不在固定档位里时就补一条），并选中它
                BindContextOptions(Settings.ContextWindow);
                SyncMaxOutputField();

                // 高级选项也随厂商档案一起切换
                SelectAdvanced(SetReasoningEffort, Settings.ReasoningEffort);
                SelectAdvanced(SetVerbosity, Settings.Verbosity);
            }
            finally
            {
                _binding = false;
            }

            RaiseChanged();

            // 切厂商后后台拉一次该厂商的模型列表（带缓存）：让「默认模型」下拉来自提供商而非写死目录
            _ = RefreshProviderModelsAsync(Settings.Provider);
        }

        private void Model_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_binding || Settings == null)
            {
                return;
            }

            var selected = SetModel.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selected))
            {
                // 用户正在手输、而且还没匹配到任何一项：不提交，等他打完（回车 / 失焦再提交）
                return;
            }

            // 可编辑下拉的一个坑：手输的过程中 WPF 会随着文本变化不断改 SelectedItem，
            // 于是「每敲一个字母」都会走到这里这时**不能**提交 —— 否则每个字母都写一次设置，
            // 而 ApplyModel 还会把输入框内容改写成显示名，光标被顶到末尾，手感就是「打不进去」
            // 只有在「文本与选中项完全一致」（真正选中了某一项 / 回车提交）时才落库
            if (!string.Equals(SetModel.Text, selected, StringComparison.Ordinal))
            {
                return;
            }

            ApplyModel(selected);
        }

        /// <summary>
        /// 可编辑下拉里按回车即提交（不必先去点别处让它失焦）
        /// </summary>
        private void Model_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            ApplyModel(SetModel.Text);
        }

        /// <summary>
        /// 可编辑下拉：手输后失焦（或回车）提交
        /// </summary>
        private void Model_TextCommitted(object sender, RoutedEventArgs e)
        {
            if (_binding || Settings == null)
            {
                return;
            }

            ApplyModel(SetModel.Text);
        }

        private void ApplyModel(string displayNameOrId)
        {
            var modelId = ResolveModelId(displayNameOrId);
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return;
            }

            // 真的换了模型才动限额：同一个模型被重新提交一次（回车 / 失焦）时，
            // 不该把用户自己调过的上下文窗口按"模型能力"改回去
            var modelChanged = !string.Equals(modelId, Settings.Model, StringComparison.OrdinalIgnoreCase);

            Settings.Model = modelId;
            Settings.StoreActiveProfile();

            _binding = true;
            try
            {
                // 手输的模型可能对应另一个厂商：这里只做回显归一化，不擅自改厂商
                // 正在输入时**不要**改写文本框：那会把光标顶到末尾，看起来像「打字被吃掉」
                // 失焦提交那条路径（Model_TextCommitted）此时焦点已经离开，才会做归一化
                if (!SetModel.IsKeyboardFocusWithin)
                {
                    SetModel.Text = DisplayNameOf(modelId);
                }

                // 选中模型后：上下文窗口 / 最大输出按该模型的真实能力自动带出
                if (modelChanged)
                {
                    Settings.AlignLimitsToModel(modelId);
                }

                UpdateModelInfoLine();

                BindContextOptions(Settings.ContextWindow);
                SyncMaxOutputField();
            }
            finally
            {
                _binding = false;
            }

            RaiseChanged();
        }

        /// <summary>
        /// 把「最大输出」输入框刷新为设置里的当前值（切换模型会自动改写它）
        /// </summary>
        private void SyncMaxOutputField()
        {
            if (Settings == null || PanelsScroll == null)
            {
                return;
            }

            foreach (var box in Tagged<TextBox>(PanelsScroll))
            {
                if (string.Equals(box.Tag as string, "MaxOutputTokens", StringComparison.Ordinal))
                {
                    box.Text = Settings.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        /// <summary>
        /// 切厂商后后台拉取该厂商的模型列表（带缓存、失败静默）；回来时仍停留于该厂商则刷新下拉
        /// </summary>
        private async Task RefreshProviderModelsAsync(string provider)
        {
            if (Settings == null || string.IsNullOrWhiteSpace(provider))
            {
                return;
            }

            try
            {
                var result = await ModelDiscovery.FetchCachedAsync(Settings, force: false, CancellationToken.None);
                if (!result.Ok || !string.Equals(provider, Settings.Provider, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // 写盘 + 重载目录都在线程池上完成（await 期间不占用 UI 线程）
                await ModelCatalog.SaveDiscoveredAsync(provider, result.Ids).ConfigureAwait(true);

                if (_binding)
                {
                    return;
                }

                _binding = true;
                try
                {
                    BindModelList(Settings.Provider);
                }
                finally
                {
                    _binding = false;
                }
            }
            catch (Exception)
            {
                // 自动刷新失败不打扰用户
            }
        }


        // ============================================================
        // 模型高级选项
        // ============================================================

        /// <summary>
        /// 推理强度 / 回答详细度改动：取下拉项的 Tag（空串表示「默认」）写回设置
        /// </summary>
        private void AdvancedOption_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_binding || Settings == null)
            {
                return;
            }

            var combo = sender as ComboBox;
            var value = (combo?.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

            if (ReferenceEquals(combo, SetReasoningEffort))
            {
                Settings.ReasoningEffort = value;
            }
            else if (ReferenceEquals(combo, SetVerbosity))
            {
                Settings.Verbosity = value;
            }

            Settings.StoreActiveProfile();
            RaiseChanged();
        }

        /// <summary>
        /// 把高级选项下拉选到与设置值一致的项（找不到就选「默认」）
        /// </summary>
        private static void SelectAdvanced(ComboBox combo, string value)
        {
            if (combo == null)
            {
                return;
            }

            var target = value ?? string.Empty;
            for (var i = 0; i < combo.Items.Count; i++)
            {
                var tag = (combo.Items[i] as ComboBoxItem)?.Tag as string ?? string.Empty;
                if (string.Equals(tag, target, StringComparison.OrdinalIgnoreCase))
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
    }
}
