using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>
/// 把"改名"要动的地方算出来：**声明点 + 所有引用点**。
///
/// 只算不改、也不落盘 —— 真正的写入统一走拟定 → `confirm_draft`（和别的拟定编辑同一条路），
/// 这样"改名"自动获得拟定的全部保障：预检、冲突检测、落盘许可、原子写、journal。
///
/// 判等口径与 <see cref="ReferenceFinder"/> 一致：用 <c>OriginalDefinition</c>，
/// 所以泛型、扩展方法、同名重载不会被混在一起。
///
/// **边界**：只管**当前编译**里的语法树。字符串里拼出来的名字、注释、非 C# 引用（XAML/JSON/配置）、
/// 以及**别的项目**对它的引用，都不在范围里 —— 那些得调用方自己核对。
/// </summary>
public static class SymbolRenamer
{
    /// <summary>一处要改的标识符。</summary>
    public sealed record Hit(SyntaxTree Tree, SyntaxNode Node, bool IsDeclaration)
    {
        /// <summary>这处是"声明"还是"引用"。</summary>
        public string Kind => IsDeclaration ? "声明" : "引用";

        /// <summary>行号（1 起）、列号（1 起），按**改名之前**的原文算。</summary>
        public (int Line, int Column) Where()
        {
            FileLinePositionSpan span = Tree.GetLineSpan(Node.Span);
            return (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        }
    }

    /// <summary>这个符号（含它自己的声明）出现的全部标识符。</summary>
    public static IReadOnlyList<Hit> Hits(Compilation compilation, ISymbol target)
    {
        ISymbol wanted = target.OriginalDefinition;
        List<Hit> hits = [];

        // 引用点：IdentifierName（Foo）与 GenericName（Foo<T>）两种写法
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

                hits.Add(new Hit(tree, node, false));
            }
        }

        // 声明点：声明里的"名字"不是 IdentifierNameSyntax，得单独取
        foreach (SyntaxReference reference in target.DeclaringSyntaxReferences)
        {
            SyntaxNode declared = reference.GetSyntax();
            SyntaxToken token = NameTokenOf(declared);
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.Parent != null)
            {
                hits.Add(new Hit(reference.SyntaxTree, token.Parent, true));
            }
        }

        return hits;
    }

    /// <summary>按文件分组：一个文件只重写一次。</summary>
    public static Dictionary<SyntaxTree, List<SyntaxNode>> ByTree(IReadOnlyList<Hit> hits)
    {
        Dictionary<SyntaxTree, List<SyntaxNode>> grouped = [];
        foreach (Hit hit in hits)
        {
            if (!grouped.TryGetValue(hit.Tree, out List<SyntaxNode>? nodes))
            {
                nodes = [];
                grouped[hit.Tree] = nodes;
            }

            nodes.Add(hit.Node);
        }

        return grouped;
    }

    /// <summary>把这些节点换成新名字。</summary>
    public static SyntaxNode Rename(SyntaxNode root, IEnumerable<SyntaxNode> nodes, string newName)
    {
        HashSet<SyntaxNode> targets = [.. nodes];
        return root.ReplaceNodes(targets, (original, _) => WithName(original, newName));
    }

    private static SyntaxNode WithName(SyntaxNode node, string newName)
    {
        return node switch
        {
            IdentifierNameSyntax identifier => identifier.WithIdentifier(Token(identifier.Identifier, newName)),
            GenericNameSyntax generic => generic.WithIdentifier(Token(generic.Identifier, newName)),
            MethodDeclarationSyntax method => method.WithIdentifier(Token(method.Identifier, newName)),
            PropertyDeclarationSyntax property => property.WithIdentifier(Token(property.Identifier, newName)),
            EventDeclarationSyntax @event => @event.WithIdentifier(Token(@event.Identifier, newName)),
            VariableDeclaratorSyntax variable => variable.WithIdentifier(Token(variable.Identifier, newName)),
            BaseTypeDeclarationSyntax type => type.WithIdentifier(Token(type.Identifier, newName)),
            DelegateDeclarationSyntax @delegate => @delegate.WithIdentifier(Token(@delegate.Identifier, newName)),
            EnumMemberDeclarationSyntax member => member.WithIdentifier(Token(member.Identifier, newName)),
            ParameterSyntax parameter => parameter.WithIdentifier(Token(parameter.Identifier, newName)),
            TypeParameterSyntax typeParameter => typeParameter.WithIdentifier(Token(typeParameter.Identifier, newName)),
            LocalFunctionStatementSyntax local => local.WithIdentifier(Token(local.Identifier, newName)),
            ConstructorDeclarationSyntax constructor => constructor.WithIdentifier(Token(constructor.Identifier, newName)),
            _ => node,
        };
    }

    /// <summary>换名字但**保留 trivia**：接上原有的空白/注释，免得动一处格式全乱。</summary>
    private static SyntaxToken Token(SyntaxToken original, string newName)
    {
        return SyntaxFactory.Identifier(newName)
            .WithLeadingTrivia(original.LeadingTrivia)
            .WithTrailingTrivia(original.TrailingTrivia);
    }

    /// <summary>声明语法里的"名字"token（拿不到就返回 default，调用方跳过）。</summary>
    private static SyntaxToken NameTokenOf(SyntaxNode declared)
    {
        return declared switch
        {
            MethodDeclarationSyntax method => method.Identifier,
            PropertyDeclarationSyntax property => property.Identifier,
            FieldDeclarationSyntax field => SingleVariable(field.Declaration),
            EventDeclarationSyntax @event => @event.Identifier,
            EventFieldDeclarationSyntax eventField => SingleVariable(eventField.Declaration),
            VariableDeclaratorSyntax variable => variable.Identifier,
            BaseTypeDeclarationSyntax type => type.Identifier,
            DelegateDeclarationSyntax @delegate => @delegate.Identifier,
            EnumMemberDeclarationSyntax member => member.Identifier,
            ParameterSyntax parameter => parameter.Identifier,
            TypeParameterSyntax typeParameter => typeParameter.Identifier,
            LocalFunctionStatementSyntax local => local.Identifier,
            ConstructorDeclarationSyntax constructor => constructor.Identifier,
            _ => default,
        };
    }

    /// <summary>一条声明里的唯一变量名；`int a, b;` 这种一次声明多个的不猜（返回 default）。</summary>
    private static SyntaxToken SingleVariable(VariableDeclarationSyntax declaration)
    {
        return declaration.Variables.Count == 1 ? declaration.Variables[0].Identifier : default;
    }
}
