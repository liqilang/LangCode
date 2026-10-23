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
        /// 一轮「无动作」结束后，判断是否还要继续（自动验证 / 自动修复）
        /// </summary>
        private async Task<string> BuildFollowUpAsync(AgentCallbacks callbacks, CancellationToken cancellationToken)
        {
            if (SubTaskDepth > 0 || !_editApplied || _settings == null)
            {
                return null;
            }

            // 验证循环：改过文件 + 有验证命令（用户填的，或自动探测的）+ 本轮还没验过
            var verifyCommand = EffectiveVerifyCommand();
            if (_settings.AutoVerify && !_verifiedThisRun && !string.IsNullOrWhiteSpace(verifyCommand))
            {
                _verifiedThisRun = true;

                var autoDetected = string.IsNullOrWhiteSpace(_settings.VerifyCommand);
                var label = autoDetected ? verifyCommand + "（自动探测）" : verifyCommand;

                var policyBlocked = _permission.CommandBlockReason(verifyCommand);
                if (policyBlocked != null)
                {
                    callbacks.OnNotice?.Invoke("自动验证被工作区策略拒绝：" + policyBlocked);
                    return null;
                }

                callbacks.OnNotice?.Invoke("自动验证：" + label);
                var result = await _commands.RunAsync(verifyCommand, string.Empty, callbacks.OnCommandOutput, cancellationToken).ConfigureAwait(false);

                callbacks.OnNotice?.Invoke("自动验证" + (result.Ok ? "通过" : "失败") + "：" + label);

                return "系统自动执行了验证命令：`" + verifyCommand + "`" + (autoDetected ? "（根据工作区自动探测）" : string.Empty) + "\n" +
                       "结果：" + (result.Ok ? "通过（退出码 0）" : "失败") + "\n" +
                       "输出：\n```\n" + Trim(result.Output, MaxAutomationOutputChars) + "\n```\n" +
                       "如果失败，请定位并修复真正的原因（最小改动）；" +
                       "如果通过，请用自然语言给出最终结论，不要再调用工具";
            }

            // 自动修复循环：读编译诊断，有错误就回灌（受轮次上限约束）
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
