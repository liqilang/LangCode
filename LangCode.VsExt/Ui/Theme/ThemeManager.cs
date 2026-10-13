using Microsoft.VisualStudio.PlatformUI;
using System;
using System.Collections.Generic;
using System.Windows;

namespace LangCode.VsExt.Ui.Theme
{
    /// <summary>
    /// Visual Studio 当前主题的「明暗」归类（蓝色主题按深色处理）
    /// </summary>
    public enum VsThemeKind
    {
        /// <summary>
        /// 深色（VS 的「深色」「蓝色」主题）
        /// </summary>
        Dark = 0,

        /// <summary>
        /// 浅色（VS 的「浅色」主题，含高对比度浅色）
        /// </summary>
        Light = 1,
    }

    /// <summary>
    /// 主题管理器：让扩展的 WPF 界面**跟随 Visual Studio 主题**
    /// </summary>
    public static class ThemeManager
    {
        private const string DarkDictUri = "pack://application:,,,/LangCode.VsExt;component/Ui/Theme/Palette.xaml";
        private const string LightDictUri = "pack://application:,,,/LangCode.VsExt;component/Ui/Theme/PaletteLight.xaml";

        private static readonly object Gate = new object();

        /// <summary>
        /// 界面拿到的调色板实例：**终身不变**，内容随主题切换
        /// </summary>
        private static readonly ResourceDictionary LivePalette = [];

        private static ResourceDictionary _dark;
        private static ResourceDictionary _light;

        private static bool _initialized;
        private static bool _subscribed;
        private static VsThemeKind _current = VsThemeKind.Dark;

        /// <summary>
        /// 当前生效的主题归类
        /// </summary>
        public static VsThemeKind Current
        {
            get { return _current; }
        }

        /// <summary>
        /// 恒定不变的调色板字典实例（内容为当前主题的 <c>Ap.*</c> 令牌）
        /// UserControl 通过 <see cref="Attach"/> 并入它即可
        /// </summary>
        public static ResourceDictionary Palette
        {
            get
            {
                EnsurePalette();
                return LivePalette;
            }
        }

        /// <summary>
        /// 主题（明暗归类）发生变化时触发需要自行重建内容的界面（如 Markdown 渲染）可订阅
        /// </summary>
        public static event EventHandler ThemeChanged;

        /// <summary>
        /// 初始化：检测当前 VS 主题并订阅「主题切换」事件
        /// 必须在 **UI 线程**调用（由 <c>LangCodePackage</c> 在包初始化时调用）
        /// </summary>
        public static void Initialize()
        {
            lock (Gate)
            {
                EnsurePaletteCore();

                if (!_subscribed)
                {
                    try
                    {
                        VSColorTheme.ThemeChanged += OnVsThemeChanged;
                        _subscribed = true;
                    }
                    catch
                    {
                        // 非 VS 宿主（设计器 / 单元测试）里没有该服务：静默降级为不跟随
                    }
                }

                _initialized = true;
            }

            RefreshFromEnvironment(force: true);
        }

        /// <summary>
        /// 把调色板并入宿主的资源字典（幂等）在 UserControl 构造期、<c>InitializeComponent()</c> 之后调用
        /// </summary>
        public static void Attach(FrameworkElement host)
        {
            if (host == null)
            {
                return;
            }

            // 工具窗口可能在包初始化之前就被创建（例如 VS 恢复了上次布局），这里做一次兜底初始化
            if (!_initialized && host.Dispatcher != null && host.Dispatcher.CheckAccess())
            {
                Initialize();
            }
            else
            {
                EnsurePalette();
            }

            var dicts = host.Resources.MergedDictionaries;
            if (!dicts.Contains(LivePalette))
            {
                dicts.Insert(0, LivePalette);
            }
        }

        /// <summary>
        /// 按当前 VS 环境重新评估主题；<paramref name="force"/> 为 true 时无视「是否变化」一律重放
        /// </summary>
        public static void RefreshFromEnvironment(bool force)
        {
            var kind = DetectVsTheme();
            if (!force && kind == _current)
            {
                return;
            }

            _current = kind;

            lock (Gate)
            {
                EnsurePaletteCore();
                ApplyToPalette(kind == VsThemeKind.Light ? _light : _dark);
            }

            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static void OnVsThemeChanged(ThemeChangedEventArgs e)
        {
            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(() => RefreshFromEnvironment(force: false));
                return;
            }

            RefreshFromEnvironment(force: false);
        }

        /// <summary>
        /// 就地重填调色板：键集合与目标主题完全对齐，避免残留上一主题的键
        /// </summary>
        private static void ApplyToPalette(ResourceDictionary source)
        {
            if (source == null)
            {
                return;
            }

            var stale = new List<object>();
            foreach (var key in LivePalette.Keys)
            {
                if (!source.Contains(key))
                {
                    stale.Add(key);
                }
            }

            foreach (var key in stale)
            {
                LivePalette.Remove(key);
            }

            foreach (var key in source.Keys)
            {
                LivePalette[key] = source[key];
            }
        }

        private static void EnsurePalette()
        {
            if (_dark != null || _light != null)
            {
                return;
            }

            lock (Gate)
            {
                EnsurePaletteCore();
            }
        }

        private static void EnsurePaletteCore()
        {
            _dark ??= LoadDictionary(DarkDictUri);

            _light ??= LoadDictionary(LightDictUri);

            if (LivePalette.Count == 0)
            {
                ApplyToPalette(_current == VsThemeKind.Light ? _light : _dark);
            }
        }

        private static ResourceDictionary LoadDictionary(string uri)
        {
            try
            {
                return new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) };
            }
            catch
            {
                // 极端情况下（无 Application / 无 pack 资源）返回空表，界面退回 WPF 默认色，但不崩
                return [];
            }
        }

        /// <summary>
        /// 依据 VS 工具窗口背景色的亮度判断明暗；取不到（非 VS 宿主）时回退深色，
        /// 与扩展原有的默认观感保持一致
        /// </summary>
        private static VsThemeKind DetectVsTheme()
        {
            try
            {
                var color = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
                return luminance < 0.5 ? VsThemeKind.Dark : VsThemeKind.Light;
            }
            catch
            {
                return VsThemeKind.Dark;
            }
        }
    }
}

