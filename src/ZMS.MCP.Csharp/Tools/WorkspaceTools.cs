using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Csharp.Draft;
using ZMS.MCP.Csharp.NuGet;
using ZMS.MCP.Csharp.Project;

namespace ZMS.MCP.Csharp.Tools;

/// <summary>
/// 项目层工具：查看 slnx（虚拟文件夹树）/ 查看 csproj（原文 + 向上找 props 链）/
/// 迁移 sln→slnx / 编辑元数据 / 查看包引用 / 安装与移除包。
/// </summary>
[McpServerToolType]
public static class WorkspaceTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Show a .slnx as a tree, including its virtual folders (Folder Name=\"/src/\"). " +
        "Different from the project scan view: this one mirrors the solution file itself. " +
        "Editing solutions is only supported for .slnx, so .sln must be migrated first.")]
    public static string ViewSolutionTree(
        [Description("Path to the .slnx file (or to a folder containing it)")] string path)
    {
        return ToolGuard.Run(() => SolutionViewer.ViewTree(SolutionViewer.ResolveSolutionFile(path)));
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Show a csproj verbatim, then every file that takes part in declaring it and can be found upwards: " +
        "the nearest Directory.Build.props, Directory.Packages.props, Directory.Build.targets, global.json, NuGet.config, " +
        "and the restore-generated obj/<project>.csproj.nuget.g.props|targets. " +
        "csprojPath may be a full path, or just a project name when that name is unique in the nearest solution.")]
    public static string ViewProject(
        [Description("csproj path, or a unique project name")] string csprojPath)
    {
        return ToolGuard.Run(() => ProjectViewer.View(csprojPath));
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Migrate a .sln to .slnx with 'dotnet sln migrate'. " +
        "This is a console operation that changes disk immediately and is NOT part of any draft/transaction (cannot be rolled back).")]
    public static string MigrateSolutionToSlnx(
        [Description("Path to the .sln file (or to a folder containing it)")] string path,
        [Description("Overwrite an existing .slnx (passes --force)")] bool force = false)
    {
        return ToolGuard.Run(() => SolutionViewer.MigrateToSlnx(path, force));
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Replace a csproj's content. The content must pass XML syntax check and the minimal csproj check " +
        "(root element <Project>) before anything is written; it is written back using the file's original encoding.")]
    public static string EditProjectMetadata(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("Full new csproj content")] string content,
        [Description("Only validate and show the diff, do not write")] bool dryRun = false)
    {
        return ToolGuard.Run(() => ProjectEditor.EditMetadata(csprojPath, content, dryRun));
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Show NuGet package references of a project: direct packages, packages pulled in transitively, " +
        "and packages that come in through ProjectReference. The graph comes from a real restore " +
        "(MSBuild ReferencePath items, falling back to project.assets.json whose location is asked from MSBuild).")]
    public static string ListProjectPackages(
        [Description("csproj path, or a unique project name")] string csprojPath)
    {
        return ToolGuard.Run(() =>
        {
            PackageGraphResult graph = PackageGraph.Build(csprojPath);
            System.Text.StringBuilder builder = new();
            builder.AppendLine("# 包引用");
            builder.AppendLine($"- 项目：{graph.ProjectPath}");
            if (graph.AssetsMissing)
            {
                // 没有还原产物时只说这一句：再打"可能已过期"是同一件事说两遍
                string assets = graph.AssetsPath.Length == 0 ? "（MSBuild 没给出 ProjectAssetsFile）" : graph.AssetsPath;
                builder.AppendLine($"- 依赖图来源：ReferencePath（还原产物缺失，可能不全）{assets}");
            }
            else
            {
                builder.AppendLine("- 依赖图来源：真实还原结果");
                builder.AppendLine($"- assets：{graph.AssetsPath}");
                if (graph.MayBeStale)
                {
                    builder.AppendLine("- ⚠ 依赖图可能已过期（csproj / props 比还原产物新）");
                }
            }

            builder.AppendLine();
            builder.AppendLine("## 顶级包（直接引用）");
            AppendNodes(builder, graph.Direct);
            builder.AppendLine();
            builder.AppendLine("## 依赖传递包");
            AppendNodes(builder, graph.Transitive);
            builder.AppendLine();
            builder.AppendLine("## 项目引用而传递的顶级包");
            AppendNodes(builder, graph.FromProjectReferences);
            return builder.ToString();
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(
        "Install NuGet packages through 'dotnet add package' (console operation, NOT part of any draft/transaction). " +
        "Only top-level packages are added directly unless a transitive package's version conflicts with the request. " +
        "Versions are chosen from the local cache first, then nuget.org; vulnerable versions are replaced by the " +
        "latest non-vulnerable one using the NuGet vulnerability index. Each item is 'Name' or 'Name@Version'.")]
    public static string InstallPackages(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("Packages to install: each item is 'nugetName' or 'nugetName@version', e.g. [\"Newtonsoft.Json@13.0.3\", \"Polly\"]")] string[] nugetPack,
        [Description("Only decide and show what would happen, do not run the commands")] bool dryRun = false,
        [Description("Also allow prerelease versions")] bool allowPrerelease = false)
    {
        return ToolGuard.Run(() => PackageManager.Install(csprojPath, ParseRequests(nugetPack), dryRun, allowPrerelease));
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Remove NuGet packages through 'dotnet remove package' (console operation, NOT part of any draft/transaction). " +
        "Builds the package graph before and after so the report can list which transitive packages disappeared too.")]
    public static string RemovePackages(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("nugetName: package ids to remove")] string[] nugetName,
        [Description("Only show what would happen, do not run the commands")] bool dryRun = false)
    {
        return ToolGuard.Run(() => PackageManager.Remove(csprojPath, nugetName, dryRun));
    }

    internal static List<PackageRequest> ParseRequests(IEnumerable<string> nugetPack)
    {
        List<PackageRequest> requests = [];
        foreach (string item in nugetPack)
        {
            string trimmed = (item ?? "").Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            int at = trimmed.LastIndexOf('@');
            requests.Add(at > 0
                ? new PackageRequest(trimmed[..at].Trim(), trimmed[(at + 1)..].Trim())
                : new PackageRequest(trimmed, ""));
        }

        return requests;
    }

    private static void AppendNodes(System.Text.StringBuilder builder, IReadOnlyList<PackageNode> nodes)
    {
        if (nodes.Count == 0)
        {
            builder.AppendLine("（无）");
            return;
        }

        foreach (PackageNode node in nodes)
        {
            builder.AppendLine($"- {node}");
        }
    }
}
