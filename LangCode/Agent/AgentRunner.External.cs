using LangCode.Chat;
using LangCode.Domain.Catalog;
using LangCode.Domain.Chat;
using LangCode.Domain.Settings;
using LangCode.Mcp;
using LangCode.Persistence;
using LangCode.VsIntegration;
using LangCode.Workspace;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LangCode.Agent
{
    /// <summary>
    /// AgentRunner 分部：有外部副作用的工具（命令 / 测试 / 浏览器 / MCP / 调试器）—— 统一「先问再做」
    /// </summary>
    public sealed partial class AgentRunner
    {
        /// <summary>
        /// 执行有副作用的工具：**先问再做**
        /// </summary>
        private async Task<ToolResult> ExecuteExternalAsync(ToolAction action, bool auto, AgentCallbacks callbacks, CancellationToken cancellationToken)
        {
            var preview = BuildPreview(action);

            if (!auto && callbacks.ApproveActionAsync != null)
            {
                var approved = await callbacks.ApproveActionAsync(action, new ToolResult { Ok = true, Output = preview }).ConfigureAwait(false);

                if (!approved)
                {
                    return new ToolResult
                    {
                        Ok = false,
                        Output = "用户拒绝了这次操作（未执行）：" + action.Name,
                    };
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            switch (action.Name)
            {
                case "run_command":
                    {
                        var command = action.Get("command");
                        var directory = action.Get("working_directory");
                        var result = await _commands.RunAsync(command, directory, callbacks.OnCommandOutput, cancellationToken).ConfigureAwait(false);

                        // 确认卡上显示的那一行要能对上实际执行的命令
                        result.Output = "命令：" + _commands.Describe(command, directory) + "\n\n" + result.Output + FormatExitCode(result);
                        return result;
                    }

                case "run_tests":
                    {
                        var testCommand = ResolveTestCommand();
                        if (testCommand == null)
                        {
                            return new ToolResult
                            {
                                Ok = false,
                                Output = "无法自动识别测试命令：请在工作区打开一个受支持的项目，或在设置里填写「验证命令」",
                            };
                        }

                        testCommand = ApplyTestFilter(testCommand, action.Get("filter"));
                        return await RunTestsWithReportAsync(testCommand, callbacks, cancellationToken).ConfigureAwait(false);
                    }

                case "debug_inspect":
                case "debug_control":
                    return await RunDebugAsync(action, callbacks, cancellationToken).ConfigureAwait(false);

                case "open_url":
                    {
                        var url = action.Get("url");
                        var fetch = string.Equals(action.Get("fetch"), "true", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(action.Get("fetch"), "1", StringComparison.Ordinal);

                        return await _browser.OpenAsync(url, fetch, cancellationToken).ConfigureAwait(false);
                    }

                case "web_search":
                    {
                        var limit = ParseInt(action.Get("limit")) ?? (_settings?.WebSearchMaxResults ?? 5);
                        return await _webSearch.SearchAsync(action.Get("query"), limit, cancellationToken).ConfigureAwait(false);
                    }
                case "browser_action":
                    {
                        if (_settings != null && !_settings.BrowserAutomation)
                        {
                            return new ToolResult
                            {
                                Ok = false,
                                Output = "浏览器自动化已在设置里关闭（设置 → 高级 → 自动化）",
                            };
                        }

                        return await _browserAutomation.ExecuteAsync(
                            action.Get("action"),
                            action.Get("url"),
                            action.Get("selector"),
                            action.Get("value"),
                            action.Get("script"),
                            cancellationToken).ConfigureAwait(false);
                    }

                case "mcp_call":
                    {
                        var text = await _mcp.CallAsync(_settings, action.Get("server"), action.Get("tool"), action.Get("arguments"), cancellationToken).ConfigureAwait(false);

                        return new ToolResult { Ok = true, Output = text };
                    }

                default:
                    return new ToolResult { Ok = false, Output = "✗ 未知工具：" + action.Name };
            }
        }

        /// <summary>
        /// 运行测试；<c>dotnet test</c> 时附带 TRX 记录器并把报告解析成结构化汇总
        /// （通过 / 失败计数 + 失败用例与堆栈），让自动修复有的放矢
        /// </summary>
        private async Task<ToolResult> RunTestsWithReportAsync(string testCommand, AgentCallbacks callbacks, CancellationToken cancellationToken)
        {
            if (!VsTestRunner.IsDotnetTest(testCommand))
            {
                var plain = await _commands.RunAsync(testCommand, string.Empty, callbacks.OnCommandOutput, cancellationToken).ConfigureAwait(false);
                plain.Output = "测试命令：" + _commands.Describe(testCommand, string.Empty) + "\n\n" + plain.Output + FormatExitCode(plain);
                return plain;
            }

            var resultsDir = Path.Combine(Path.GetTempPath(), "LangCode-tests", Guid.NewGuid().ToString("N"));
            var trxPath = Path.Combine(resultsDir, "langcode-results.trx");
            try
            {
                Directory.CreateDirectory(resultsDir);
            }
            catch (Exception)
            {
                // 建目录失败：仍执行测试，只是拿不到结构化报告
            }

            var fullCommand = testCommand +
                              " --logger \"trx;LogFileName=langcode-results.trx\"" +
                              " --results-directory \"" + resultsDir + "\"";

            var result = await _commands.RunAsync(fullCommand, string.Empty, callbacks.OnCommandOutput, cancellationToken).ConfigureAwait(false);

            var report = VsTestRunner.ParseTrx(trxPath);
            var summary = VsTestRunner.Describe(report);

            result.Output = "测试命令：" + _commands.Describe(testCommand, string.Empty) + "\n\n" +
                            summary + "\n\n---- 原始输出（尾部） ----\n" + result.Output +
                            FormatExitCode(result);

            if (report.HasResults && report.Failed > 0)
            {
                result.Ok = false;
            }

            return result;
        }

        /// <summary>
        /// 把退出码作为独立一行附在工具输出末尾（输出本体保持干净，便于解析）
        /// </summary>
        private static string FormatExitCode(ToolResult result)
        {
            if (result == null || result.Canceled || result.ExitCode < 0)
            {
                return string.Empty;
            }

            return "\n（退出码 " + result.ExitCode + "）";
        }

        /// <summary>
        /// 调试器操作：统一转成 <see cref="DebugCommand"/> 交给界面侧（在 UI 线程执行 DTE）
        /// </summary>
        private async Task<ToolResult> RunDebugAsync(ToolAction action, AgentCallbacks callbacks, CancellationToken cancellationToken)
        {
            if (callbacks.DebugControlAsync == null)
            {
                return new ToolResult
                {
                    Ok = false,
                    Output = "当前环境不支持调试器操作（未在 Visual Studio 宿主中运行）",
                };
            }

            var command = new DebugCommand
            {
                Action = (action.Get("action") ?? string.Empty).Trim().ToLowerInvariant(),
                File = action.Get("path") ?? action.Get("file"),
                Line = ParseInt(action.Get("line")) ?? 0,
                Condition = action.Get("condition"),
                Expression = action.Get("expression"),
                MaxItems = ParseInt(action.Get("limit")) ?? 30,
            };

            if (command.Action.Length == 0)
            {
                return new ToolResult { Ok = false, Output = "action 不能为空（如 status / list_breakpoints / get_stack）" };
            }

            var text = await callbacks.DebugControlAsync(command, cancellationToken).ConfigureAwait(false);
            var ok = !string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith("✗", StringComparison.Ordinal);

            return new ToolResult
            {
                Ok = ok,
                Output = string.IsNullOrWhiteSpace(text) ? "（调试器没有返回内容）" : text,
            };
        }

        /// <summary>
        /// 按探测到的技术栈给出测试命令；识别不出时退回用户的「验证命令」
        /// 两者都没有时返回 <c>null</c>
        /// </summary>
        private string ResolveTestCommand()
        {
            var kind = (_toolchain?.Kind ?? string.Empty).ToLowerInvariant();

            if (kind.Contains("node") || kind.Contains("npm") || kind.Contains("javascript") || kind.Contains("typescript"))
            {
                var packageManager = string.IsNullOrWhiteSpace(_toolchain?.PackageManager) ? "npm" : _toolchain.PackageManager;
                return packageManager + " test";
            }

            if (kind.Contains(".net") || kind.Contains("dotnet") || kind.Contains("c#") ||
                kind.Contains("f#") || kind.Contains("vb") || kind.Contains("visual basic"))
            {
                return "dotnet test";
            }

            if (kind.Contains("python"))
            {
                return "pytest";
            }

            if (kind.Contains("rust"))
            {
                return "cargo test";
            }

            if (kind.Contains("go"))
            {
                return "go test ./...";
            }

            if (kind.Contains("maven") || kind.Contains("java"))
            {
                return "mvn test";
            }

            // 兜底：用户显式配置的验证命令（若它本身就是测试命令）
            return string.IsNullOrWhiteSpace(_settings?.VerifyCommand) ? null : _settings.VerifyCommand.Trim();
        }

        /// <summary>
        /// <c>filter</c> 目前只对 <c>dotnet test</c> 生效（其它框架的过滤参数各不相同）
        /// </summary>
        private static string ApplyTestFilter(string command, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter) || !command.StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase))
            {
                return command;
            }

            return command + " --filter \"" + filter.Replace("\"", string.Empty) + "\"";
        }
    }
}
