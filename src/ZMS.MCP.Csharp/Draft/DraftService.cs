using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ZMS.MCP.Csharp.Project;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Draft;

/// <summary>一条诊断的配对键：**错误码 + 消息 + 文件**（行号不参与配对，改动会挪行号）。</summary>
public sealed record DiagnosticKey(string Code, string Message, string File, int Line)
{
    public string PairingKey => $"{Code}\u0001{Message}\u0001{File}";

    public override string ToString()
    {
        return $"{Code} {Message}（{File}:{Line}）";
    }
}

/// <summary>一次诊断快照。</summary>
public sealed record DiagnosticSnapshot(IReadOnlyList<DiagnosticKey> Errors, IReadOnlyList<DiagnosticKey> Warnings)
{
    public static DiagnosticSnapshot Empty { get; } = new([], []);
}

/// <summary>
/// 「拟定新增或修改或删除符号」与「拟定修改确认」。
///
/// 拟定可以累积：多次调用依次叠加在同一个拟定上；
/// 拟定状态存 sqlite（放在 MCP 自己的目录）；MCP 重启从库里恢复，agent 重启靠「列出拟定」拿 cookit。
/// </summary>
public static class DraftService
{
    /// <summary>拟定：先做语法检查，通过才记进拟定；<paramref name="content"/> 为 null 表示删除成员。</summary>
    public static string Stage(string csprojPath, string typePath, string memberName, string? content)
    {
        string projectPath = ProjectViewer.ResolveProjectFile(csprojPath);
        DraftStore store = new();
        DraftRecord record = store.GetOrCreate(projectPath);
        LoadedProject project = LoadedProject.Load(projectPath);

        // 把拟定里已经算好的内容重放进编译对象，这样「累积」时第二次拟定看到的是第一次的结果
        CSharpCompilation compilation = Replay(project.Compilation, record.Edits);
        List<CodeChange> changes = BuildChanges(project, compilation, typePath, memberName, content, out string description);

        StringBuilder builder = new();
        builder.AppendLine("# 拟定已累加");
        builder.AppendLine($"- 项目：{projectPath}");
        builder.AppendLine($"- cookit：`{record.Cookit}`");
        builder.AppendLine($"- 本次：{description}");
        if (project.ModeNotice().Length > 0)
        {
            builder.AppendLine($"- {project.ModeNotice()}");
        }

        builder.AppendLine();
        builder.AppendLine("- 本次涉及的文件：");
        foreach (CodeChange change in changes)
        {
            string baseline = BaselineHash(record, change.FilePath);
            store.Append(projectPath, typePath, memberName, content, change.FilePath, baseline, change.NewContent, change.Action);
            builder.AppendLine($"  - {change.FilePath}（{change.Action}）");
        }

        return builder.ToString();
    }

