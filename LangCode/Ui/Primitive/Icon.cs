using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LangCode.Ui.Primitive
{
    /// <summary>
    /// 本地矢量图标控件（用 <c>Icons/*.svg</c>，不用图标字体）
    /// </summary>
    public sealed class Icon : FrameworkElement
    {
        /// <summary>
        /// 没有显式指定尺寸时的默认边长（与设计稿的 16px 图标栅格一致）
        /// </summary>
        private const double DefaultSize = 16;

        /// <summary>
        /// 图标名（对应 <c>Icons/<名字>.svg</c>）
        /// </summary>
        public static readonly DependencyProperty IconNameProperty =
            DependencyProperty.Register(
                "IconName",
                typeof(string),
                typeof(Icon),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// 显式几何（优先于 IconName；按实心绘制）
        /// </summary>
        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(
                "Data",
                typeof(Geometry),
                typeof(Icon),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// 前景色：登记为可继承，于是父级按钮/列表项改前景色时图标跟着变
        /// </summary>
        public static readonly DependencyProperty ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner(
                typeof(Icon),
                new FrameworkPropertyMetadata(
                    Brushes.White,
                    FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public Icon()
        {
            Focusable = false;
            IsHitTestVisible = false;

            // 小尺寸矢量图标的两个必要设置：
            //   · 布局取整：否则 11~13px 下几何会被放到半像素上，笔画糊成两像素灰边；
            //   · 像素对齐：线条落回整数像素，才是「清晰」而不是「发毛」
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
        }

        /// <summary>
        /// 图标名
        /// </summary>
        public string IconName
        {
            get { return (string)GetValue(IconNameProperty); }
            set { SetValue(IconNameProperty, value); }
        }

        /// <summary>
        /// 显式几何（优先于 IconName）
        /// </summary>
        public Geometry Data
        {
            get { return (Geometry)GetValue(DataProperty); }
            set { SetValue(DataProperty, value); }
        }

        /// <summary>
        /// 图标颜色
        /// </summary>
        public Brush Foreground
        {
            get { return (Brush)GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // 默认 16×16；显式 Width/Height 由布局系统覆盖
            // 但父级给的空间更小时必须跟着缩 —— 之前无论可用空间多少都返回 16×16，
            // 于是窄容器里的图标会「按 16 画」，把旁边的文字挤掉、或自己超出边界被裁一半，
            // 表现出来就是「图标和文字折叠在一起」
            var width = double.IsInfinity(availableSize.Width)
                ? DefaultSize
                : Math.Min(DefaultSize, availableSize.Width);

            var height = double.IsInfinity(availableSize.Height)
                ? DefaultSize
                : Math.Min(DefaultSize, availableSize.Height);

            return new Size(Math.Max(0, width), Math.Max(0, height));
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var icon = Data != null
                ? new LocalIcons.IconData(Data, false, 0, 0, 0)
                : LocalIcons.GetIcon(IconName);

            if (icon == null)
            {
                return;
            }

            var data = icon.Value;
            var geometry = data.Geometry;
            if (geometry == null)
            {
                return;
            }

            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            // 描边图标的笔画是**居中**在路径上的，会向外扩出半个线宽（还有圆角端点）
            // 不留这点内缩，最外圈那半个线宽就在可视区之外 —— 小尺寸下特别明显，
            // 看起来像图标被「啃掉一圈」
            var inset = data.IsStroke ? Math.Min(data.StrokeWidth, Math.Min(width, height) / 4) : 0;
            var boxWidth = Math.Max(1, width - inset);
            var boxHeight = Math.Max(1, height - inset);

            double scale;
            double offsetX;
            double offsetY;

            if (data.ViewBoxWidth > 0 && data.ViewBoxHeight > 0)
            {
                scale = Math.Min(boxWidth / data.ViewBoxWidth, boxHeight / data.ViewBoxHeight);
                offsetX = (width - (data.ViewBoxWidth * scale)) / 2;
                offsetY = (height - (data.ViewBoxHeight * scale)) / 2;
            }
            else
            {
                var bounds = geometry.Bounds;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return;
                }

                scale = Math.Min(boxWidth / bounds.Width, boxHeight / bounds.Height);
                offsetX = ((width - (bounds.Width * scale)) / 2) - (bounds.X * scale);
                offsetY = ((height - (bounds.Height * scale)) / 2) - (bounds.Y * scale);
            }

            drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
            drawingContext.PushTransform(new ScaleTransform(scale, scale));

            if (data.IsStroke)
            {
                // 线宽也要跟着缩放 —— 但**不能低于 1 个设备像素**：
                // 24 视图框的图标缩到 11px 时，1.5 的线宽只剩 0.69px，
                // 抗锯齿把它摊成一层灰，看起来就是「糊的、和文字粘在一起」
                var strokeWidth = Math.Max(data.StrokeWidth, 1.0 / Math.Max(scale, 0.0001));

                var pen = new Pen(Foreground, strokeWidth)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                drawingContext.DrawGeometry(null, pen, geometry);
            }
            else
            {
                drawingContext.DrawGeometry(Foreground, null, geometry);
            }

            drawingContext.Pop();
            drawingContext.Pop();
        }
    }
}

