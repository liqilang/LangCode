namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：供外部（编辑器右键动作 / 命令）把一条提示词送进对话
    /// </summary>
    public partial class AgentToolWindowControl
    {
        /// <summary>
        /// 由 VS 命令调用：把 <paramref name="text"/> 作为一条用户消息发送（若输入框已有草稿则保留在前）
        /// </summary>
        public void SubmitExternalPrompt(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var existing = Input.Text ?? string.Empty;
            Input.Text = existing.Trim().Length == 0 ? text : existing.TrimEnd() + "\n\n" + text;
            Input.SelectionStart = Input.Text.Length;
            Input.Focus();

            Submit();
        }
    }
}
