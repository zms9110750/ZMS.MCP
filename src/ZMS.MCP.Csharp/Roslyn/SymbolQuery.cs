using Microsoft.CodeAnalysis;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>
/// 符号种类。字母取自需求文档的 <c>NCSIPFEMD</c>：
/// N 命名空间、C 类型（class）、S 结构、I 接口、P 属性、F 字段、E 事件、M 方法、D 文档注释。
///
/// 说明：
/// - 文档写的是 <c>NCSIPFED</c>（没有 M）；方法显然要能列，这里补上 M。
/// - 文档注释那边的 <c>T</c>（类型）在这里展开成 C|S|I（源码能分辨 class/struct/interface）。
/// - <c>D</c> 不是一种"种类"，而是过滤开关：给了它就只列**带 XML 文档注释**的符号；
///   只给 D（不给别的字母）时按"所有种类"处理。
/// </summary>
[Flags]
public enum SymbolKinds
{
    None = 0,
    Namespace = 1 << 0,
    Class = 1 << 1,
    Struct = 1 << 2,
    Interface = 1 << 3,
    Property = 1 << 4,
    Field = 1 << 5,
    Event = 1 << 6,
    Method = 1 << 7,
    Document = 1 << 8,
    All = Namespace | Class | Struct | Interface | Property | Field | Event | Method,
}

/// <summary>
/// 修饰符过滤：**所有给定条件都要满足**（AND）。
/// 注意可访问性是互斥的，所以同时给「公开,私有」会得到空集 —— 这是刻意的，不做"取或"猜测。
/// </summary>
[Flags]
public enum SymbolModifiers
{
    None = 0,
    Public = 1 << 0,
    Internal = 1 << 1,
    Protected = 1 << 2,
    Private = 1 << 3,
    Static = 1 << 4,
    Const = 1 << 5,
    Abstract = 1 << 6,
    ReadOnly = 1 << 7,
    Virtual = 1 << 8,
    Override = 1 << 9,
}

/// <summary>一个被列出的符号。</summary>
public sealed class SymbolEntry
{
    public ISymbol Symbol { get; }

    /// <summary>种类词（class / struct / property / method …）。</summary>
    public string Kind { get; }

    /// <summary>所属命名空间（点分；全局命名空间为空串）。</summary>
    public string Namespace { get; }

    /// <summary>所属类型（点分，含嵌套类型；命名空间级符号为空串）。</summary>
    public string Container { get; }

    /// <summary>显示用签名（含修饰符、类型、参数、可空标注）。</summary>
    public string Signature { get; }

    public string FilePath { get; }

    public int Line { get; }

    public bool IsType { get; }

    public SymbolEntry(
        ISymbol symbol,
        string kind,
        string @namespace,
        string container,
        string signature,
        string filePath,
        int line,
        bool isType)
    {
        Symbol = symbol;
        Kind = kind;
        Namespace = @namespace;
        Container = container;
        Signature = signature;
        FilePath = filePath;
        Line = line;
        IsType = isType;
    }
}

/// <summary>把需求文档里的字符串写法解析成过滤条件。</summary>
public static class SymbolFilterParser
{
    private const string KnownKindLetters = "NCSITPFEMD";

    /// <summary>解析 <c>NCSIPFEMD</c> 这样的字母串（大小写不敏感，非法字母忽略）。</summary>
    public static SymbolKinds ParseKinds(string value)
    {
        SymbolKinds kinds = SymbolKinds.None;
        foreach (char raw in value)
        {
            switch (char.ToUpperInvariant(raw))
            {
                case 'N':
                    kinds |= SymbolKinds.Namespace;
                    break;
                case 'C':
                    kinds |= SymbolKinds.Class;
                    break;
                case 'S':
                    kinds |= SymbolKinds.Struct;
                    break;
                case 'I':
                    kinds |= SymbolKinds.Interface;
                    break;
                case 'T':
                    // 类型：源码侧展开成三种具体类型
                    kinds |= SymbolKinds.Class | SymbolKinds.Struct | SymbolKinds.Interface;
                    break;
                case 'P':
                    kinds |= SymbolKinds.Property;
                    break;
                case 'F':
                    kinds |= SymbolKinds.Field;
                    break;
                case 'E':
                    kinds |= SymbolKinds.Event;
                    break;
                case 'M':
                    kinds |= SymbolKinds.Method;
                    break;
                case 'D':
                    kinds |= SymbolKinds.Document;
                    break;
            }
        }

        return kinds;
    }

