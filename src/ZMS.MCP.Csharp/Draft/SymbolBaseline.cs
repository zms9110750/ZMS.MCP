using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZMS.MCP.Csharp.Draft;

/// <summary>基线差异：变化 / 新出现 / 消失的符号。</summary>
public sealed record BaselineDiff(
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed)
{
    public bool IsEmpty => Changed.Count == 0 && Added.Count == 0 && Removed.Count == 0;
}

/// <summary>
/// 追踪基线：**符号级语义快照** —— 「符号完全限定名 → 语义 hash」。
/// 不含文件路径，所以**移动文件 / 文件改名不算变化**（见 `docs/Csharp-拟定流程v3.md` 第五节）。
///
/// hash 算的是**语义**不是文本：声明里的每个名字都先解析成符号、再写成规范身份，
/// 所以 `List&lt;int&gt;` / `System.Collections.Generic.List&lt;int&gt;` / `using L = …; L&lt;int&gt;`
/// 三种写法算出来是同一个值（using 与别名不参与），注释与空白这些 trivia 也不参与。
/// </summary>
public static class SymbolBaseline
{
    /// <summary>
    /// 符号身份的显示格式（含泛型元数与参数类型）—— 全项目只用这一份，别到处手拼字符串。
    /// </summary>
    private static readonly SymbolDisplayFormat IdentityFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>拍一份基线：遍历程序集里本项目声明的符号（类型 + 成员）。</summary>
    public static Dictionary<string, string> Capture(Compilation compilation)
    {
        Dictionary<string, string> baseline = new(StringComparer.Ordinal);

        // 同一棵语法树只取一次 SemanticModel（很贵），整个遍历共用
        Dictionary<SyntaxTree, SemanticModel> models = new();
        CollectNamespace(compilation.Assembly.GlobalNamespace, baseline, compilation, models);
        return baseline;
    }

    /// <summary>比两份基线：谁变了、谁新来、谁走了。</summary>
    public static BaselineDiff Compare(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> current)
    {
        List<string> changed = [];
        List<string> removed = [];
        foreach (KeyValuePair<string, string> pair in baseline)
        {
            if (!current.TryGetValue(pair.Key, out string? hash))
            {
                removed.Add(pair.Key);
                continue;
            }

            if (!string.Equals(hash, pair.Value, StringComparison.Ordinal))
            {
                changed.Add(pair.Key);
            }
        }

        List<string> added = [.. current.Keys.Where(key => !baseline.ContainsKey(key))];
        changed.Sort(StringComparer.Ordinal);
        added.Sort(StringComparer.Ordinal);
        removed.Sort(StringComparer.Ordinal);
        return new BaselineDiff(changed, added, removed);
    }

    /// <summary>符号身份（完全限定名，含参数类型与泛型元数）。</summary>
    public static string Key(ISymbol symbol)
    {
        return symbol.ToDisplayString(IdentityFormat);
    }

    /// <summary>存进 sqlite 的 JSON。</summary>
    public static string Serialize(IReadOnlyDictionary<string, string> baseline)
    {
        return JsonSerializer.Serialize(baseline);
    }

