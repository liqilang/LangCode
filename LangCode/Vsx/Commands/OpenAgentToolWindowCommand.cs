using LangCode.Ui.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace LangCode.Vsx.Commands
{
    /// <summary>
    /// 「工具 → 打开 AI Agent 助手窗口」命令
    /// </summary>
    internal static class OpenAgentToolWindowCommand
    {
        /// <summary>
        /// 命令 ID（须与 OpenAgentToolWindow.vsct 里的 OpenAgentToolWindowCommandId 一致）
        /// </summary>
        public const int CommandId = 0x0100;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                return;
            }

            var menuCommandId = new CommandID(LangCodePackage.CommandSet, CommandId);
            var menuItem = new MenuCommand((_, __) => OpenToolWindow(package), menuCommandId);
            commandService.AddCommand(menuItem);
        }

        private static void OpenToolWindow(AsyncPackage package)
        {
            // 异步显示工具窗口（ShowToolWindowAsync 必须在 UI 线程调用）
            package.JoinableTaskFactory.RunAsync(async () =>
            {
                var window = await package.ShowToolWindowAsync(
                    typeof(AgentToolWindow),
                    id: 0,
                    create: true,
                    cancellationToken: package.DisposalToken);

                if (window == null || window.Frame == null)
                {
                    throw new NotSupportedException("无法创建 LangCode 工具窗口");
                }
            });
        }
    }
}

