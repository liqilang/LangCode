using LangCode.Domain.Chat;
using LangCode.VsIntegration;
using LangCode.Workspace;
using System.Windows;
using System.Windows.Input;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：跟随打开 / 路径跳转 / 抽屉入口类定义与字段见 AgentToolWindowControl.xaml.cs
    /// </summary>
    public partial class AgentToolWindowControl
    {
        // ============================================================
        // 抽屉
        // ============================================================
        private void BtnHistory_Click(object sender, RoutedEventArgs e)
        {
            SettingsDrawer.Close();
            McpDrawer.Close();

            // 打开前重新读一次盘，并同步「正在查看」的高亮
            _session.ReloadHistory();
            HistoryDrawer.SetActiveConversation(_session.ConversationId);
            HistoryDrawer.Open();
        }

        /// <summary>
        /// 主页「最近的会话」标题右侧的「查看全部」：与头部历史按钮同一条路径，
        /// 直接打开历史抽屉、定位到完整的会话列表
        /// </summary>
        private void HomeViewAllHistory_Click(object sender, RoutedEventArgs e)
        {
            BtnHistory_Click(sender, e);
        }

        private void BtnMcp_Click(object sender, RoutedEventArgs e)
        {
            HistoryDrawer.Close();
            SettingsDrawer.Close();
            McpDrawer.Open();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            HistoryDrawer.Close();
            McpDrawer.Close();
            SettingsDrawer.Open();
            SettingsDrawer.SelectTab("general");
        }

        // ============================================================
        // 跟随显示 / 打开文件（点击路径也能直接跳转）
        // ============================================================
        /// <summary>
        /// 跟随打开一个文件（读取 / 新增时用）
        /// </summary>
        private void FollowOpen(string relativePath, int line)
        {
            // 解析路径 / 打开文件都要碰 DTE，只能在 UI 线程执行
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (!_settings.FollowFileChanges)
            {
                return;
            }

            var absolute = ResolveInRoot(relativePath);
            if (absolute != null)
            {
                VsFileOpener.TryOpenFile(absolute, line);
            }
        }

        /// <summary>
        /// 跟随显示一次改动：打开 VS 的差异对比（修改前 vs 现在）
        /// </summary>
        private void FollowEdit(PendingEdit edit)
        {
            // 打开差异对比同样依赖 DTE，只能在 UI 线程执行
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (!_settings.FollowFileChanges || edit == null)
            {
                return;
            }

            var absolute = ResolveInRoot(edit.RelativePath);
            if (absolute != null)
            {
                VsFileOpener.TryOpenDiff(edit.OriginalText, absolute);
            }
        }

        /// <summary>
        /// 对话里点击文件名 → 在 VS 里打开（读文件等有行号就跳过去；编辑类直接开差异对比）
        /// </summary>
        private void Path_Click(object sender, MouseButtonEventArgs e)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (OpenBlockInVs(BlockOf(sender)))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// 点整张卡片 → 与点文件名同一条路径：读文件 / 新增文件在 VS 里打开文件，
        /// 编辑类卡片直接开 VS 的差异对比 —— 对话里不再就地展开内容
        /// </summary>
        private void Card_Click(object sender, MouseButtonEventArgs e)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (OpenBlockInVs(BlockOf(sender)))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// 把一个卡片对应的文件在 VS 里打开：编辑类卡片优先开「改动前 vs 改动后」的差异对比，
        /// 其余（读文件 / 新增文件等）直接打开文件本身。返回是否真的打开了什么
        /// </summary>
        private bool OpenBlockInVs(ChatBlock block)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (block == null || string.IsNullOrWhiteSpace(block.Path))
            {
                return false;
            }

            // 卡片自带「改动后」全文（= 待审批的新增 / 编辑）：此时磁盘上可能还没有这个文件，
            // 解析路径不能要求它已存在，否则「新增文件」的卡片点了没反应
            var hasProposed = block.Kind == ChatBlockKind.DiffCard &&
                (!string.IsNullOrEmpty(block.BeforeText) || !string.IsNullOrEmpty(block.AfterText));

            var absolute = hasProposed
                ? ResolveInRootAllowMissing(block.Path)
                : ResolveInRoot(block.Path);

            if (absolute == null)
            {
                // 路径可能是 `server · tool` / 命令文本这类非文件描述，静默忽略
                return false;
            }

            if (block.Kind == ChatBlockKind.DiffCard)
            {
                // ① 卡片自带改动前后全文（本次会话内的编辑 / 新增）：
                //    待审批时看的是「提议的内容」，已应用时看的是落盘后的内容
                if (!string.IsNullOrEmpty(block.BeforeText) || !string.IsNullOrEmpty(block.AfterText))
                {
                    if (VsFileOpener.TryOpenDiffContents(block.BeforeText, block.AfterText, absolute))
                    {
                        return true;
                    }
                }

                // ② 载入历史会话后卡片里没有全文：用本次会话抓到的「改动前」内容作基线
                if (_sessionBaselines.TryGetValue(block.Path, out var baseline) &&
                    VsFileOpener.TryOpenDiff(baseline, absolute))
                {
                    return true;
                }

                // ③ 再退一步：用 git HEAD 里的版本作基线
                var head = GitWorkspace.TryReadHeadContent(_workspace?.Root, block.Path);
                if (head != null && VsFileOpener.TryOpenDiff(head, absolute))
                {
                    return true;
                }
            }

            // 读文件 / 新增文件 / 拿不到基线：至少把文件本身打开
            return VsFileOpener.TryOpenFile(absolute, block.LineHint);
        }

        /// <summary>
        /// 「本次变更」摘要里点击文件名 → 在 VS 里打开**差异对比**（改动前 vs 现在）
        /// </summary>
        private void SummaryPath_Click(object sender, MouseButtonEventArgs e)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if ((sender as FrameworkElement)?.DataContext is not SessionChange change || string.IsNullOrWhiteSpace(change.Path))
            {
                return;
            }

            var absolute = ResolveInRoot(change.Path);
            if (absolute == null)
            {
                return;
            }

            e.Handled = true;

            // ① 优先用本次会话抓到的「改动前」内容开差异对比
            if (_sessionBaselines.TryGetValue(change.Path, out var baseline) &&
                VsFileOpener.TryOpenDiff(baseline, absolute))
            {
                return;
            }

            // ② 重开会话后内存里的基线没了：退而用 git HEAD 里的版本作基线
            var head = GitWorkspace.TryReadHeadContent(_workspace?.Root, change.Path);
            if (head != null && VsFileOpener.TryOpenDiff(head, absolute))
            {
                return;
            }

            // ③ 实在拿不到基线（不是 git 仓库 / 新文件）：至少把文件打开
            VsFileOpener.TryOpenFile(absolute, 0);
        }

        /// <summary>
        /// 相对路径 → 工作区内的绝对路径（不存在或越界返回 null）
        /// </summary>
        private string ResolveInRoot(string relativePath)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            return VsFileOpener.ResolveInRoot(CurrentRootForResolve(), relativePath);
        }

        /// <summary>
        /// 同 <see cref="ResolveInRoot"/>，但**允许文件尚不存在**
        /// （待审批的「新增文件」在磁盘上还没有）
        /// </summary>
        private string ResolveInRootAllowMissing(string relativePath)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            return VsFileOpener.ResolveInRootAllowMissing(CurrentRootForResolve(), relativePath);
        }

        /// <summary>
        /// 解析前重新定位一次工作区根目录：切换项目后即使还没发消息，
        /// 点击文件链接也应落到当前项目根目录
        /// </summary>
        private string CurrentRootForResolve()
        {
            // EnsureWorkspace / VsWorkspaceLocator 都会访问 DTE，只能在 UI 线程执行
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            EnsureWorkspace();

            var root = _workspace?.Root;

            // 工作区还没定位（工具窗口先于解决方案打开）时再取一次
            if (string.IsNullOrWhiteSpace(root))
            {
                root = VsWorkspaceLocator.TryGetSolutionDirectory();
            }

            return root;
        }

        private static int ParseLine(string text)
        {
            return int.TryParse(text, out var line) && line > 0 ? line : 0;
        }

    }
}

