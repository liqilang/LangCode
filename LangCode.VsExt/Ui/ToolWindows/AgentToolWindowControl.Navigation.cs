using LangCode.VsExt.Domain.Chat;
using LangCode.VsExt.VsIntegration;
using LangCode.VsExt.Workspace;
using System.Windows;
using System.Windows.Input;

namespace LangCode.VsExt.Ui.ToolWindows
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

            // 打开前重新读一次盘，并同步「正在查看」的高亮
            _session.ReloadHistory();
            HistoryDrawer.SetActiveConversation(_session.ConversationId);
            HistoryDrawer.Open();
        }

        private void BtnMcp_Click(object sender, RoutedEventArgs e)
        {
            HistoryDrawer.Close();
            SettingsDrawer.Open();
            SettingsDrawer.SelectTab("mcp");
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            HistoryDrawer.Close();
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
        /// 对话里点击文件名 → 在 VS 里打开（有行号就跳过去）
        /// </summary>
        private void Path_Click(object sender, MouseButtonEventArgs e)
        {
            var block = BlockOf(sender);
            if (block == null || string.IsNullOrWhiteSpace(block.Path))
            {
                return;
            }

            var absolute = ResolveInRoot(block.Path);
            if (absolute == null)
            {
                // 路径可能是 `server · tool` 这类非文件描述，静默忽略
                return;
            }

            e.Handled = true;
            VsFileOpener.TryOpenFile(absolute, block.LineHint);
        }

        /// <summary>
        /// 「本次变更」摘要里点击文件名 → 在 VS 里打开
        /// </summary>
        private void SummaryPath_Click(object sender, MouseButtonEventArgs e)
        {
            var change = (sender as FrameworkElement)?.DataContext as SessionChange;
            if (change == null || string.IsNullOrWhiteSpace(change.Path))
            {
                return;
            }

            var absolute = ResolveInRoot(change.Path);
            if (absolute == null)
            {
                return;
            }

            e.Handled = true;
            VsFileOpener.TryOpenFile(absolute, 0);
        }

        /// <summary>
        /// 相对路径 → 工作区内的绝对路径（不存在或越界返回 null）
        /// </summary>
        private string ResolveInRoot(string relativePath)
        {
            var root = _workspace?.Root;

            // 工作区还没定位（工具窗口先于解决方案打开）时再取一次
            if (string.IsNullOrWhiteSpace(root))
            {
                root = VsWorkspaceLocator.TryGetSolutionDirectory();
            }

            return VsFileOpener.ResolveInRoot(root, relativePath);
        }

        private static int ParseLine(string text)
        {
            return int.TryParse(text, out var line) && line > 0 ? line : 0;
        }

    }
}

