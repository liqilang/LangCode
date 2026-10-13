using LangCode.VsExt.Chat;
using LangCode.VsExt.Domain.Catalog;
using LangCode.VsExt.Domain.Settings;
using LangCode.VsExt.Mcp;
using LangCode.VsExt.Persistence;
using LangCode.VsExt.Workspace;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LangCode.VsExt.Ui.ToolWindows
{
    /// <summary>
    /// SettingsDrawerControl 分部：导出 / 导入 / 重置与调试信息
    /// </summary>
    public partial class SettingsDrawerControl
    {
        // ============================================================
        // 导出 / 导入 / 重置
        // ============================================================
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出 LangCode 设置",
                FileName = "langcode-settings.json",
                Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var error = SettingsStore.Export(Settings, dialog.FileName);
            MessageBox.Show(
                error == null ? "已导出到：\n" + dialog.FileName : "导出失败：" + error,
                "LangCode",
                MessageBoxButton.OK,
                error == null ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        // ============================================================
        // 调试信息（排查「看不到推理过程」等问题）
        // ============================================================

        /// <summary>
        /// 把内存里的聊天调试日志复制到剪贴板，用户可直接粘贴发回
        /// </summary>
        private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            var text = ChatDiagnostics.Dump();
            if (string.IsNullOrWhiteSpace(text) || text.IndexOf("=====", StringComparison.Ordinal) < 0)
            {
                SetDiagnosticsStatus("暂无可复制的调试信息：请先在聊天里发一条消息复现问题，再回来复制");
                return;
            }

            try
            {
                Clipboard.SetText(text);
                SetDiagnosticsStatus("已复制 " + text.Length + " 个字符到剪贴板，直接粘贴发回即可");
            }
            catch (Exception ex)
            {
                SetDiagnosticsStatus("复制失败：" + ex.Message);
            }
        }

        private void ClearDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            ChatDiagnostics.Clear();
            SetDiagnosticsStatus("调试信息已清空（建议现在去聊天里复现一次，再回来复制）");
        }

        private void SetDiagnosticsStatus(string text)
        {
            if (DiagnosticsStatus != null)
            {
                DiagnosticsStatus.Text = text ?? string.Empty;
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入 LangCode 设置",
                Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var imported = SettingsStore.Import(dialog.FileName, out var error);
            if (imported == null)
            {
                MessageBox.Show("导入失败：" + error, "LangCode", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 灌进现有实例（不换引用），否则宿主那边还指着旧对象
            Settings.CopyFrom(imported);

            _binding = true;
            try
            {
                BindUi();
            }
            finally
            {
                _binding = false;
            }

            RaiseChanged();

            MessageBox.Show("设置已导入，点击「保存」后生效", "LangCode", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(
                "确定把所有设置恢复为出厂状态吗？\n（API Key、自定义指令、MCP 服务器列表都会被清空）",
                "LangCode",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK)
            {
                return;
            }

            Settings.CopyFrom(SettingsStore.CreateDefault());

            _binding = true;
            try
            {
                BindUi();
            }
            finally
            {
                _binding = false;
            }

            RaiseChanged();
        }
    }
}
