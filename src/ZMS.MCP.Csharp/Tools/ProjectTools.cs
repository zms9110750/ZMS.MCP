using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Tools;

/// <summary>项目层工具：扫盘、解决方案读写、创建项目。</summary>
[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Scan a folder for .sln / .slnx / .csproj. Output: one block per solution — " +
        "'<solution path>(described+extra)' then its described projects as a tree (├─ / └─), " +
        "then the projects that exist under the solution folder but are NOT described by it, prefixed with '-'. " +
        "Projects not covered by any solution follow as plain lines. bin/obj/.git are skipped.")]
    public static string ScanProjects(
        [Description("Absolute folder path to scan")] string folder,
        [Description("Max recursion depth (default 4)")] int depth = 4,
        [Description("Kinds to include, comma separated: sln,slnx,csproj. Empty = all three")] string kinds = "")
    {
        return ToolGuard.Run(() =>
        {
            ScanResult result = SolutionExplorer.ScanTree(folder, depth, kinds);
            if (result.Solutions.Count == 0 && result.LooseProjects.Count == 0)
            {
                return $"No solution/project files under {Path.GetFullPath(folder)} (depth {depth}).";
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

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the projects inside a solution (.sln or .slnx).")]
    public static string ListSolutionProjects(
        [Description("Path to the .sln or .slnx file")] string solutionPath)
    {
        return ToolGuard.Run(() =>
        {
            string fullPath = Path.GetFullPath(solutionPath);
            IReadOnlyList<SolutionProject> projects = SolutionExplorer.ReadProjects(fullPath);
            StringBuilder builder = new();
            builder.AppendLine($"# {fullPath}");
            builder.AppendLine();
            if (projects.Count == 0)
            {
                builder.AppendLine("_(no projects)_");
                return builder.ToString();
            }

            builder.AppendLine($"{projects.Count} project(s):");
            builder.AppendLine();
            foreach (SolutionProject project in projects)
            {
                string missing = File.Exists(project.AbsolutePath) ? "" : "  _(missing on disk)_";
                builder.AppendLine($"- `{project.Name}` → `{project.RelativePath}`{missing}");
            }

            return builder.ToString();
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Add an existing project to a solution via 'dotnet sln add'.")]
    public static async Task<string> AddProjectToSolution(
        [Description("Path to the .sln or .slnx file")] string solutionPath,
        [Description("Path to the project file to add")] string projectPath)
    {
        (int exitCode, string output) = await SolutionExplorer.AddProjectToSolution(solutionPath, projectPath);
        return FormatDotnetResult(exitCode, output, $"Added {Path.GetFullPath(projectPath)} to {Path.GetFullPath(solutionPath)}");
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Remove a project from a solution via 'dotnet sln remove'.")]
    public static async Task<string> RemoveProjectFromSolution(
        [Description("Path to the .sln or .slnx file")] string solutionPath,
        [Description("Path to the project file to remove")] string projectPath)
    {
        (int exitCode, string output) = await SolutionExplorer.RemoveProjectFromSolution(solutionPath, projectPath);
        return FormatDotnetResult(exitCode, output, $"Removed {Path.GetFullPath(projectPath)} from {Path.GetFullPath(solutionPath)}");
    }

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a new dotnet project with 'dotnet new' (e.g. template=classlib, console, xunit).")]
    public static async Task<string> CreateProject(
        [Description("Folder to create the project in")] string folder,
        [Description("Project name")] string name,
        [Description("dotnet new template short name (default classlib)")] string template = "classlib")
    {
        (int exitCode, string output) = await SolutionExplorer.CreateProject(folder, name, template);
        return FormatDotnetResult(exitCode, output, $"Created {name} ({template}) in {Path.GetFullPath(folder)}");
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