    /// <summary>从 sqlite 读回；坏数据当空基线（宁可报"一片未追踪更改"，也不要抛）。</summary>
    public static Dictionary<string, string> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                is { } parsed
                ? new Dictionary<string, string>(parsed, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>文本 → hash（快照比对用；与基线 hash 同一套"换行归一"规则）。</summary>
    internal static string HashText(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));
    }

    /// <summary>符号的声明文本（不是 hash）—— 拟定快照 / 三方展示要用。</summary>
    internal static string DeclaredText(ISymbol symbol)
    {
        List<string> parts = [];
        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
        {
            parts.Add(reference.GetSyntax().ToString());
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n---\n", parts);
    }

    private static void CollectNamespace(
        INamespaceSymbol @namespace,
        Dictionary<string, string> baseline,
        Compilation compilation,
        Dictionary<SyntaxTree, SemanticModel> models)
    {
        foreach (INamespaceSymbol child in @namespace.GetNamespaceMembers())
        {
            CollectNamespace(child, baseline, compilation, models);
        }

        foreach (INamedTypeSymbol type in @namespace.GetTypeMembers())
        {
            CollectType(type, baseline, compilation, models);
        }
    }

    private static void CollectType(
        INamedTypeSymbol type,
        Dictionary<string, string> baseline,
        Compilation compilation,
        Dictionary<SyntaxTree, SemanticModel> models)
    {
        if (type.IsImplicitlyDeclared)
        {
            return;
        }

        baseline[Key(type)] = SemanticHash(type, compilation, models);

        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            CollectType(nested, baseline, compilation, models);
        }

        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || IsAccessor(member))
            {
                continue;
            }

            baseline[Key(member)] = SemanticHash(member, compilation, models);
        }
    }

    /// <summary>
    /// 语义 hash：符号身份 + 它每一处声明的「名字都换成符号身份」的规范文本。
    ///
    /// 比纯文本比对**细**（语句、顺序、字面量都还在，所以不会漏报"真被改了"），
    /// 但把 using / 别名 / 简称带来的写法差异抹平了（那些解析到**同一个符号**，算出来同一个值）。
    /// </summary>
    private static string SemanticHash(
        ISymbol symbol,
        Compilation compilation,
        Dictionary<SyntaxTree, SemanticModel> models)
    {
        StringBuilder builder = new();
        builder.Append(SymbolIdentity(symbol)).Append('\n');

        List<SyntaxNode> declarations = [];
        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
        {
            declarations.Add(reference.GetSyntax());
        }

        declarations.Sort((left, right) => string.CompareOrdinal(left.ToString(), right.ToString()));
        foreach (SyntaxNode declaration in declarations)
        {
            SyntaxTree tree = declaration.SyntaxTree;
            if (!models.TryGetValue(tree, out SemanticModel? model))
            {
                model = compilation.GetSemanticModel(tree);
                models[tree] = model;
            }

            AppendNormalized(builder, declaration, model);
            builder.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// 把一个声明写成"名字都换成符号身份"的规范文本。
    ///
    /// 关键在**整棵子树只出一个身份**：遇到类型引用或名称引用就先序整棵替换，
    /// 于是 `System.Collections.Generic.List&lt;int&gt;`、`List&lt;int&gt;`、`using L = …; L`
    /// 三种写法得到同一个串。trivia（注释、空白）根本不参与。
    /// </summary>
    private static void AppendNormalized(StringBuilder builder, SyntaxNode node, SemanticModel model)
    {
        string? identity = IdentityOf(node, model);
        if (identity != null)
        {
            builder.Append(identity).Append(' ');
            return;
        }

        // 按序走子节点与子 token：`+` 这类**运算符是 token**，漏掉它 `a + b` 和 `a - b` 就分不出来了
        foreach (SyntaxNodeOrToken item in node.ChildNodesAndTokens())
        {
            if (item.IsNode)
            {
                AppendNormalized(builder, item.AsNode()!, model);
                continue;
            }

            SyntaxToken token = item.AsToken();
            if (!token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.EndOfFileToken))
            {
                builder.Append(token.Text).Append(' ');
            }
        }
    }

    /// <summary>这个节点整棵该被一个符号身份替代吗（类型引用 / 名称引用）；否则返回 null 继续往下走。</summary>
    private static string? IdentityOf(SyntaxNode node, SemanticModel model)
    {
        if (node is TypeSyntax)
        {
            return model.GetTypeInfo(node).Type is { } type ? SymbolIdentity(type) : null;
        }

        if (node is NameSyntax)
        {
            ISymbol? symbol = model.GetSymbolInfo(node).Symbol ?? model.GetDeclaredSymbol(node);
            return symbol == null ? null : SymbolIdentity(symbol);
        }

        return null;
    }

    /// <summary>
    /// 符号的规范身份：**完全限定的显示名**（用构造后的符号，所以泛型实参在里面）。
    ///
    /// 为什么不用 XML 文档 ID：它只给到"定义" —— `List&lt;int&gt;` 与 `List&lt;string&gt;` 都返回
    /// `T:System.Collections.Generic.List\`1`，**类型实参被吞掉**，两者 hash 就相等了；
    /// 那样"别人改过"检测不出来，预检放行、落盘会覆盖掉别人的改动。
    /// 又为什么不取 <c>OriginalDefinition</c>：它同样是未构造的 `List&lt;T&gt;`，实参一样会没。
    /// 完全限定格式则与 using / 别名 / 简称无关
    /// （`List&lt;int&gt;`、`System.Collections.Generic.List&lt;int&gt;`、`using L = …; L&lt;int&gt;`
    /// 解析到同一个类型，得到同一个串）。
    /// </summary>
    private static string SymbolIdentity(ISymbol symbol)
    {
        return symbol.ToDisplayString(IdentityFormat);
    }

    private static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n");
    }

    private static bool IsAccessor(ISymbol member)
    {
        return member is IMethodSymbol method && method.MethodKind is
            MethodKind.PropertyGet or MethodKind.PropertySet or
            MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise;
    }
}
