using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>一处引用：文件、行号、所在成员（能判定时）与该行原文。</summary>
public sealed record SymbolReference(string FilePath, int Line, string Container, string Text);

/// <summary>
/// 查找引用。只有 <see cref="Compilation"/>、没有 Solution / Workspace 时的可行路径：
/// 先按名字缩小候选（<c>GetSymbolsWithName</c> 不适用于所有场景，所以这里逐棵树走语义模型），
/// 判等一律用 <c>OriginalDefinition</c> —— 泛型、扩展方法、同名重载都能正确区分。
/// （官方 <c>SymbolFinder.FindReferencesAsync</c> 的第一个参数必须是 Solution，我们拿不到，故不走它。）
/// </summary>
public static class ReferenceFinder
{
    /// <summary>找某个符号被引用到的位置（不含它自己的声明）。</summary>
    public static IReadOnlyList<SymbolReference> Find(Compilation compilation, ISymbol target)
    {
        ISymbol wanted = target.OriginalDefinition;
        List<SymbolReference> results = [];
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
            {
                if (node is not (IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax))
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

                FileLinePositionSpan span = node.GetLocation().GetLineSpan();
                results.Add(new SymbolReference(
                    span.Path,
                    span.StartLinePosition.Line + 1,
                    ContainerOf(model, node),
                    TextOf(tree, span.StartLinePosition.Line)));
            }
        }

        return results;
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

    /// <summary>引用所在的成员/类型（逐层往上问声明的符号）。</summary>
    private static string ContainerOf(SemanticModel model, SyntaxNode node)
    {
        SyntaxNode? current = node;
        while (current != null)
        {
            ISymbol? declared = model.GetDeclaredSymbol(current);
            if (declared is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol)
            {
                return declared.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            }

            current = current.Parent;
        }

        return "(全局)";
    }

    private static string TextOf(SyntaxTree tree, int zeroBasedLine)
    {
        return tree.GetText().Lines[zeroBasedLine].ToString().Trim();
    }
}
