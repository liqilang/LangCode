using LangCode.Domain.Catalog;
using LangCode.VsIntegration;
using LangCode.Workspace;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：输入框 / 斜杠命令 / @提及 / 提交类定义与字段见 AgentToolWindowControl.xaml.cs
    /// </summary>
    public partial class AgentToolWindowControl
    {
        // ============================================================
        // 输入 / 发送
        // ============================================================
        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 下面刷新 @ 提及索引时会访问 DTE，必须处于 UI 线程
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            UpdateSendState();
            AutoResizeInput();

            // 斜杠命令（/plan、/review…）：与 @ 提及共用同一个弹层，先于提及判定
            var slash = SlashQuery();
            if (slash != null)
            {
                RefreshSlashCommands(slash);

                if (!MentionPopup.IsOpen)
                {
                    ClosePopups();
                    MentionPopup.IsOpen = true;
                }

                return;
            }

            // @ 提及：光标前最近的 @ 之后那段就是过滤词，候选来自真实工作区文件 / 目录
            var query = MentionQuery();
            if (query == null)
            {
                if (MentionPopup.IsOpen)
                {
                    MentionPopup.IsOpen = false;
                    MentionBtn.IsChecked = false;
                }

                return;
            }

            // 新开弹层总是从分类根菜单开始（上一次下钻到文件 / 文件夹不延续），
            // 并顺手刷新一次工作区索引（工具窗口可能是在打开解决方案之前创建的）
            if (!MentionPopup.IsOpen)
            {
                ClosePopups();
                RefreshMentionIndex(forceRescan: false);
                _mentionView = MentionView.Root;
                MentionPopup.IsOpen = true;
                MentionBtn.IsChecked = true;
            }

            RefreshMentions(query);
        }

        /// <summary>
        /// 取光标前最近一个 <c>/</c> 之后的过滤词（斜杠命令）；不在写命令时返回 null
        /// </summary>
        private string SlashQuery()
        {
            var text = Input.Text ?? string.Empty;
            var caret = Math.Max(0, Math.Min(Input.SelectionStart, text.Length));
            if (caret == 0)
            {
                return null;
            }

            var slash = text.LastIndexOf('/', caret - 1);
            if (slash < 0)
            {
                return null;
            }

            if (slash > 0 && !char.IsWhiteSpace(text[slash - 1]))
            {
                return null;
            }

            var value = text.Substring(slash + 1, caret - slash - 1);
            return value.IndexOf(' ') >= 0 || value.IndexOf('\n') >= 0 ? null : value;
        }

        /// <summary>
        /// 按关键词刷新斜杠命令候选（复用 @ 提及那个弹层）
        /// </summary>
        private void RefreshSlashCommands(string query)
        {
            MentionList.Children.Clear();
            MentionList.Children.Add(MentionGroupHeader("命令"));

            var commands = SlashCommands.Match(query);
            if (commands.Count == 0)
            {
                MentionList.Children.Add(MentionHint("没有匹配「/" + query + "」的命令"));
                return;
            }

            foreach (var command in commands)
            {
                MentionList.Children.Add(SlashRow(command));
            }
        }

        /// <summary>
        /// 斜杠命令的一行：图标 + <c>/命令</c> + 标题
        /// </summary>
        private FrameworkElement SlashRow(SlashCommand command)
        {
            var button = new Button
            {
                Tag = (command.IsAction ? SlashActionPrefix : SlashInsertPrefix) + command.Key,
                ToolTip = command.Description,
            };

            var style = TryFindResource("MenuItemButton") as Style;
            if (style != null)
            {
                button.Style = style;
            }

            button.Click += MentionItem_Click;

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var commandGlyph = new Ui.Primitive.Icon
            {
                IconName = command.IsAction ? "trash" : "zap",
                Width = 13,
                Height = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            commandGlyph.SetResourceReference(Ui.Primitive.Icon.ForegroundProperty, "Ap.Accent");
            row.Children.Add(commandGlyph);

            var commandLabel = new TextBlock
            {
                Text = command.Display,
                Margin = new Thickness(9, 0, 0, 0),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            commandLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgMenu");
            row.Children.Add(commandLabel);

            var commandHint = new TextBlock
            {
                Text = command.Title,
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            commandHint.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
            row.Children.Add(commandHint);

            button.Content = row;
            return button;
        }

        /// <summary>
        /// 取光标前最近一个 <c>@</c> 之后的过滤词；当前不在写提及则返回 null
        /// </summary>
        private string MentionQuery()
        {
            var text = Input.Text ?? string.Empty;
            var caret = Math.Max(0, Math.Min(Input.SelectionStart, text.Length));
            if (caret == 0)
            {
                return null;
            }

            var at = text.LastIndexOf('@', caret - 1);
            if (at < 0)
            {
                return null;
            }

            if (at > 0 && !char.IsWhiteSpace(text[at - 1]))
            {
                return null;
            }

            var value = text.Substring(at + 1, caret - at - 1);
            return value.IndexOf(' ') >= 0 || value.IndexOf('\n') >= 0 ? null : value;
        }

        /// <summary>
        /// @ 提及弹层当前所在的视图（分类根菜单 / 全部文件夹 / 全部文件）
        /// </summary>
        private MentionView _mentionView = MentionView.Root;

        /// <summary>
        /// 提及弹层的视图层级：先选分类，再下钻到文件夹 / 文件的整表 
        /// </summary>
        private enum MentionView
        {
            Root,
            Folders,
            Files,
        }

        /// <summary>
        /// 分类行 / 返回行的 <c>Tag</c> 前缀：点它只切换视图，不往输入框插入文本
        /// </summary>
        private const string MentionViewTagPrefix = "#mention-view:";

        /// <summary>
        /// 是否已经挂过「后台扫描完成后自动刷新」的回调（避免重复挂）
        /// </summary>
        private bool _mentionScanRefreshHooked;

        /// <summary>
        /// 按关键词刷新提及候选
        /// </summary>
        private void RefreshMentions(string query)
        {
            MentionList.Children.Clear();

            if (MentionTitle != null)
            {
                MentionTitle.Text = _mentionView == MentionView.Folders ? "所有文件夹"
                    : _mentionView == MentionView.Files ? "所有文件"
                    : "提及";
            }

            var text = (query ?? string.Empty).Trim();

            // 需要文件表的视图（搜索 / 下钻）在后台扫描完成前不阻塞 UI：
            // 先显示「扫描中」，扫完再自动刷新
            if ((text.Length > 0 || _mentionView != MentionView.Root) &&
                _mentions != null && _mentions.IsReady &&
                !_mentions.TryGetAll(out _))
            {
                MentionList.Children.Add(MentionHint("正在后台扫描项目文件，稍候…"));
                HookMentionScanRefresh();
                return;
            }

            switch (_mentionView)
            {
                case MentionView.Folders:
                    RenderMentionDrilldown(text, folders: true);
                    return;

                case MentionView.Files:
                    RenderMentionDrilldown(text, folders: false);
                    return;
            }

            if (text.Length == 0)
            {
                RenderMentionRoot();
            }
            else
            {
                RenderMentionSearch(text);
            }
        }

        /// <summary>
        /// 挂上「后台扫描完成后自动刷新弹层」的回调（只挂一次；弹层已关就不再刷新）
        /// </summary>
        private void HookMentionScanRefresh()
        {
            if (_mentionScanRefreshHooked || _mentions == null)
            {
                return;
            }

            _mentionScanRefreshHooked = true;
            var index = _mentions;
            index.WhenScanCompleted(() => Dispatcher.BeginInvoke(new Action(() =>
            {
                _mentionScanRefreshHooked = false;

                // 期间换了工作区 / 关了弹层：这次回调作废
                if (!ReferenceEquals(index, _mentions) || !MentionPopup.IsOpen)
                {
                    return;
                }

                RefreshMentions(MentionQuery() ?? string.Empty);
            })));
        }

        /// <summary>
        /// 根视图：一行一个分类（图标 + 标题 + 一句说明）
        /// </summary>
        private void RenderMentionRoot()
        {
            MentionList.Children.Add(MentionGroupHeader("上下文"));

            MentionList.Children.Add(MentionCategoryRow("problem", "问题",
                "插入 VS「错误列表」里的编译错误", ContextTagPrefix + "problems"));
            MentionList.Children.Add(MentionCategoryRow("terminal", "终端",
                "插入剪贴板里的终端输出（先在终端里复制）", ContextTagPrefix + "terminal"));
            MentionList.Children.Add(MentionCategoryRow("link", "黏贴URL以获取内容",
                "抓取剪贴板里的网址正文并插入", ContextTagPrefix + "web"));
            MentionList.Children.Add(MentionCategoryRow("folder", "文件夹",
                "浏览项目里的所有文件夹", MentionViewTagPrefix + "folders"));
            MentionList.Children.Add(MentionCategoryRow("file", "文件",
                "浏览项目里的所有文件", MentionViewTagPrefix + "files"));
            MentionList.Children.Add(MentionCategoryRow("github", "Git Commits",
                "插入最近 10 条提交", ContextTagPrefix + "git-log"));

            // 其余上下文入口收在「更多上下文」里：能力不丢，但不跟上面六个主入口抢视线
            MentionList.Children.Add(MentionGroupHeader("更多上下文"));
            MentionList.Children.Add(MentionChip("current", "当前文件", "插入当前编辑的文件与光标位置"));
            MentionList.Children.Add(MentionChip("selection", "当前选中代码", "插入编辑器里选中的代码"));
            MentionList.Children.Add(MentionChip("debug", "调试状态", "插入断点 / 当前栈帧与局部变量"));
            MentionList.Children.Add(MentionChip("git-changes", "Git 变更", "插入当前未提交的改动清单"));
        }

        /// <summary>
        /// 有筛词：上下文芯片命中 + Git 改动 + 文件 / 目录即时命中（边打边搜）
        /// </summary>
        private void RenderMentionSearch(string text)
        {
            // 分类入口也能被搜到：敲「文件夹 / folder」或「文件 / file」可直接进入整表视图，
            // 不必先清空筛词再点分类
            if (Matches(text, "文件夹") || Matches(text, "folder"))
            {
                MentionList.Children.Add(MentionGroupHeader("分类"));
                MentionList.Children.Add(MentionCategoryRow("folder", "文件夹",
                    "浏览项目里的所有文件夹", MentionViewTagPrefix + "folders"));
            }

            if (Matches(text, "文件") || Matches(text, "file"))
            {
                if (MentionList.Children.Count == 0)
                {
                    MentionList.Children.Add(MentionGroupHeader("分类"));
                }

                MentionList.Children.Add(MentionCategoryRow("file", "文件",
                    "浏览项目里的所有文件", MentionViewTagPrefix + "files"));
            }

            var anyChip = false;

            void Chip(string key, string title, string description)
            {
                if (!Matches(text, key) && !Matches(text, title))
                {
                    return;
                }

                if (!anyChip)
                {
                    MentionList.Children.Add(MentionGroupHeader("上下文"));
                    anyChip = true;
                }

                MentionList.Children.Add(MentionChip(key, title, description));
            }

            Chip("problems", "问题", "插入 VS「错误列表」里的编译错误");
            Chip("terminal", "终端", "插入剪贴板里的终端输出");
            Chip("web", "黏贴URL以获取内容", "抓取剪贴板里的网址正文并插入");
            Chip("git-log", "Git Commits", "插入最近 10 条提交");
            Chip("current", "当前文件", "插入当前编辑的文件与光标位置");
            Chip("selection", "当前选中代码", "插入编辑器里选中的代码");
            Chip("debug", "调试状态", "插入断点 / 当前栈帧与局部变量");
            Chip("git-changes", "Git 变更", "插入当前未提交的改动清单");

            if (_mentions == null || !_mentions.IsReady)
            {
                if (MentionList.Children.Count == 0)
                {
                    MentionList.Children.Add(MentionHint("未检测到工作区，无法列出项目文件在 VS 里打开解决方案或文件夹后再试"));
                }

                return;
            }

            // Git 改动：这些文件最可能是用户接下来要提到的东西，排在最前
            if (Matches(text, "git"))
            {
                var changes = GitWorkspace.LoadChanges(_workspaceRoot);
                if (changes.Count > 0)
                {
                    MentionList.Children.Add(MentionGroupHeader("Git 改动"));
                    foreach (var change in changes.Take(8))
                    {
                        MentionList.Children.Add(MentionRow(
                            new MentionItem(change.Path, isDirectory: false), change.Marker));
                    }
                }
            }

            var items = _mentions.Search(text, 12);
            if (items.Count == 0)
            {
                if (MentionList.Children.Count == 0)
                {
                    MentionList.Children.Add(MentionHint("没有匹配「" + text + "」的文件或目录"));
                }

                return;
            }

            var directories = items.Where(i => i.IsDirectory).ToList();
            var files = items.Where(i => !i.IsDirectory).ToList();

            if (directories.Count > 0)
            {
                MentionList.Children.Add(MentionGroupHeader("文件夹"));
                foreach (var directory in directories)
                {
                    MentionList.Children.Add(MentionRow(directory, "目录"));
                }
            }

            if (files.Count > 0)
            {
                MentionList.Children.Add(MentionGroupHeader("文件"));
                foreach (var file in files)
                {
                    MentionList.Children.Add(MentionRow(file, "文件"));
                }
            }
        }

        /// <summary>
        /// 下钻视图：列出项目的<b>全部文件夹</b>或<b>全部文件</b>（可按当前筛词继续过滤）
        /// </summary>
        private void RenderMentionDrilldown(string text, bool folders)
        {
            MentionList.Children.Add(MentionBackRow());

            if (_mentions == null || !_mentions.IsReady)
            {
                MentionList.Children.Add(MentionHint("未检测到工作区，无法列出项目内容在 VS 里打开解决方案或文件夹后再试"));
                return;
            }

            IEnumerable<MentionItem> matched = _mentions.All.Where(i => i.IsDirectory == folders);

            if (text.Length > 0)
            {
                matched = matched.Where(i =>
                    i.RelativePath.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // 上限保护：超大仓库一次铺几千行会把工具窗口拖卡
            const int limit = 400;
            var ordered = matched
                .OrderBy(i => i.RelativePath.Count(c => c == '/'))
                .ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();

            if (ordered.Count == 0)
            {
                MentionList.Children.Add(MentionHint(folders ? "没有匹配的文件夹" : "没有匹配的文件"));
                return;
            }

            MentionList.Children.Add(MentionGroupHeader(folders ? "所有文件夹" : "所有文件"));
            foreach (var item in ordered)
            {
                MentionList.Children.Add(MentionRow(item, folders ? "目录" : "文件"));
            }

            if (ordered.Count >= limit)
            {
                MentionList.Children.Add(MentionHint("只显示前 " + limit + " 项，输入关键字可继续缩小范围"));
            }
        }

        /// <summary>
        /// 分类行：图标 + 标题 + 一句说明；<c>Tag</c> 决定点击是插入上下文还是切换视图
        /// </summary>
        private FrameworkElement MentionCategoryRow(string icon, string title, string description, string tag)
        {
            var button = new Button
            {
                Tag = tag,
                ToolTip = description,
            };

            var style = TryFindResource("MenuItemButton") as Style;
            if (style != null)
            {
                button.Style = style;
            }

            button.Click += MentionItem_Click;

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var groupGlyph = new Ui.Primitive.Icon
            {
                IconName = icon,
                Width = 13,
                Height = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            groupGlyph.SetResourceReference(Ui.Primitive.Icon.ForegroundProperty, "Ap.Accent");
            row.Children.Add(groupGlyph);

            var groupLabel = new TextBlock
            {
                Text = title,
                Margin = new Thickness(9, 0, 0, 0),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            groupLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgMenu");
            row.Children.Add(groupLabel);

            button.Content = row;
            return button;
        }

        /// <summary>
        /// 「返回」行：从全部文件夹 / 全部文件回到分类根菜单
        /// </summary>
        private FrameworkElement MentionBackRow()
        {
            var button = new Button
            {
                Tag = MentionViewTagPrefix + "root",
            };

            var style = TryFindResource("MenuItemButton") as Style;
            if (style != null)
            {
                button.Style = style;
            }

            button.Click += MentionItem_Click;

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var backGlyph = new Ui.Primitive.Icon
            {
                IconName = "chevron-left",
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            backGlyph.SetResourceReference(Ui.Primitive.Icon.ForegroundProperty, "Ap.FgFaint");
            row.Children.Add(backGlyph);

            var backLabel = new TextBlock
            {
                Text = "返回",
                Margin = new Thickness(7, 0, 0, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            backLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
            row.Children.Add(backLabel);

            button.Content = row;
            return button;
        }

        private static bool Matches(string query, string keyword)
        {
            return query.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private FrameworkElement MentionGroupHeader(string title)
        {
            var header = new TextBlock
            {
                Text = title,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(9, 8, 9, 3),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
            return header;
        }

        /// <summary>
        /// 上下文芯片：<c>Tag</c> 用 <c>#context:</c> 前缀标记，点击时现算文本
        /// </summary>
        private FrameworkElement MentionChip(string key, string title, string description)
        {
            var button = new Button
            {
                Tag = "#context:" + key,
                ToolTip = description,
            };

            var style = TryFindResource("MenuItemButton") as Style;
            if (style != null)
            {
                button.Style = style;
            }

            button.Click += MentionItem_Click;

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var contextGlyph = new Ui.Primitive.Icon
            {
                IconName = "book",
                Width = 13,
                Height = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            contextGlyph.SetResourceReference(Ui.Primitive.Icon.ForegroundProperty, "Ap.Teal");
            row.Children.Add(contextGlyph);

            var contextLabel = new TextBlock
            {
                Text = title,
                Margin = new Thickness(9, 0, 0, 0),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            contextLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgMenu");
            row.Children.Add(contextLabel);

            var contextTag = new TextBlock
            {
                Text = "上下文",
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            contextTag.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
            row.Children.Add(contextTag);

            button.Content = row;
            return button;
        }

        /// <summary>
        /// <c>@web</c>：抓取剪贴板里的网址正文并插入
        /// </summary>
        private void BeginWebContext()
        {
            var url = Clipboard.ContainsText() ? (Clipboard.GetText() ?? string.Empty).Trim() : string.Empty;

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                InsertAtCursor(
                    "\n（@web：请先把要抓取的网址复制到剪贴板再点「网页正文」；" +
                    "也可以直接把网址贴进输入框，让模型用 open_url 抓取）\n");
                return;
            }

            _ = FetchWebContextAsync(new BrowserTool(_workspaceRoot), url);
        }

        private async Task FetchWebContextAsync(BrowserTool tool, string url)
        {
            string text = null;
            try
            {
                text = await tool.FetchTextAsync(url, CancellationToken.None);
            }
            catch (Exception)
            {
                // 失败按空处理，下面给提示
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                InsertAtCursor("\n（@web：抓取失败或正文为空：" + url + "）\n");
                return;
            }

            InsertAtCursor("\n以下是网页 " + url + " 的正文：\n\n" + text + "\n");
        }

        /// <summary>
        /// 把上下文芯片展开成要插入输入框的文本（点的时候才算，避免每敲一个字就 git 一次）
        /// </summary>
        private string BuildContextText(string key)
        {
            // 取「当前选中代码」要读 DTE，只能在 UI 线程做
            // 这里显式断言之，分析器才认得出这条路径（否则 VSTHRD010 会一路飘上去）
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            switch (key)
            {
                case "git-changes":
                    return GitWorkspace.BuildChangesContext(_workspaceRoot);

                case "git-log":
                    return GitWorkspace.BuildRecentCommitsContext(_workspaceRoot, 10);

                case "selection":
                    // 读 VS 编辑器里当前选中的文本（没选中时给一句明确提示，而不是插入空白）
                    var selected = VsFileOpener.TryGetSelection();
                    return string.IsNullOrWhiteSpace(selected)
                        ? "（编辑器里当前没有选中的代码）"
                        : "以下是编辑器里选中的代码：\n\n" + selected;

                case "problems":
                    // 读 VS「错误列表」里的编译错误（只在 UI 线程可用；这里已在 UI 线程）
                    var diagnostics = VsDiagnosticsReader.TryRead(50);
                    return string.IsNullOrWhiteSpace(diagnostics)
                        ? "（当前「错误列表」里没有错误）"
                        : "以下是 Visual Studio「错误列表」里的编译错误：\n" + diagnostics;

                case "current":
                    // 当前活动文档的路径 + 光标行号
                    var current = VsContextProvider.DescribeActiveDocument(_workspaceRoot);
                    return string.IsNullOrWhiteSpace(current)
                        ? "（当前没有打开的编辑器文档）"
                        : current;

                case "debug":
                    // 读 VS 调试器的只读状态（断点 / 当前栈帧 / 局部变量）
                    var debug = VsDebuggerReader.TryRead();
                    return string.IsNullOrWhiteSpace(debug)
                        ? "（当前不在调试状态，或调试器不可用）"
                        : "以下是 Visual Studio 调试器的当前状态：\n" + debug;

                case "terminal":
                    // 终端上下文：把剪贴板里的终端输出原样插入
                    // VS 扩展拿不到集成终端的缓冲区，让用户在终端里复制后点这里是最稳的做法
                    var terminal = Clipboard.ContainsText() ? (Clipboard.GetText() ?? string.Empty).Trim() : string.Empty;
                    return terminal.Length == 0
                        ? "（剪贴板为空：请先在终端里复制输出，再点「终端」）"
                        : "以下是终端输出：\n\n" + terminal;

                case "web":
                    // 由 BeginWebContext 异步处理（这里不该被走到）
                    return string.Empty;

                default:
                    return string.Empty;
            }
        }

        private FrameworkElement MentionHint(string text)
        {
            var hint = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Margin = new Thickness(9, 7, 9, 7),
                TextWrapping = TextWrapping.Wrap,
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
            return hint;
        }

        /// <summary>
        /// 一行候选（标记取「文件 / 目录」）
        /// </summary>
        private FrameworkElement MentionRow(MentionItem item)
        {
            return MentionRow(item, item.IsDirectory ? "目录" : "文件");
        }

        /// <summary>
        /// 一行候选：图标 + 相对路径 + 自定义标记（Git 改动用 U / M / A / D / R）
        /// </summary>
        private FrameworkElement MentionRow(MentionItem item, string marker)
        {
            var button = new Button
            {
                // Tag 直接挂 MentionItem 本体：点了会变成一枚「图标 + 名称」的提及芯片，
                // 不再往输入框塞一长串路径（发送时再还原成 @路径，见 ComposeInputText）
                Tag = item,
                ToolTip = item.RelativePath,
            };

            var style = TryFindResource("MenuItemButton") as Style;
            if (style != null)
            {
                button.Style = style;
            }

            button.Click += MentionItem_Click;

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var itemGlyph = new Ui.Primitive.Icon
            {
                IconName = item.IsDirectory ? "folder" : "file",
                Width = 13,
                Height = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            itemGlyph.SetResourceReference(Ui.Primitive.Icon.ForegroundProperty, item.IsDirectory ? "Ap.Teal" : "Ap.Accent");
            row.Children.Add(itemGlyph);

            var itemPath = new TextBlock
            {
                Text = item.RelativePath,
                Margin = new Thickness(9, 0, 0, 0),
                FontFamily = TryFindResource("Ap.FontCode") as FontFamily ?? new FontFamily("Consolas"),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            itemPath.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgMenu");
            row.Children.Add(itemPath);

            var itemMarker = new TextBlock
            {
                Text = marker,
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            itemMarker.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgFaint");
            row.Children.Add(itemMarker);

            button.Content = row;
            return button;
        }

        private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 有动作在等拍板时，快捷键优先：Enter 接受 / Esc 拒绝 ——
            // 与请求条上那行提示保持一致，避免「提示了却不生效」
            if (HasPendingApproval)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    DecideApproval(true);
                    return;
                }

                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    DecideApproval(false);
                    return;
                }
            }

            // 生成中按 Esc = 停止（和「发送」按钮变成「停止」是同一个动作）
            if (e.Key == Key.Escape && _session.IsBusy)
            {
                e.Handled = true;
                StopCurrent();
                ClosePopups();
                return;
            }

            if ((e.KeyboardDevice.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Enter)
            {
                e.Handled = true;
                Submit();
                return;
            }

            if (e.Key == Key.Enter && (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
            {
                if (MentionPopup.IsOpen)
                {
                    return;
                }

                e.Handled = true;
                Submit();
                return;
            }

            if (e.Key == Key.Escape)
            {
                // 在下钻视图（全部文件夹 / 全部文件）里按 Esc：先退回分类根菜单，再按一次才关闭
                if (MentionPopup.IsOpen && _mentionView != MentionView.Root)
                {
                    SwitchMentionView("root");
                    return;
                }

                ClosePopups();
            }
        }

        /// <summary>
        /// 回车提交：空闲就直接发；<b>生成中先排队</b> —— 这就是「正在对话也能继续发任务」的入口
        /// （按钮在生成中是「停止」，不会再被用来发送）
        /// </summary>
        private void Submit()
        {
            var text = Input.Text.Trim();

            // 正在等用户回答提问：输入框里的内容就是**答案**，而不是一条新任务
            // （提问是阻塞的，此时发送按钮的语义随之改变）
            if (HasPendingQuestion)
            {
                if (text.Length == 0)
                {
                    return;
                }

                Input.Text = string.Empty;
                UpdateSendState();
                AnswerQuestion(text);
                return;
            }

            // 允许「只贴图 / 只挂提及、不打字」：只发一张报错截图、或只挂几个文件很常见
            if (text.Length == 0 && _pendingImages.Count == 0 && _pendingMentions.Count == 0)
            {
                return;
            }

            if (_session.IsBusy)
            {
                // 排队：把芯片还原成 @路径 后再入队
                var queued = ComposeInputText();
                Input.Text = string.Empty;
                TakePendingMentions();
                Enqueue(queued);
                UpdateSendState();
                return;
            }

            _ = SendCurrentAsync();
        }

        /// <summary>
        /// 发送按钮：<b>始终是发送</b> 生成中点了就排队，
        /// 中断交给输入框上方的通栏「取消」与 Esc，不再让同一个按钮一会儿发送一会儿停止
        /// </summary>
        private void Send_Click(object sender, RoutedEventArgs e) => Submit();

        /// <summary>
        /// 停止当前生成
        /// </summary>
        private void StopCurrent()
        {
            try
            {
                _cts?.Cancel();
            }
            catch (Exception)
            {
                // 已经完成/释放：忽略
            }

            CancelPendingApprovals("已取消");
            CancelPendingQuestion("已取消");
        }

        /// <summary>
        /// 把还没拍板的动作按「拒绝」收尾：卡片标注状态，等待中的 Agent 立刻继续
        /// </summary>
        private void CancelPendingApprovals(string status)
        {
            // 排队中的审批拿到信号量后也要立刻退出，不能在本轮取消后又弹出一张
            _approvalCancelled = true;

            var pending = _pendingApproval;
            var card = _pendingCard;

            _pendingApproval = null;
            _pendingCard = null;

            if (card != null)
            {
                card.IsRejected = true;
                card.IsAccepted = false;
                card.StatusText = status;
                card.IsPendingApproval = false;
            }

            RefreshRequestBar();

            pending?.TrySetResult(false);
        }

        /// <summary>
        /// 插入命令的提示词，并把它已经敲下的 <c>/query</c> 一并替换掉
        /// </summary>
        private void InsertSlashCommand(string key)
        {
            var command = SlashCommands.Find(key);
            if (command == null)
            {
                return;
            }

            RemoveSlashQuery();

            if (command.InsertText.Length > 0)
            {
                InsertAtCursor(command.InsertText);
            }
        }

        /// <summary>
        /// 执行命令型动作（/clear）
        /// </summary>
        private void RunSlashAction(string key)
        {
            RemoveSlashQuery();

            switch ((key ?? string.Empty).ToLowerInvariant())
            {
                case "clear":
                    if (_session.IsBusy)
                    {
                        AddNote("正在生成中，等这一轮结束后再清空会话");
                        return;
                    }

                    NewConversation();
                    return;

                default:
                    return;
            }
        }

        /// <summary>
        /// 把光标前那段 <c>/query</c> 从输入框里抹掉（插入/执行命令前调用）
        /// </summary>
        private void RemoveSlashQuery()
        {
            var text = Input.Text ?? string.Empty;
            var caret = Math.Max(0, Math.Min(Input.SelectionStart, text.Length));

            var slash = caret > 0 ? text.LastIndexOf('/', caret - 1) : -1;
            if (slash < 0)
            {
                return;
            }

            Input.Text = text.Remove(slash, caret - slash);
            Input.SelectionStart = slash;
        }

        private void InsertAtCursor(string text)
        {
            var start = Input.SelectionStart;
            Input.Text = Input.Text.Insert(start, text);
            Input.SelectionStart = start + text.Length;
        }

        // ============================================================
        // 从「解决方案资源管理器」拖入文件
        // ============================================================

        /// <summary>
        /// 拖入悬停：拖的是文件就显示「复制」光标
        /// </summary>
        private void Input_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        /// <summary>
        /// 拖放落点：把拖进来的文件转成 <c>@提及</c>；图片则作为多模态附件
        /// </summary>
        private void Input_Drop(object sender, DragEventArgs e)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // 非文件拖放（例如文本框内部拖动文字）：交回默认处理
                return;
            }

            e.Handled = true;

            // 拖入即视为「现在就要用工作区」：顺手确认一次根目录，好把绝对路径转成相对路径
            RefreshMentionIndex(forceRescan: false);

            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            var multimodal = _settings == null || _settings.Multimodal;
            var added = false;

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                // 图片：作为附件（拖入一张报错截图是最常见的用法）
                if (multimodal && IsImagePath(path) && File.Exists(path) && TryAddImageFile(path))
                {
                    continue;
                }

                var relative = ToMentionPath(path);
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                // 普通文件 / 文件夹：挂成「图标 + 名称」的提及芯片（与 @ 菜单一致）
                AddMention(new MentionItem(relative, Directory.Exists(path)));
                added = true;
            }

            if (added)
            {
                Input.Focus();
            }
        }

        /// <summary>
        /// 把拖入的绝对路径转成提及文本用的路径（工作区内转相对路径；统一用 <c>/</c>）
        /// </summary>
        private string ToMentionPath(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var root = _workspace?.Root ?? _workspaceRoot;

                if (!string.IsNullOrEmpty(root))
                {
                    var basePath = Path.GetFullPath(root).TrimEnd('\\', '/');
                    var prefix = basePath + Path.DirectorySeparatorChar;

                    if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return full.Substring(prefix.Length).Replace('\\', '/');
                    }
                }

                return full.Replace('\\', '/');
            }
            catch (Exception)
            {
                return (path ?? string.Empty).Replace('\\', '/');
            }
        }

    }
}

