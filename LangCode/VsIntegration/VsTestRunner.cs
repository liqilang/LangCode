using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace LangCode.VsIntegration
{
    /// <summary>
    /// 一次测试运行的汇总结果（从 TRX 报告解析）
    /// </summary>
    public sealed class TestRunReport
    {
        public bool HasResults { get; set; }

        public int Total { get; set; }
        public int Executed { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }

        /// <summary>
        /// 失败的测试名 → 失败信息（含堆栈）
        /// </summary>
        public List<KeyValuePair<string, string>> Failures { get; } = new List<KeyValuePair<string, string>>();
    }

    /// <summary>
    /// 测试联动：识别 <c>dotnet test</c> 并把它产出的 TRX 报告解析成结构化结果，
    /// 让「跑测试」不只是回灌一坨文本，而是给出通过 / 失败计数与失败用例与堆栈。
    /// </summary>
    public static class VsTestRunner
    {
        /// <summary>
        /// 该命令是不是 <c>dotnet test</c>（只有它支持 <c>--logger trx</c> 结构化输出）
        /// </summary>
        public static bool IsDotnetTest(string command)
        {
            var text = (command ?? string.Empty).TrimStart();
            return text.StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("dotnet.exe test", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 解析 TRX 报告（命名空间无关；文件不存在 / 损坏返回 <see cref="TestRunReport.HasResults"/> = false）
        /// </summary>
        public static TestRunReport ParseTrx(string path)
        {
            var report = new TestRunReport();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                return report;
            }

            try
            {
                var document = new XmlDocument();
                document.Load(path);

                // 汇总计数：<ResultSummary><Counters total=... executed=... passed=... failed=... /></...>
                var counters = document.SelectSingleNode("//*[local-name()='Counters']");
                if (counters?.Attributes != null)
                {
                    report.Total = Attr(counters, "total");
                    report.Executed = Attr(counters, "executed");
                    report.Passed = Attr(counters, "passed");
                    report.Failed = Attr(counters, "failed");
                    report.Skipped = Attr(counters, "notExecuted") + Attr(counters, "disconnected");
                    report.HasResults = true;
                }

                foreach (XmlNode result in document.SelectNodes("//*[local-name()='UnitTestResult']"))
                {
                    var outcome = result.Attributes?["outcome"]?.Value ?? string.Empty;
                    if (!string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(outcome, "Error", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var testName = result.Attributes?["testName"]?.Value ?? "(未知测试)";

                    var message = string.Empty;
                    var stack = string.Empty;
                    var errorInfo = result.SelectSingleNode(".//*[local-name()='ErrorInfo']");
                    if (errorInfo != null)
                    {
                        message = errorInfo.SelectSingleNode(".//*[local-name()='Message']")?.InnerText ?? string.Empty;
                        stack = errorInfo.SelectSingleNode(".//*[local-name()='StackTrace']")?.InnerText ?? string.Empty;
                    }

                    var detail = string.IsNullOrWhiteSpace(message)
                        ? outcome
                        : message.Trim() + (string.IsNullOrWhiteSpace(stack) ? string.Empty : "\n" + stack.Trim());

                    report.Failures.Add(new KeyValuePair<string, string>(testName, detail));
                    report.HasResults = true;
                }
            }
            catch (Exception)
            {
                // 报告损坏 / 不完整：返回已解析到的部分（HasResults 可能为 false）
            }

            return report;
        }

        /// <summary>
        /// 把报告渲染成给模型看的汇总
        /// </summary>
        public static string Describe(TestRunReport report, int maxFailures = 12)
        {
            if (report == null || !report.HasResults)
            {
                return "（未能解析到测试报告：可能没有运行任何测试，或测试框架不支持 TRX 输出）";
            }

            var builder = new StringBuilder();
            builder.Append("测试结果：共 ").Append(report.Total)
                   .Append(" 个，通过 ").Append(report.Passed)
                   .Append("，失败 ").Append(report.Failed);
            if (report.Skipped > 0)
            {
                builder.Append("，跳过 ").Append(report.Skipped);
            }

            builder.Append('\n');

            if (report.Failures.Count > 0)
            {
                builder.Append("\n失败用例：\n");
                var shown = 0;
                foreach (var failure in report.Failures)
                {
                    if (shown++ >= maxFailures)
                    {
                        builder.Append("…（其余失败省略）\n");
                        break;
                    }

                    builder.Append("✗ ").Append(failure.Key).Append('\n')
                           .Append(Clip(failure.Value, 1200)).Append('\n');
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static int Attr(XmlNode node, string name)
        {
            var value = node.Attributes?[name]?.Value;
            return int.TryParse(value, out var parsed) ? parsed : 0;
        }

        private static string Clip(string text, int max)
        {
            var value = (text ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "\n…（已截断）";
        }
    }
}
