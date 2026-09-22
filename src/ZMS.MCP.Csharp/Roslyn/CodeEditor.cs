using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>一次写操作的结果：改动的文件与行号范围。</summary>
public sealed record EditResult(string FilePath, int StartLine, int EndLine, string Action);

/// <summary>基于 Roslyn 语法树的成员查看与编辑（不依赖文件路径定位）。</summary>
public static class CodeEditor
{
    private static readonly SyntaxAnnotation EditAnchor = new("ZMS.EditAnchor");

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

    /// <summary>整段替换一个成员声明。</summary>
    public static EditResult ReplaceMember(ISymbol symbol, string newCode, bool format)
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
        return WriteBack(node.SyntaxTree.FilePath, updated, "replaced");
    }

    /// <summary>在类型里新增成员，可用 <paramref name="before"/> 指定插到某个已有成员之前。</summary>
    public static EditResult AddMember(INamedTypeSymbol type, string code, string before, bool format)
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
        return WriteBack(typeNode.SyntaxTree.FilePath, updated, "added");
    }

    /// <summary>删除一个成员声明。</summary>
    public static EditResult RemoveMember(ISymbol symbol, bool format)
    {
        SyntaxNode node = SourceNode(symbol)
            ?? throw new InvalidOperationException($"'{symbol.Name}' has no source declaration to remove.");
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        SyntaxNode root = node.SyntaxTree.GetRoot();
        SyntaxNode? updated = root.RemoveNode(node, SyntaxRemoveOptions.KeepLeadingTrivia)
            ?? throw new InvalidOperationException("Failed to remove the node.");
        updated = FormatIfNeeded(updated, format);
        return WriteBack(node.SyntaxTree.FilePath, updated, "removed", (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
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

    private static EditResult WriteBack(string filePath, SyntaxNode root, string action, (int Start, int End)? fallback = null)
    {
        List<SyntaxNode> targets = root.GetAnnotatedNodes(EditAnchor).ToList();
        File.WriteAllText(filePath, root.ToFullString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (targets.Count > 0)
        {
            int start = targets.Min(node => node.GetLocation().GetLineSpan().StartLinePosition.Line) + 1;
            int end = targets.Max(node => node.GetLocation().GetLineSpan().EndLinePosition.Line) + 1;
            return new EditResult(filePath, start, end, action);
        }

        if (fallback.HasValue)
        {
            return new EditResult(filePath, fallback.Value.Start, fallback.Value.End, action);
        }

        return new EditResult(filePath, 0, 0, action);
    }

    /// <summary>
    /// 把代码片段解析成成员声明列表：包进一个临时类型再取 Members，
    /// 这样「一次传多个成员」「带 XML 注释」「带语法错误」都能得到确定结果。
    /// </summary>
    private static List<MemberDeclarationSyntax> ParseMembers(string code)
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
