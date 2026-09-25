using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>
/// 一次**算好但还没写**的代码改动。拟定靠它累积：先算内容，确认之后才落盘。
/// </summary>
public sealed record CodeChange(string FilePath, string NewContent, int StartLine, int EndLine, string Action);

/// <summary>基于 Roslyn 语法树的成员查看与编辑（不依赖文件路径定位）。</summary>
public static class CodeEditor
{
    private static readonly SyntaxAnnotation EditAnchor = new("ZMS.EditAnchor");

    /// <summary>结构视图用的签名格式：含访问性/修饰符/参数/类型，但**不含方法体**。</summary>
    private static readonly SymbolDisplayFormat StructureFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeAccessibility
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>查看成员的签名、位置与源码片段。</summary>
    public static string Describe(ISymbol symbol)
    {
        StringBuilder builder = new();
        builder.AppendLine("## " + symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        builder.AppendLine();
        builder.AppendLine($"- Kind: `{symbol.Kind}`");
        if (symbol.ContainingType != null)
        {
            builder.AppendLine($"- Declaring type: `{SymbolLocator.DisplayName(symbol.ContainingType)}`");
        }

        string? documentation = symbol.GetDocumentationCommentXml();
        if (!string.IsNullOrWhiteSpace(documentation))
        {
            builder.AppendLine();
            builder.AppendLine("### Documentation");
            builder.AppendLine("```xml");
            builder.AppendLine(documentation.Trim());
            builder.AppendLine("```");
        }

        SyntaxNode? node = SourceNode(symbol);
        if (node == null)
        {
            builder.AppendLine();
            builder.AppendLine("_(no source declaration — this symbol comes from metadata)_");
            return builder.ToString();
        }

        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        builder.AppendLine($"- File: `{span.Path}`");
        builder.AppendLine($"- Lines: {span.StartLinePosition.Line + 1}-{span.EndLinePosition.Line + 1}");
        builder.AppendLine();
        builder.AppendLine("```csharp");
        builder.AppendLine(ReadSource(node));
        builder.AppendLine("```");
        return builder.ToString();
    }

    /// <summary>整段替换一个成员声明（**只算，不写**）。</summary>
    public static CodeChange ComputeReplace(ISymbol symbol, string newCode, bool format)
    {
        if (string.IsNullOrWhiteSpace(newCode))
        {
            throw new ArgumentException("newCode is empty.");
        }

        SyntaxNode node = SourceNode(symbol)
            ?? throw new InvalidOperationException($"'{symbol.Name}' has no source declaration to replace.");
        List<MemberDeclarationSyntax> parsed = ParseMembers(newCode);
        if (parsed.Count != 1)
        {
            throw new InvalidOperationException($"newCode must contain exactly one member declaration, but {parsed.Count} were parsed.");
        }

        MemberDeclarationSyntax replacement = parsed[0].WithTriviaFrom(node).WithAdditionalAnnotations(EditAnchor);
        SyntaxNode root = node.SyntaxTree.GetRoot();
        SyntaxNode updated = root.ReplaceNode(node, replacement);
        updated = FormatIfNeeded(updated, format);
        return Compute(node.SyntaxTree.FilePath, updated, "replaced");
    }

    /// <summary>在类型里新增成员（**只算，不写**），可用 <paramref name="before"/> 指定插到某个已有成员之前。</summary>
    public static CodeChange ComputeAdd(INamedTypeSymbol type, string code, string before, bool format)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("code is empty.");
        }

        SyntaxNode? typeNode = SourceNode(type)
            ?? throw new InvalidOperationException($"Type '{type.Name}' has no source declaration.");
        if (typeNode is not TypeDeclarationSyntax declaration)
        {
            throw new InvalidOperationException($"Type '{type.Name}' cannot contain members ({typeNode.Kind()}).");
        }

        List<MemberDeclarationSyntax> members = ParseMembers(code)
            .Select(member => member.WithAdditionalAnnotations(EditAnchor))
            .ToList();

        SyntaxNode updatedDeclaration;
        if (string.IsNullOrWhiteSpace(before))
        {
            updatedDeclaration = declaration.AddMembers(members.ToArray());
        }
        else
        {
            MemberDeclarationSyntax? anchorNode = declaration.Members
                .FirstOrDefault(existing => string.Equals(MemberName(existing), before, StringComparison.Ordinal));
            if (anchorNode == null)
            {
                string names = string.Join(", ", declaration.Members.Select(MemberName));
                throw new InvalidOperationException($"Member '{before}' not found in '{type.Name}'. Existing members: {names}");
            }

            updatedDeclaration = declaration.InsertNodesBefore(anchorNode, members);
        }

