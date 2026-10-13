using LangCode.Ui.Theme;
using LangCode.Ui.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace LangCode.Vsx
{
    /// <summary>
    /// 扩展入口（AsyncPackage）
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("LangCode", "LangCode 编程助手", "1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(AgentToolWindow))]
    [Guid(PackageGuidString)]
    public sealed class LangCodePackage : AsyncPackage
    {
        /// <summary>
        /// 包 GUID（须与 OpenAgentToolWindow.vsct 里的 guidLangCodePackage 一致）
        /// </summary>
        public const string PackageGuidString = "2F3A9C14-6B7D-4E58-9A10-1B2C3D4E5F60";

        /// <summary>
        /// 命令集 GUID（须与 vsct 里的 guidLangCodeCmdSet 一致）
        /// </summary>
        public static readonly Guid CommandSet = new Guid("7C4E1D82-3F5A-4B69-8C7D-9E0F1A2B3C4D");

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // 命令注册需要 UI 线程（OleMenuCommandService 的约束）
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // 主题：检测当前 VS 主题并订阅切换事件，让界面跟随 VS 的深色/浅色主题
            ThemeManager.Initialize();

            await Commands.OpenAgentToolWindowCommand.InitializeAsync(this);
            await Commands.EditorActionsCommand.InitializeAsync(this);
        }
    }
}

