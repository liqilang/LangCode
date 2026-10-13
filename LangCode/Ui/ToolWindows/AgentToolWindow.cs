using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// LangCode 工具窗口
    /// </summary>
    [Guid("A1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")]
    public sealed class AgentToolWindow : ToolWindowPane
    {
        public AgentToolWindow() : base(null)
        {
            Caption = "LangCode —— AI 编程助手";

            // 工具窗口内容 = WPF 用户控件（1:1 复刻 UIUX 设计稿）
            Content = new AgentToolWindowControl();
        }
    }
}
