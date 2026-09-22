using Xunit;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 扫盘的边界用例（审查补的）：路径包含判定、同目录 .sln/.slnx、嵌套解决方案归属、
/// 跳过目录的大小写、额外关系顺序、扫描根之外的项目。
/// </summary>
public sealed class SolutionExplorerEdgeCaseTests
{
    private const string EmptyProject = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "zms-mcp-scan-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ───────── IsUnder 的包含判定 ─────────

    [Fact]
    public void IsUnder_handles_drive_root()
    {
        // 盘根（GetFullPath("C:\") 本身就带尾分隔符）不能因为多拼一个分隔符而失配
        string driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        string file = Path.Combine(driveRoot, "some-project.csproj");

        Assert.True(SolutionExplorer.IsUnder(driveRoot, file));
    }

    [Fact]
    public void IsUnder_rejects_sibling_with_common_prefix()
    {
        string directory = Path.Combine(Path.GetTempPath(), "src");

        Assert.True(SolutionExplorer.IsUnder(directory, Path.Combine(directory, "a", "a.csproj")));
        // "src2" 不能因为共享前缀 "src" 被当成在 "src" 之下
        Assert.False(SolutionExplorer.IsUnder(directory, Path.Combine(Path.GetTempPath(), "src2", "a.csproj")));
        Assert.False(SolutionExplorer.IsUnder(directory, Path.Combine(Path.GetTempPath(), "other", "a.csproj")));
    }

    // ───────── 同目录 .sln / .slnx ─────────

    [Fact]
    public void ScanTree_prefers_slnx_over_sln_of_same_name()
    {
        string root = NewRoot();
        WriteFile(Path.Combine(root, "App.slnx"), """
            <Solution>
              <Project Path="src/Cli/Cli.csproj" />
            </Solution>
            """);
        WriteFile(Path.Combine(root, "App.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Other", "src\Other\Other.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
        WriteFile(Path.Combine(root, "src", "Cli", "Cli.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "src", "Other", "Other.csproj"), EmptyProject);

        ScanResult result = SolutionExplorer.ScanTree(root, 4, "");

        // 只算一个解决方案，且是 .slnx
        ScanSolutionGroup group = Assert.Single(result.Solutions);
        Assert.Equal("App.slnx", group.SolutionPath);
        Assert.Equal(["src/Cli/Cli.csproj"], group.Projects);
    }

    // ───────── 嵌套解决方案归属 ─────────

    [Fact]
    public void ScanTree_assigns_project_to_deepest_solution()
    {
        string root = NewRoot();
        WriteFile(Path.Combine(root, "Outer.slnx"), """
            <Solution>
              <Project Path="src/Inner/Inner.csproj" />
            </Solution>
            """);
        WriteFile(Path.Combine(root, "src", "Inner.slnx"), """
            <Solution></Solution>
            """);
        WriteFile(Path.Combine(root, "src", "Inner", "Inner.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "src", "Inner", "Deep", "Deep.csproj"), EmptyProject);

        ScanResult result = SolutionExplorer.ScanTree(root, 4, "");

        Assert.Equal(2, result.Solutions.Count);
        ScanSolutionGroup outer = result.Solutions.Single(group => group.SolutionPath == "Outer.slnx");
        ScanSolutionGroup inner = result.Solutions.Single(group => group.SolutionPath == "src/Inner.slnx");

        // Inner 目录下的 Deep 只归给更贴近的 src/Inner.slnx，不再出现在 Outer 组里
        Assert.Equal(["src/Inner/Deep/Deep.csproj"], inner.ExtraProjects);
        Assert.Empty(outer.ExtraProjects);
        // 被描述的 Inner 项目仍然在 Outer 的描述列表里（描述关系不受归属影响）
        Assert.Equal(["src/Inner/Inner.csproj"], outer.Projects);
    }

    // ───────── 跳过目录大小写 ─────────

    [Fact]
    public void ScanTree_skips_bin_and_obj_case_insensitively()
    {
        string root = NewRoot();
        WriteFile(Path.Combine(root, "App.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "Bin", "Hidden.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "OBJ", "Hidden.csproj"), EmptyProject);

        ScanResult result = SolutionExplorer.ScanTree(root, 4, "");

        Assert.Equal(["App.csproj"], result.LooseProjects);
    }

    // ───────── 额外关系顺序 ─────────

    [Fact]
    public void ScanTree_orders_extra_projects_stably()
    {
        string root = NewRoot();
        WriteFile(Path.Combine(root, "App.slnx"), "<Solution></Solution>");
        WriteFile(Path.Combine(root, "zeta", "Zeta.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "alpha", "Alpha.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "mid", "Mid.csproj"), EmptyProject);

        ScanResult result = SolutionExplorer.ScanTree(root, 4, "");

        ScanSolutionGroup group = Assert.Single(result.Solutions);
        Assert.Equal(["alpha/Alpha.csproj", "mid/Mid.csproj", "zeta/Zeta.csproj"], group.ExtraProjects);
    }

    // ───────── 扫描根之外的项目 ─────────

    [Fact]
    public void ScanTree_describes_project_outside_scan_root_with_absolute_path()
    {
        string parent = NewRoot();
        string root = Path.Combine(parent, "Workspace");
        Directory.CreateDirectory(root);
        WriteFile(Path.Combine(root, "App.slnx"), """
            <Solution>
              <Project Path="../Outside/Outside.csproj" />
            </Solution>
            """);

        ScanResult result = SolutionExplorer.ScanTree(root, 4, "");

        ScanSolutionGroup group = Assert.Single(result.Solutions);
        string described = Assert.Single(group.Projects);
        // 扫描根之外：照实给绝对路径
        Assert.True(Path.IsPathFullyQualified(described), $"应为绝对路径，实际是 {described}");
        Assert.EndsWith("Outside.csproj", described);
    }
}
