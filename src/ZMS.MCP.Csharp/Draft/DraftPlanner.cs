using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Draft;

/// <summary>
/// 落盘现场：把"符号 + 意图"的拟定**当场**算成整文件新文本（拟定本身不存文件路径、不存整文件内容）。
/// 见 `docs/Csharp-拟定流程v3.md` 第八节 —— 同一文件里的多个符号按序逐条应用，
/// 非拟定区域一律取自**当前磁盘内容**。
/// </summary>
public static class DraftPlanner
{
    /// <summary>
    /// 一个文件算出来的结果（Actions 用来区分"新建文件"与"改既有文件"）。
    /// `RenameSites` 只对改名有值：**改名要把"会动哪几处"摆出来**（文件里的 行:列 + 是声明还是引用），
    /// 好让调用方一眼确认这次改的确实是他想改的那个符号 —— 这是语义改名对文本替换的唯一优势。
    /// </summary>
    public sealed record PlannedFile(
        string FilePath,
        string NewContent,
        IReadOnlyList<string> SymbolKeys,
        IReadOnlyList<string> Actions,
        IReadOnlyList<string> RenameSites);

    /// <summary>一次现场计算的结论：要写哪些文件、哪些符号已经定位不到、以及应用这些拟定之后的编译树。</summary>
    public sealed record Plan(
        IReadOnlyList<PlannedFile> Files,
        IReadOnlyList<string> Missing,
        CSharpCompilation Projected);


    /// <summary>按拟定逐条现场计算（每条都基于"前一条的结果"，所以同文件多符号能正确合成）。</summary>
    public static Plan Compute(LoadedProject project, IReadOnlyList<DraftEdit> edits)
    {
        CSharpCompilation compilation = project.Compilation;
        Dictionary<string, PlannedFileBuilder> files = new(PathComparison.Comparer);
        List<string> missing = [];

        // **定向**判断"原本存在的符号现在还找得到吗"：不建全量符号基线
        // （那是 O(拟定条数 × 项目符号数)，大项目上会把 confirm_draft 拖慢）
        static bool CanStillLocate(DraftEdit edit, CSharpCompilation compilation)
        {
            INamedTypeSymbol? type = SymbolLocator.FindType(compilation, edit.TypePath);
            if (type == null)
            {
                return false;
            }

            return edit.MemberName.Length == 0 || SymbolLocator.FindMembers(type, edit.MemberName).Count > 0;
        }

        foreach (DraftEdit edit in edits.OrderBy(item => item.Sequence))
        {
            // 原本就存在的符号必须**现在还找得到**，否则这条拟定作废（窗口期被改名/删除后找不到）
            if (edit.SymbolKey.Length > 0
                && edit.SymbolSnapshot.Length > 0
                && !CanStillLocate(edit, compilation))
            {
                missing.Add(edit.SymbolKey);
                continue;
            }

            List<CodeChange> changes;
            Dictionary<string, List<string>> renameSites = new(PathComparison.Comparer);
            try
            {
                if (edit.Action == DraftService.RenameAction)
                {
                    (changes, renameSites) = BuildRenameChanges(compilation, edit);
                }
                else
                {
                    changes = DraftService.BuildChanges(
                        project,
                        compilation,
                        edit.TypePath,
                        edit.MemberName,
                        edit.RequestedContent,
                        out _);
                }
            }
            catch (InvalidOperationException)
            {
                missing.Add(edit.SymbolKey.Length > 0 ? edit.SymbolKey : DraftService.EditLabel(edit));
                continue;
            }

            foreach (CodeChange change in changes)
            {
                if (!files.TryGetValue(change.FilePath, out PlannedFileBuilder? builder))
                {
                    builder = new PlannedFileBuilder(change.FilePath);
                    files[change.FilePath] = builder;
                }

                builder.NewContent = change.NewContent;

                if (renameSites.TryGetValue(change.FilePath, out List<string>? sites))
                {
                    builder.Sites.AddRange(sites);
                }

                // "补 partial"、"改名的引用点"都是附带改动：符号只挂在它真正要写的那个文件上。
                // 否则"符号 → 文件"成一对多，落盘校验会因遍历顺序不同误报"移动/改名"。
                if (!IsAttachment(change.Action))
                {
                    builder.SymbolKeys.Add(edit.SymbolKey.Length > 0 ? edit.SymbolKey : DraftService.EditLabel(edit));
                }

                if (!builder.Actions.Contains(change.Action, StringComparer.Ordinal))
                {
                    builder.Actions.Add(change.Action);
                }

                compilation = WithContent(compilation, change.FilePath, change.NewContent);
            }
        }

        List<PlannedFile> planned =
        [
            .. files.Values.Select(builder => new PlannedFile(
                builder.FilePath,
                builder.NewContent,
                builder.SymbolKeys,
                builder.Actions,
                builder.Sites)),
        ];
        return new Plan(planned, missing, compilation);
    }

