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
    /// SettingsDrawerControl 分部：厂商与模型的字段渲染（Base URL / API Key / 专属字段 / 能力与价格）
    /// </summary>
    public partial class SettingsDrawerControl
    {
        /// <summary>
        /// 按厂商刷新「默认模型」下拉
        /// 列表项显示**友好名**（供人看），内部映射回 model id（发给 API 的值）
        /// </summary>
        private void BindModelList(string provider)
        {
            _models = ModelCatalog.All
                .Where(m => string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 当前模型不在目录里（自定义网关 / 手输的 id）：补一条，别让它丢失
            if (!string.IsNullOrWhiteSpace(Settings.Model) &&
                !_models.Any(m => string.Equals(m.Id, Settings.Model, StringComparison.OrdinalIgnoreCase)))
            {
                _models.Insert(0, new ModelInfo(Settings.Model, Settings.Model, provider ?? string.Empty, "#8A8A8A", "自定义"));
            }

            SetModel.ItemsSource = _models.Select(m => m.DisplayName).ToList();
            SetModel.Text = DisplayNameOf(Settings.Model);
        }

        private string DisplayNameOf(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return string.Empty;
            }

            var info = _models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
            return info?.DisplayName ?? modelId;
        }

        /// <summary>
        /// 把「显示名或手输文本」还原成 model id（手输内容按 id 处理）
        /// </summary>
        private string ResolveModelId(string textOrDisplayName)
        {
            var text = (textOrDisplayName ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            var info = _models.FirstOrDefault(m => string.Equals(m.DisplayName, text, StringComparison.OrdinalIgnoreCase));
            return info?.Id ?? text;
        }

        /// <summary>
        /// 该厂商当前生效的 Base URL（自定义优先，否则内置默认）
        /// </summary>
        private string EffectiveBaseUrl(string provider)
        {
            var custom = Settings.BaseUrl;
            return string.IsNullOrWhiteSpace(custom) ? BuiltInBaseUrl(provider) : custom;
        }

        private static string BuiltInBaseUrl(string provider)
        {
            return ChatBackendFactory.ResolveBaseUrl(provider, string.Empty);
        }

        // ============================================================
        // 提供商专属字段 
        // ============================================================

        /// <summary>
        /// 按当前提供商动态生成「专属字段」区：
        /// 登录说明 / Base URL（直接或勾选后显示）/ API Key（含「获取」链接）/ 额外字段
        /// </summary>
        private void RenderProviderFields()
        {
            if (ProviderFieldsPanel == null || Settings == null)
            {
                return;
            }

            ProviderFieldsPanel.Children.Clear();
            _apiKeyBox = null;
            _apiKeyPlain = null;
            _baseUrlBox = null;
            _customBaseUrlToggle = null;
            _providerOptionControls.Clear();

            var spec = ProviderForms.Of(Settings.Provider);

            if (!string.IsNullOrWhiteSpace(spec.SignInNote))
            {
                ProviderFieldsPanel.Children.Add(HintText(spec.SignInNote));
            }

            // ---- Base URL ----
            if (spec.BaseUrlMode == ProviderBaseUrlMode.Plain)
            {
                ProviderFieldsPanel.Children.Add(FieldText(spec.BaseUrlLabel));
                _baseUrlBox = UrlBox(spec.BaseUrlPlaceholder);
                _baseUrlBox.Text = Settings.BaseUrl ?? string.Empty;
                _baseUrlBox.TextChanged += (_, __) => OnBaseUrlChanged();
                ProviderFieldsPanel.Children.Add(_baseUrlBox);
                ProviderFieldsPanel.Children.Add(HintText("填写该提供商的 OpenAI 兼容端点地址（通常以 /v1 结尾）；模型填该端点支持的 id"));
            }
            else if (spec.BaseUrlMode == ProviderBaseUrlMode.Toggle)
            {
                _baseUrlBox = UrlBox(spec.BaseUrlPlaceholder);
                _baseUrlBox.Text = Settings.BaseUrl ?? string.Empty;
                _baseUrlBox.Visibility = string.IsNullOrWhiteSpace(Settings.BaseUrl) ? Visibility.Collapsed : Visibility.Visible;
                _baseUrlBox.TextChanged += (_, __) => OnBaseUrlChanged();

                _customBaseUrlToggle = new CheckBox
                {
                    Content = "使用自定义 Base URL",
                    Margin = new Thickness(0, 0, 0, 6),
                    IsChecked = !string.IsNullOrWhiteSpace(Settings.BaseUrl),
                };
                _customBaseUrlToggle.Checked += (_, __) =>
                {
                    _baseUrlBox.Visibility = Visibility.Visible;
                    if (!_binding)
                    {
                        RaiseChanged();
                    }
                };
                _customBaseUrlToggle.Unchecked += (_, __) =>
                {
                    _baseUrlBox.Visibility = Visibility.Collapsed;
                    if (_binding)
                    {
                        return;
                    }

                    Settings.BaseUrl = string.Empty;
                    Settings.StoreActiveProfile();
                    UpdateProviderError();
                    RaiseChanged();
                };

                ProviderFieldsPanel.Children.Add(_customBaseUrlToggle);
                ProviderFieldsPanel.Children.Add(_baseUrlBox);
                ProviderFieldsPanel.Children.Add(HintText("默认使用官方地址；勾选后可指定自定义网关"));
            }

            // ---- API Key ----
            if (!string.IsNullOrWhiteSpace(spec.ApiKeyLabel))
            {
                ProviderFieldsPanel.Children.Add(FieldText(spec.ApiKeyLabel));

                var grid = new Grid();
                _apiKeyBox = new PasswordBox
                {
                    Height = 30,
                    Padding = new Thickness(9, 0, 9, 0),
                    PasswordChar = '●',
                    FontFamily = TryFindResource("Ap.FontCode") as FontFamily ?? new FontFamily("Consolas"),
                    FontSize = 11.5,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };

                // 颜色用资源引用挂载（而非一次性取色），VS 主题切换时自动跟随
                _apiKeyBox.SetResourceReference(Control.BackgroundProperty, "Ap.ControlBg");
                _apiKeyBox.SetResourceReference(Control.BorderBrushProperty, "Ap.ControlBorder");
                _apiKeyBox.SetResourceReference(Control.ForegroundProperty, "Ap.FgStrong");
                _apiKeyBox.SetResourceReference(PasswordBox.CaretBrushProperty, "Ap.FgStrong");
                _apiKeyBox.Password = Settings.ApiKey ?? string.Empty;
                _apiKeyBox.PasswordChanged += (_, __) =>
                {
                    if (_binding)
                    {
                        return;
                    }

                    Settings.ApiKey = _apiKeyBox.Password ?? string.Empty;
                    Settings.StoreActiveProfile();
                    _binding = true;
                    try
                    {
                        if (_apiKeyPlain != null)
                        {
                            _apiKeyPlain.Text = Settings.ApiKey;
                        }
                    }
                    finally
                    {
                        _binding = false;
                    }

                    UpdateProviderError();
                    RaiseChanged();
                };

                _apiKeyPlain = new TextBox
                {
                    Height = 30,
                    Padding = new Thickness(9, 0, 9, 0),
                    Visibility = Visibility.Collapsed,
                    FontFamily = TryFindResource("Ap.FontCode") as FontFamily ?? new FontFamily("Consolas"),
                    FontSize = 11.5,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };

                // 颜色用资源引用挂载（而非一次性取色），VS 主题切换时自动跟随
                _apiKeyPlain.SetResourceReference(Control.BackgroundProperty, "Ap.ControlBg");
                _apiKeyPlain.SetResourceReference(Control.BorderBrushProperty, "Ap.ControlBorder");
                _apiKeyPlain.SetResourceReference(Control.ForegroundProperty, "Ap.FgStrong");
                _apiKeyPlain.SetResourceReference(TextBox.CaretBrushProperty, "Ap.FgStrong");
                _apiKeyPlain.Text = Settings.ApiKey ?? string.Empty;
                _apiKeyPlain.TextChanged += (_, __) =>
                {
                    if (_binding)
                    {
                        return;
                    }

                    Settings.ApiKey = _apiKeyPlain.Text ?? string.Empty;
                    Settings.StoreActiveProfile();
                    _binding = true;
                    try
                    {
                        if (_apiKeyBox != null)
                        {
                            _apiKeyBox.Password = Settings.ApiKey;
                        }
                    }
                    finally
                    {
                        _binding = false;
                    }

                    UpdateProviderError();
                    RaiseChanged();
                };

                var eye = new Button
                {
                    Style = TryFindResource("IconButton") as Style,
                    Width = 24,
                    Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 3, 0),
                    ToolTip = "显示 / 隐藏",
                    Content = new Ui.Primitive.Icon { IconName = "problem", Width = 14, Height = 14 },
                };
                eye.Click += (_, __) => ToggleKeyVisibility();

                grid.Children.Add(_apiKeyBox);
                grid.Children.Add(_apiKeyPlain);
                grid.Children.Add(eye);
                ProviderFieldsPanel.Children.Add(grid);

                ProviderFieldsPanel.Children.Add(HintText("密钥按厂商分别保存在本地（%APPDATA%\\LangCode\\vsext）"));

            }

            // ---- 额外字段 ----
            foreach (var extra in spec.Extras)
            {
                if (extra.OnlyForCodestral && !IsCodestralSelected())
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(extra.Label))
                {
                    ProviderFieldsPanel.Children.Add(FieldText(extra.Label));
                }

                if (extra.Kind == ProviderFieldKind.Select)
                {
                    var current = extra.Key == "endpoint"
                        ? (Settings.BaseUrl ?? string.Empty)
                        : Settings.GetProviderOption(extra.Key);

                    var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
                    var selected = 0;
                    if (extra.Options != null)
                    {
                        for (var i = 0; i < extra.Options.Count; i++)
                        {
                            var option = extra.Options[i];
                            combo.Items.Add(new ComboBoxItem { Content = option.Value, Tag = option.Key });
                            if (string.Equals(option.Key, current, StringComparison.OrdinalIgnoreCase))
                            {
                                selected = i;
                            }
                        }
                    }

                    combo.SelectedIndex = selected;
                    var extraKey = extra.Key;
                    combo.SelectionChanged += (sender, __) =>
                    {
                        if (_binding)
                        {
                            return;
                        }

                        var value = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
                        if (extraKey == "endpoint")
                        {
                            Settings.BaseUrl = value;
                        }
                        else
                        {
                            Settings.SetProviderOption(extraKey, value);
                        }

                        Settings.StoreActiveProfile();
                        RaiseChanged();
                    };
                    _providerOptionControls.Add(combo);
                    ProviderFieldsPanel.Children.Add(combo);
                }
                else
                {
                    var box = new TextBox
                    {
                        Style = TryFindResource("FieldControl") as Style,
                        Text = Settings.GetProviderOption(extra.Key) ?? string.Empty,
                        Margin = new Thickness(0, 0, 0, 6),
                    };
                    var extraKey = extra.Key;
                    box.TextChanged += (sender, __) =>
                    {
                        if (_binding)
                        {
                            return;
                        }

                        Settings.SetProviderOption(extraKey, (sender as TextBox)?.Text ?? string.Empty);
                        Settings.StoreActiveProfile();
                        RaiseChanged();
                    };
                    _providerOptionControls.Add(box);
                    ProviderFieldsPanel.Children.Add(box);
                }

                if (!string.IsNullOrWhiteSpace(extra.Hint))
                {
                    ProviderFieldsPanel.Children.Add(HintText(extra.Hint));
                }
            }

            // 自定义 OpenAI 兼容模型的「能力与价格」（动态：提示缓存 / 推理等级会展开更多字段）
            RenderCapabilities();

            UpdateProviderError();
        }

        // ============================================================
        // 自定义 OpenAI 兼容模型的「能力与价格」 
        // ============================================================

        private void RenderCapabilities()
        {
            if (ProviderFieldsPanel == null || Settings == null)
            {
                return;
            }

            if (!string.Equals(
                    ProviderCatalog.Canonicalize(Settings.Provider),
                    ProviderCatalog.OpenAiCompatible,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ProviderFieldsPanel.Children.Add(FieldText("模型能力"));
            ProviderFieldsPanel.Children.Add(HintText("配置该自定义模型的能力与价格，会影响工具调用与费用统计"));

            ProviderFieldsPanel.Children.Add(OptionNumber("caps.maxTokens", "最大输出 token"));
            ProviderFieldsPanel.Children.Add(OptionNumber("caps.contextWindow", "上下文窗口 token"));

            var images = NewOptionCheck("caps.supportsImages", "图像支持");
            ProviderFieldsPanel.Children.Add(CheckRow(images, "该模型是否能处理与理解图片"));

            var cachePanel = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            cachePanel.Children.Add(OptionNumber("caps.cacheReadsPrice", "缓存读取价格（$ / 百万 token）"));
            cachePanel.Children.Add(OptionNumber("caps.cacheWritesPrice", "缓存写入价格（$ / 百万 token）"));

            var cacheBox = NewOptionCheck("caps.supportsPromptCache", "提示缓存");
            cachePanel.Visibility = cacheBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            cacheBox.Checked += (_, __) => { if (!_binding) { cachePanel.Visibility = Visibility.Visible; } };
            cacheBox.Unchecked += (_, __) => { if (!_binding) { cachePanel.Visibility = Visibility.Collapsed; } };
            ProviderFieldsPanel.Children.Add(CheckRow(cacheBox, "该模型是否支持提示缓存（可降低费用）"));
            ProviderFieldsPanel.Children.Add(cachePanel);

            ProviderFieldsPanel.Children.Add(OptionNumber("caps.inputPrice", "输入价格（$ / 百万 token）"));
            ProviderFieldsPanel.Children.Add(OptionNumber("caps.outputPrice", "输出价格（$ / 百万 token）"));

            // 附加请求参数：把 OpenAI 协议之外的厂商私有开关直接拼进请求体。
            // 典型场景：vLLM 上跑 Qwen3，需要 chat_template_kwargs.enable_thinking 才会输出思考过程
            ProviderFieldsPanel.Children.Add(OptionText(
                "caps.extraBody",
                "附加请求参数 (JSON)",
                "原样拼进请求体根对象，用于补上厂商私有开关。例：{\"chat_template_kwargs\":{\"enable_thinking\":true}}"));

            var reasoningPanel = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            reasoningPanel.Children.Add(OptionSelect("caps.reasoningEffort", "推理强度", new[] { "low", "medium", "high", "xhigh", "max" }));

            var reasoningBox = NewOptionCheck("caps.enableReasoningEffort", "设置推理等级");
            reasoningPanel.Visibility = reasoningBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            reasoningBox.Checked += (_, __) => { if (!_binding) { reasoningPanel.Visibility = Visibility.Visible; } };
            reasoningBox.Unchecked += (_, __) => { if (!_binding) { reasoningPanel.Visibility = Visibility.Collapsed; } };
            ProviderFieldsPanel.Children.Add(CheckRow(reasoningBox, "开启后可为该模型指定推理强度"));
            ProviderFieldsPanel.Children.Add(reasoningPanel);

            var reset = new Button
            {
                Content = "重置为默认能力",
                Style = TryFindResource("BtnBase") as Style,
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 11.5,
            };
            reset.Click += (_, __) =>
            {
                foreach (var key in new[]
                {
                    "caps.maxTokens", "caps.contextWindow", "caps.supportsImages",
                    "caps.supportsPromptCache", "caps.cacheReadsPrice", "caps.cacheWritesPrice",
                    "caps.inputPrice", "caps.outputPrice",
                    "caps.enableReasoningEffort", "caps.reasoningEffort", "caps.extraBody",
                })
                {
                    Settings.SetProviderOption(key, string.Empty);
                }

                Settings.StoreActiveProfile();
                RenderProviderFields();
                RaiseChanged();
            };
            ProviderFieldsPanel.Children.Add(reset);
        }

        /// <summary>
        /// 带标签的数字 / 文本项（写入 ProviderOptions）
        /// </summary>
        private FrameworkElement OptionNumber(string key, string label)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(FieldText(label));

            var box = new TextBox { Text = Settings.GetProviderOption(key) ?? string.Empty };
            if (TryFindResource("FieldControl") is Style style)
            {
                box.Style = style;
            }

            box.TextChanged += (_, __) =>
            {
                if (_binding)
                {
                    return;
                }

                Settings.SetProviderOption(key, box.Text ?? string.Empty);
                Settings.StoreActiveProfile();
                RaiseChanged();
            };
            panel.Children.Add(box);
            return panel;
        }

        /// <summary>
        /// 带标签的<b>多行</b>文本框（写入 ProviderOptions）：用于 JSON 之类的长参数
        /// </summary>
        private FrameworkElement OptionText(string key, string label, string hint)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(FieldText(label));

            if (!string.IsNullOrWhiteSpace(hint))
            {
                panel.Children.Add(HintText(hint));
            }

            var box = new TextBox
            {
                Text = Settings.GetProviderOption(key) ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 56,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            if (TryFindResource("FieldControl") is Style style)
            {
                box.Style = style;
            }

            box.TextChanged += (_, __) =>
            {
                if (_binding)
                {
                    return;
                }

                Settings.SetProviderOption(key, box.Text ?? string.Empty);
                Settings.StoreActiveProfile();
                RaiseChanged();
            };

            panel.Children.Add(box);
            return panel;
        }

        /// <summary>
        /// 生成一个能力开关（写入 ProviderOptions 的 "true"/空）
        /// </summary>
        private CheckBox NewOptionCheck(string key, string label)
        {
            var check = new CheckBox
            {
                Content = label,
                IsChecked = IsTrue(Settings.GetProviderOption(key)),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            check.Checked += (_, __) =>
            {
                if (_binding)
                {
                    return;
                }

                Settings.SetProviderOption(key, "true");
                Settings.StoreActiveProfile();
                RaiseChanged();
            };
            check.Unchecked += (_, __) =>
            {
                if (_binding)
                {
                    return;
                }

                Settings.SetProviderOption(key, string.Empty);
                Settings.StoreActiveProfile();
                RaiseChanged();
            };
            return check;
        }

        private FrameworkElement CheckRow(CheckBox check, string description)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(check);
            if (!string.IsNullOrWhiteSpace(description))
            {
                panel.Children.Add(HintText(description));
            }

            return panel;
        }

        /// <summary>
        /// 带标签的下拉项（写入 ProviderOptions）
        /// </summary>
        private FrameworkElement OptionSelect(string key, string label, string[] values)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(FieldText(label));

            var combo = new ComboBox();
            var current = Settings.GetProviderOption(key);
            var selected = 0;
            for (var i = 0; i < values.Length; i++)
            {
                combo.Items.Add(new ComboBoxItem { Content = values[i], Tag = values[i] });
                if (string.Equals(values[i], current, StringComparison.OrdinalIgnoreCase))
                {
                    selected = i;
                }
            }

            combo.SelectedIndex = selected;
            combo.SelectionChanged += (sender, __) =>
            {
                if (_binding)
                {
                    return;
                }

                var value = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
                Settings.SetProviderOption(key, value);
                Settings.StoreActiveProfile();
                RaiseChanged();
            };
            panel.Children.Add(combo);
            return panel;
        }

        /// <summary>
        /// 明文 / 密文切换（对齐原设计：两个真控件切换显示最稳）
        /// </summary>
        private void ToggleKeyVisibility()
        {
            if (_apiKeyBox == null || _apiKeyPlain == null)
            {
                return;
            }

            var showPlain = _apiKeyBox.Visibility == Visibility.Visible;

            _binding = true;
            try
            {
                if (showPlain)
                {
                    _apiKeyPlain.Text = _apiKeyBox.Password ?? string.Empty;
                }
                else
                {
                    _apiKeyBox.Password = _apiKeyPlain.Text ?? string.Empty;
                }

                _apiKeyBox.Visibility = showPlain ? Visibility.Collapsed : Visibility.Visible;
                _apiKeyPlain.Visibility = showPlain ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _binding = false;
            }

            if (showPlain)
            {
                _apiKeyPlain.Focus();
                _apiKeyPlain.CaretIndex = _apiKeyPlain.Text.Length;
            }
        }

        private void OnBaseUrlChanged()
        {
            if (_binding || Settings == null || _baseUrlBox == null)
            {
                return;
            }

            var text = (_baseUrlBox.Text ?? string.Empty).Trim();

            // 填回「官方默认地址」时存空值：表示继续跟随内置默认（厂商换地址时自动受益）
            Settings.BaseUrl = string.Equals(text, BuiltInBaseUrl(Settings.Provider), StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : text;

            Settings.StoreActiveProfile();
            UpdateProviderError();
            RaiseChanged();
        }

        private bool IsCodestralSelected()
        {
            return (Settings?.Model ?? string.Empty).StartsWith("codestral-", StringComparison.OrdinalIgnoreCase);
        }

        private TextBlock FieldText(string text)
        {
            var block = new TextBlock { Text = text ?? string.Empty, Margin = new Thickness(0, 0, 0, 5) };
            if (TryFindResource("TdFieldLabel") is Style style)
            {
                block.Style = style;
            }

            return block;
        }

        private TextBlock HintText(string text)
        {
            var block = new TextBlock
            {
                Text = text ?? string.Empty,
                Margin = new Thickness(0, 4, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            };
            if (TryFindResource("TdFieldHint") is Style style)
            {
                block.Style = style;
            }

            return block;
        }

        private TextBox UrlBox(string placeholder)
        {
            var box = new TextBox { Margin = new Thickness(0, 0, 0, 6), ToolTip = placeholder };
            if (TryFindResource("FieldControl") is Style style)
            {
                box.Style = style;
            }

            return box;
        }

        /// <summary>
        /// 校验错误条 ：缺 Key / 缺地址时提示
        /// </summary>
        private void UpdateProviderError()
        {
            if (ApiErrorBar == null || ApiErrorText == null || Settings == null)
            {
                return;
            }

            var provider = Settings.Provider ?? string.Empty;
            var spec = ProviderForms.Of(provider);
            var def = ProviderCatalog.Find(provider);

            string error = null;
            if (def != null && def.NeedsApiKey && !spec.ApiKeyOptional &&
                !string.IsNullOrWhiteSpace(spec.ApiKeyLabel) && string.IsNullOrWhiteSpace(Settings.ApiKey))
            {
                error = "需要填写 " + spec.ApiKeyLabel + "";
            }
            else if (def != null && def.NeedsManualBaseUrl && string.IsNullOrWhiteSpace(Settings.BaseUrl))
            {
                error = "需要填写 Base URL";
            }

            ApiErrorBar.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            ApiErrorText.Text = error ?? string.Empty;
        }
    }
}
