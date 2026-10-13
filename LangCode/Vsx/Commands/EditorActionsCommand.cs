using EnvDTE;
using EnvDTE80;
using LangCode.Chat;
using LangCode.Persistence;
using LangCode.Ui.ToolWindows;
using LangCode.VsIntegration;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace LangCode.Vsx.Commands
{
    /// <summary>
    /// 编辑器动作命令：代码窗口右键的「解释 / 重构 / 补测试 / 补文档 / 修复」，
    /// 以及快捷键 <c>Ctrl+K</c> 的选区内联编辑
    /// </summary>
    internal static class EditorActionsCommand
    {
        public const int ExplainId = 0x0101;
        public const int RefactorId = 0x0102;
        public const int GenerateTestsId = 0x0103;
        public const int GenerateDocsId = 0x0104;
        public const int FixId = 0x0105;
        public const int InlineEditId = 0x0110;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                return;
            }

            Register(commandService, package, ExplainId, "explain");
            Register(commandService, package, RefactorId, "refactor");
            Register(commandService, package, GenerateTestsId, "tests");
            Register(commandService, package, GenerateDocsId, "docs");
            Register(commandService, package, FixId, "fix");
            Register(commandService, package, InlineEditId, "inline");
        }

        private static void Register(OleMenuCommandService commandService, AsyncPackage package, int id, string action)
        {
            var commandId = new CommandID(LangCodePackage.CommandSet, id);
            var command = new MenuCommand((_, __) => Run(package, action), commandId)
            {
                Enabled = true,
                Visible = true,
            };

            commandService.AddCommand(command);
        }

        private static void Run(AsyncPackage package, string action)
        {
            package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    if (action == "inline")
                    {
                        await RunInlineEditAsync(package);
                    }
                    else
                    {
                        await RunPromptActionAsync(package, action);
                    }
                }
                catch (Exception)
                {
                    // 命令失败不该打断用户工作；静默（可在 VS 输出窗口看）
                }
            });
        }

        /// <summary>
        /// 把「提示词 + 选中代码」送进对话窗口
        /// </summary>
        private static async Task RunPromptActionAsync(AsyncPackage package, string action)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var selection = VsFileOpener.TryGetSelection();
            if (string.IsNullOrWhiteSpace(selection))
            {
                return;
            }

            var file = ActiveDocumentName();
            var prompt = BuildPrompt(action, selection, file);

            var window = await package.ShowToolWindowAsync(
                typeof(AgentToolWindow), id: 0, create: true, cancellationToken: package.DisposalToken);

            if (window?.Content is AgentToolWindowControl control)
            {
                control.SubmitExternalPrompt(prompt);
            }
        }

        /// <summary>
        /// Ctrl+K：生成替换选中代码的新代码，并用 VS 原生撤销栈写回
        /// </summary>
        private static async Task RunInlineEditAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var selection = VsFileOpener.TryGetSelection();
            if (string.IsNullOrWhiteSpace(selection))
            {
                return;
            }

            var dialog = new InlineEditDialog();
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Instruction))
            {
                return;
            }

            var instruction = dialog.Instruction;
            var file = ActiveDocumentName();

            var settings = SettingsStore.Load();
            var backend = ChatBackendFactory.Create(settings);
            var engine = new InlineEngine(backend);

            var code = await engine.EditAsync(
                new InlineEditRequest
                {
                    Model = settings.Model,
                    FilePath = file,
                    Selection = selection,
                    Instruction = instruction,
                },
                CancellationToken.None);

            if (string.IsNullOrWhiteSpace(code) || string.Equals(code, selection, StringComparison.Ordinal))
            {
                return;
            }

            // 回到 UI 线程替换选区（TextSelection.Text 属性赋值会进入 VS 撤销栈，Ctrl+Z 可回退）
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte?.ActiveDocument?.Selection is TextSelection textSelection)
            {
                textSelection.Text = code;
            }
        }

        private static string ActiveDocumentName()
        {
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                return dte?.ActiveDocument?.Name ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 按动作拼提示词（选中代码作为上下文正文一起发出）
        /// </summary>
        private static string BuildPrompt(string action, string selection, string file)
        {
            string lead;
            switch (action)
            {
                case "refactor":
                    lead = "重构下面这段代码：在不改变行为的前提下提升可读性与结构，先说明你改了什么，再给出代码";
                    break;
                case "tests":
                    lead = "为下面这段代码补上单元测试：先说明测试框架与放在哪个文件，再动手实现";
                    break;
                case "docs":
                    lead = "为下面这段代码补充文档注释（按语言惯例，如 C# 的 XML doc、JS/TS 的 JSDoc）";
                    break;
                case "fix":
                    lead = "定位并修复下面这段代码里的问题：先说清根因，再给出最小改动";
                    break;
                default:
                    lead = "解释下面这段代码的作用、关键流程与潜在陷阱";
                    break;
            }

            var builder = new System.Text.StringBuilder();
            builder.Append(lead).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(file))
            {
                builder.Append("（文件：").Append(file).Append("）\n");
            }

            builder.Append("```\n").Append(selection).Append("\n```\n");
            return builder.ToString();
        }
    }
}