    /// <summary>列出这个项目当前的拟定（agent 重启后靠它重新拿到 cookit 与进度）。</summary>
    public static string List(string csprojPath)
    {
        string projectPath = ProjectViewer.ResolveProjectFile(csprojPath);
        DraftStore store = new();
        DraftRecord? record = store.Find(projectPath);
        if (record == null)
        {
            return $"# 拟定\n{projectPath}\n没有未完成的拟定。";
        }

        StringBuilder builder = new();
        builder.AppendLine("# 拟定");
        builder.AppendLine($"- 项目：{projectPath}");
        builder.AppendLine($"- cookit：`{record.Cookit}`");
        builder.AppendLine($"- 起始时间：{record.CreatedAt}");
        builder.AppendLine($"- 条数：{record.Edits.Count}");
        builder.AppendLine();
        foreach (DraftEdit edit in record.Edits)
        {
            string action = edit.IsDelete ? "删除" : "写入";
            builder.AppendLine($"- [{edit.Sequence}] {action} {edit.TypePath}.{edit.MemberName} → {edit.FilePath}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 确认拟定：列出变更分类与诊断对比，并给出 cookit。
    /// 带了 cookit 并且与拟定一致时**落盘**；否则只预演。
    /// </summary>
    public static string Confirm(string csprojPath, string cookit, bool apply)
    {
        string projectPath = ProjectViewer.ResolveProjectFile(csprojPath);
        DraftStore store = new();
        DraftRecord? record = store.Find(projectPath);
        if (record == null || record.Edits.Count == 0)
        {
            return $"# 拟定确认\n{projectPath}\n没有未完成的拟定。";
        }

        bool cookitGiven = !string.IsNullOrWhiteSpace(cookit);
        if (cookitGiven && !cookit.Trim().Equals(record.Cookit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"cookit 不匹配（拟定里是 {record.Cookit}）—— 拒绝落盘，避免把别人的改动当成自己的。");
        }

        LoadedProject before = LoadedProject.Load(projectPath);
        DiagnosticSnapshot beforeDiagnostics = Analyze(before.Compilation);
        DiagnosticSnapshot afterDiagnostics = Analyze(Replay(before.Compilation, record.Edits));

        StringBuilder builder = new();
        builder.AppendLine("# 拟定确认");
        builder.AppendLine($"- 项目：{projectPath}");
        builder.AppendLine($"- cookit：`{record.Cookit}`");
        builder.AppendLine();
        builder.AppendLine("## 变更分类");
        foreach (IGrouping<string, DraftEdit> group in record.Edits.GroupBy(edit => edit.IsDelete ? "删除" : "写入"))
        {
            builder.AppendLine($"### {group.Key}（{group.Count()}）");
            foreach (DraftEdit edit in group)
            {
                builder.AppendLine($"- {edit.TypePath}.{edit.MemberName} → {edit.FilePath}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 诊断对比（按 错误码 + 消息 + 文件 配对，行号只用于展示）");
        AppendDiagnostics(builder, "新增", afterDiagnostics, beforeDiagnostics);
        AppendDiagnostics(builder, "消失", beforeDiagnostics, afterDiagnostics);

        if (!apply || !cookitGiven)
        {
            builder.AppendLine();
            builder.AppendLine("（预演，未落盘。带准确的 cookit 再调用一次即落盘。）");
            return builder.ToString();
        }

        // 落盘前校验基线 hash：拟定期间被外部改动过的文件 → 报冲突、拒绝落盘，不覆盖别人的改动
        List<string> targets = [.. record.Edits.Select(edit => edit.FilePath).Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> conflicts = [];
        foreach (string file in targets)
        {
            DraftEdit first = record.Edits.First(edit => edit.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(FileWriter.ComputeHash(file), first.BaselineHash, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(file);
            }
        }

        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                "拟定期间这些文件被外部改动过，拒绝覆盖：\n" + string.Join("\n", conflicts.Select(file => "  - " + file)));
        }

        List<KeyValuePair<string, string>> files = [];
        foreach (string file in targets)
        {
            DraftEdit last = record.Edits.Last(edit => edit.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
            files.Add(new KeyValuePair<string, string>(file, last.ResultContent));
        }

        // 多文件落盘不是真原子：先记写前日志，**写完之后**才清（中途崩溃日志还在，下次可以前滚补齐）
        store.RecordJournal(record.Cookit, files);
        foreach (KeyValuePair<string, string> file in files)
        {
            FileWriter.WriteAtomic(file.Key, file.Value);
        }

        store.ClearJournal(record.Cookit);
        // 拟定到这里就算完成了。后面的 format 只是锦上添花：
        // 它失败也不能把拟定留在「磁盘已改、拟定还在」的卡死状态（重试会被基线校验拦下来）。
        store.Clear(projectPath);
        string formatLog = RunFormat(projectPath, targets);

        builder.AppendLine();
        builder.AppendLine("## 已落盘");
        foreach (string file in targets)
        {
            builder.AppendLine($"- {file}");
        }

        builder.AppendLine("- 编码：按各文件原编码写回（新建文件 UTF-8 无 BOM）");
        builder.AppendLine($"- 格式化：{formatLog}");
        builder.AppendLine("- 未做任何 git 操作（提交/分支/贮藏都不动）");
        return builder.ToString();
    }

    /// <summary>把拟定里算好的内容重放进编译对象（每个文件只取最后一条结果）。</summary>
    internal static CSharpCompilation Replay(CSharpCompilation compilation, IReadOnlyList<DraftEdit> edits)
    {
        CSharpCompilation result = compilation;
        foreach (string file in edits.Select(edit => edit.FilePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DraftEdit last = edits.Last(edit => edit.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
            SyntaxTree? existing = result.SyntaxTrees.FirstOrDefault(tree => tree.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
            CSharpParseOptions options = existing is CSharpSyntaxTree csharp
                ? csharp.Options
                : new CSharpParseOptions(LanguageVersion.Preview);
            SyntaxTree replacement = CSharpSyntaxTree.ParseText(last.ResultContent, options, path: file);
            result = existing == null
                ? result.AddSyntaxTrees(replacement)
                : result.ReplaceSyntaxTree(existing, replacement);
        }

        return result;
    }

    /// <summary>把一条拟定算成若干次文件改动（改成员 / 删成员 / 新增成员 / 新建类型都可能牵扯多个文件）。</summary>
    internal static List<CodeChange> BuildChanges(
        LoadedProject project,
        CSharpCompilation compilation,
        string typePath,
        string memberName,
        string? content,
        out string description)
    {
        INamedTypeSymbol? type = SymbolLocator.FindType(compilation, typePath);
        if (type == null)
        {
            description = $"新建类型 {typePath}";
            return [CreateNewType(project.Info, compilation, typePath, content)];
        }

        string member = (memberName ?? "").Trim();
        IReadOnlyList<ISymbol> members = member.Length == 0 ? [] : SymbolLocator.FindMembers(type, member);
        if (members.Count > 1)
        {
            string overloads = string.Join(
                "\n",
                members.Select(item => "  - " + item.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
            throw new InvalidOperationException($"'{member}' 匹配到 {members.Count} 个重载，请带上参数列表消歧：\n{overloads}");
        }

        List<CodeChange> changes = [];
        if (members.Count == 1)
        {
            ISymbol symbol = members[0];
            if (content == null)
            {
                description = $"删除成员 {typePath}.{member}";
                changes.Add(CodeEditor.ComputeRemove(symbol, format: true));
            }
            else
            {
                description = $"修改成员 {typePath}.{member}";
                changes.Add(CodeEditor.ComputeReplace(symbol, content, format: true));
            }

            return changes;
        }

        if (content == null)
        {
            throw new InvalidOperationException($"要删除的成员不存在：{typePath}.{member}");
        }

        description = $"在 {typePath} 新增成员 {member}";
        changes.AddRange(EnsurePartial(type));
        changes.Add(CodeEditor.ComputeAdd(type, content, before: "", format: true));
        return changes;
    }

    /// <summary>新增成员要求**所有**分部声明都带 <c>partial</c>；缺的补上（每个文件一条改动）。</summary>
    internal static List<CodeChange> EnsurePartial(INamedTypeSymbol type)
    {
        List<CodeChange> changes = [];
        Dictionary<string, List<SyntaxNode>> perFile = new(StringComparer.OrdinalIgnoreCase);
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            SyntaxNode node = reference.GetSyntax();
            string file = node.SyntaxTree.FilePath;
            if (!perFile.TryGetValue(file, out List<SyntaxNode>? nodes))
            {
                nodes = [];
                perFile[file] = nodes;
            }

            nodes.Add(node);
        }

        foreach (KeyValuePair<string, List<SyntaxNode>> pair in perFile)
        {
            SyntaxNode updated = pair.Value[0].SyntaxTree.GetRoot();
            bool touched = false;
            foreach (SyntaxNode node in pair.Value)
            {
                if (node is not TypeDeclarationSyntax declaration || HasPartial(declaration))
                {
                    continue;
                }

                updated = updated.ReplaceNode(node, AddPartial(declaration));
                touched = true;
            }

            if (touched)
            {
                changes.Add(new CodeChange(pair.Key, updated.ToFullString(), 0, 0, "补 partial"));
            }
        }

        return changes;
    }

    /// <summary>新建类型：外部类按命名空间 + 根命名空间定路径；内部类用 <c>Outer.Inner.cs</c>。</summary>
    internal static CodeChange CreateNewType(
        ProjectFileInfo info,
        CSharpCompilation compilation,
        string typePath,
        string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"类型不存在，必须在同一次拟定里给出 content 才能新建：{typePath}");
        }

        string[] segments = typePath.Split('.');
        string typeName = segments[^1];

        for (int index = segments.Length - 1; index > 0; index--)
        {
            string candidate = string.Join('.', segments[..index]);
            INamedTypeSymbol? outer = SymbolLocator.FindType(compilation, candidate);
            if (outer?.DeclaringSyntaxReferences.FirstOrDefault() is { } reference)
            {
                string outerFile = reference.SyntaxTree.FilePath;
                string nestedDirectory = Path.GetDirectoryName(outerFile) ?? info.ProjectDirectory;
                string nestedPath = Path.Combine(nestedDirectory, typeName + ".cs");
                return new CodeChange(nestedPath, BuildNestedTypeSource(outer, typeName, content), 0, 0, "新建内部类");
            }
        }

        string rootNamespace = ReadRootNamespace(info.ProjectPath);
        string namespaceName = string.Join('.', segments[..^1]);
        string relative = namespaceName;
        if (rootNamespace.Length > 0 && namespaceName.StartsWith(rootNamespace, StringComparison.Ordinal))
        {
            relative = namespaceName[rootNamespace.Length..].Trim('.');
        }

        string targetDirectory = relative.Length == 0
            ? info.ProjectDirectory
            : Path.Combine(info.ProjectDirectory, relative.Replace('.', Path.DirectorySeparatorChar));

        return new CodeChange(
            Path.Combine(targetDirectory, typeName + ".cs"),
            BuildTypeSource(namespaceName, typeName, content),
            0,
            0,
            "新建类型");
    }

    private static string BuildTypeSource(string namespaceName, string typeName, string content)
    {
        StringBuilder builder = new();
        if (namespaceName.Length > 0)
        {
            builder.AppendLine($"namespace {namespaceName};");
            builder.AppendLine();
        }

        builder.AppendLine($"public class {typeName}");
        builder.AppendLine("{");
        builder.AppendLine(Indent(content, "    "));
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// 内部类：新文件里从最外层到宿主类都写一遍（都带 <c>partial</c>），
    /// 这样「外部类用 partial 定义」的规则在新文件里也成立；修饰符照抄原声明（访问级别必须一致）。
    /// </summary>
    private static string BuildNestedTypeSource(INamedTypeSymbol outer, string typeName, string content)
    {
        List<INamedTypeSymbol> chain = [];
        for (INamedTypeSymbol? current = outer; current != null; current = current.ContainingType)
        {
            chain.Insert(0, current);
        }

        string namespaceName = chain[0].ContainingNamespace is { IsGlobalNamespace: false } container
            ? container.ToDisplayString()
            : "";

        StringBuilder builder = new();
        if (namespaceName.Length > 0)
        {
            builder.AppendLine($"namespace {namespaceName};");
            builder.AppendLine();
        }

        string indent = "";
        foreach (INamedTypeSymbol link in chain)
        {
            string modifiers = ModifiersOf(link);
            string head = modifiers.Length == 0 ? "partial" : modifiers + " partial";
            builder.AppendLine($"{indent}{head} class {link.Name}");
            builder.AppendLine($"{indent}{{");
            indent += "    ";
        }

        builder.AppendLine($"{indent}public class {typeName}");
        builder.AppendLine($"{indent}{{");
        builder.AppendLine(Indent(content, indent + "    "));
        builder.AppendLine($"{indent}}}");

        for (int index = chain.Count - 1; index >= 0; index--)
        {
            indent = indent[..^4];
            builder.AppendLine($"{indent}}}");
        }

        return builder.ToString();
    }

    /// <summary>照抄原声明的修饰符（同一类型的分部声明访问级别必须一致）。</summary>
    private static string ModifiersOf(INamedTypeSymbol type)
    {
        if (type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not TypeDeclarationSyntax declaration)
        {
            return "public";
        }

        return string.Join(' ', declaration.Modifiers.Select(token => token.Text));
    }

    private static string Indent(string text, string prefix)
    {
        return string.Join(
            Environment.NewLine,
            text.Replace("\r\n", "\n").Split('\n').Select(line => line.Length == 0 ? line : prefix + line));
    }

    private static bool HasPartial(TypeDeclarationSyntax declaration)
    {
        return declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));
    }

    /// <summary>在访问修饰符之后插入 <c>partial</c>（写成 <c>public partial class</c>，不是 <c>public class partial</c>）。</summary>
    private static TypeDeclarationSyntax AddPartial(TypeDeclarationSyntax declaration)
    {
        List<SyntaxToken> modifiers = [.. declaration.Modifiers];
        int index = 0;
        while (index < modifiers.Count && IsAccessModifier(modifiers[index]))
        {
            index++;
        }

        // 关键字要在访问修饰符之后、class 之前；带上尾随空格，避免拼出 "partialclass"
        SyntaxToken partial = SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        modifiers.Insert(index, partial);
        return declaration.WithModifiers(SyntaxFactory.TokenList(modifiers));
    }

    private static bool IsAccessModifier(SyntaxToken token)
    {
        return token.IsKind(SyntaxKind.PublicKeyword)
            || token.IsKind(SyntaxKind.PrivateKeyword)
            || token.IsKind(SyntaxKind.ProtectedKeyword)
            || token.IsKind(SyntaxKind.InternalKeyword);
    }

    private static string BaselineHash(DraftRecord record, string filePath)
    {
        DraftEdit? existing = record.Edits.FirstOrDefault(edit => edit.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        return existing != null ? existing.BaselineHash : FileWriter.ComputeHash(filePath);
    }

    internal static DiagnosticSnapshot Analyze(Compilation compilation)
    {
        List<DiagnosticKey> errors = [];
        List<DiagnosticKey> warnings = [];
        foreach (Diagnostic diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            {
                continue;
            }

            FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
            DiagnosticKey key = new(
                diagnostic.Id,
                diagnostic.GetMessage(),
                span.Path,
                span.StartLinePosition.Line + 1);
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                errors.Add(key);
            }
            else
            {
                warnings.Add(key);
            }
        }

        return new DiagnosticSnapshot(errors, warnings);
    }

    private static void AppendDiagnostics(
        StringBuilder builder,
        string label,
        DiagnosticSnapshot current,
        DiagnosticSnapshot previous)
    {
        HashSet<string> existing =
        [
            .. previous.Errors.Select(item => item.PairingKey),
            .. previous.Warnings.Select(item => item.PairingKey),
        ];
        List<DiagnosticKey> added =
        [
            .. current.Errors.Where(item => !existing.Contains(item.PairingKey)),
            .. current.Warnings.Where(item => !existing.Contains(item.PairingKey)),
        ];
        if (added.Count == 0)
        {
            builder.AppendLine($"- {label}：无");
            return;
        }

        builder.AppendLine($"- {label}：");
        foreach (DiagnosticKey item in added.Take(50))
        {
            builder.AppendLine($"  - {item}");
        }

        if (added.Count > 50)
        {
            builder.AppendLine($"  - …（还有 {added.Count - 50} 条）");
        }
    }

    private static string RunFormat(string projectPath, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return "（没有文件需要格式化）";
        }

        List<string> arguments = ["format", projectPath, "--no-restore", "--include"];
        arguments.AddRange(files);
        try
        {
            CommandResult result = CommandRunner.Run("dotnet", arguments, Path.GetDirectoryName(projectPath) ?? ".", 300);
            return result.Succeeded
                ? "已对本次改动的文件跑 dotnet format"
                : $"dotnet format 未成功（退出码 {result.ExitCode}）：{FirstLine(result.Output)}";
        }
        catch (Exception exception) when (exception is TimeoutException or System.ComponentModel.Win32Exception)
        {
            return $"dotnet format 未执行：{exception.Message}";
        }
    }

    private static string FirstLine(string text)
    {
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
    }

    private static string ReadRootNamespace(string projectPath)
    {
        try
        {
            XElement? root = XDocument.Load(projectPath).Root;
            return root == null ? "" : ProjectFileInfo.ReadProperty(root, "RootNamespace");
        }
        catch (System.Xml.XmlException)
        {
            return "";
        }
    }
}
