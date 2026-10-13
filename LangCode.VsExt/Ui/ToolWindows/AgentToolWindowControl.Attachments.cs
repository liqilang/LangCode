using LangCode.VsExt.Domain.Chat;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LangCode.VsExt.Ui.ToolWindows
{
    /// <summary>
    /// AgentToolWindowControl 分部：多模态图片附件（粘贴 / 缩略片 / 随消息发送）
    /// </summary>
    public partial class AgentToolWindowControl
    {
        /// <summary>
        /// 待发送的图片附件（发送后清空）
        /// </summary>
        private readonly List<ChatImage> _pendingImages = [];

        /// <summary>
        /// 挂上图片粘贴处理（在构造函数里调用一次）
        /// </summary>
        private void HookImagePaste()
        {
            DataObject.AddPastingHandler(Input, Input_Pasting);
        }

        /// <summary>
        /// 粘贴处理：剪贴板里有图片就转成附件，不让它落进文本框变成乱码
        /// </summary>
        private void Input_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (_settings != null && !_settings.Multimodal)
            {
                return;
            }

            try
            {
                if (!Clipboard.ContainsImage())
                {
                    return;
                }

                e.CancelCommand();

                var max = _settings?.MaxImagesPerMessage ?? 4;
                if (_pendingImages.Count >= max)
                {
                    AddNote("一条消息最多附带 " + max + " 张图片；请先移除一些再粘贴");
                    return;
                }

                var source = Clipboard.GetImage();
                var image = source == null ? null : EncodeClipboardImage(source);
                if (image == null)
                {
                    AddNote("无法把剪贴板里的图片编码为可发送的格式（可能体积过大）");
                    return;
                }

                _pendingImages.Add(image);
                RenderAttachments();
                UpdateSendState();
            }
            catch (Exception)
            {
                // 剪贴板可能被其它进程占用：静默忽略，不打断输入
            }
        }

        /// <summary>
        /// 取走待发送图片并清空附件条（发送时调用）
        /// </summary>
        private IList<ChatImage> TakePendingImages()
        {
            var copy = new List<ChatImage>(_pendingImages);
            _pendingImages.Clear();
            RenderAttachments();
            return copy;
        }

        /// <summary>
        /// 把磁盘上的图片文件加入附件条（从解决方案资源管理器拖入图片时用）
        /// </summary>
        private bool TryAddImageFile(string path)
        {
            try
            {
                var max = _settings?.MaxImagesPerMessage ?? 4;
                if (_pendingImages.Count >= max)
                {
                    AddNote("一条消息最多附带 " + max + " 张图片；请先移除一些再拖入");
                    return true;
                }

                var bytes = File.ReadAllBytes(path);
                var maxBytes = _settings?.ImageMaxBytes ?? (5 * 1024 * 1024);
                if (bytes.Length == 0 || bytes.Length > maxBytes)
                {
                    AddNote("图片过大或为空，未作为附件：" + Path.GetFileName(path));
                    return true;
                }

                _pendingImages.Add(new ChatImage(MimeOfImage(path), Convert.ToBase64String(bytes)));
                RenderAttachments();
                UpdateSendState();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 拖入的文件是不是图片（决定按附件还是按 <c>@提及</c> 处理）
        /// </summary>
        private static bool IsImagePath(string path)
        {
            var extension = Path.GetExtension(path) ?? string.Empty;

            return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 图片文件的 MIME（拼 <c>data:</c> URL 时用）
        /// </summary>
        private static string MimeOfImage(string path)
        {
            switch ((Path.GetExtension(path) ?? string.Empty).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";

                case ".gif":
                    return "image/gif";

                case ".bmp":
                    return "image/bmp";

                case ".webp":
                    return "image/webp";

                default:
                    return "image/png";
            }
        }

        /// <summary>
        /// 把剪贴板位图编码成 PN Gbase64；超过体积上限时逐步缩小
        /// </summary>
        private ChatImage EncodeClipboardImage(BitmapSource source)
        {
            var maxBytes = _settings?.ImageMaxBytes ?? (5 * 1024 * 1024);
            var scale = 1.0;

            for (var attempt = 0; attempt < 6; attempt++)
            {
                var frame = scale >= 1.0 ? source : new TransformedBitmap(source, new ScaleTransform(scale, scale));
                var bytes = EncodePng(frame);
                if (bytes == null)
                {
                    return null;
                }

                if (bytes.Length <= maxBytes || scale < 0.2)
                {
                    return new ChatImage("image/png", Convert.ToBase64String(bytes));
                }

                scale *= 0.7;
            }

            return null;
        }

        private static byte[] EncodePng(BitmapSource source)
        {
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 重绘附件条
        /// </summary>
        private void RenderAttachments()
        {
            if (AttachmentBar == null)
            {
                return;
            }

            AttachmentBar.Children.Clear();

            if (_pendingImages.Count == 0)
            {
                AttachmentBar.Visibility = Visibility.Collapsed;
                return;
            }

            AttachmentBar.Visibility = Visibility.Visible;
            foreach (var image in _pendingImages.ToList())
            {
                AttachmentBar.Children.Add(AttachmentChip(image));
            }
        }

        /// <summary>
        /// 一张图片的缩略片：说明文字 + 「×」移除
        /// </summary>
        private FrameworkElement AttachmentChip(ChatImage image)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(8, 3, 3, 3),
                ToolTip = "随消息发送的图片：" + image.MimeType,
            };
            border.SetResourceReference(Border.BackgroundProperty, "Ap.BgInput");
            border.SetResourceReference(Border.BorderBrushProperty, "Ap.InputBorder");

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var chipCaption = new TextBlock
            {
                Text = "🖼 " + image.Summary,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chipCaption.SetResourceReference(TextBlock.ForegroundProperty, "Ap.FgMenu");
            row.Children.Add(chipCaption);

            var remove = new Button
            {
                Content = "×",
                Tag = image,
                Width = 16,
                Height = 16,
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "移除这张图片",
            };
            remove.SetResourceReference(Control.ForegroundProperty, "Ap.FgFaint");

            remove.Click += (_, __) =>
            {
                _pendingImages.Remove(image);
                RenderAttachments();
                UpdateSendState();
            };

            row.Children.Add(remove);
            border.Child = row;
            return border;
        }
    }
}