        SyntaxNode root = declaration.SyntaxTree.GetRoot();
        SyntaxNode updated = root.ReplaceNode(declaration, updatedDeclaration);
        updated = FormatIfNeeded(updated, format);
        return Compute(typeNode.SyntaxTree.FilePath, updated, "added");
    }

    /// <summary>删除一个成员声明（**只算，不写**）。</summary>
    public static CodeChange ComputeRemove(ISymbol symbol, bool format)
    {
        SyntaxNode node = SourceNode(symbol)
            ?? throw new InvalidOperationException($"'{symbol.Name}' has no source declaration to remove.");
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        SyntaxNode root = node.SyntaxTree.GetRoot();
        SyntaxNode? updated = root.RemoveNode(node, SyntaxRemoveOptions.KeepLeadingTrivia)
            ?? throw new InvalidOperationException("Failed to remove the node.");
        updated = FormatIfNeeded(updated, format);
        return Compute(node.SyntaxTree.FilePath, updated, "removed", (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
    }

    /// <summary>成员声明节点对应的源码文本（不带行号，方便直接复制修改）。</summary>
    public static string ReadSource(SyntaxNode node)
    {
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        if (!File.Exists(span.Path))
        {
            return node.ToFullString().TrimEnd();
        }

        string[] lines = File.ReadAllLines(span.Path);
        int start = span.StartLinePosition.Line;
        int end = Math.Min(span.EndLinePosition.Line, lines.Length - 1);
        if (start < 0 || start > end)
        {
            return node.ToFullString().TrimEnd();
        }

        return string.Join(Environment.NewLine, lines[start..(end + 1)]).TrimEnd();
    }

    // ───────── 类型结构视图 + 文档预算（get_member 给类型时只给结构） ─────────

    /// <summary>
    /// 给"类型"的**结构**视图：类型头（含基类/接口列表与主构造器参数）+ 成员声明概要。
    /// **不展开实现** —— 方法只给签名、属性/索引器的访问器只用 <c>{ get { … } }</c> 记号、
    /// 字段/属性/事件带上初始化器表达式。要看实现就精确点名某个成员（<see cref="Describe"/>）。
    /// </summary>
    /// <param name="type">目标类型。</param>
    /// <param name="documentedMembers">允许显示文档注释的成员；null = 一个都不显示。</param>
    /// <param name="documentationLineLimit">文档注释的行数上限；小于等于 0 表示不截断。</param>
    public static string DescribeType(
        INamedTypeSymbol type,
        IReadOnlySet<ISymbol>? documentedMembers = null,
        int documentationLineLimit = 200)
    {
        StringBuilder builder = new();
        builder.AppendLine("## " + TypeHeader(type));
        builder.AppendLine();
        builder.AppendLine($"- Kind: `{type.TypeKind}`");
        builder.AppendLine($"- Members: {CountMembers(type)}");
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            FileLinePositionSpan span = reference.GetSyntax().GetLocation().GetLineSpan();
            builder.AppendLine($"- File: `{span.Path}`（{span.StartLinePosition.Line + 1}-{span.EndLinePosition.Line + 1}）");
        }

        builder.AppendLine();
        builder.AppendLine("### Members");
        List<ISymbol> members =
        [
            .. type.GetMembers()
                .Where(member => !member.IsImplicitlyDeclared && !IsAccessor(member))
                .OrderBy(member => member.Name, StringComparer.Ordinal),
        ];
        foreach (IReadOnlyList<ISymbol> group in MemberListRendering.GroupByName(members, member => member))
        {
            // 3.7：同名重载超过 10 个 → 分组显示（不重复方法名）
            if (MemberListRendering.IsOverloadedGroup(group, member => member))
            {
                builder.AppendLine($"- `{type.Name}.{group[0].Name}` 有 {group.Count} 个重载：");
                foreach (ISymbol member in group)
                {
                    builder.AppendLine($"  - `{MemberListRendering.ParameterList(member)}`");
                }

                continue;
            }

            foreach (ISymbol member in group)
            {
                builder.AppendLine("- " + MemberSignature(member));
                if (documentedMembers != null && documentedMembers.Contains(member))
                {
                    AppendMemberDocumentation(builder, member, documentationLineLimit);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 文档注释的预算（需求 3.8）：单个成员 → 全显示；不止一个 → 每个截 200 行；超过 5 个 → 每个截 50 行；
    /// 超过 10 个 → 只给前 10 个成员显示文档（各 50 行）。
    /// </summary>
    internal static (int DocumentedMembers, int LineLimit) DocumentationBudget(int memberCount)
    {
        if (memberCount <= 1)
        {
            return (1, 0);
        }

        if (memberCount <= 5)
        {
            return (memberCount, 200);
        }

        if (memberCount <= 10)
        {
            return (memberCount, 50);
        }

        return (10, 50);
    }

    /// <summary>
    /// 需求 3.7：同名成员只显示前 10 个的"文档 + 实现"；列表里有**至少两个不同名字**的方法时，
    /// 每个名字只显示前 5 个。
    /// </summary>
    internal static HashSet<ISymbol> DocumentedMembers(IReadOnlyList<ISymbol> members)
    {
        List<IGrouping<string, ISymbol>> groups = [.. members.GroupBy(member => member.Name)];
        bool severalMethodNames = groups.Count(group => group.Any(member => member is IMethodSymbol)) >= 2;
        int perName = severalMethodNames ? 5 : 10;

        HashSet<ISymbol> allowed = new(SymbolEqualityComparer.Default);
        foreach (IGrouping<string, ISymbol> group in groups)
        {
            foreach (ISymbol member in group.Take(perName))
            {
                allowed.Add(member);
            }
        }

        return allowed;
    }

    /// <summary>按行数截断文本；<paramref name="maxLines"/> 小于等于 0 表示不截断。</summary>
    internal static IReadOnlyList<string> TruncateLines(string text, int maxLines)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (maxLines <= 0 || lines.Length <= maxLines)
        {
            return lines;
        }

        return [.. lines.Take(maxLines), $"…（注释共 {lines.Length} 行，此处截断到 {maxLines} 行）"];
    }

    /// <summary>语法检查：这段代码必须能被解析成合法的 C# 成员声明（不通过就抛，附上编译器原话）。</summary>
    internal static void EnsureMembersParse(string code)
    {
        ParseMembers(code);
    }

    /// <summary>成员的**结构级**签名：方法不带体、属性/索引器给访问器记号、字段/事件带初始化器。</summary>
    internal static string MemberSignature(ISymbol member)
    {
        SyntaxNode? node = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        switch (member)
        {
            case IPropertySymbol property:
                return PropertySignature(property, node);
            case IFieldSymbol field:
                return FieldSignature(field, node);
            case IEventSymbol @event:
                return EventSignature(@event, node);
            default:
                return member.ToDisplayString(StructureFormat);
        }
    }

    /// <summary>类型头：修饰符 + 关键字 + 名字 + 泛型参数 + 主构造器参数 + 基类/接口列表。</summary>
    private static string TypeHeader(INamedTypeSymbol type)
    {
        SyntaxNode? node = type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (node is not TypeDeclarationSyntax declaration)
        {
            return type.ToDisplayString(StructureFormat);
        }

        List<string> parts = [.. declaration.Modifiers.Select(modifier => modifier.Text)];
        parts.Add(declaration.Keyword.Text);
        // 名字 + 泛型参数 + 主构造器参数要连在一起写（Base(int seed)），不能加空格
        string name = declaration.Identifier.Text;
        if (declaration.TypeParameterList != null)
        {
            name += declaration.TypeParameterList.ToString();
        }

        if (declaration.ParameterList != null)
        {
            name += Collapse(declaration.ParameterList.ToString());
        }

        parts.Add(name);

        if (declaration.BaseList != null)
        {
            // BaseList 的文本自带冒号（": Base, IFoo"），不要再补一个
            parts.Add(Collapse(declaration.BaseList.ToString()));
        }

        return string.Join(' ', parts);
    }

    private static int CountMembers(INamedTypeSymbol type)
    {
        return type.GetMembers().Count(member => !member.IsImplicitlyDeclared && !IsAccessor(member));
    }

    /// <summary>
    /// 属性的结构签名：**属性本身的修饰符全部显示**（访问权限 / static / abstract / virtual / override / readonly / ref），
    /// 访问器只**隐藏实现**（没有精确匹配到该访问器时），访问器自己的访问权限 / readonly 也同样显示。
    /// </summary>
    private static string PropertySignature(IPropertySymbol property, SyntaxNode? node)
    {
        List<string> head = [.. DeclarationModifiers(node)];
        if (head.Count == 0)
        {
            // 拿不到源码声明（分部声明里没写访问器表等）→ 用符号信息兼容
            head.Add(AccessibilityText(property.DeclaredAccessibility));
            if (property.IsStatic)
            {
                head.Add("static");
            }

            if (property.IsAbstract)
            {
                head.Add("abstract");
            }

            if (property.IsVirtual)
            {
                head.Add("virtual");
            }

            if (property.IsOverride)
            {
                head.Add("override");
            }

            if (property.IsReadOnly)
            {
                head.Add("readonly");
            }
        }

        string refText = RefText(property.RefKind);
        if (refText.Length > 0)
        {
            head.Add(refText);
        }

        head.Add(property.Type.ToDisplayString(StructureFormat));
        if (property.IsIndexer)
        {
            string parameters = string.Join(
                ", ",
                property.Parameters.Select(parameter => parameter.Type.ToDisplayString(StructureFormat) + " " + parameter.Name));
            head.Add("this[" + parameters + "]");
        }
        else
        {
            head.Add(property.Name);
        }

        string text = string.Join(' ', head.Where(part => part.Length > 0)) + " " + AccessorsText(property, node);
        string initializer = InitializerText(node);
        if (initializer.Length > 0)
        {
            text += " = " + initializer;
        }

        // 只有源码里真的写了分号（带初始化器的那种）才补分号：
        // 纯 { get; set; } 的自动属性结尾没有分号
        bool hasSemicolon = node is PropertyDeclarationSyntax declaration
            && !declaration.SemicolonToken.IsKind(SyntaxKind.None);
        return hasSemicolon ? text + ";" : text;
    }

    /// <summary>
    /// 访问器记号：自动访问器写 <c>get;</c>，**带实现**的写 <c>get { … }</c>（只隐藏实现）；
    /// 访问器自己的访问权限（<c>private set</c>）与 <c>readonly</c> 都照写。
    /// </summary>
    private static string AccessorsText(IPropertySymbol property, SyntaxNode? node)
    {
        AccessorListSyntax? list = node switch
        {
            PropertyDeclarationSyntax propertyDeclaration => propertyDeclaration.AccessorList,
            IndexerDeclarationSyntax indexerDeclaration => indexerDeclaration.AccessorList,
            _ => null,
        };

        List<string> parts = [];
        if (list != null)
        {
            foreach (AccessorDeclarationSyntax accessor in list.Accessors)
            {
                parts.Add(AccessorSignature(accessor));
            }
        }

        if (parts.Count == 0)
        {
            if (property.GetMethod != null)
            {
                parts.Add("get;");
            }

            if (property.SetMethod != null)
            {
                parts.Add("set;");
            }
        }

        return "{ " + string.Join(' ', parts) + " }";
    }

    /// <summary>一个访问器的记号：修饰符（访问权限 / readonly 等）+ 关键字，实现只给 <c>{ … }</c>。</summary>
    private static string AccessorSignature(AccessorDeclarationSyntax accessor)
    {
        List<string> parts = [.. accessor.Modifiers.Select(modifier => modifier.Text)];
        bool hasBody = accessor.Body != null || accessor.ExpressionBody != null;
        parts.Add(hasBody ? $"{accessor.Keyword.Text} {{ … }}" : accessor.Keyword.Text + ";");
        return string.Join(' ', parts);
    }

    /// <summary>声明上的修饰符（照抄源码顺序）；没有源码声明时返回空表。</summary>
    private static IReadOnlyList<string> DeclarationModifiers(SyntaxNode? node)
    {
        return node switch
        {
            PropertyDeclarationSyntax property => [.. property.Modifiers.Select(modifier => modifier.Text)],
            IndexerDeclarationSyntax indexer => [.. indexer.Modifiers.Select(modifier => modifier.Text)],
            _ => [],
        };
    }

    /// <summary><c>ref</c> / <c>ref readonly</c> 的前缀（没有就返回空串）。</summary>
    private static string RefText(RefKind kind)
    {
        return kind switch
        {
            RefKind.Ref => "ref",
            RefKind.RefReadOnly or RefKind.RefReadOnlyParameter => "ref readonly",
            _ => "",
        };
    }

    private static string FieldSignature(IFieldSymbol field, SyntaxNode? node)
    {
        List<string> parts = [AccessibilityText(field.DeclaredAccessibility)];
        if (field.IsConst)
        {
            parts.Add("const");
        }
        else if (field.IsStatic)
        {
            parts.Add("static");
        }

        if (field.IsReadOnly)
        {
            parts.Add("readonly");
        }

        parts.Add(field.Type.ToDisplayString(StructureFormat));
        parts.Add(field.Name);
        string text = string.Join(' ', parts.Where(part => part.Length > 0));
        string initializer = InitializerText(node);
        return initializer.Length > 0 ? text + " = " + initializer + ";" : text + ";";
    }

    private static string EventSignature(IEventSymbol @event, SyntaxNode? node)
    {
        List<string> parts = [AccessibilityText(@event.DeclaredAccessibility)];
        if (@event.IsStatic)
        {
            parts.Add("static");
        }

        parts.Add("event");
        parts.Add(@event.Type.ToDisplayString(StructureFormat));
        parts.Add(@event.Name);
        string text = string.Join(' ', parts.Where(part => part.Length > 0));
        string initializer = InitializerText(node);
        return initializer.Length > 0 ? text + " = " + initializer + ";" : text + ";";
    }

    /// <summary>字段/属性的初始化器表达式（压成单行）。</summary>
    private static string InitializerText(SyntaxNode? node)
    {
        EqualsValueClauseSyntax? initializer = node switch
        {
            VariableDeclaratorSyntax declarator => declarator.Initializer,
            PropertyDeclarationSyntax property => property.Initializer,
            _ => null,
        };
        return initializer == null ? "" : Collapse(initializer.Value.ToString());
    }

    /// <summary>把多行文本压成单行（多余空白合并）。</summary>
    private static string Collapse(string text)
    {
        return WhitespaceRegex.Replace(text.Replace('\r', ' ').Replace('\n', ' '), " ").Trim();
    }

    private static string AccessibilityText(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Private => "private",
            _ => "",
        };
    }

    /// <summary>把成员的文档注释按行数上限追加（缩进两格，挂在成员行下面）。</summary>
    private static void AppendMemberDocumentation(StringBuilder builder, ISymbol member, int maxLines)
    {
        string? documentation = member.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(documentation))
        {
            return;
        }

        builder.AppendLine("  ```xml");
        foreach (string line in TruncateLines(documentation.Trim(), maxLines))
        {
            builder.AppendLine("  " + line);
        }

        builder.AppendLine("  ```");
    }

    private static bool IsAccessor(ISymbol member)
    {
        return member is IMethodSymbol method && method.MethodKind is
            MethodKind.PropertyGet or MethodKind.PropertySet or
            MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise;
    }

    private static SyntaxNode? SourceNode(ISymbol symbol)
    {
        return symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
    }

    private static SyntaxNode FormatIfNeeded(SyntaxNode node, bool format)
    {
        if (!format)
        {
            return node;
        }

        using AdhocWorkspace workspace = new();
        return Formatter.Format(node, workspace);
    }

    /// <summary>把改写后的语法树算成一次待落盘改动（行号用锚点定位，锚点丢了就用调用方给的兜底值）。</summary>
    private static CodeChange Compute(string filePath, SyntaxNode root, string action, (int Start, int End)? fallback = null)
    {
        List<SyntaxNode> targets = root.GetAnnotatedNodes(EditAnchor).ToList();
        string content = root.ToFullString();
        if (targets.Count > 0)
        {
            int start = targets.Min(node => node.GetLocation().GetLineSpan().StartLinePosition.Line) + 1;
            int end = targets.Max(node => node.GetLocation().GetLineSpan().EndLinePosition.Line) + 1;
            return new CodeChange(filePath, content, start, end, action);
        }

        if (fallback.HasValue)
        {
            return new CodeChange(filePath, content, fallback.Value.Start, fallback.Value.End, action);
        }

        return new CodeChange(filePath, content, 0, 0, action);
    }

    /// <summary>
    /// 在**给定的源码文本**上给某个类型加成员。
    /// 用于"先补 partial、再加成员"的场景：两处改动必须落在同一份内容上，
    /// 否则落盘时"每个文件只取最后一条"会把补 partial 盖掉。
    /// </summary>
    /// <param name="filePath">文件路径（重新解析出来的语法树要用它）。</param>
    /// <param name="sourceText">要改的完整源码文本。</param>
    /// <param name="typePath">完全限定名（含命名空间与嵌套类型链）。</param>
    /// <param name="code">要加入的成员声明。</param>
    /// <param name="format">是否跑 Roslyn formatter。</param>
    public static CodeChange ComputeAddToSource(string filePath, string sourceText, string typePath, string code, bool format)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("code is empty.");
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            sourceText,
            new CSharpParseOptions(LanguageVersion.Preview),
            path: filePath);
        TypeDeclarationSyntax declaration = FindDeclaration(tree, typePath)
            ?? throw new InvalidOperationException($"Type '{typePath}' has no declaration in '{filePath}'.");
        List<MemberDeclarationSyntax> members = ParseMembers(code)
            .Select(member => member.WithAdditionalAnnotations(EditAnchor))
            .ToList();

        SyntaxNode root = tree.GetRoot();
        SyntaxNode updated = root.ReplaceNode(declaration, declaration.AddMembers(members.ToArray()));
        updated = FormatIfNeeded(updated, format);
        return Compute(filePath, updated, "added");
    }

    /// <summary>按完全限定名（命名空间 + 嵌套类型链）在语法树里找类型声明。</summary>
    private static TypeDeclarationSyntax? FindDeclaration(SyntaxTree tree, string typePath)
    {
        foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
        {
            if (node is TypeDeclarationSyntax declaration && QualifiedName(declaration) == typePath)
            {
                return declaration;
            }
        }

        return null;
    }

    /// <summary>语法树上的完全限定名：命名空间 + 外层类型链 + 自己。</summary>
    private static string QualifiedName(SyntaxNode node)
    {
        List<string> parts = [];
        for (SyntaxNode? current = node; current != null; current = current.Parent)
        {
            if (current is TypeDeclarationSyntax type)
            {
                parts.Insert(0, type.Identifier.Text);
            }
            else if (current is BaseNamespaceDeclarationSyntax @namespace)
            {
                parts.Insert(0, @namespace.Name.ToString());
            }
        }

        return string.Join('.', parts);
    }

    /// <summary>把代码片段解析成成员声明列表：包进一个临时类型再取 Members，
    /// 这样「一次传多个成员」「带 XML 注释」「带语法错误」都能得到确定结果。
    /// </summary>
    internal static List<MemberDeclarationSyntax> ParseMembers(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is empty.");
        }

        string trimmed = code.Trim();
        string wrapped = "class __ZmsScratchClass__\n{\n" + trimmed + "\n}\n";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(wrapped, new CSharpParseOptions(LanguageVersion.Preview));
        string[] errors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.GetMessage())
            .Distinct()
            .ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException("Code does not parse as C# member declaration(s): " + string.Join("; ", errors));
        }

        ClassDeclarationSyntax? scratch = tree.GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault();
        if (scratch == null || scratch.Members.Count == 0)
        {
            throw new InvalidOperationException("Code does not contain any member declaration.");
        }

        List<MemberDeclarationSyntax> members = [];
        foreach (MemberDeclarationSyntax member in scratch.Members)
        {
            members.Add(NormalizeTrivia(member));
        }

        return members;
    }

    /// <summary>
    /// 去掉包装解析留下的空行 trivia（首尾空白/换行），保留文档注释等有意义 trivia，
    /// 并补一个弹性换行，交给 formatter（或直接输出）决定最终缩进。
    /// </summary>
    private static MemberDeclarationSyntax NormalizeTrivia(MemberDeclarationSyntax member)
    {
        SyntaxTriviaList leading = member.GetLeadingTrivia();
        int start = 0;
        while (start < leading.Count && IsBlank(leading[start]))
        {
            start++;
        }

        SyntaxTriviaList trailing = member.GetTrailingTrivia();
        int end = trailing.Count;
        while (end > 0 && IsBlank(trailing[end - 1]))
        {
            end--;
        }

        SyntaxTriviaList normalizedLeading = SyntaxFactory.TriviaList(leading.Skip(start));
        normalizedLeading = normalizedLeading.Insert(0, SyntaxFactory.ElasticCarriageReturnLineFeed);
        return member.WithLeadingTrivia(normalizedLeading).WithTrailingTrivia(trailing.Take(end));
    }

    private static bool IsBlank(SyntaxTrivia trivia)
    {
        return trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia);
    }

    private static string MemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method.Identifier.Text,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
            DestructorDeclarationSyntax destructor => "~" + destructor.Identifier.Text,
            PropertyDeclarationSyntax property => property.Identifier.Text,
            EventDeclarationSyntax @event => @event.Identifier.Text,
            IndexerDeclarationSyntax => "this[]",
            OperatorDeclarationSyntax @operator => "operator" + @operator.OperatorToken.Text,
            FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(variable => variable.Identifier.Text)),
            EventFieldDeclarationSyntax eventField => string.Join(",", eventField.Declaration.Variables.Select(variable => variable.Identifier.Text)),
            _ => member.Kind().ToString(),
        };
    }
}
