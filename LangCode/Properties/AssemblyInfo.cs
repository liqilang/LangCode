using System.Reflection;
using System.Runtime.InteropServices;

// 程序集级别的通用信息（项目文件里已关闭自动生成，统一在此声明）
[assembly: AssemblyTitle("LangCode")]
[assembly: AssemblyDescription("LangCode —— Visual Studio 的 WPF AI 编程助手工具窗口")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("LangCode")]
[assembly: AssemblyProduct("LangCode")]
[assembly: AssemblyCopyright("Copyright © LangCode")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// 版本号：必须与 source.extension.vsixmanifest 的 Identity@Version 一致，
// 否则界面 / 安装包上的版本会和实际装上的对不上
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

// 不向 COM 暴露（VS 通过托管接口调用扩展）
[assembly: ComVisible(false)]

// 刻意「不加」 [assembly: ThemeInfo(...)]：
//   · 我们不使用 generic.xaml / 按控件主题字典（资源字典都是显式合并的），它没有作用；
//   · 它会引用 PresentationFramework 的类型，使任何「反射程序集级特性」的调用
//     （例如用 DataContractJsonSerializer 解析设置/模型目录）在**没有 WPF 的宿主**里
//     直接抛 FileNotFoundException: PresentationFramework —— CI 的 net8 自检正是这样发现的

// 让 CI 门禁（tools/XamlGuard）能直接断言内部纯逻辑：流式增量解析、工作区根目录解析等
// 这些是「模型输出 → 落盘」链路上最容易静默出错的地方，必须可被自动测试覆盖
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LangCode.XamlGuard")]
