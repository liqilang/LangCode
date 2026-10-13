using LangCode.VsExt.Ui.Rendering;
using LangCode.VsExt.Ui.Theme;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace LangCode.VsExt.Ui.Controls
{
    /// <summary>
    /// 助手正文的 Markdown 视图：把模型返回的 Markdown 渲染成真正的排版元素
    /// （标题 / 段落 / 列表 / 引用 / 分隔线 / 围栏代码 / <b>表格</b>），而不是一整块纯文本
    /// </summary>
    public sealed class MarkdownView : ContentControl
    {
        /// <summary>
        /// 标题各级字号
        /// </summary>
        private static readonly double[] HeadingSizes = { 17, 15.5, 14.5, 13.5, 13, 13 };

        public static readonly DependencyProperty MarkdownProperty =
            DependencyProperty.Register(
                "Markdown",
                typeof(string),
                typeof(MarkdownView),
                new FrameworkPropertyMetadata(string.Empty, OnMarkdownChanged));

        /// <summary>
        /// 正文基准字号（设置页的「字号」）；内部所有尺寸按它等比缩放
        /// </summary>
        public static readonly DependencyProperty BaseFontSizeProperty =
            DependencyProperty.Register(
                "BaseFontSize",
                typeof(double),
                typeof(MarkdownView),
                new FrameworkPropertyMetadata(13.0, OnBaseFontSizeChanged));

        private string _pending = string.Empty;
        private string _rendered;
        private bool _rebuildScheduled;

        // ==================== 分块增量渲染的状态 ====================
        // 流式输出时正文每 50ms 变一次整棵视觉树重建一次的成本随正文长度线性增长，
        // 一次长回答就是成百上千次重建这里记住上一次每一块的「内容指纹」与已建好的元素，
        // 只重建真正变化的那一块 —— 流式的典型情况是「只有最后一块在变」

        private StackPanel _panel;
        private double _renderedScale = 1.0;
        private readonly List<string> _blockKeys = [];
        private readonly List<UIElement> _blockElements = [];

        public MarkdownView()
        {
            Focusable = false;

            // 主题跟随：VS 主题切换时整棵重建，让下方 Res(...) 取到的主题画刷换成新主题的值
            // 订阅随上树/下树成对，避免静态事件长期持有控件实例
            Loaded += (_, __) => ThemeManager.ThemeChanged += OnThemeChanged;
            Unloaded += (_, __) =>
            {
                ThemeManager.ThemeChanged -= OnThemeChanged;
                ResetRenderCache();
            };

            // 上树之后资源可用，需要重新渲染一次以取到真实主题色
            // 但若内容与已渲染的一致（例如同一控件被重新挂回可视树），就不必整棵重建 ——
            // 长会话滚动时这能省掉大量重复的 Markdown 解析与视觉树构建
            Loaded += (_, __) =>
            {
                if (_panel != null &&
                    string.Equals(_pending ?? string.Empty, _rendered, StringComparison.Ordinal) &&
                    Math.Abs(_renderedScale - Scale) < 0.001)
                {
                    return;
                }

                ResetRenderCache();
                Rebuild();
            };
        }

        /// <summary>
        /// VS 主题切换：丢掉渲染缓存并重建，使正文取到新主题的颜色
        /// </summary>
        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            ResetRenderCache();
            Rebuild();
        }

        /// <summary>
        /// 丢掉渲染缓存（离开可视树 / 刚上树时用）
        /// </summary>
        private void ResetRenderCache()
        {
            _rendered = null;
            _panel = null;
            _blockKeys.Clear();
            _blockElements.Clear();
        }

        /// <summary>
        /// 要渲染的 Markdown 原文
        /// </summary>
        public string Markdown
        {
            get { return (string)GetValue(MarkdownProperty); }
            set { SetValue(MarkdownProperty, value); }
        }

        /// <summary>
        /// 正文基准字号（默认 13，对应设置页的字号滑块）
        /// </summary>
        public double BaseFontSize
        {
            get { return (double)GetValue(BaseFontSizeProperty); }
            set { SetValue(BaseFontSizeProperty, value); }
        }

        /// <summary>
        /// 相对 13px 基准的缩放系数
        /// </summary>
        private double Scale
        {
            get { return BaseFontSize <= 0 ? 1.0 : BaseFontSize / 13.0; }
        }

        private static void OnBaseFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (MarkdownView)d;

            // 字号变了必须整体重建（视觉树里的尺寸是算好的）
            view._rendered = null;
            view.ScheduleRebuild();
        }

        private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (MarkdownView)d;
            view._pending = e.NewValue as string ?? string.Empty;
            view.ScheduleRebuild();
        }

        private void ScheduleRebuild()
        {
            if (_rebuildScheduled)
            {
                return;
            }

            _rebuildScheduled = true;

            var dispatcher = Dispatcher;
            if (dispatcher == null)
            {
                Rebuild();
                return;
            }

            // 合并同一轮消息循环里的多次增量：只重建一次
            // Render 优先级：与流式吐字同帧完成「改文本 → 重建 Markdown → 布局 → 渲染」，
            // 不会像 Background 那样被排在渲染之后，导致画面比实际内容慢一帧、看起来发顿
            dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _rebuildScheduled = false;
                    Rebuild();
                }),
                DispatcherPriority.Render);
        }

        private void Rebuild()
        {
            var text = _pending ?? string.Empty;
            var scale = Scale;

            if (string.Equals(text, _rendered, StringComparison.Ordinal) && _panel != null &&
                Math.Abs(_renderedScale - scale) < 0.001)
            {
                return;
            }

            _rendered = text;

            // 首次渲染、或字号变了：内部尺寸/缩放都变了，整棵树都要重来
            if (_panel == null || Math.Abs(_renderedScale - scale) > 0.001)
            {
                _panel = new StackPanel { Orientation = Orientation.Vertical };
                if (Math.Abs(scale - 1.0) > 0.001)
                {
                    _panel.LayoutTransform = new ScaleTransform(scale, scale);
                }

                Content = _panel;
                _renderedScale = scale;
                _blockKeys.Clear();
                _blockElements.Clear();
            }

            var blocks = MarkdownParser.Parse(text);

            // 找出第一个与上次不同的块：它之前的元素可以整体复用
            var firstDiff = 0;
            var shared = Math.Min(blocks.Count, _blockElements.Count);
            while (firstDiff < shared && _blockKeys[firstDiff] == BlockKey(blocks[firstDiff]))
            {
                firstDiff++;
            }

            // 从差异点起全部拆掉重来（后缀重建）
            while (_panel.Children.Count > firstDiff)
            {
                _panel.Children.RemoveAt(_panel.Children.Count - 1);
            }

            while (_blockElements.Count > firstDiff)
            {
                _blockElements.RemoveAt(_blockElements.Count - 1);
                _blockKeys.RemoveAt(_blockKeys.Count - 1);
            }

            for (var i = firstDiff; i < blocks.Count; i++)
            {
                var element = BuildBlock(blocks[i]);
                _blockKeys.Add(BlockKey(blocks[i]));

                if (element == null)
                {
                    _blockElements.Add(null);
                    continue;
                }

                _panel.Children.Add(element);
                _blockElements.Add(element);
            }
        }

        /// <summary>
        /// 块的内容指纹：只有真的变了，才重建它对应的视觉元素
        /// </summary>
        private static string BlockKey(MdBlock block)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append((int)block.Kind).Append('|')
                   .Append(block.Level).Append('|')
                   .Append(block.Unclosed ? '1' : '0').Append('|')
                   .Append(block.Language).Append('|')
                   .Append(block.Text).Append('|')
                   .Append(block.Code).Append('|')
                   .Append(block.ColumnCount);

            foreach (var header in block.Headers)
            {
                builder.Append('|').Append(header);
            }

            foreach (var row in block.Rows)
            {
                builder.Append('|');
                foreach (var cell in row)
                {
                    builder.Append(cell).Append('~');
                }
            }

            foreach (var item in block.Items)
            {
                builder.Append('|').Append(item.Marker).Append('~').Append(item.Indent).Append('~').Append(item.Text);
            }

            return builder.ToString();
        }

        // ==================== 块级渲染 ====================

        private UIElement BuildBlock(MdBlock block)
        {
            switch (block.Kind)
            {
                case MdBlockKind.Heading:
                    return BuildHeading(block);

                case MdBlockKind.Code:
                    return BuildCode(block);

                case MdBlockKind.Table:
                    return BuildTable(block);

                case MdBlockKind.List:
                    return BuildList(block);

                case MdBlockKind.Quote:
                    return BuildQuote(block);

                case MdBlockKind.Rule:
                    return new Border
                    {
                        Height = 1,
                        Margin = new Thickness(0, 10, 0, 10),
                        Background = Res("Ap.Divider", Color.FromRgb(0x2F, 0x2F, 0x31)),
                    };

                default:
                    return BuildParagraph(block.Text);
            }
        }

        private UIElement BuildParagraph(string markdown)
        {
            return new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 21,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = Res("Ap.FgBody", Color.FromRgb(0xCF, 0xCF, 0xCF)),
                Margin = new Thickness(0, 0, 0, 8),
            }.WithInlines(markdown, this);
        }

        private UIElement BuildHeading(MdBlock block)
        {
            var index = Math.Min(Math.Max(block.Level, 1), HeadingSizes.Length) - 1;

            return new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = HeadingSizes[index],
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("Ap.FgBright", Color.FromRgb(0xE8, 0xE8, 0xE8)),
                Margin = new Thickness(0, block.Level <= 2 ? 12 : 8, 0, 6),
            }.WithInlines(block.Text, this);
        }

        private UIElement BuildCode(MdBlock block)
        {
            var body = new StackPanel { Orientation = Orientation.Vertical };

            if (!string.IsNullOrWhiteSpace(block.Language))
            {
                body.Children.Add(new TextBlock
                {
                    Text = block.Unclosed ? block.Language + " · 仍在输出…" : block.Language,
                    FontFamily = CodeFont(),
                    FontSize = 10.5,
                    Margin = new Thickness(11, 7, 11, 0),
                    Foreground = Res("Ap.FgFaint", Color.FromRgb(0x6D, 0x6D, 0x6D)),
                });
            }

            body.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 380,
                Padding = new Thickness(11, 8, 11, 9),
                Content = new TextBlock
                {
                    Text = block.Code,
                    FontFamily = CodeFont(),
                    FontSize = 11.5,
                    LineHeight = 19,
                    TextWrapping = TextWrapping.NoWrap,
                    Foreground = Res("Ap.CodeFg", Color.FromRgb(0x9C, 0xDC, 0xFE)),
                },
            });

            return new Border
            {
                Margin = new Thickness(0, 2, 0, 10),
                CornerRadius = new CornerRadius(6),
                Background = Res("Ap.BgCard", Color.FromRgb(0x1A, 0x1A, 0x1B)),
                BorderBrush = Res("Ap.ToolBorder", Color.FromRgb(0x34, 0x34, 0x36)),
                BorderThickness = new Thickness(1),
                Child = body,
            };
        }

        /// <summary>
        /// 表格：真 Grid，不是「用空格对齐的文字」
        /// 外面套横向 ScrollViewer —— 列多的表格在窄工具窗口里必须能横向滚动，否则会被压成一列乱码
        /// </summary>
        private UIElement BuildTable(MdBlock block)
        {
            var columns = Math.Max(1, block.ColumnCount);
            var grid = new Grid();

            for (var i = 0; i < columns; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            // 表头
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columns; c++)
            {
                var cell = Cell(Get(block.Headers, c), isHeader: true);
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            // 数据行
            for (var r = 0; r < block.Rows.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (var c = 0; c < columns; c++)
                {
                    var cell = Cell(Get(block.Rows[r], c), isHeader: false);
                    Grid.SetRow(cell, r + 1);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }

            return new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, 10),
                Content = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    BorderBrush = Res("Ap.ToolBorder", Color.FromRgb(0x34, 0x34, 0x36)),
                    BorderThickness = new Thickness(1, 1, 0, 0),
                    Child = grid,
                },
            };
        }

        private Border Cell(string markdown, bool isHeader)
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxWidth = 260,
                Foreground = isHeader
                    ? Res("Ap.FgStrong", Color.FromRgb(0xDC, 0xDC, 0xDC))
                    : Res("Ap.FgBody", Color.FromRgb(0xCF, 0xCF, 0xCF)),
            };

            if (isHeader)
            {
                text.FontWeight = FontWeights.SemiBold;
            }

            text.WithInlines(markdown, this);

            return new Border
            {
                Padding = new Thickness(9, 6, 9, 6),
                BorderBrush = Res("Ap.ToolBorder", Color.FromRgb(0x34, 0x34, 0x36)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = isHeader
                    ? Res("Ap.ToolHeadBg", Color.FromRgb(0x26, 0x26, 0x2A))
                    : null,
                Child = text,
            };
        }

        private UIElement BuildList(MdBlock block)
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8) };

            foreach (var item in block.Items)
            {
                var row = new Grid { Margin = new Thickness(item.Indent * 14, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var marker = new TextBlock
                {
                    Text = item.Marker,
                    FontSize = 12,
                    MinWidth = 18,
                    LineHeight = 21,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = Res("Ap.Teal", Color.FromRgb(0x4E, 0xC9, 0xB0)),
                };

                var body = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    LineHeight = 21,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = Res("Ap.FgBody", Color.FromRgb(0xCF, 0xCF, 0xCF)),
                };

                body.WithInlines(item.Text, this);

                Grid.SetColumn(marker, 0);
                Grid.SetColumn(body, 1);
                row.Children.Add(marker);
                row.Children.Add(body);
                panel.Children.Add(row);
            }

            return panel;
        }

        private UIElement BuildQuote(MdBlock block)
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                LineHeight = 20,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                FontStyle = FontStyles.Italic,
                Foreground = Res("Ap.FgDim", Color.FromRgb(0x8A, 0x8A, 0x8A)),
            };

            text.WithInlines(block.Text, this);

            return new Border
            {
                BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = Res("Ap.Border", Color.FromRgb(0x3C, 0x3C, 0x3C)),
                Padding = new Thickness(10, 2, 0, 2),
                Margin = new Thickness(0, 2, 0, 10),
                Child = text,
            };
        }

        // ==================== 行内渲染 ====================

        internal void AppendInlines(TextBlock target, string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                return;
            }

            foreach (var run in MarkdownParser.ParseInline(markdown))
            {
                Inline inline;

                if (run.IsLink)
                {
                    var link = new Hyperlink(new Run(run.Text))
                    {
                        Foreground = Res("Ap.Accent", Color.FromRgb(0x37, 0x94, 0xFF)),
                    };

                    var uri = SafeUri(run.Url);
                    if (uri != null)
                    {
                        link.NavigateUri = uri;
                        link.RequestNavigate += OnRequestNavigate;
                    }

                    inline = link;
                }
                else if (run.Code)
                {
                    inline = new Run(run.Text)
                    {
                        FontFamily = CodeFont(),
                        Foreground = Res("Ap.InlineCodeFg", Color.FromRgb(0xD7, 0xBA, 0x7D)),
                    };
                }
                else
                {
                    inline = new Run(run.Text);
                }

                if (run.Bold)
                {
                    inline = new Bold(inline);
                }

                if (run.Italic)
                {
                    inline = new Italic(inline);
                }

                target.Inlines.Add(inline);
            }
        }

        /// <summary>
        /// 只放行 http / https / mailto，避免把脚本或本地路径当链接打开
        /// </summary>
        private static Uri SafeUri(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                return null;
            }

            var scheme = uri.Scheme;
            var ok = string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);

            return ok ? uri : null;
        }

        private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch (Exception)
            {
                // 打不开浏览器就算了，不能让一次点击把工具窗口带崩
            }
        }

        private static string Get(IList<string> list, int index)
        {
            return list != null && index >= 0 && index < list.Count ? list[index] : string.Empty;
        }

        private FontFamily CodeFont()
        {
            return TryFindResource("Ap.FontCode") as FontFamily
                   ?? new FontFamily("Cascadia Mono, Consolas, Courier New");
        }

        /// <summary>
        /// 取主题画刷；取不到就用兜底色
        /// </summary>
        private Brush Res(string key, Color fallback)
        {
            return TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
        }
    }

    /// <summary>
    /// 让「建 TextBlock → 填 Inlines」写成一行的小工具
    /// </summary>
    internal static class MarkdownViewExtensions
    {
        internal static TextBlock WithInlines(this TextBlock target, string markdown, MarkdownView owner)
        {
            owner.AppendInlines(target, markdown);
            return target;
        }
    }
}