    /// <summary>
    /// 返回 kind 串里无法识别的字母（去重，忽略空白）。
    /// 调用方据此提醒使用者，而不是静默忽略 —— 否则"拼错的字母"看起来就像"筛出来是空的"。
    /// </summary>
    public static IReadOnlyList<char> UnknownKindLetters(string value)
    {
        List<char> unknown = [];
        foreach (char raw in value)
        {
            char upper = char.ToUpperInvariant(raw);
            if (char.IsWhiteSpace(upper) || KnownKindLetters.Contains(upper) || unknown.Contains(upper))
            {
                continue;
            }

            unknown.Add(upper);
        }

        return unknown;
    }

    /// <summary>解析修饰符词（中文或英文），无法识别的词忽略。</summary>
    public static SymbolModifiers ParseModifiers(IEnumerable<string> values)
    {
        SymbolModifiers modifiers = SymbolModifiers.None;
        foreach (string raw in values)
        {
            string value = raw.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (value is "公开" || value.Equals("public", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= SymbolModifiers.Public;
            }
            else if (value is "程序集" || value.Equals("internal", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= SymbolModifiers.Internal;
            }
            else if (value is "保护" || value.Equals("protected", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= SymbolModifiers.Protected;
            }
            else if (value is "私有" || value.Equals("private", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= SymbolModifiers.Private;
            }
            else if (value is "静态" || value.Equals("static", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= SymbolModifiers.Static;
            }
            else if (value is "常量" || value.Equals("const", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= SymbolModifiers.Const;
            }
            else if (value is "抽象" || value.Equals("abstract", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= SymbolModifiers.Abstract;
            }
            else if (value is "只读" || value.Equals("readonly", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= SymbolModifiers.ReadOnly;
            }
            else if (value is "虚" || value.Equals("virtual", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= SymbolModifiers.Virtual;
            }
            else if (value is "覆写" || value.Equals("override", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= SymbolModifiers.Override;
            }
        }

        return modifiers;
    }

    /// <summary>参数类型列表：逗号分隔（如 <c>int,string</c>）。空串表示不过滤。</summary>
    public static IReadOnlyList<string> ParseArgumentTypes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}

/// <summary>
/// 列出项目源码里声明的符号（不含引用的程序集），支持按种类、修饰符、方法参数、是否有文档注释过滤。
/// </summary>
public static class SymbolQuery
{
    private static readonly SymbolDisplayFormat SignatureFormat = new(
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
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            // 保留 string? 这类可空标注，否则签名看不到可空性
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>一次查询的过滤条件。</summary>
    private sealed record QueryFilter(
        SymbolKinds Kinds,
        SymbolModifiers Modifiers,
        IReadOnlyList<string> ArgumentTypes,
        bool DocumentedOnly);

    public static IReadOnlyList<SymbolEntry> List(
        Compilation compilation,
        SymbolKinds kinds,
        SymbolModifiers modifiers,
        IReadOnlyList<string> argumentTypes)
    {
        // D 是"只看有文档注释的"开关，不参与种类判断
        bool documentedOnly = kinds.HasFlag(SymbolKinds.Document);
        SymbolKinds effectiveKinds = kinds & ~SymbolKinds.Document;
        if (effectiveKinds == SymbolKinds.None)
        {
            effectiveKinds = SymbolKinds.All;
        }

        QueryFilter filter = new(effectiveKinds, modifiers, argumentTypes, documentedOnly);
        List<SymbolEntry> entries = [];
        CollectNamespace(compilation.Assembly.GlobalNamespace, entries, filter);

        // Container 升序：先顶层类型，再它的成员，再嵌套类型，再嵌套类型的成员…
        // 与源码层级一致（同层按行号）
        return entries
            .OrderBy(entry => entry.Namespace, StringComparer.Ordinal)
            .ThenBy(entry => entry.Container, StringComparer.Ordinal)
            .ThenBy(entry => entry.Line)
            .ToList();
    }

    private static void CollectNamespace(INamespaceSymbol @namespace, List<SymbolEntry> entries, QueryFilter filter)
    {
        // 只有子命名空间、自己没有类型定义的命名空间不列：否则 ZMS / ZMS.MCP 这类纯层级外壳
        // 会各自出现一条，且"位置"是整棵子树的所有文件，看起来跟最深的那条完全重复。
        bool emptyContainer = !@namespace.GetTypeMembers().Any() && @namespace.GetNamespaceMembers().Any();

        // 给了参数过滤时只看方法：命名空间和类型都不该出现；命名空间没有访问性/静态等修饰符，
        // 所以给了任何修饰符过滤时它都会被排除
        if (filter.ArgumentTypes.Count == 0 &&
            @namespace.ContainingNamespace != null &&
            filter.Kinds.HasFlag(SymbolKinds.Namespace) &&
            IsInSource(@namespace) &&
            !emptyContainer &&
            MatchesModifiers(@namespace, filter.Modifiers) &&
            MatchesDocumented(@namespace, filter))
        {
            entries.Add(Create(@namespace));
        }

        foreach (INamedTypeSymbol type in @namespace.GetTypeMembers())
        {
            CollectType(type, entries, filter);
        }

        foreach (INamespaceSymbol child in @namespace.GetNamespaceMembers())
        {
            CollectNamespace(child, entries, filter);
        }
    }

    private static void CollectType(INamedTypeSymbol type, List<SymbolEntry> entries, QueryFilter filter)
    {
        if (!IsInSource(type))
        {
            // 引用的程序集不列
            return;
        }

        if (MatchesTypeKind(type, filter.Kinds) &&
            MatchesModifiers(type, filter.Modifiers) &&
            filter.ArgumentTypes.Count == 0 &&
            MatchesDocumented(type, filter))
        {
            entries.Add(Create(type));
        }

        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || IsAccessor(member) || !IsInSource(member))
            {
                continue;
            }

            if (!MatchesMemberKind(member, filter.Kinds) ||
                !MatchesModifiers(member, filter.Modifiers) ||
                !MatchesDocumented(member, filter) ||
                !MatchesArguments(member, filter.ArgumentTypes))
            {
                continue;
            }

            entries.Add(Create(member));
        }

        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            CollectType(nested, entries, filter);
        }
    }

    private static bool MatchesTypeKind(INamedTypeSymbol type, SymbolKinds kinds)
    {
        return type.TypeKind switch
        {
            TypeKind.Class => kinds.HasFlag(SymbolKinds.Class),
            TypeKind.Struct => kinds.HasFlag(SymbolKinds.Struct),
            // 枚举跟着 S 走：S 在这个工具里表示"值类型"，enum 也是值类型。
            // delegate 不参与类型过滤（用户明确：不要）。
            TypeKind.Enum => kinds.HasFlag(SymbolKinds.Struct),
            TypeKind.Interface => kinds.HasFlag(SymbolKinds.Interface),
            _ => false,
        };
    }

    private static bool MatchesMemberKind(ISymbol member, SymbolKinds kinds)
    {
        return member switch
        {
            IPropertySymbol => kinds.HasFlag(SymbolKinds.Property),
            IFieldSymbol => kinds.HasFlag(SymbolKinds.Field),
            IEventSymbol => kinds.HasFlag(SymbolKinds.Event),
            IMethodSymbol => kinds.HasFlag(SymbolKinds.Method),
            _ => false,
        };
    }

    /// <summary>D：只保留带 XML 文档注释的符号。</summary>
    private static bool MatchesDocumented(ISymbol symbol, QueryFilter filter)
    {
        if (!filter.DocumentedOnly)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(symbol.GetDocumentationCommentXml());
    }

    private static bool MatchesModifiers(ISymbol symbol, SymbolModifiers modifiers)
    {
        if (modifiers == SymbolModifiers.None)
        {
            return true;
        }

        SymbolModifiers actual = SymbolModifiers.None;
        switch (symbol.DeclaredAccessibility)
        {
            case Accessibility.Public:
                actual |= SymbolModifiers.Public;
                break;
            case Accessibility.Internal:
                actual |= SymbolModifiers.Internal;
                break;
            case Accessibility.Protected:
            case Accessibility.ProtectedOrInternal:
                actual |= SymbolModifiers.Protected;
                break;
            case Accessibility.ProtectedAndInternal:
                // private protected：既是保护也是程序集
                actual |= SymbolModifiers.Protected | SymbolModifiers.Internal;
                break;
            case Accessibility.Private:
                actual |= SymbolModifiers.Private;
                break;
        }

        if (symbol.IsStatic)
        {
            actual |= SymbolModifiers.Static;
        }

        if (symbol is IFieldSymbol { IsConst: true })
        {
            actual |= SymbolModifiers.Const;
        }

        if (symbol.IsAbstract)
        {
            actual |= SymbolModifiers.Abstract;
        }

        if (symbol is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true })
        {
            actual |= SymbolModifiers.ReadOnly;
        }

        if (symbol.IsVirtual)
        {
            actual |= SymbolModifiers.Virtual;
        }

        if (symbol.IsOverride)
        {
            actual |= SymbolModifiers.Override;
        }

        // 全部给定条件都要满足（"公开 + 静态" = 公开且静态；"公开 + 私有" = 空集）
        return (modifiers & actual) == modifiers;
    }

    private static bool MatchesArguments(ISymbol member, IReadOnlyList<string> argumentTypes)
    {
        if (argumentTypes.Count == 0)
        {
            return true;
        }

        if (member is not IMethodSymbol method || method.Parameters.Length != argumentTypes.Count)
        {
            // 给了参数过滤就只看方法
            return false;
        }

        for (int index = 0; index < argumentTypes.Count; index++)
        {
            if (!SymbolLocator.TypeMatches(argumentTypes[index], method.Parameters[index].Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAccessor(ISymbol member)
    {
        return member is IMethodSymbol method && method.MethodKind is
            MethodKind.PropertyGet or MethodKind.PropertySet or
            MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise;
    }

    private static bool IsInSource(ISymbol symbol)
    {
        return symbol.Locations.Any(location => location.IsInSource);
    }

    private static SymbolEntry Create(ISymbol symbol)
    {
        FileLinePositionSpan span = symbol.Locations
            .Where(location => location.IsInSource)
            .Select(location => location.GetLineSpan())
            .FirstOrDefault();

        return new SymbolEntry(
            symbol,
            KindLabel(symbol),
            NamespaceName(symbol),
            ContainerName(symbol),
            Signature(symbol),
            span.Path ?? "",
            span.Path == null ? 0 : span.StartLinePosition.Line + 1,
            symbol is INamedTypeSymbol);
    }

    /// <summary>所属类型（点分，含嵌套类型）；命名空间级符号为空串。</summary>
    private static string ContainerName(ISymbol symbol)
    {
        INamedTypeSymbol? container = symbol is INamedTypeSymbol named
            ? named.ContainingType
            : symbol.ContainingType;
        return container?.ToDisplayString(SignatureFormat) ?? "";
    }

    private static string NamespaceName(ISymbol symbol)
    {
        INamespaceSymbol? @namespace = symbol switch
        {
            INamespaceSymbol item => item,
            INamedTypeSymbol item => item.ContainingNamespace,
            _ => symbol.ContainingType?.ContainingNamespace ?? symbol.ContainingNamespace,
        };

        string name = @namespace?.ToDisplayString() ?? "";
        return name == "<global namespace>" ? "" : name;
    }

    private static string Signature(ISymbol symbol)
    {
        string kind = KindLabel(symbol);
        string text = symbol.ToDisplayString(SignatureFormat);
        return symbol is INamedTypeSymbol ? kind + " " + text : text;
    }

    private static string KindLabel(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol type => type.TypeKind switch
            {
                TypeKind.Class => "class",
                TypeKind.Struct => "struct",
                TypeKind.Interface => "interface",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                _ => type.TypeKind.ToString().ToLowerInvariant(),
            },
            IMethodSymbol method => method.MethodKind switch
            {
                MethodKind.Constructor => "constructor",
                MethodKind.StaticConstructor => "static constructor",
                MethodKind.Destructor => "destructor",
                MethodKind.UserDefinedOperator or MethodKind.Conversion => "operator",
                _ => "method",
            },
            IPropertySymbol property => property.IsIndexer ? "indexer" : "property",
            IFieldSymbol field => field.IsConst ? "const" : "field",
            IEventSymbol => "event",
            INamespaceSymbol => "namespace",
            _ => symbol.Kind.ToString().ToLowerInvariant(),
        };
    }
}
