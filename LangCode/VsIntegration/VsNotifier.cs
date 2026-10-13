using System;

namespace LangCode.VsIntegration
{
    /// <summary>
    /// 把「任务完成 / 有操作等你确认」这类事件告诉用户 —— 即使工具窗口不在前台
    /// </summary>
    internal static class VsNotifier
    {
        /// <summary>
        /// 发一条通知（必须在 UI 线程调用）
        /// </summary>
        public static void Notify(string message, bool beep)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.StatusBar != null)
                {
                    dte.StatusBar.Text = message ?? string.Empty;
                }
            }
            catch (Exception)
            {
                // 拿不到 DTE（例如窗口已关闭）：静默忽略，通知不是关键路径
            }

            if (!beep)
            {
                return;
            }

            try
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
            catch (Exception)
            {
                // 没有音频设备：忽略
            }
        }
    }
}

