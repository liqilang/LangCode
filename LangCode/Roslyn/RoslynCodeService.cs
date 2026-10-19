using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LangCode.Roslyn
{
    /// <summary>
    /// 一个由 Roslyn 语法树提取出的符号（类型 / 方法 / 属性 / 字段 / 事件 / 构造函数）
    /// </summary>
    public sealed class RoslynSymbol
    {
        public string Name = string.Empty;

        /// <summary>
        /// 类别：类 / 接口 / 结构 / 记录 / 枚举 / 方法 / 属性 / 字段 / 事件 / 构造函数
        /// </summary>
        public string Kind = string.Empty;

        /// <summary>
        /// 所在容器（命名空间.外层类型），顶层为命名空间
        /// </summary>
        public string Container = string.Empty;

        /// <summary>
        /// 签名（尽量还原声明头，去掉实现体）
        /// </summary>
        public string Signature = string.Empty;

        public int StartLine;
        public int EndLine;
    }

    /// <summary>
    /// 一个类型声明（含基类 / 接口列表），用于继承链与实现查询
    /// </summary>
    public sealed class RoslynTypeInfo
    {
        public string Name = string.Empty;

        /// <summary>
        /// 命名空间限定的全名（尽力而为，仅基于本文件的命名空间声明）
        /// </summary>
        public string FullName = string.Empty;

        /// <summary>
        /// class / interface / struct / record / enum
        /// </summary>
        public string Kind = string.Empty;

        /// <summary>
        /// 基类 / 实现的接口（已去掉泛型参数与命名空间前缀）
        /// </summary>
        public List<string> Bases = new List<string>();

        public string Path = string.Empty;
        public int Line;
    }

    /// <summary>
    /// 一处符号引用
    /// </summary>
    public sealed class RoslynReference
    {
        public string Path = string.Empty;
        public int Line;
        public string Text = string.Empty;

        /// <summary>
        /// 该引用是否是声明处（而不是使用处）
        /// </summary>
        public bool IsDeclaration;
    }

    /// <summary>
    /// 语义重命名后的单个文件结果
    /// </summary>
    public sealed class SemanticRenameFile
    {
        public string Path = string.Empty;
        public string NewText = string.Empty;
        public int Count;
    }

    /// <summary>
    /// 基于 Roslyn 的 C# 代码智能：符号提取、标识符级引用查找、符号重命名。 
    /// 与自研正则（<c>SymbolScanner</c>）相比：直接基于语法树与标识符 token，
    /// 天然跳过字符串字面量与注释，且能拿到完整签名与精确行号。 
    /// 运行期若 Roslyn 不可用（<see cref="IsAvailable"/> 为 false），
    /// 调用方应回退到原有的正则实现。
    /// </summary>
    public static class RoslynCodeService
    {
        private static readonly Lazy<bool> Available = new Lazy<bool>(Probe, isThreadSafe: true);

        /// <summary>
        /// Roslyn 是否可用（首次访问探测一次；探测失败即永久回退）
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                try
                {
                    return Available.Value;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private static bool Probe()
        {
            try
            {
                var tree = CSharpSyntaxTree.ParseText("class __LangCodeProbe { }");
                return tree.GetRoot() != null;
            }
            catch (Exception)
            {
                // TypeLoadException / FileLoadException / 版本不匹配 —— 一律视为不可用
                return false;
            }
        }

        /// <summary>
        /// 该路径是否值得用 Roslyn 解析（只处理 C#）
        /// </summary>
        public static bool IsCSharpFile(string relativePath)
        {
            return !string.IsNullOrEmpty(relativePath) && relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        }

        // ==================== 符号提取 ====================

        /// <summary>
        /// 提取文件里的全部符号（类型与类型成员），按出现顺序
        /// </summary>
        public static IList<RoslynSymbol> ListSymbols(string code)
        {
            var result = new List<RoslynSymbol>();
            var root = ParseRoot(code);
            if (root == null)
            {
                return result;
            }

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case BaseTypeDeclarationSyntax type:
                        result.Add(new RoslynSymbol
                        {
                            Name = type.Identifier.Text,
                            Kind = KindOf(type),
                            Container = ContainerOf(type),
                            Signature = SignatureOf(type),
                            StartLine = LineOf(type),
                            EndLine = LineOf(type.Span.End, root),
                        });
                        break;

                    case MethodDeclarationSyntax method:
                        result.Add(Member(method, method.Identifier.Text, "方法", SignatureOf(method), root));
                        break;

                    case ConstructorDeclarationSyntax ctor:
                        result.Add(Member(ctor, ctor.Identifier.Text, "构造函数", SignatureOf(ctor), root));
                        break;

                    case PropertyDeclarationSyntax property:
                        result.Add(Member(property, property.Identifier.Text, "属性", SignatureOf(property), root));
                        break;

                    case EventDeclarationSyntax @event:
                        result.Add(Member(@event, @event.Identifier.Text, "事件", SignatureOf(@event), root));
                        break;

                    case EventFieldDeclarationSyntax eventField:
                        foreach (var variable in eventField.Declaration.Variables)
                        {
                            result.Add(Member(eventField, variable.Identifier.Text, "事件", SignatureOf(eventField), root));
                        }
                        break;

                    case FieldDeclarationSyntax field:
                        foreach (var variable in field.Declaration.Variables)
                        {
                            result.Add(Member(field, variable.Identifier.Text, "字段", SignatureOf(field), root));
                        }
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// 提取文件里的全部类型声明（含基类 / 接口列表）
        /// </summary>
        public static IList<RoslynTypeInfo> ListTypes(string code, string path)
        {
            var result = new List<RoslynTypeInfo>();
            var root = ParseRoot(code);
            if (root == null)
            {
                return result;
            }

            foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var info = new RoslynTypeInfo
                {
                    Name = type.Identifier.Text,
                    Kind = KindOf(type),
                    Path = path ?? string.Empty,
                    Line = LineOf(type),
                };

                var ns = NamespaceOf(type);
                info.FullName = string.IsNullOrEmpty(ns) ? info.Name : ns + "." + info.Name;

                if (type.BaseList != null)
                {
                    foreach (var baseType in type.BaseList.Types)
                    {
                        var simple = Simplify(baseType.Type?.ToString());
                        if (simple.Length > 0)
                        {
                            info.Bases.Add(simple);
                        }
                    }
                }

                result.Add(info);
            }

            return result;
        }

        // ==================== 引用查找 ====================

        /// <summary>
        /// 在单个文件里查找某个标识符的全部出现（标识符 token 级，跳过字符串与注释）
        /// </summary>
        public static IList<RoslynReference> FindReferences(string code, string path, string symbol)
        {
            var result = new List<RoslynReference>();
            var name = (symbol ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return result;
            }

            var root = ParseRoot(code);
            if (root == null)
            {
                return result;
            }

            var lines = (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            foreach (var token in root.DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.IdentifierToken) ||
                    !string.Equals(token.ValueText, name, StringComparison.Ordinal))
                {
                    continue;
                }

                var line = LineOf(token.Parent);
                result.Add(new RoslynReference
                {
                    Path = path ?? string.Empty,
                    Line = line,
                    Text = LineText(lines, line),
                    IsDeclaration = IsDeclarationToken(token),
                });
            }

            return result;
        }

        /// <summary>
        /// 把文件里某个标识符的全部出现改名；成功返回 true 并给出新文本与替换处数
        /// </summary>
        public static bool TryRename(string code, string oldName, string newName, out string renamed, out int count)
        {
            renamed = code ?? string.Empty;
            count = 0;

            var from = (oldName ?? string.Empty).Trim();
            var to = (newName ?? string.Empty).Trim();
            if (from.Length == 0 || to.Length == 0)
            {
                return false;
            }

            SyntaxNode root;
            try
            {
                root = ParseRoot(code);
            }
            catch (Exception)
            {
                return false;
            }

            if (root == null)
            {
                return false;
            }

            var tokens = root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken) && string.Equals(t.ValueText, from, StringComparison.Ordinal)).ToList();

            if (tokens.Count == 0)
            {
                return true;
            }

            try
            {
                var identifier = SyntaxFactory.Identifier(to);
                var newRoot = root.ReplaceTokens(tokens, (original, _) => identifier.WithTriviaFrom(original));
                renamed = newRoot.ToFullString();
                count = tokens.Count;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 语义级跨文件重命名：先在所有候选文件里找到唯一的目标符号，
        /// 再只改真正绑定到该符号的标识符。找不到唯一声明或编译环境不可用时返回 null。
        /// </summary>
        /// <param name="ambiguous">true 表示同名符号有多个，调用方应直接报错而不是退回文本替换</param>
        public static IList<SemanticRenameFile> TryRenameAcrossFiles(
            IList<KeyValuePair<string, string>> files,
            string oldName,
            string newName,
            out string error,
            out bool ambiguous)
        {
            error = null;
            ambiguous = false;

            var from = (oldName ?? string.Empty).Trim();
            var to = (newName ?? string.Empty).Trim();
            if (from.Length == 0 || to.Length == 0)
            {
                error = "symbol 与 new_name 都不能为空";
                return null;
            }

            if (files == null || files.Count == 0)
            {
                error = "没有可参与语义重命名的文件";
                return null;
            }

            try
            {
                var trees = new List<SyntaxTree>(files.Count);
                foreach (var file in files)
                {
                    trees.Add(CSharpSyntaxTree.ParseText(file.Value ?? string.Empty, path: file.Key));
                }

                var compilation = CSharpCompilation.Create(
                    "LangCodeSemanticRename",
                    trees,
                    DefaultMetadataReferences.Value,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                var models = new Dictionary<SyntaxTree, SemanticModel>();
                var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

                foreach (var tree in trees)
                {
                    var model = compilation.GetSemanticModel(tree);
                    models[tree] = model;

                    foreach (var node in tree.GetRoot().DescendantNodes())
                    {
                        ISymbol declared;
                        switch (node)
                        {
                            case BaseTypeDeclarationSyntax type:
                                declared = model.GetDeclaredSymbol(type);
                                break;
                            case MethodDeclarationSyntax method:
                                declared = model.GetDeclaredSymbol(method);
                                break;
                            case PropertyDeclarationSyntax property:
                                declared = model.GetDeclaredSymbol(property);
                                break;
                            case EventDeclarationSyntax @event:
                                declared = model.GetDeclaredSymbol(@event);
                                break;
                            case DelegateDeclarationSyntax @delegate:
                                declared = model.GetDeclaredSymbol(@delegate);
                                break;
                            case EnumMemberDeclarationSyntax enumMember:
                                declared = model.GetDeclaredSymbol(enumMember);
                                break;
                            case LocalFunctionStatementSyntax localFunction:
                                declared = model.GetDeclaredSymbol(localFunction);
                                break;
                            case TypeParameterSyntax typeParameter:
                                declared = model.GetDeclaredSymbol(typeParameter);
                                break;
                            case VariableDeclaratorSyntax variable:
                                declared = model.GetDeclaredSymbol(variable);
                                break;
                            case ParameterSyntax parameter:
                                declared = model.GetDeclaredSymbol(parameter);
                                break;
                            default:
                                declared = null;
                                break;
                        }

                        if (declared != null && string.Equals(declared.Name, from, StringComparison.Ordinal))
                        {
                            symbols.Add(declared);
                        }
                    }
                }

                if (symbols.Count == 0)
                {
                    error = "没有找到名为 " + from + " 的符号声明（可能只在字符串 / 注释里出现，或来自外部程序集）";
                    return null;
                }

                if (symbols.Count > 1)
                {
                    ambiguous = true;
                    error = "工作区里存在多个名为 " + from + " 的符号（" +
                            string.Join("、", symbols.Select(s => s.Kind + " " + s.ToDisplayString()).Take(5)) +
                            "）。为避免误改，语义重命名已取消；请使用 replace_in_file 或提供更精确的上下文。";
                    return null;
                }

                var target = symbols.First();
                var results = new List<SemanticRenameFile>();

                foreach (var tree in trees)
                {
                    var model = models[tree];
                    var root = tree.GetRoot();
                    var tokens = new List<SyntaxToken>();

                    foreach (var token in root.DescendantTokens())
                    {
                        if (!token.IsKind(SyntaxKind.IdentifierToken) ||
                            !string.Equals(token.ValueText, from, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (RefersToSymbol(token, model, target))
                        {
                            tokens.Add(token);
                        }
                    }

                    if (tokens.Count == 0)
                    {
                        continue;
                    }

                    var identifier = SyntaxFactory.Identifier(to);
                    var newRoot = root.ReplaceTokens(tokens, (original, _) => identifier.WithTriviaFrom(original));
                    results.Add(new SemanticRenameFile
                    {
                        Path = tree.FilePath,
                        NewText = newRoot.ToFullString(),
                        Count = tokens.Count,
                    });
                }

                return results;
            }
            catch (Exception ex)
            {
                error = "语义重命名失败：" + ex.Message;
                return null;
            }
        }

        private static bool RefersToSymbol(SyntaxToken token, SemanticModel model, ISymbol target)
        {
            var node = token.Parent;
            if (node == null)
            {
                return false;
            }

            switch (node)
            {
                case BaseTypeDeclarationSyntax type when type.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(type), target);
                case MethodDeclarationSyntax method when method.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(method), target);
                case PropertyDeclarationSyntax property when property.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(property), target);
                case EventDeclarationSyntax @event when @event.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(@event), target);
                case DelegateDeclarationSyntax @delegate when @delegate.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(@delegate), target);
                case EnumMemberDeclarationSyntax enumMember when enumMember.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(enumMember), target);
                case LocalFunctionStatementSyntax localFunction when localFunction.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(localFunction), target);
                case TypeParameterSyntax typeParameter when typeParameter.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(typeParameter), target);
                case VariableDeclaratorSyntax variable when variable.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(variable), target);
                case ParameterSyntax parameter when parameter.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(parameter), target);
                case ConstructorDeclarationSyntax constructor when constructor.Identifier == token:
                    return SameSymbol(model.GetDeclaredSymbol(constructor)?.ContainingType, target);
            }

            var info = model.GetSymbolInfo(node);
            if (SameSymbol(info.Symbol, target))
            {
                return true;
            }

            foreach (var candidate in info.CandidateSymbols)
            {
                if (SameSymbol(candidate, target))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameSymbol(ISymbol left, ISymbol right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return SymbolEqualityComparer.Default.Equals(left, right) ||
                   SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);
        }

        /// <summary>
        /// 语义分析需要的基础元数据引用（不依赖用户具体项目的 MSBuild 引用）
        /// </summary>
        private static readonly Lazy<IList<MetadataReference>> DefaultMetadataReferences =
            new Lazy<IList<MetadataReference>>(CreateDefaultReferences, isThreadSafe: true);

        private static IList<MetadataReference> CreateDefaultReferences()
        {
            var paths = new List<string>
            {
                typeof(object).Assembly.Location,
                typeof(Enumerable).Assembly.Location,
                typeof(System.Runtime.Serialization.DataContractAttribute).Assembly.Location,
                typeof(System.ComponentModel.INotifyPropertyChanged).Assembly.Location,
                typeof(System.Net.Http.HttpClient).Assembly.Location,
                typeof(System.Xml.XmlDocument).Assembly.Location,
            };

            var references = new List<MetadataReference>();
            foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch (Exception)
                {
                    // 单个引用失败不影响其它引用
                }
            }

            return references;
        }

        // ==================== 内部辅助 ====================

        private static SyntaxNode ParseRoot(string code)
        {
            var tree = CSharpSyntaxTree.ParseText(code ?? string.Empty);
            return tree.GetRoot();
        }

        private static RoslynSymbol Member(SyntaxNode node, string name, string kind, string signature, SyntaxNode root)
        {
            return new RoslynSymbol
            {
                Name = name ?? string.Empty,
                Kind = kind,
                Container = ContainerOf(node),
                Signature = signature,
                StartLine = LineOf(node),
                EndLine = LineOf(node.Span.End, root),
            };
        }

        private static string KindOf(BaseTypeDeclarationSyntax type)
        {
            switch (type)
            {
                case RecordDeclarationSyntax:
                    return "记录";
                case ClassDeclarationSyntax:
                    return "类";
                case InterfaceDeclarationSyntax:
                    return "接口";
                case StructDeclarationSyntax:
                    return "结构";
                case EnumDeclarationSyntax:
                    return "枚举";
                default:
                    return "类型";
            }
        }

        /// <summary>
        /// 声明关键字（class / interface / struct / record / enum）
        /// </summary>
        private static string TypeKeywordOf(BaseTypeDeclarationSyntax type)
        {
            switch (type)
            {
                case RecordDeclarationSyntax:
                    return "record";
                case ClassDeclarationSyntax:
                    return "class";
                case InterfaceDeclarationSyntax:
                    return "interface";
                case StructDeclarationSyntax:
                    return "struct";
                case EnumDeclarationSyntax:
                    return "enum";
                default:
                    return "type";
            }
        }

        /// <summary>
        /// 容器：命名空间（+ 外层类型）。例如 <c>LangCode.Agent.AgentRunner</c>
        /// </summary>
        private static string ContainerOf(SyntaxNode node)
        {
            var parts = new List<string>();
            foreach (var ancestor in node.Ancestors())
            {
                switch (ancestor)
                {
                    case BaseNamespaceDeclarationSyntax ns:
                        parts.Add(ns.Name?.ToString() ?? string.Empty);
                        break;
                    case BaseTypeDeclarationSyntax type:
                        parts.Add(type.Identifier.Text);
                        break;
                }
            }

            parts.Reverse();
            return string.Join(".", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        private static string NamespaceOf(SyntaxNode node)
        {
            var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            return ns?.Name?.ToString() ?? string.Empty;
        }

        private static bool IsDeclarationToken(SyntaxToken token)
        {
            switch (token.Parent)
            {
                case BaseTypeDeclarationSyntax t when t.Identifier == token:
                case MethodDeclarationSyntax m when m.Identifier == token:
                case ConstructorDeclarationSyntax c when c.Identifier == token:
                case PropertyDeclarationSyntax p when p.Identifier == token:
                case EventDeclarationSyntax e when e.Identifier == token:
                case VariableDeclaratorSyntax v when v.Identifier == token:
                case ParameterSyntax prm when prm.Identifier == token:
                    return true;
                default:
                    return false;
            }
        }

        private static string SignatureOf(BaseTypeDeclarationSyntax type)
        {
            var builder = new StringBuilder();
            builder.Append(Modifiers(type.Modifiers));
            builder.Append(TypeKeywordOf(type));
            if (type is TypeDeclarationSyntax declaration && declaration.TypeParameterList != null)
            {
                builder.Append(declaration.TypeParameterList.ToString());
            }

            return builder.Append(' ').Append(type.Identifier.Text).ToString().Trim();
        }

        private static string SignatureOf(MethodDeclarationSyntax method)
        {
            var builder = new StringBuilder();
            builder.Append(Modifiers(method.Modifiers));
            builder.Append(method.ReturnType?.ToString()).Append(' ');
            builder.Append(method.Identifier.Text);
            builder.Append(method.TypeParameterList?.ToString());
            builder.Append(method.ParameterList?.ToString());
            return builder.ToString().Trim();
        }

        private static string SignatureOf(ConstructorDeclarationSyntax ctor)
        {
            var builder = new StringBuilder();
            builder.Append(Modifiers(ctor.Modifiers));
            builder.Append(ctor.Identifier.Text);
            builder.Append(ctor.ParameterList?.ToString());
            return builder.ToString().Trim();
        }

        private static string SignatureOf(PropertyDeclarationSyntax property)
        {
            var builder = new StringBuilder();
            builder.Append(Modifiers(property.Modifiers));
            builder.Append(property.Type?.ToString()).Append(' ');
            builder.Append(property.Identifier.Text);
            return builder.ToString().Trim();
        }

        private static string SignatureOf(EventDeclarationSyntax @event)
        {
            var builder = new StringBuilder();
            builder.Append(Modifiers(@event.Modifiers));
            builder.Append("event ").Append(@event.Type?.ToString()).Append(' ').Append(@event.Identifier.Text);
            return builder.ToString().Trim();
        }

        private static string SignatureOf(EventFieldDeclarationSyntax eventField)
        {
            var builder = new StringBuilder();
            builder.Append(Modifiers(eventField.Modifiers));
            builder.Append("event ").Append(eventField.Declaration?.Type?.ToString());
            return builder.ToString().Trim();
        }

        private static string SignatureOf(FieldDeclarationSyntax field)
        {
            var builder = new StringBuilder();
            builder.Append(Modifiers(field.Modifiers));
            builder.Append(field.Declaration?.Type?.ToString());
            return builder.ToString().Trim();
        }

        private static string Modifiers(SyntaxTokenList modifiers)
        {
            if (modifiers == null || modifiers.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var modifier in modifiers)
            {
                builder.Append(modifier.Text).Append(' ');
            }

            return builder.ToString();
        }

        /// <summary>
        /// 去掉泛型参数与命名空间前缀，只留最简单的类型名（用于基类 / 接口匹配）
        /// </summary>
        private static string Simplify(string typeText)
        {
            var text = (typeText ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            // 去泛型：List<Foo> → List
            var angle = text.IndexOf('<');
            if (angle >= 0)
            {
                text = text.Substring(0, angle);
            }

            // 去命名空间：A.B.C → C
            var dot = text.LastIndexOf('.');
            if (dot >= 0)
            {
                text = text.Substring(dot + 1);
            }

            return text.Trim();
        }

        private static int LineOf(SyntaxNode node)
        {
            if (node == null)
            {
                return 0;
            }

            try
            {
                return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static int LineOf(int position, SyntaxNode root)
        {
            try
            {
                return root.GetLocation().GetLineSpan().StartLinePosition.Line + 1 +
                       CountNewlines(root.ToFullString(), position);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static int CountNewlines(string text, int upTo)
        {
            var count = 0;
            var limit = Math.Min(upTo, text?.Length ?? 0);
            for (var i = 0; i < limit; i++)
            {
                if (text[i] == '\n')
                {
                    count++;
                }
            }

            return count;
        }

        private static string LineText(string[] lines, int line)
        {
            if (lines == null || line <= 0 || line > lines.Length)
            {
                return string.Empty;
            }

            return lines[line - 1].Trim();
        }
    }
}
