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
    /// AgentRunner 分部：改完文件后的自动验证 / 自动修复（follow-up），以及截断与解析小工具
    /// </summary>
    public sealed partial class AgentRunner
    {
        /// <summary>
        /// 记录 run_tests 的结果：失败时留给收尾 follow-up 再提醒一次。
        /// </summary>
        private void TrackTestResult(ToolAction action, ToolResult result)
        {
            if (action == null || result == null ||
                !string.Equals(action.Name, "run_tests", StringComparison.OrdinalIgnoreCase) ||
                result.Canceled)
            {
                return;
            }

            if (result.Ok)
            {
                _lastTestFailure = null;
                _lastTestFailureFed = false;
                return;
            }

            var output = result.Output ?? string.Empty;

            // 用户拒绝 / 策略拒绝 / 根本没有测试命令：不是「测试失败」，不要触发自动修复
            if (output.IndexOf("用户拒绝", StringComparison.Ordinal) >= 0 ||
                output.IndexOf("安全策略拒绝", StringComparison.Ordinal) >= 0 ||
                output.IndexOf("无法自动识别测试命令", StringComparison.Ordinal) >= 0 ||
                output.IndexOf("已在设置里关闭", StringComparison.Ordinal) >= 0)
            {
                return;
            }

            if (output.Length > 0)
            {
                _lastTestFailure = output;
                _lastTestFailureFed = false;
            }
        }

        /// <summary>
        /// 一轮「无动作」结束后，判断是否还要继续（自动验证 → 测试失败 → 自动修复）。
        /// </summary>
        /// <remarks>
        /// 验证不再「一轮只跑一次」：模型每次修完都会产生新的落盘改动，
        /// 只有存在新改动且未超过 <see cref="AppSettings.AutoVerifyMaxRounds"/> 时才重新验证。
        /// 这样「改完 → 验证失败 → 修 → 再验证」才能真正闭环。
        /// </remarks>
        private async Task<string> BuildFollowUpAsync(AgentCallbacks callbacks, CancellationToken cancellationToken)
        {
            if (SubTaskDepth > 0 || !_editApplied || _settings == null)
            {
                return null;
            }

            var verifyCommand = EffectiveVerifyCommand();
            var verifyLimit = _settings.AutoVerifyMaxRounds > 0 ? _settings.AutoVerifyMaxRounds : 2;
            var hasNewEdits = _editCount != _lastVerifyEditCount;

            // ① 自动验证：有验证命令、有新改动、还没到轮次上限
            if (_settings.AutoVerify && !string.IsNullOrWhiteSpace(verifyCommand) &&
                _verifyRounds < verifyLimit && hasNewEdits)
            {
                _lastVerifyEditCount = _editCount;
                _verifyRounds++;

                var autoDetected = string.IsNullOrWhiteSpace(_settings.VerifyCommand);
                var label = autoDetected ? verifyCommand + "（自动探测）" : verifyCommand;
                var policyBlocked = _permission.CommandBlockReason(verifyCommand);

                if (policyBlocked != null)
                {
                    callbacks.OnNotice?.Invoke("自动验证被工作区策略拒绝：" + policyBlocked);
                }
                else
                {
                    callbacks.OnNotice?.Invoke("自动验证（第 " + _verifyRounds + "/" + verifyLimit + " 轮）：" + label);
                    var result = await _commands.RunAsync(verifyCommand, string.Empty, callbacks.OnCommandOutput, cancellationToken).ConfigureAwait(false);

                    callbacks.OnNotice?.Invoke("自动验证" + (result.Ok ? "通过" : "失败") + "：" + label);

                    if (!result.Ok)
                    {
                        return "系统自动执行了验证命令（第 " + _verifyRounds + "/" + verifyLimit + " 轮）：`" + verifyCommand + "`" +
                               (autoDetected ? "（根据工作区自动探测）" : string.Empty) + "\n" +
                               "结果：失败\n" +
                               "输出：\n```\n" + ToolOutputTrimmer.Shrink(result.Output, MaxAutomationOutputChars) + "\n```\n" +
                               "请定位并修复真正的原因（最小改动）；修好后会再次自动验证。" +
                               "如果判断这是与本次改动无关的既有失败，请说明理由并给出最终结论";
                    }
                }
            }

            // ② 最近一次测试失败：模型可能已经看到，但收尾时容易跳过，这里确保再回灌一次
            if (_settings.AutoFixProblems && !string.IsNullOrWhiteSpace(_lastTestFailure) && !_lastTestFailureFed)
            {
                _lastTestFailureFed = true;
                callbacks.OnNotice?.Invoke("检测到最近一次测试失败，已回灌给模型继续修复");

                return "系统注意到最近一次 run_tests 失败：\n```\n" +
                       ToolOutputTrimmer.Shrink(_lastTestFailure, MaxAutomationOutputChars) + "\n```\n" +
                       "请定位并修复真正的原因（最小改动）；修好后可用 run_tests 再验证。" +
                       "如果判断是既有失败或环境问题，请说明理由并给出最终结论";
            }

            // ③ 自动修复循环：读编译诊断，有错误就回灌（受轮次上限约束）
            if (_settings.AutoFixProblems && _fixRounds < Math.Max(1, _settings.AutoFixMaxRounds) && callbacks.ReadDiagnosticsAsync != null)
            {
                var diagnostics = await callbacks.ReadDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(diagnostics))
                {
                    _fixRounds++;
                    callbacks.OnNotice?.Invoke("自动修复（第 " + _fixRounds + " 轮）：发现编译错误，已回灌给模型");

                    return "系统自动读取到以下编译错误（自动修复第 " + _fixRounds + " 轮）：\n" +
                           Trim(diagnostics, MaxAutomationOutputChars) + "\n\n" +
                           "请修复其中真正的错误（只改相关文件、最小改动）；" +
                           "若判断是无关的既有问题，请说明理由并给出最终结论";
                }
            }

            return null;
        }

        /// <summary>
        /// 本轮实际使用的验证命令：用户显式填写的优先；留空则用自动探测结果
        /// </summary>
        private string EffectiveVerifyCommand()
        {
            if (_settings == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(_settings.VerifyCommand))
            {
                return _settings.VerifyCommand.Trim();
            }

            return _toolchain?.VerifyCommand;
        }

        /// <summary>
        /// 截断长文本（保留开头，带明确的省略标注）
        /// </summary>
        private static string Trim(string text, int maxChars)
        {
            var value = (text ?? string.Empty).Trim();
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "\n…（输出过长，已截断）";
        }

        private static int? ParseInt(string text)
        {
            return int.TryParse(text, out var value) ? value : null;
        }

        private static bool ParseBool(string text)
        {
            var value = (text ?? string.Empty).Trim();
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.Ordinal) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