    /// <summary>
    /// 算一条改名的整文件新文本：**声明点 + 本编译里的所有引用点**，按文件分组各重写一次。
    ///
    /// 只在**当前编译**的语法树里改（字符串里的名字、注释、别的项目对它的引用都不管）——
    /// 这些边界在 <see cref="SymbolRenamer"/> 里写清楚了，调用方该自己核对剩下的。
    ///
    /// 声明所在的那个文件标 `renamed`，别处（纯引用点）标 `renamed-ref` —— 后者算"附带改动"，
    /// 不挂符号键：一个符号同时出现在多个文件里是改名的**常态**，不区分就会被落盘校验当成"移动/改名"。
    /// </summary>
    private static (List<CodeChange> Changes, Dictionary<string, List<string>> Sites) BuildRenameChanges(
        CSharpCompilation compilation,
        DraftEdit edit)
    {
        ISymbol? symbol = FindRenameTarget(compilation, edit);
        if (symbol == null)
        {
            throw new InvalidOperationException($"定位不到要改名的符号：{edit.TypePath}.{edit.MemberName}");
        }

        string declaringFile = symbol.DeclaringSyntaxReferences
            .Select(reference => reference.SyntaxTree.FilePath)
            .FirstOrDefault(path => path.Length > 0) ?? "";

        string newName = edit.RequestedContent ?? "";
        List<CodeChange> changes = [];
        Dictionary<string, List<string>> sites = new(PathComparison.Comparer);
        foreach (IGrouping<string, SymbolRenamer.Hit> group in SymbolRenamer.Hits(compilation, symbol)
            .Where(hit => hit.Tree.FilePath.Length > 0)
            .GroupBy(hit => hit.Tree.FilePath, PathComparison.Comparer))
        {
            string filePath = group.Key;
            List<SymbolRenamer.Hit> hits = [.. group];

            SyntaxTree tree = hits[0].Tree;
            SyntaxNode renamed = SymbolRenamer.Rename(tree.GetRoot(), hits.Select(hit => hit.Node), newName);
            string action = filePath.Equals(declaringFile, PathComparison.Comparison) ? "renamed" : "renamed-ref";
            changes.Add(new CodeChange(filePath, renamed.ToFullString(), 0, 0, action));

            List<string> where = [];
            foreach (SymbolRenamer.Hit hit in hits.OrderBy(hit => hit.Node.SpanStart))
            {
                (int line, int column) = hit.Where();
                where.Add($"{line}:{column} {hit.Kind}");
            }

            sites[filePath] = where;
        }

        return (changes, sites);
    }

    /// <summary>
    /// 这次改动是"顺便的"吗（补 partial、改名的引用点）—— 附带改动不挂符号键。
    /// 符号 → 文件必须是一对一，否则落盘校验会因遍历顺序不同误报"移动/改名"。
    /// </summary>
    private static bool IsAttachment(string action)
    {
        return action.Contains("partial", StringComparison.Ordinal)
            || action.EndsWith("-ref", StringComparison.Ordinal);
    }

    /// <summary>按拟定里存的"类型 + 成员"重新定位要改名的符号（找不到/重载歧义都算定位失败）。</summary>
    private static ISymbol? FindRenameTarget(CSharpCompilation compilation, DraftEdit edit)
    {
        INamedTypeSymbol? type = SymbolLocator.FindType(compilation, edit.TypePath);
        if (type == null)
        {
            return null;
        }

        if (edit.MemberName.Length == 0)
        {
            return type;
        }

        IReadOnlyList<ISymbol> members = SymbolLocator.FindMembers(type, edit.MemberName);
        return members.Count == 1 ? members[0] : null;
    }

    /// <summary>把某个文件的内容重放进编译对象（算下一条时要基于它）。</summary>
    private static CSharpCompilation WithContent(CSharpCompilation compilation, string filePath, string content)
    {
        CSharpParseOptions baseline = compilation.SyntaxTrees
            .OfType<CSharpSyntaxTree>()
            .Select(tree => tree.Options)
            .FirstOrDefault()
            ?? new CSharpParseOptions(LanguageVersion.Preview);
        SyntaxTree? existing = compilation.SyntaxTrees
            .FirstOrDefault(tree => tree.FilePath.Equals(filePath, PathComparison.Comparison));
        CSharpParseOptions options = existing is CSharpSyntaxTree csharp
            ? csharp.Options
            : baseline;
        SyntaxTree replacement = CSharpSyntaxTree.ParseText(content, options, path: filePath);
        return existing == null
            ? compilation.AddSyntaxTrees(replacement)
            : compilation.ReplaceSyntaxTree(existing, replacement);
    }

    private sealed class PlannedFileBuilder(string filePath)
    {
        public string FilePath { get; } = filePath;

        public string NewContent { get; set; } = "";

        public List<string> SymbolKeys { get; } = [];

        public List<string> Actions { get; } = [];

        public List<string> Sites { get; } = [];
    }
}
