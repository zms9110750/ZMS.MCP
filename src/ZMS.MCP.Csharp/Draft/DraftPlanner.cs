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
    /// <summary>一个文件算出来的结果（Actions 用来区分"新建文件"与"改既有文件"）。</summary>
    public sealed record PlannedFile(
        string FilePath,
        string NewContent,
        IReadOnlyList<string> SymbolKeys,
        IReadOnlyList<string> Actions);

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
            try
            {
                changes = DraftService.BuildChanges(
                    project,
                    compilation,
                    edit.TypePath,
                    edit.MemberName,
                    edit.RequestedContent,
                    out _);
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

                // "补 partial" 是附带改动：同一个符号只挂在它真正要写的那个文件上。
                // 否则"符号 → 文件"成一对多，落盘校验会因遍历顺序不同误报"移动/改名"。
                if (!change.Action.Contains("partial", StringComparison.Ordinal))
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
                builder.Actions)),
        ];
        return new Plan(planned, missing, compilation);
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
    }
}
