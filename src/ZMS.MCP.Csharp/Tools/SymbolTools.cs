using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using ZMS.MCP.Csharp.Draft;
using ZMS.MCP.Csharp.Project;
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
    [Description(
        "List symbols declared in the project's own source (referenced assemblies are excluded). " +
        "type = letters from NCSIPFEMD (N namespace, C class, S struct, I interface, P property, F field, E event, M method); " +
        "T = the three type kinds together; D = only symbols carrying an XML doc comment; empty = all. " +
        "modifier = comma separated public / internal / protected / private / static / const / abstract / readonly / virtual / override; " +
        "every given condition must match (the accessibility conditions are mutually exclusive). " +
        "argsList = comma separated parameter types; when set, only methods with exactly those parameter types are listed.")]
    public static string ListSymbols(
        [Description("Path to the .csproj")] string csprojPath,
        [Description("Type letters, e.g. 'C' or 'NCSIPFEMD'. Empty = all")] string type = "",
        [Description("Modifier filter, e.g. 'public,static'. Empty = no filter")] string modifier = "",
        [Description("Method parameter types, e.g. 'string,int'. Empty = no filter")] string argsList = "")
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(csprojPath);
            SymbolKinds kinds = SymbolFilterParser.ParseKinds(type);
            SymbolModifiers modifierFilter = SymbolFilterParser.ParseModifiers(
                modifier.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            IReadOnlyList<string> parameters = SymbolFilterParser.ParseArgumentTypes(argsList);

            IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(project.Compilation, kinds, modifierFilter, parameters);
            IReadOnlyList<char> unknownKindLetters = SymbolFilterParser.UnknownKindLetters(type);

            StringBuilder builder = new();
            builder.AppendLine($"# {project.Info.ProjectPath}");
            AppendModeNotice(builder, project);
            builder.AppendLine(
                $"- TFM: `{project.Info.TargetFramework}` | 符号: {entries.Count}" +
                $" | 过滤: type='{type}' modifier='{modifier}' args='{argsList}'");
            if (unknownKindLetters.Count > 0)
            {
                // 别静默吞掉拼错的字母 —— 否则"筛出来是空的"看起来就像"项目里没符号"
                builder.AppendLine($"⚠ 无法识别的 type 字母已忽略：{string.Join(", ", unknownKindLetters)}（可用：N C S I T P F E M D）");
            }

            if (entries.Count == 0)
            {
                builder.AppendLine();
                builder.AppendLine("_(无匹配符号)_");
                if (SymbolFilterParser.ParseKinds(type).HasFlag(SymbolKinds.Document))
                {
                    // D 是"只看带文档注释的符号"的开关：误用它会让结果看起来像"项目里没符号"
                    builder.AppendLine("提示：`type` 里的 `D` 表示「只看带 XML 文档注释的符号」；去掉 `D` 即列出全部种类。");
                }

                return builder.ToString();
            }

            string? currentNamespace = null;
            List<SymbolEntry> namespaceSection = [];
            foreach (SymbolEntry entry in entries)
            {
                if (!string.Equals(currentNamespace, entry.Namespace, StringComparison.Ordinal))
                {
                    AppendSymbolSection(builder, namespaceSection, project);
                    namespaceSection = [];
                    currentNamespace = entry.Namespace;
                    builder.AppendLine();
                    builder.AppendLine($"## {(currentNamespace.Length == 0 ? "(global)" : currentNamespace)}");
                }

                namespaceSection.Add(entry);
            }

            AppendSymbolSection(builder, namespaceSection, project);
            return builder.ToString();
        });
    }

    /// <summary>一段（同一命名空间）里的符号行；按所属类型分层，并在同名重载超过 10 个时改分组显示。</summary>
    private static void AppendSymbolSection(StringBuilder builder, IReadOnlyList<SymbolEntry> section, LoadedProject project)
    {
        foreach (IGrouping<string, SymbolEntry> containerGroup in section.GroupBy(entry => entry.Container))
        {
            List<SymbolEntry> items = [.. containerGroup];
            // 缩进按所属层级的深度走：顶层 0，外层类型的成员与嵌套类型 1，嵌套类型的成员 2 …
            int depth = items[0].Container.Length == 0 ? 0 : items[0].Container.Split('.').Length;
            string indent = new string(' ', depth * 2);
            string container = items[0].Container.Length == 0 ? "" : $" [{items[0].Container}]";

            foreach (IReadOnlyList<SymbolEntry> group in MemberListRendering.GroupByName(items, entry => entry.Symbol))
            {
                // 3.7：同名重载超过 10 个 → 分组显示（不重复方法名）
                if (MemberListRendering.IsOverloadedGroup(group, entry => entry.Symbol))
                {
                    string owner = items[0].Container.Length == 0
                        ? group[0].Symbol.Name
                        : $"{items[0].Container}.{group[0].Symbol.Name}";
                    builder.AppendLine($"{indent}- `{owner}` 有 {group.Count} 个重载：");
                    foreach (SymbolEntry item in group)
                    {
                        builder.AppendLine($"{indent}  - `{MemberListRendering.ParameterList(item.Symbol)}`{Location(project, item.Symbol)}");
                    }

                    continue;
                }

                foreach (SymbolEntry entry in group)
                {
                    builder.AppendLine($"{indent}- `{entry.Signature}` ({entry.Kind}){container}{Location(project, entry.Symbol)}");
                }
            }
        }
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List every type declared in a C# project: fully qualified name, file and line. Use the returned names as typePath for other tools.")]
    public static string ListTypes(
        [Description("Path to the .csproj")] string csprojPath,
        [Description("Optional case-insensitive substring filter on the type name")] string filter = "")
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(csprojPath);
            IReadOnlyList<(INamedTypeSymbol Symbol, IReadOnlyList<(string FilePath, int Line)> Locations)> types =
                SymbolLocator.ListTypes(project.Compilation, filter);

            StringBuilder builder = new();
            builder.AppendLine($"# {project.Info.ProjectPath}");
            AppendModeNotice(builder, project);
            builder.AppendLine($"- TFM: `{project.Info.TargetFramework}` | source files: {project.Info.SourceFiles.Count} | types: {types.Count}");
            builder.AppendLine();
            foreach ((INamedTypeSymbol symbol, IReadOnlyList<(string FilePath, int Line)> locations) in types)
            {
                // 分部类：一行里列出全部声明位置
                string where = string.Join(
                    ", ",
                    locations.Select(item => $"{Relative(project.Info.ProjectDirectory, item.FilePath)}:{item.Line}"));
                builder.AppendLine($"- `{SymbolLocator.DisplayName(symbol)}` ({symbol.TypeKind.ToString().ToLowerInvariant()}) — {where}");
            }

            return builder.ToString();
        });
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the members of a type. typePath = fully qualified type name, e.g. 'My.Namespace.MyType'.")]
    public static string ListMembers(
        [Description("Path to the .csproj")] string csprojPath,
        [Description("Fully qualified type name")] string typePath)
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(csprojPath);
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

            foreach (IReadOnlyList<ISymbol> group in MemberListRendering.GroupByName(members, member => member))
            {
                // 3.7：同名重载超过 10 个 → 分组显示（不重复方法名）
                if (MemberListRendering.IsOverloadedGroup(group, member => member))
                {
                    builder.AppendLine($"- `{SymbolLocator.DisplayName(type)}.{group[0].Name}` 有 {group.Count} 个重载：");
                    foreach (ISymbol member in group)
                    {
                        builder.AppendLine($"  - `{MemberListRendering.ParameterList(member)}`{Location(project, member)}");
                    }

                    continue;
                }

                foreach (ISymbol member in group)
                {
                    builder.AppendLine($"- `{member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}`{Location(project, member)}");
                }
            }

            return builder.ToString();
        });
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Read one member (or a whole type when memberSpec is omitted): signature, file, line range and source code — without needing the file path. " +
        "When the symbol has a pending draft, also prints its draft view (snapshot / on-disk / drafted); " +
        "on a conflict it returns an in-memory selectCookie for select_draft (each call rotates that cookie; nothing is written).")]
    public static string GetMember(
        [Description("Path to the .csproj")] string csprojPath,
        [Description("'Ns.Type' for the type itself, or 'Ns.Type.Member' / 'Ns.Type.Method(int,string)' for a member")] string memberPath)
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(csprojPath);
            ResolvedPath resolved = SymbolLocator.Resolve(project.Compilation, memberPath);
            string body;
            if (string.IsNullOrWhiteSpace(resolved.MemberSpec))
            {
                // 给类型：**只给结构**（方法只签名、访问器只给 `{ get { … } }` 记号），
                // 文档注释按 3.7（前 10 / 多名字前 5）与 3.8（行数档位）的预算显示
                List<ISymbol> members =
                [
                    .. resolved.Type.GetMembers()
                        .Where(member => !member.IsImplicitlyDeclared && !IsAccessor(member)),
                ];
                (int documentedCount, int lineLimit) = CodeEditor.DocumentationBudget(members.Count);
                HashSet<ISymbol> documented = CodeEditor.DocumentedMembers([.. members.Take(documentedCount)]);
                body = CodeEditor.DescribeType(resolved.Type, documented, lineLimit);
            }
            else
            {
                body = CodeEditor.Describe(SymbolLocator.ResolveSingleMember(resolved.Type, resolved.MemberSpec));
            }

            string notice = project.ModeNotice();
            string output = string.IsNullOrEmpty(notice)
                ? body
                : notice + Environment.NewLine + Environment.NewLine + body;
            return output + AppendDraftView(project, csprojPath, resolved);
        });
    }

    /// <summary>
    /// 拟定视图（v3 文档第三节 3）：这个符号有拟定就给出「快照 / 现状 / 拟定」三份内容；
    /// 现状被非工具改动过（冲突）时**只在内存**发一个 selectCookie 给 select_draft 用（本工具仍是只读）。
    /// </summary>
    private static string AppendDraftView(LoadedProject project, string csprojPath, ResolvedPath resolved)
    {
        string projectPath = ProjectViewer.ResolveProjectFile(csprojPath);
        DraftRecord? record = new DraftStore().Find(projectPath);
        if (record == null || record.Edits.Count == 0)
        {
            return "";
        }

        ISymbol symbol = resolved.MemberSpec.Length == 0
            ? resolved.Type
            : SymbolLocator.ResolveSingleMember(resolved.Type, resolved.MemberSpec);
        string symbolKey = SymbolBaseline.Key(symbol);
        DraftEdit? edit = record.Edits.FirstOrDefault(item => item.SymbolKey.Equals(symbolKey, StringComparison.Ordinal));
        if (edit == null)
        {
            return "";
        }

        string current = SymbolBaseline.DeclaredText(symbol);
        string draft = edit.RequestedContent ?? "（删除）";
        bool conflict = edit.SymbolSnapshot.Length > 0
            && !string.Equals(
                SymbolBaseline.HashText(current),
                SymbolBaseline.HashText(edit.SymbolSnapshot),
                StringComparison.Ordinal);

        StringBuilder builder = new();
        builder.AppendLine();
        builder.AppendLine("## 拟定视图");
        builder.AppendLine($"- 符号：{edit.SymbolKey}");
        builder.AppendLine($"- 本次拟定：{(edit.RequestedContent == null ? "删除" : "写入")}");
        if (conflict)
        {
            string cookie = PermitStore.GrantSelect(projectPath, edit.SymbolKey, edit.SymbolSnapshot, draft, current);
            builder.AppendLine("- ⚠ 冲突：这个符号在拟定期间被非工具改动过");
            builder.AppendLine(
                $"- 解决：select_draft(csprojPath, memberPath=\"{edit.SymbolKey}\", selectCookie=\"{cookie}\", choice=...)");
            builder.AppendLine("  - `draft` 用拟定内容 / `snapshot` 用快照内容 / `disk` 用现状内容 / `drop` 取消该符号的拟定");
        }

        AppendCodeBlock(builder, "### 快照（拟定开始时）", edit.SymbolSnapshot);
        AppendCodeBlock(builder, "### 现状（磁盘上，忽略拟定）", current);
        AppendCodeBlock(builder, "### 拟定（要写进磁盘的）", draft);
        return builder.ToString();
    }

    private static void AppendCodeBlock(StringBuilder builder, string title, string text)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine(text.Length == 0 ? "（不存在）" : text.TrimEnd());
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Replace an existing member declaration with new C# code. Returns the changed file and line range.")]
    public static string UpdateMember(
        [Description("Path to the .csproj")] string csprojPath,
        [Description("'Ns.Type.Member' or 'Ns.Type.Method(int,string)'")] string memberPath,
        [Description("New member declaration, e.g. 'public int Add(int a, int b) { return a + b; }'")] string code,
        [Description("Run Roslyn formatter on the file afterwards (default true)")] bool format = true)
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(csprojPath);
            ResolvedPath resolved = SymbolLocator.Resolve(project.Compilation, memberPath);
            ISymbol symbol = SymbolLocator.ResolveSingleMember(resolved.Type, resolved.MemberSpec);
            EditResult result = CodeEditor.ReplaceMember(symbol, code, format);
            return DescribeEdit(project, result, symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Add a new member to a type. before = name of an existing member to insert before (empty = append at the end). Returns the changed file and line range.")]
    public static string AddMember(
        [Description("Path to the .csproj")] string csprojPath,
        [Description("Fully qualified type name")] string typePath,
        [Description("Member declaration to add")] string code,
        [Description("Existing member name to insert before (optional)")] string before = "",
        [Description("Run Roslyn formatter on the file afterwards (default true)")] bool format = true)
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(csprojPath);
            INamedTypeSymbol type = SymbolLocator.Resolve(project.Compilation, typePath).Type;
            EditResult result = CodeEditor.AddMember(type, code, before, format);
            return DescribeEdit(project, result, "new member in " + SymbolLocator.DisplayName(type));
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Remove a member from its type. Returns the changed file and line range.")]
    public static string RemoveMember(
        [Description("Path to the .csproj")] string csprojPath,
        [Description("'Ns.Type.Member' or 'Ns.Type.Method(int,string)'")] string memberPath,
        [Description("Run Roslyn formatter on the file afterwards (default true)")] bool format = true)
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(csprojPath);
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
        // 类型（尤其是分部类）列出**全部**声明位置；成员一般只有一处
        List<string> places = [];
        foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
        {
            SyntaxNode node = reference.GetSyntax();
            FileLinePositionSpan span = node.GetLocation().GetLineSpan();
            places.Add($"{Relative(project.Info.ProjectDirectory, span.Path)}:{span.StartLinePosition.Line + 1}");
        }

        return places.Count == 0 ? "" : " — " + string.Join(", ", places);
    }

    private static string Relative(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }
}
