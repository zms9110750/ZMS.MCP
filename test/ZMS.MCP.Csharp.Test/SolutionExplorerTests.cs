using Xunit;
using ZMS.MCP.Csharp.Roslyn;
using ZMS.MCP.Csharp.Tools;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 扫盘（按解决方案分组）与输出渲染的测试。只在系统临时目录里造假目录结构，不碰仓库。
/// </summary>
public sealed class SolutionExplorerTests
{
    private const string EmptyProject = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "zms-mcp-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>App.slnx 描述两个项目；tools/Extra.csproj 在它目录下但没被描述。</summary>
    private static string CreateSolutionWithExtra(out string root)
    {
        root = NewRoot();
        WriteFile(Path.Combine(root, "App.slnx"), """
            <Solution>
              <Project Path="src/Cli/Cli.csproj" />
              <Project Path="src/Lib/Lib.csproj" />
            </Solution>
            """);
        WriteFile(Path.Combine(root, "src", "Cli", "Cli.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "src", "Lib", "Lib.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "tools", "Extra.csproj"), EmptyProject);
        return root;
    }

    [Fact]
    public void ScanTree_groups_described_projects_and_extra_ones()
    {
        string root = CreateSolutionWithExtra(out _);

        ScanResult result = SolutionExplorer.ScanTree(root, 4, "");

        ScanSolutionGroup group = Assert.Single(result.Solutions);
        Assert.Equal("App.slnx", group.SolutionPath);
        Assert.Equal(["src/Cli/Cli.csproj", "src/Lib/Lib.csproj"], group.Projects);
        Assert.Equal(["tools/Extra.csproj"], group.ExtraProjects);
        Assert.Empty(result.LooseProjects);
    }

    [Fact]
    public void ScanTree_lists_projects_outside_any_solution_as_loose()
    {
        string root = NewRoot();
        // 解决方案在子目录：根下的项目不归它管，算散装
        WriteFile(Path.Combine(root, "src", "App.slnx"), """
            <Solution>
              <Project Path="Cli/Cli.csproj" />
            </Solution>
            """);
        WriteFile(Path.Combine(root, "src", "Cli", "Cli.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "Loose", "Loose.csproj"), EmptyProject);

        ScanResult result = SolutionExplorer.ScanTree(root, 4, "");

        ScanSolutionGroup group = Assert.Single(result.Solutions);
        Assert.Equal("src/App.slnx", group.SolutionPath);
        Assert.Equal(["src/Cli/Cli.csproj"], group.Projects);
        Assert.Empty(group.ExtraProjects);
        Assert.Equal(["Loose/Loose.csproj"], result.LooseProjects);
    }

    [Fact]
    public void ScanTree_reads_classic_sln()
    {
        string root = NewRoot();
        WriteFile(Path.Combine(root, "Legacy.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Legacy", "src\Legacy\Legacy.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
        WriteFile(Path.Combine(root, "src", "Legacy", "Legacy.csproj"), EmptyProject);

        ScanResult result = SolutionExplorer.ScanTree(root, 4, "");

        ScanSolutionGroup group = Assert.Single(result.Solutions);
        Assert.Equal("Legacy.sln", group.SolutionPath);
        Assert.Equal(["src/Legacy/Legacy.csproj"], group.Projects);
        Assert.Empty(result.LooseProjects);
    }

    [Fact]
    public void ScanTree_respects_depth_limit()
    {
        string root = NewRoot();
        WriteFile(Path.Combine(root, "App.slnx"), """
            <Solution>
              <Project Path="src/Cli/Cli.csproj" />
            </Solution>
            """);
        WriteFile(Path.Combine(root, "src", "Cli", "Cli.csproj"), EmptyProject);

        // 深度 0 只扫根目录：看不到 src/Cli/Cli.csproj（既不在描述里也扫不到）
        ScanResult result = SolutionExplorer.ScanTree(root, 1, "");

        ScanSolutionGroup group = Assert.Single(result.Solutions);
        Assert.Empty(group.ExtraProjects);
        Assert.Empty(result.LooseProjects);
        Assert.Single(group.Projects);
    }

    [Fact]
    public void ScanTree_skips_bin_and_obj()
    {
        string root = NewRoot();
        WriteFile(Path.Combine(root, "App.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "bin", "Hidden.csproj"), EmptyProject);
        WriteFile(Path.Combine(root, "obj", "Hidden.csproj"), EmptyProject);

        ScanResult result = SolutionExplorer.ScanTree(root, 4, "");

        Assert.Equal(["App.csproj"], result.LooseProjects);
    }

    [Fact]
    public void ScanProjects_renders_tree_with_counts_and_extra_marker()
    {
        string root = CreateSolutionWithExtra(out _);

        string output = ProjectTools.ScanProjects(root, 4);

        Assert.Contains("App.slnx(2+1)", output);
        // 有额外关系时，描述项目全部用 ├─，额外关系用 - 开头
        Assert.Contains("├─src/Cli/Cli.csproj", output);
        Assert.Contains("├─src/Lib/Lib.csproj", output);
        Assert.Contains("-tools/Extra.csproj", output);
        Assert.DoesNotContain("└─", output);
    }

    [Fact]
    public void ScanProjects_uses_closing_connector_when_no_extra()
    {
        string root = NewRoot();
        WriteFile(Path.Combine(root, "App.slnx"), """
            <Solution>
              <Project Path="src/Cli/Cli.csproj" />
            </Solution>
            """);
        WriteFile(Path.Combine(root, "src", "Cli", "Cli.csproj"), EmptyProject);

        string output = ProjectTools.ScanProjects(root, 4);

        Assert.Contains("App.slnx(1+0)", output);
        Assert.Contains("└─src/Cli/Cli.csproj", output);
    }
}
