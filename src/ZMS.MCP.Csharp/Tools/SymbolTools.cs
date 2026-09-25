using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using ZMS.MCP.Csharp.Draft;
using ZMS.MCP.Csharp.Project;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Tools;

/// <summary>
/// 符号层工具：**一个工具，按参数区分「列符号 / 读符号」**（原来的 list_types / list_members / list_symbols / get_member 合并到这里）。
/// <list type="bullet">
/// <item><c>path</c> 为空 → 列项目源码里的符号（type / modifier / argsList / nameFilter 过滤）；</item>
/// <item><c>path</c> = 类型 → <c>read=false</c> 列它的成员（带文件与行号）、<c>read=true</c> 读它的结构（成员不带实现）；</item>
/// <item><c>path</c> = 成员（<c>Ns.Type.Member(参数类型,...)</c>）→ 读它的签名、位置与源码；末尾追加 <c>.get</c>/<c>.set</c> 可精确匹配一个访问器。</item>
/// </list>
/// memberPath 语法：<c>命名空间.类型[.成员[(参数类型,...)]]</c>
/// </summary>
[McpServerToolType]
public static class SymbolTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "One symbol tool with two modes: list symbols, or read a symbol. " +
        "path empty = list the symbols declared in the project's own source (referenced assemblies are excluded); " +
        "type = letters from NCSITPFEMD (N namespace, C class, S struct, I interface, T the three type kinds, P property, F field, E event, M method, D only symbols carrying an XML doc comment); empty = all; " +
        "modifier = comma separated public / internal / protected / private / static / const / abstract / readonly / virtual / override, every given condition must match; " +
        "argsList = comma separated parameter types, when set only methods with exactly those parameter types are listed; " +
        "nameFilter = case-insensitive substring filter on the fully qualified name. " +
        "path = 'Ns.Type': read=false lists that type's members with file and line, read=true shows the type structure (member bodies stay hidden); " +
        "path = 'Ns.Type.Member' or 'Ns.Type.Method(int,string)' ignores read and shows that member's signature, file, line range and source; " +
        "append '.get' / '.set' / '.add' / '.remove' to that member path to match one accessor exactly (then its implementation is shown). " +
        "When the symbol has a pending draft the output also prints its draft view, and on an unresolved conflict it returns an in-memory selectCookie for select_draft (each call rotates that cookie; nothing is written).")]
    public static string Symbols(
        [Description("Path to the .csproj")] string csprojPath,
        [Description("Empty = list project symbols; 'Ns.Type' = that type; 'Ns.Type.Member(...)' = that member")] string path = "",
        [Description("true = read structure/source instead of listing (only used when path is a type)")] bool read = false,
        [Description("Type letters, e.g. 'C' or 'NCSITPFEMD'. Empty = all")] string type = "",
        [Description("Modifier filter, e.g. 'public,static'. Empty = no filter")] string modifier = "",
        [Description("Method parameter types, e.g. 'string,int'. Empty = no filter")] string argsList = "",
        [Description("Case-insensitive substring filter on the fully qualified name. Empty = no filter")] string nameFilter = "")
    {
        return ToolGuard.Run(() =>
        {
            LoadedProject project = LoadedProject.Load(csprojPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                return ListProjectSymbols(project, type, modifier, argsList, nameFilter);
            }

            return ReadOrListPath(project, csprojPath, path, read);
        });
    }

    // ───────── 列：整个项目 ─────────

    private static string ListProjectSymbols(
        LoadedProject project,
        string type,
        string modifier,
        string argsList,
        string nameFilter)
    {
        SymbolKinds kinds = SymbolFilterParser.ParseKinds(type);
        SymbolModifiers modifierFilter = SymbolFilterParser.ParseModifiers(
            modifier.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        IReadOnlyList<string> parameters = SymbolFilterParser.ParseArgumentTypes(argsList);
        IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(project.Compilation, kinds, modifierFilter, parameters);
        IReadOnlyList<char> unknownKindLetters = SymbolFilterParser.UnknownKindLetters(type);

        string filter = (nameFilter ?? "").Trim();
        if (filter.Length > 0)
        {
            // 按完全限定名过滤（原来 list_types 的 filter 就是这个能力）
            entries = [.. entries.Where(entry => SymbolBaseline.Key(entry.Symbol).Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        StringBuilder builder = new();
        builder.AppendLine($"# {project.Info.ProjectPath}");
        AppendModeNotice(builder, project);
        builder.AppendLine(
            $"- TFM: `{project.Info.TargetFramework}` | 符号: {entries.Count}" +
            $" | 过滤: type='{type}' modifier='{modifier}' args='{argsList}' nameFilter='{filter}'");
        if (unknownKindLetters.Count > 0)
        {
            // 别静默吞掉拼错的字母 —— 否则"筛出来是空的"看起来就像"项目里没符号"
            builder.AppendLine($"⚠ 无法识别的 type 字母已忽略：{string.Join(", ", unknownKindLetters)}（可用：N C S I T P F E M D）");
        }

        if (entries.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("_(无匹配符号)_");
            if (kinds.HasFlag(SymbolKinds.Document))
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

    // ───────── 列 / 读：一个类型或一个成员 ─────────

    private static string ReadOrListPath(LoadedProject project, string csprojPath, string path, bool read)
    {
        ResolvedPath resolved = SymbolLocator.Resolve(project.Compilation, path);
        if (resolved.MemberSpec.Length > 0)
        {
            // path 指到成员（含访问器）→ 读它（read 参数在这里没有意义）
            ISymbol symbol = SymbolLocator.ResolveSingleMember(resolved.Type, resolved.MemberSpec);
            return ReadSymbol(project, csprojPath, symbol);
        }

        return read
            ? ReadType(project, csprojPath, resolved.Type)
            : ListTypeMembers(project, resolved.Type);
    }

    /// <summary>列一个类型的成员（完全限定名 + 位置），对应原来的 list_members。</summary>
    private static string ListTypeMembers(LoadedProject project, INamedTypeSymbol type)
    {
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
    }

    /// <summary>读一个类型的**结构**（方法只签名、属性/索引器的访问器只给记号、字段带初始化器）。</summary>
    private static string ReadType(LoadedProject project, string csprojPath, INamedTypeSymbol type)
    {
        List<ISymbol> members =
        [
            .. type.GetMembers()
                .Where(member => !member.IsImplicitlyDeclared && !IsAccessor(member)),
        ];
        (int documentedCount, int lineLimit) = CodeEditor.DocumentationBudget(members.Count);
        HashSet<ISymbol> documented = CodeEditor.DocumentedMembers([.. members.Take(documentedCount)]);
        string body = CodeEditor.DescribeType(type, documented, lineLimit);
        return AppendModeNoticeLine(project, body) + AppendDraftView(project, csprojPath, type);
    }

    /// <summary>读一个符号：签名、位置与源码（不带文件路径定位）。</summary>
    private static string ReadSymbol(LoadedProject project, string csprojPath, ISymbol symbol)
    {
        return AppendModeNoticeLine(project, CodeEditor.Describe(symbol)) + AppendDraftView(project, csprojPath, symbol);
    }

    /// <summary>
    /// 拟定 / 冲突视图：这个符号有拟定就给出「首次编辑时的内容 / 现状 / 拟定」；
    /// 与追踪基线不一致（未解决冲突）时**只在内存**发一个 selectCookie 给 select_draft 用（本工具仍是只读）。
    /// </summary>
    private static string AppendDraftView(LoadedProject project, string csprojPath, ISymbol symbol)
    {
        string projectPath = ProjectViewer.ResolveProjectFile(csprojPath);
        DraftStore store = new();
        DraftRecord? record = store.Find(projectPath);
        TrackingRecord? tracking = store.GetTracking(projectPath);

        string symbolKey = SymbolBaseline.Key(symbol);
        DraftEdit? edit = record?.Edits.FirstOrDefault(item => item.SymbolKey.Equals(symbolKey, StringComparison.Ordinal));
        bool conflict = tracking != null
            && ConflictService.IsUnresolved(tracking.Baseline, SymbolBaseline.Capture(project.Compilation), symbolKey);
        if (edit == null && !conflict)
        {
            return "";
        }

        StringBuilder builder = new();
        builder.AppendLine();
        builder.AppendLine("## 拟定 / 冲突视图");
        builder.AppendLine($"- 符号：{symbolKey}");
        if (edit != null)
        {
            builder.AppendLine($"- 本次拟定：{(edit.RequestedContent == null ? "删除" : "写入")}");
        }
        else
        {
            builder.AppendLine("- 本次拟定：（没有 —— 这个符号只是与追踪基线不一致）");
        }

        if (conflict)
        {
            string cookie = PermitStore.GrantSelect(projectPath, symbolKey);
            builder.AppendLine("- ⚠ 未解决的冲突：这个符号的 hash 与追踪时保存的不一致（追踪之后被外部改动 / 新增 / 删除）");
            builder.AppendLine(
                $"- 解决：select_draft(csprojPath=\"{projectPath}\", memberPath=\"{symbolKey}\", selectCookie=\"{cookie}\", choice=...)");
            builder.AppendLine("  - `keep` 保持拟定（坚持本次写法） / `drop` 移除拟定（放弃本次写法、接受现状）");
            builder.AppendLine("  - 也可以重新 stage_draft 重新拟定；两种做法都会把这个符号的追踪 hash 更新为现在的 hash");
        }

        if (edit != null)
        {
            AppendCodeBlock(builder, "### 首次编辑时的内容", edit.SymbolSnapshot);
            AppendCodeBlock(builder, "### 现状（磁盘上，忽略拟定）", SymbolBaseline.DeclaredText(symbol));
            AppendCodeBlock(builder, "### 拟定（要写进磁盘的）", edit.RequestedContent ?? "（删除）");
        }

        return builder.ToString();
    }

    private static void AppendCodeBlock(StringBuilder builder, string title, string text)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine(text.Length == 0 ? "（不存在）" : text.TrimEnd());
    }

    /// <summary>降级加载时把"简化模式"提示插到输出前面（评估模式不插）。</summary>
    private static string AppendModeNoticeLine(LoadedProject project, string body)
    {
        string notice = project.ModeNotice();
        return string.IsNullOrEmpty(notice)
            ? body
            : notice + Environment.NewLine + Environment.NewLine + body;
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
