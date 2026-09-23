using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Tools;

/// <summary>
/// 符号层工具：完全限定名 + 成员路径 直接定位类型/成员，读改写都不需要知道文件路径。
/// memberPath 语法：<c>命名空间.类型[.成员[(参数类型,...)]]</c>
/// </summary>
[McpServerToolType]
public static class SymbolTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List every type declared in a C# project: fully qualified name, file and line. Use the returned names as typePath for other tools.")]
    public static string ListTypes(
        [Description("Path to the .csproj")] string projectPath,
        [Description("Optional case-insensitive substring filter on the type name")] string filter = "")
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(projectPath);
            IReadOnlyList<(INamedTypeSymbol Symbol, string FilePath, int Line)> types = SymbolLocator.ListTypes(project.Compilation, filter);

            StringBuilder builder = new();
            builder.AppendLine($"# {project.Info.ProjectPath}");
            AppendModeNotice(builder, project);
            builder.AppendLine($"- TFM: `{project.Info.TargetFramework}` | source files: {project.Info.SourceFiles.Count} | types: {types.Count}");
            builder.AppendLine();
            foreach ((INamedTypeSymbol symbol, string filePath, int line) in types)
            {
                builder.AppendLine($"- `{SymbolLocator.DisplayName(symbol)}` ({symbol.TypeKind.ToString().ToLowerInvariant()}) — {Relative(project.Info.ProjectDirectory, filePath)}:{line}");
            }

            return builder.ToString();
        });
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the members of a type. typePath = fully qualified type name, e.g. 'My.Namespace.MyType'.")]
    public static string ListMembers(
        [Description("Path to the .csproj")] string projectPath,
        [Description("Fully qualified type name")] string typePath)
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(projectPath);
            INamedTypeSymbol type = SymbolLocator.Resolve(project.Compilation, typePath).Type;

            StringBuilder builder = new();
            builder.AppendLine($"# {SymbolLocator.DisplayName(type)}  ({type.TypeKind.ToString().ToLowerInvariant()})");
            AppendModeNotice(builder, project);
            builder.AppendLine($"- TFM: `{project.Info.TargetFramework}`");
            builder.AppendLine();
            List<ISymbol> members = type.GetMembers()
                .Where(member => !member.IsImplicitlyDeclared && !IsAccessor(member))
                .OrderBy(member => member.Kind.ToString(), StringComparer.Ordinal)
                .ThenBy(member => member.Name, StringComparer.Ordinal)
                .ToList();
            if (members.Count == 0)
            {
                builder.AppendLine("_(no members)_");
                return builder.ToString();
            }

            foreach (ISymbol member in members)
            {
                builder.AppendLine($"- `{member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}`{Location(project, member)}");
            }

            return builder.ToString();
        });
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read one member (or a whole type when memberSpec is omitted): signature, file, line range and source code — without needing the file path.")]
    public static string GetMember(
        [Description("Path to the .csproj")] string projectPath,
        [Description("'Ns.Type' for the type itself, or 'Ns.Type.Member' / 'Ns.Type.Method(int,string)' for a member")] string memberPath)
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(projectPath);
            ResolvedPath resolved = SymbolLocator.Resolve(project.Compilation, memberPath);
            string body = string.IsNullOrWhiteSpace(resolved.MemberSpec)
                ? CodeEditor.Describe(resolved.Type)
                : CodeEditor.Describe(SymbolLocator.ResolveSingleMember(resolved.Type, resolved.MemberSpec));

            string notice = project.ModeNotice();
            return string.IsNullOrEmpty(notice)
                ? body
                : notice + Environment.NewLine + Environment.NewLine + body;
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Replace an existing member declaration with new C# code. Returns the changed file and line range.")]
    public static string UpdateMember(
        [Description("Path to the .csproj")] string projectPath,
        [Description("'Ns.Type.Member' or 'Ns.Type.Method(int,string)'")] string memberPath,
        [Description("New member declaration, e.g. 'public int Add(int a, int b) { return a + b; }'")] string code,
        [Description("Run Roslyn formatter on the file afterwards (default true)")] bool format = true)
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(projectPath);
            ResolvedPath resolved = SymbolLocator.Resolve(project.Compilation, memberPath);
            ISymbol symbol = SymbolLocator.ResolveSingleMember(resolved.Type, resolved.MemberSpec);
            EditResult result = CodeEditor.ReplaceMember(symbol, code, format);
            return DescribeEdit(project, result, symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Add a new member to a type. before = name of an existing member to insert before (empty = append at the end). Returns the changed file and line range.")]
    public static string AddMember(
        [Description("Path to the .csproj")] string projectPath,
        [Description("Fully qualified type name")] string typePath,
        [Description("Member declaration to add")] string code,
        [Description("Existing member name to insert before (optional)")] string before = "",
        [Description("Run Roslyn formatter on the file afterwards (default true)")] bool format = true)
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(projectPath);
            INamedTypeSymbol type = SymbolLocator.Resolve(project.Compilation, typePath).Type;
            EditResult result = CodeEditor.AddMember(type, code, before, format);
            return DescribeEdit(project, result, "new member in " + SymbolLocator.DisplayName(type));
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Remove a member from its type. Returns the changed file and line range.")]
    public static string RemoveMember(
        [Description("Path to the .csproj")] string projectPath,
        [Description("'Ns.Type.Member' or 'Ns.Type.Method(int,string)'")] string memberPath,
        [Description("Run Roslyn formatter on the file afterwards (default true)")] bool format = true)
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(projectPath);
            ResolvedPath resolved = SymbolLocator.Resolve(project.Compilation, memberPath);
            ISymbol symbol = SymbolLocator.ResolveSingleMember(resolved.Type, resolved.MemberSpec);
            EditResult result = CodeEditor.RemoveMember(symbol, format);
            return DescribeEdit(project, result, symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        });
    }

    /// <summary>降级加载时把"简化模式"提示插到输出里（评估模式不插）。</summary>
    private static void AppendModeNotice(StringBuilder builder, LoadedProject project)
    {
        string notice = project.ModeNotice();
        if (!string.IsNullOrEmpty(notice))
        {
            builder.AppendLine(notice);
        }
    }

    private static string DescribeEdit(LoadedProject project, EditResult result, string target)
    {
        StringBuilder builder = new();
        AppendModeNotice(builder, project);
        builder.AppendLine($"✅ {result.Action} `{target}`");
        builder.AppendLine($"- File: `{result.FilePath}` ({Relative(project.Info.ProjectDirectory, result.FilePath)})");
        builder.AppendLine($"- Lines: {result.StartLine}-{result.EndLine}");
        builder.AppendLine();
        builder.AppendLine("Re-read with GetMember to verify the result.");
        return builder.ToString();
    }

    private static bool IsAccessor(ISymbol member)
    {
        return member is IMethodSymbol method && method.MethodKind is
            MethodKind.PropertyGet or MethodKind.PropertySet or
            MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise;
    }

    private static string Location(LoadedProject project, ISymbol member)
    {
        SyntaxNode? node = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (node == null)
        {
            return "";
        }

        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        return $" — {Relative(project.Info.ProjectDirectory, span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    private static string Relative(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }
}
