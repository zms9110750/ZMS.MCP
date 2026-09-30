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
        "Scan a folder for solutions and projects. Output: one block per solution — " +
        "'<solution path>(described+extra)' then its described projects as a tree, " +
        "then the projects that exist under the solution folder but are NOT described by it, prefixed with '-'. " +
        "Projects that no solution covers follow as plain lines. " +
        ".sln / .slnx / .csproj are all picked up; bin / obj / .git are skipped.")]
    public static string ScanProjects(
        [Description("Absolute path of the folder to scan")] string path,
        [Description("Max recursion depth (default 4)")] int depth = 4)
    {
        return ToolGuard.Run(() =>
        {
            ScanResult result = SolutionExplorer.ScanTree(path, depth, "");
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
    public static Task<string> AddProjectToSolution(
        [Description("Path to the .slnx file")] string slnxPath,
        [Description("Path to the project file to add (must already exist)")] string csprojPath,
        [Description("Virtual folder inside the slnx, e.g. 'src/Core' (empty = root)")] string folder = "")
    {
        // 整段（含路径解析与等待）都放进 ToolGuard：否则参数/路径异常会以裸异常冒泡，
        // 错误格式与其余工具不一致。
        return ToolGuard.RunAsync(async () =>
        {
            (int exitCode, string output) = await SolutionExplorer.AddProjectToSolution(slnxPath, csprojPath, folder);
            string target = string.IsNullOrWhiteSpace(folder)
                ? Path.GetFullPath(slnxPath)
                : $"{Path.GetFullPath(slnxPath)}（/{folder.Trim().Replace('\\', '/').Trim('/')}/）";
            return FormatDotnetResult(exitCode, output, $"Added {Path.GetFullPath(csprojPath)} to {target}");
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Remove a project from a .slnx via 'dotnet sln remove'. Editing solutions is only supported for .slnx.")]
    public static Task<string> RemoveProjectFromSolution(
        [Description("Path to the .slnx file")] string slnxPath,
        [Description("Path to the project file to remove")] string csprojPath)
    {
        // 同 AddProjectToSolution：整段进 ToolGuard，错误格式才与其余工具一致。
        return ToolGuard.RunAsync(async () =>
        {
            (int exitCode, string output) = await SolutionExplorer.RemoveProjectFromSolution(slnxPath, csprojPath);
            return FormatDotnetResult(exitCode, output, $"Removed {Path.GetFullPath(csprojPath)} from {Path.GetFullPath(slnxPath)}");
        });
    }

    private static string FormatDotnetResult(int exitCode, string output, string successMessage)
    {
        if (exitCode != 0)
        {
            // 失败就得是**失败**：抛出去，ToolGuard 会转成 Error: 前缀，过滤器再把 isError 置为 true。
            // 否则命令行的 exit code 1 只活在文本里，调用方会以为改成功了（实测踩过：项目没加进 slnx）。
            throw new InvalidOperationException(
                $"dotnet 退出码 {exitCode}，{successMessage} 没有完成。" + Environment.NewLine + output.Trim());
        }

        StringBuilder builder = new();
        builder.AppendLine("✅ " + successMessage);
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
