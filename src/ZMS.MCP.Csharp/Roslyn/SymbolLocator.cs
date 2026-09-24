using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>一个 <c>命名空间.类型[.成员]</c> 路径解析结果。</summary>
public sealed record ResolvedPath(INamedTypeSymbol Type, string MemberSpec);

/// <summary>把完全限定名解析成 Roslyn 符号。</summary>
public static class SymbolLocator
{
    private static readonly SymbolDisplayFormat QualifiedNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    /// <summary>按 <c>命名空间.类型</c> 找类型，支持嵌套类型（用 <c>.</c> 分隔）。</summary>
    public static INamedTypeSymbol? FindType(Compilation compilation, string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException("Type name is empty.");
        }

        string trimmed = typeName.Trim();
        INamedTypeSymbol? direct = compilation.GetTypeByMetadataName(trimmed);
        if (direct != null)
        {
            return direct;
        }

        string[] parts = trimmed.Split('.');
        INamespaceSymbol? current = compilation.GlobalNamespace;
        for (int i = 0; i < parts.Length; i++)
        {
            INamedTypeSymbol? type = current == null ? null : PickType(trimmed, current.GetTypeMembers(), parts[i]);
            if (type != null)
            {
                for (int j = i + 1; j < parts.Length; j++)
                {
                    INamedTypeSymbol? nested = PickType(trimmed, type.GetTypeMembers(), parts[j]);
                    if (nested == null)
                    {
                        break;
                    }

                    type = nested;
                }

                return type;
            }

            current = current?.GetNamespaceMembers().FirstOrDefault(ns => ns.Name == parts[i]);
            if (current == null)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 在候选类型里按名字挑一个：支持 <c>List</c> 与带元数的 <c>List`1</c> 两种写法。
    /// 命中**多个**声明时不猜 —— 直接报"匹配到多个声明"（同名类型分处两个文件等）。
    /// </summary>
    private static INamedTypeSymbol? PickType(string path, IEnumerable<INamedTypeSymbol> candidates, string name)
    {
        List<INamedTypeSymbol> hits =
        [
            .. candidates.Where(type => type.Name.Equals(name, StringComparison.Ordinal)
                || type.MetadataName.Equals(name, StringComparison.Ordinal)),
        ];
        if (hits.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{path}' 匹配到 {hits.Count} 个类型声明，请给出更完整的路径：{Environment.NewLine}"
                + string.Join(Environment.NewLine, hits.Select(type => "  - " + type.ToDisplayString())));
        }

        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>按**顶层**逗号切分参数类型：括号 / 尖括号里的逗号不算（如 <c>Dictionary&lt;int,string&gt;,int</c>）。</summary>
    internal static List<string> SplitTopLevel(string text)
    {
        List<string> parts = [];
        int depth = 0;
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (current is '(' or '<' or '[')
            {
                depth++;
            }
            else if (current is ')' or '>' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (current == ',' && depth == 0)
            {
                parts.Add(text[start..index].Trim());
                start = index + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return [.. parts.Where(part => part.Length > 0)];
    }

    /// <summary>
    /// 解析 <c>命名空间.类型.成员(参数类型,...)</c>。
    /// 从右往左找「能被解析成类型的最长前缀」，其余部分作为成员说明。
    /// </summary>
    public static ResolvedPath Resolve(Compilation compilation, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is empty.");
        }

        string trimmed = path.Trim();
        INamedTypeSymbol? best = null;
        int bestIndex = -1;
        int index = -1;
        while ((index = trimmed.IndexOf('.', index + 1)) >= 0)
        {
            INamedTypeSymbol? type = FindType(compilation, trimmed[..index]);
            if (type != null)
            {
                best = type;
                bestIndex = index;
            }
        }

        if (best == null)
        {
            best = FindType(compilation, trimmed);
            bestIndex = -1;
        }

        if (best == null)
        {
            throw new InvalidOperationException($"Type not found for path '{path}'. Use ListTypes to see available fully qualified names.");
        }

        string memberSpec = bestIndex < 0 ? "" : trimmed[(bestIndex + 1)..];
        return new ResolvedPath(best, memberSpec);
    }

    /// <summary>成员说明可以是 <c>Name</c> 或 <c>Name(参数类型,...)</c>（参数类型支持简名）。</summary>
    public static IReadOnlyList<ISymbol> FindMembers(INamedTypeSymbol type, string memberSpec)
    {
        if (string.IsNullOrWhiteSpace(memberSpec))
        {
            return [];
        }

        string name = memberSpec.Trim();
        string[]? parameters = null;
        int bracket = name.IndexOf('(');
        if (bracket >= 0 && name.EndsWith(')'))
        {
            string inner = name[(bracket + 1)..^1].Trim();
            parameters = inner.Length == 0
                ? []
                : [.. SplitTopLevel(inner)];
            name = name[..bracket].Trim();
        }

        List<ISymbol> candidates = type.GetMembers(name)
            .Where(member => !member.IsImplicitlyDeclared)
            .ToList();
        if (candidates.Count == 0)
        {
            candidates = type.GetMembers()
                .Where(member => !member.IsImplicitlyDeclared && string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (parameters == null)
        {
            return candidates;
        }

        return candidates
            .Where(member => member is IMethodSymbol method && MatchesParameters(method, parameters))
            .ToList();
    }

    /// <summary>解析成恰好一个成员，找不到或有歧义时报错。</summary>
    public static ISymbol ResolveSingleMember(INamedTypeSymbol type, string memberSpec)
    {
        if (string.IsNullOrWhiteSpace(memberSpec))
        {
            throw new InvalidOperationException($"Member path is required (type '{type.ToDisplayString(QualifiedNameFormat)}').");
        }

        IReadOnlyList<ISymbol> members = FindMembers(type, memberSpec);
        if (members.Count == 0)
        {
            throw new InvalidOperationException($"Member '{memberSpec}' not found in '{type.ToDisplayString(QualifiedNameFormat)}'. Use ListMembers first.");
        }

        if (members.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{memberSpec}' matches {members.Count} overloads, add a parameter list to disambiguate:{Environment.NewLine}"
                + MemberListRendering.DescribeOverloads(members));
        }

        return members[0];
    }

    /// <summary>
    /// 列出项目源码中声明的所有类型（完全限定名）+ **全部**声明位置。
    /// 分部类（<c>partial</c>）的多个声明合并成一条，位置按文件与行号排序。
    /// </summary>
    public static IReadOnlyList<(INamedTypeSymbol Symbol, IReadOnlyList<(string FilePath, int Line)> Locations)> ListTypes(
        Compilation compilation,
        string filter)
    {
        Dictionary<INamedTypeSymbol, List<(string FilePath, int Line)>> collected = new(SymbolEqualityComparer.Default);
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
            {
                if (node is not BaseTypeDeclarationSyntax && node is not DelegateDeclarationSyntax)
                {
                    continue;
                }

                if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol symbol)
                {
                    continue;
                }

                string name = symbol.ToDisplayString(QualifiedNameFormat);
                if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileLinePositionSpan span = node.GetLocation().GetLineSpan();
                if (!collected.TryGetValue(symbol, out List<(string FilePath, int Line)>? locations))
                {
                    locations = [];
                    collected[symbol] = locations;
                }

                locations.Add((span.Path, span.StartLinePosition.Line + 1));
            }
        }

        return collected
            .Select(pair => (
                Symbol: pair.Key,
                Locations: (IReadOnlyList<(string FilePath, int Line)>)
                [
                    .. pair.Value
                        .OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.Line),
                ]))
            .OrderBy(item => item.Locations[0].FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Locations[0].Line)
            .ToList();
    }

    public static string DisplayName(INamedTypeSymbol type)
    {
        return type.ToDisplayString(QualifiedNameFormat);
    }

    private static bool MatchesParameters(IMethodSymbol method, string[] parameters)
    {
        if (method.Parameters.Length != parameters.Length)
        {
            return false;
        }

        for (int i = 0; i < parameters.Length; i++)
        {
            if (!TypeMatches(parameters[i], method.Parameters[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>比较参数类型：支持 C# 关键字别名（string/int/...）、简名与全名。</summary>
    internal static bool TypeMatches(string expected, ITypeSymbol type)
    {
        string trimmed = expected.Trim();
        string full = type.ToDisplayString(QualifiedNameFormat);
        string shortName = type.Name;
        if (string.Equals(trimmed, full, StringComparison.Ordinal) ||
            string.Equals(trimmed, shortName, StringComparison.Ordinal))
        {
            return true;
        }

        if (KeywordAliases.TryGetValue(trimmed, out string? alias))
        {
            return string.Equals(alias, shortName, StringComparison.Ordinal);
        }

        return string.Equals(trimmed, full, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, shortName, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, string> KeywordAliases = new(StringComparer.Ordinal)
    {
        ["bool"] = "Boolean",
        ["byte"] = "Byte",
        ["sbyte"] = "SByte",
        ["char"] = "Char",
        ["decimal"] = "Decimal",
        ["double"] = "Double",
        ["float"] = "Single",
        ["int"] = "Int32",
        ["uint"] = "UInt32",
        ["long"] = "Int64",
        ["ulong"] = "UInt64",
        ["short"] = "Int16",
        ["ushort"] = "UInt16",
        ["object"] = "Object",
        ["dynamic"] = "Object",
        ["string"] = "String",
    };
}
