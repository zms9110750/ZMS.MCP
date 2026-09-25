using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Tools;

/// <summary>项目层工具：扫盘、解决方案读写、创建项目。</summary>
[McpServerToolType]
public static class ProjectTools
{
    /// <summary>扫描文件夹（原来注册为 `scan_projects`；现在并入 `view` 工具，这里只保留实现）。</summary>
    public static string ScanProjects(
        [Description("Absolute path of the folder to scan")] string path,
        [Description("Max recursion depth (default 4)")] int depth = 4,
        [Description("Kinds to include, comma separated: sln,slnx,csproj. Empty = all three")] string kinds = "")
    {
        return ToolGuard.Run(() =>
        {
            ScanResult result = SolutionExplorer.ScanTree(path, depth, kinds);
            if (result.Solutions.Count == 0 && result.LooseProjects.Count == 0)
            {
                return $"No solution/project files under {Path.GetFullPath(path)} (depth {depth}).";
            }

            StringBuilder builder = new();
            foreach (ScanSolutionGroup group in result.Solutions)
            {
                builder.AppendLine($"{group.SolutionPath}({group.Projects.Count}+{group.ExtraProjects.Count})");
                for (int index = 0; index < group.Projects.Count; index++)
                {
                    // 树状收尾：没有额外关系时，最后一个描述项目用 └─
                    bool isLast = index == group.Projects.Count - 1;
                    string connector = isLast && group.ExtraProjects.Count == 0 ? "└─" : "├─";
                    builder.AppendLine(connector + group.Projects[index]);
                }

                foreach (string extra in group.ExtraProjects)
                {
                    builder.AppendLine("-" + extra);
                }

                builder.AppendLine();
            }

            foreach (string project in result.LooseProjects)
            {
                builder.AppendLine(project);
            }

            return builder.ToString().TrimEnd() + Environment.NewLine;
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Add an existing project to a .slnx via 'dotnet sln add'. " +
        "Editing solutions is only supported for .slnx — a .sln is refused, migrate it first. " +
        "Folder puts the project into that virtual folder of the slnx (empty = solution root). " +
        "The project must already exist; creating projects is not a tool here, run the CLI yourself.")]
    public static async Task<string> AddProjectToSolution(
        [Description("Path to the .slnx file")] string slnxPath,
        [Description("Path to the project file to add (must already exist)")] string csprojPath,
        [Description("Virtual folder inside the slnx, e.g. 'src/Core' (empty = root)")] string folder = "")
    {
        (int exitCode, string output) = await SolutionExplorer.AddProjectToSolution(slnxPath, csprojPath, folder);
        string target = string.IsNullOrWhiteSpace(folder)
            ? Path.GetFullPath(slnxPath)
            : $"{Path.GetFullPath(slnxPath)}（/{folder.Trim().Replace('\\', '/').Trim('/')}/）";
        return FormatDotnetResult(exitCode, output, $"Added {Path.GetFullPath(csprojPath)} to {target}");
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Remove a project from a .slnx via 'dotnet sln remove'. Editing solutions is only supported for .slnx.")]
    public static async Task<string> RemoveProjectFromSolution(
        [Description("Path to the .slnx file")] string slnxPath,
        [Description("Path to the project file to remove")] string csprojPath)
    {
        (int exitCode, string output) = await SolutionExplorer.RemoveProjectFromSolution(slnxPath, csprojPath);
        return FormatDotnetResult(exitCode, output, $"Removed {Path.GetFullPath(csprojPath)} from {Path.GetFullPath(slnxPath)}");
    }

    private static string FormatDotnetResult(int exitCode, string output, string successMessage)
    {
        StringBuilder builder = new();
        builder.AppendLine(exitCode == 0 ? "✅ " + successMessage : $"❌ exit code {exitCode}");
        if (!string.IsNullOrWhiteSpace(output))
        {
            builder.AppendLine();
            builder.AppendLine("```");
            builder.AppendLine(output);
            builder.AppendLine("```");
        }

        return builder.ToString();
    }
}
