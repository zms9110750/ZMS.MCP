using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>某个成员被引用的次数（连同它所属类型）。</summary>
public sealed record SymbolReferenceCount(string Member, string Type, int Count);

/// <summary>
/// 查找引用（`symbols` 的 `type` 里那个 `R`）。
/// 只有 <see cref="Compilation"/>、没有 Solution / Workspace 时的可行路径：
/// 逐棵语法树用 SemanticModel 找引用，判等一律用 <c>OriginalDefinition</c>
/// （泛型 / 扩展方法 / 同名重载才能区分），声明点不算引用。
///
/// **粒度按审查意见收口**：最细只到「成员」，**绝不暴露文件名与行号**；
/// 成员级条目超过 <see cref="DetailLimit"/> 时再缩略成「按类型聚合」。
/// </summary>
public static class ReferenceFinder
{
    /// <summary>成员级明细的条数上限，超过就缩略为类型级。</summary>
    public const int DetailLimit = 200;

    /// <summary>按成员聚合的引用次数（不含声明点），按次数降序。</summary>
    public static IReadOnlyList<SymbolReferenceCount> Count(Compilation compilation, ISymbol target)
    {
        ISymbol wanted = target.OriginalDefinition;
        Dictionary<(string Member, string Type), int> counts = [];
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
            {
                if (node is not (IdentifierNameSyntax or GenericNameSyntax))
                {
                    continue;
                }

                ISymbol? symbol = model.GetSymbolInfo(node).Symbol;
                if (symbol == null || !SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, wanted))
                {
                    continue;
                }

                if (IsDeclaration(symbol, tree, node))
                {
                    continue;
                }

                (string member, string type) = ContainerOf(model, node);
                counts.TryGetValue((member, type), out int current);
                counts[(member, type)] = current + 1;
            }
        }

        List<SymbolReferenceCount> results = [];
        foreach (KeyValuePair<(string Member, string Type), int> pair in counts)
        {
            results.Add(new SymbolReferenceCount(pair.Key.Member, pair.Key.Type, pair.Value));
        }

        results.Sort((left, right) => right.Count.CompareTo(left.Count));
        return results;
    }

    /// <summary>
    /// 渲染成给 agent 看的文本。**「精确」= path 命中了一个具体符号** —— 类型与成员都算精确；
    /// 只有"没指到具体符号"（命名空间）才算不精确、只报类型级汇总。
    /// 精确时说到「某个类型的某个成员里被引用多少次」；成员级条目超过上限就缩略成按类型聚合。
    /// 任何情况下都不给文件名与行号。
    /// </summary>
    public static string Describe(Compilation compilation, ISymbol target)
    {
        // 「精确」= 命中一个具体符号：类型与成员都算精确，只有命名空间这种"没指到具体符号"的才算不精确。
        bool exact = target is not INamespaceSymbol;
        IReadOnlyList<SymbolReferenceCount> counts = Count(compilation, target);
        StringBuilder builder = new();
        builder.AppendLine("## 引用");
        builder.AppendLine($"- 符号：{target.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}（{(exact ? "精确到成员" : "只到类型")}）");

        int total = 0;
        foreach (SymbolReferenceCount item in counts)
        {
            total += item.Count;
        }

        if (total == 0)
        {
            builder.AppendLine("- 没有被引用。");
            return builder.ToString();
        }

        builder.AppendLine($"- 合计 {total} 次，分布在 {counts.Count} 个成员里");

        if (!exact)
        {
            // 符号不精确：只给「某某类里被引用了多少次」
            Dictionary<string, int> byType = SumByType(counts);
            foreach (KeyValuePair<string, int> pair in byType.OrderByDescending(pair => pair.Value))
            {
                builder.AppendLine($"  - {pair.Key}：{pair.Value} 次");
            }

            return builder.ToString();
        }

        if (counts.Count > DetailLimit)
        {
            // 成员级太多：缩略成「多少类，类里面引用多少次」
            builder.AppendLine($"  （成员条目超过 {DetailLimit}，缩略为按类型统计）");
            Dictionary<string, int> byType = SumByType(counts);
            foreach (KeyValuePair<string, int> pair in byType.OrderByDescending(pair => pair.Value))
            {
                builder.AppendLine($"  - {pair.Key}：{pair.Value} 次");
            }

            return builder.ToString();
        }

        foreach (SymbolReferenceCount item in counts)
        {
            builder.AppendLine($"  - {item.Type} 的 {item.Member}：{item.Count} 次");
        }

        return builder.ToString();
    }

    private static Dictionary<string, int> SumByType(IReadOnlyList<SymbolReferenceCount> counts)
    {
        Dictionary<string, int> byType = [];
        foreach (SymbolReferenceCount item in counts)
        {
            byType.TryGetValue(item.Type, out int current);
            byType[item.Type] = current + item.Count;
        }

        return byType;
    }

    /// <summary>这个节点是不是该符号自己的声明点（声明不算引用）。</summary>
    private static bool IsDeclaration(ISymbol symbol, SyntaxTree tree, SyntaxNode node)
    {
        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
        {
            if (reference.SyntaxTree == tree && reference.Span == node.Span)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>引用所在的成员与类型（逐层往上问声明的符号）。</summary>
    private static (string Member, string Type) ContainerOf(SemanticModel model, SyntaxNode node)
    {
        SyntaxNode? current = node;
        while (current != null)
        {
            ISymbol? declared = model.GetDeclaredSymbol(current);
            if (declared is INamedTypeSymbol type)
            {
                INamedTypeSymbol? owner = type.ContainingType;
                return (
                    type.Name,
                    owner == null ? SymbolLocator.DisplayName(type) : SymbolLocator.DisplayName(owner));
            }

            if (declared is IMethodSymbol method)
            {
                return (ShortName(method) + Parameters(method), TypeNameOf(method));
            }

            if (declared is IPropertySymbol or IFieldSymbol or IEventSymbol)
            {
                INamedTypeSymbol? owner = declared.ContainingType;
                return (declared.Name, owner == null ? "(全局)" : SymbolLocator.DisplayName(owner));
            }

            current = current.Parent;
        }

        return ("(全局)", "(全局)");
    }

    private static string TypeNameOf(ISymbol symbol)
    {
        INamedTypeSymbol? owner = symbol.ContainingType;
        return owner == null ? "(全局)" : SymbolLocator.DisplayName(owner);
    }

    private static string ShortName(IMethodSymbol method)
    {
        return method.MethodKind == MethodKind.Constructor ? method.ContainingType.Name : method.Name;
    }

    private static string Parameters(IMethodSymbol method)
    {
        List<string> parts = [];
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            parts.Add(parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }

        return "(" + string.Join(", ", parts) + ")";
    }
}
