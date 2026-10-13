using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace LangCode.Ui.Primitive
{
    /// <summary>
    /// 本地矢量图标（与桌面端同一套机制：不用图标字体）
    /// </summary>
    internal static class LocalIcons
    {
        private static string _resolvedDirectory;

        /// <summary>
        /// 图标目录（<c>Ui/Assets/Icons/</c>）
        /// </summary>
        public static string Directory
        {
            get
            {
                var resolved = _resolvedDirectory;
                if (resolved != null)
                {
                    return resolved;
                }

                foreach (var candidate in CandidateDirectories())
                {
                    try
                    {
                        if (System.IO.Directory.Exists(candidate) &&
                            System.IO.Directory.EnumerateFiles(candidate, "*.svg").Any())
                        {
                            _resolvedDirectory = candidate;
                            return candidate;
                        }
                    }
                    catch (Exception)
                    {
                        // 单个候选目录不可访问：继续试下一个
                    }
                }

                resolved = CandidateDirectories().FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "Ui", "Assets", "Icons");
                _resolvedDirectory = resolved;
                return resolved;
            }
        }

        /// <summary>
        /// 候选图标目录（按优先级；新结构 <c>Ui/Assets/Icons</c> 优先，兼容旧 <c>Icons</c>）
        /// </summary>
        private static IEnumerable<string> CandidateDirectories()
        {
            var roots = new List<string>();

            try
            {
                var assemblyLocation = typeof(LocalIcons).Assembly.Location;
                if (!string.IsNullOrEmpty(assemblyLocation))
                {
                    // 扩展程序集所在目录：VSIX 会把 Ui/Assets/Icons/ 与其放在一起
                    roots.Add(Path.GetDirectoryName(assemblyLocation));
                }
            }
            catch (Exception)
            {
                // 单文件/动态程序集拿不到 Location：忽略
            }

            roots.Add(AppContext.BaseDirectory);
            roots.Add(System.IO.Directory.GetCurrentDirectory());

            foreach (var root in roots)
            {
                if (!string.IsNullOrEmpty(root))
                {
                    yield return Path.Combine(root, "Ui", "Assets", "Icons");
                    // 兼容旧版本部署残留的 Icons/ 目录
                    yield return Path.Combine(root, "Icons");
                }
            }
        }

        /// <summary>
        /// 一个图标：几何 + 画法（线稿/实心）、线宽与 viewBox
        /// </summary>
        internal readonly struct IconData
        {
            public IconData(Geometry geometry, bool isStroke, double strokeWidth, double viewBoxWidth = 24, double viewBoxHeight = 24)
            {
                Geometry = geometry;
                IsStroke = isStroke;
                StrokeWidth = strokeWidth;
                ViewBoxWidth = viewBoxWidth;
                ViewBoxHeight = viewBoxHeight;
            }

            public Geometry Geometry { get; }

            public bool IsStroke { get; }

            public double StrokeWidth { get; }

            public double ViewBoxWidth { get; }

            public double ViewBoxHeight { get; }
        }

        private static readonly Dictionary<string, IconData?> Cache =
            new Dictionary<string, IconData?>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex ElementRegex = new Regex(
            "<(?<tag>path|circle|rect|line|polyline|polygon)\\b(?<attrs>[^>]*)>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex StrokeRegex = new Regex(
            "stroke\\s*=\\s*\"(?!none)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex StrokeNoneRegex = new Regex(
            "stroke\\s*=\\s*\"none\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex StrokeWidthRegex = new Regex(
            "stroke-width\\s*=\\s*\"(?<w>[\\d.]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex PointRegex = new Regex("-?[\\d.]+", RegexOptions.CultureInvariant);

        private static readonly Regex ViewBoxRegex = new Regex(
            "viewBox\\s*=\\s*\"(?<v>[^\"]*)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 按名字取图标（带缓存）；找不到或解析失败返回 null（界面退化为「无图标」而不是崩溃）
        /// </summary>
        public static IconData? GetIcon(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            lock (Cache)
            {
                if (Cache.TryGetValue(name, out var cached))
                {
                    return cached;
                }
            }

            var data = Load(name);

            lock (Cache)
            {
                Cache[name] = data;
            }

            return data;
        }

        /// <summary>
        /// 清空缓存（自检 / 换工作目录时用）
        /// </summary>
        public static void Invalidate()
        {
            lock (Cache)
            {
                Cache.Clear();
            }

            _resolvedDirectory = null;
        }

        private static IconData? Load(string name)
        {
            try
            {
                var file = Path.Combine(Directory, name + ".svg");
                return File.Exists(file) ? Parse(File.ReadAllText(file)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 把一份 SVG 文本解析成「几何 + 画法」只支持本仓库图标用到的子集
        /// </summary>
        internal static IconData? Parse(string svg)
        {
            var shapes = new List<Geometry>();

            foreach (Match match in ElementRegex.Matches(svg))
            {
                var tag = match.Groups["tag"].Value.ToLowerInvariant();
                var attrs = match.Groups["attrs"].Value;

                Geometry geometry;
                switch (tag)
                {
                    case "path":
                        geometry = FromPath(Attr(attrs, "d"));
                        break;
                    case "circle":
                        geometry = FromCircle(attrs);
                        break;
                    case "rect":
                        geometry = FromRect(attrs);
                        break;
                    case "line":
                        geometry = FromLine(attrs);
                        break;
                    case "polyline":
                        geometry = FromPoints(Attr(attrs, "points"), false);
                        break;
                    case "polygon":
                        geometry = FromPoints(Attr(attrs, "points"), true);
                        break;
                    default:
                        geometry = null;
                        break;
                }

                if (geometry != null)
                {
                    shapes.Add(geometry);
                }
            }

            if (shapes.Count == 0)
            {
                return null;
            }

            var combined = shapes.Count == 1 ? shapes[0] : Group(shapes);

            // 画法：只要出现非 none 的 stroke，就按线稿画（设计稿几乎全是线稿）
            var isStroke = StrokeRegex.IsMatch(svg) && !StrokeNoneRegex.IsMatch(svg);
            var width = 1.7;
            var widthMatch = StrokeWidthRegex.Match(svg);
            if (widthMatch.Success
                && double.TryParse(widthMatch.Groups["w"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0)
            {
                width = parsed;
            }

            ParseViewBox(svg, out double viewBoxWidth, out double viewBoxHeight);

            return new IconData(combined, isStroke, width, viewBoxWidth, viewBoxHeight);
        }

        private static void ParseViewBox(string svg, out double width, out double height)
        {
            width = 0;
            height = 0;

            var match = ViewBoxRegex.Match(svg);
            if (!match.Success)
            {
                return;
            }

            var numbers = PointRegex.Matches(match.Groups["v"].Value)
                .Cast<Match>()
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToList();

            if (numbers.Count >= 4 && numbers[2] > 0 && numbers[3] > 0)
            {
                width = numbers[2];
                height = numbers[3];
            }
        }

        private static Geometry FromPath(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return null;
            }

            try
            {
                return PathGeometry.CreateFromGeometry(Geometry.Parse(data));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static Geometry FromCircle(string attrs)
        {
            var cx = Number(attrs, "cx");
            var cy = Number(attrs, "cy");
            var r = Number(attrs, "r");
            return cx == null || cy == null || r == null
                ? null
                : new EllipseGeometry(new Point(cx.Value, cy.Value), r.Value, r.Value);
        }

        private static Geometry FromRect(string attrs)
        {
            var x = Number(attrs, "x") ?? 0;
            var y = Number(attrs, "y") ?? 0;
            var w = Number(attrs, "width");
            var h = Number(attrs, "height");
            if (w == null || h == null)
            {
                return null;
            }

            var rx = Number(attrs, "rx") ?? 0;
            var ry = Number(attrs, "ry") ?? rx;
            return new RectangleGeometry(new Rect(x, y, w.Value, h.Value), rx, ry);
        }

        private static Geometry FromLine(string attrs)
        {
            var x1 = Number(attrs, "x1");
            var y1 = Number(attrs, "y1");
            var x2 = Number(attrs, "x2");
            var y2 = Number(attrs, "y2");
            return x1 == null || y1 == null || x2 == null || y2 == null
                ? null
                : new LineGeometry(new Point(x1.Value, y1.Value), new Point(x2.Value, y2.Value));
        }

        private static Geometry FromPoints(string points, bool close)
        {
            if (string.IsNullOrWhiteSpace(points))
            {
                return null;
            }

            var numbers = PointRegex.Matches(points)
                .Cast<Match>()
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToList();

            if (numbers.Count < 4)
            {
                return null;
            }

            var figure = new PathFigure
            {
                StartPoint = new Point(numbers[0], numbers[1]),
                IsClosed = close,
                IsFilled = close,
            };

            for (var i = 2; i + 1 < numbers.Count; i += 2)
            {
                figure.Segments.Add(new LineSegment(new Point(numbers[i], numbers[i + 1]), true));
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static Geometry Group(List<Geometry> shapes)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var shape in shapes)
            {
                group.Children.Add(shape);
            }

            return group;
        }

        private static string Attr(string attrs, string name)
        {
            var match = Regex.Match(
                attrs,
                name + "\\s*=\\s*\"(?<v>[^\"]*)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["v"].Value : null;
        }

        private static double? Number(string attrs, string name)
        {
            var raw = Attr(attrs, name);
            if (raw == null)
            {
                return null;
            }

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : null;
        }
    }
}

