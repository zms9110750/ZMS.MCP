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
        "View a project or a solution: pass a csproj (or a project name that is unique in the nearest solution) to get the csproj verbatim " +
        "followed by every file that takes part in declaring it and can be found upwards, or pass a .slnx to get the solution as a tree " +
        "with its virtual folders. A .sln is refused with a hint to migrate it first; anything else is treated as a project name.")]
    public static string ViewProjectOrSolution(
        [Description("csproj path (or a unique project name), or a .slnx file")] string path)
    {
        return ToolGuard.Run(() =>
        {
            string trimmed = (path ?? "").Trim();
            string full = Path.GetFullPath(trimmed);
            if (File.Exists(full) && full.EndsWith(".slnx", PathComparison.Comparison))
            {
                return SolutionViewer.ViewTree(full);
            }

            if (File.Exists(full) && full.EndsWith(".sln", PathComparison.Comparison))
            {
                throw new InvalidOperationException(
                    $"只能查看 .slnx：{Path.GetFileName(full)}。请先用「迁移解决方案为 slnx」把它迁过来。");
            }

            return ProjectViewer.View(trimmed);
        });
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
        "Edit a csproj in TWO steps. " +
        "Step 1 - call with an empty content: returns the current csproj verbatim, its encoding, and a cookie. " +
        "Step 2 - call with the complete new content plus that cookie: re-reads the file, compares the cookie, " +
        "and only then validates (XML syntax + root element <Project>) and writes it back using the file's original encoding. " +
        "If the file changed in between, the write is refused - the cookie exists so the content you replace is the content you actually read.")]
    public static string EditProjectMetadata(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("Complete new csproj content; empty = just read and return the cookie")] string content = "",
        [Description("Cookie from the read step; the write is refused if the file changed since")] string cookie = "")
    {
        return ToolGuard.Run(() => ProjectEditor.EditMetadata(csprojPath, content, cookie));
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
        "Reports and does it in one call: the report lists what will be introduced, what got covered by another package's " +
        "dependency, and which vulnerable versions were replaced - then the commands really run, followed by a post-write " +
        "vulnerability check. There is no dry-run step, because 'dotnet add package' reads the csproj as it is right now: " +
        "there is no stale snapshot for a dry run to protect against. " +
        "Each item is 'Name' or 'Name@Version'.")]
    public static string InstallPackages(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("Packages to install: each item is 'nugetName' or 'nugetName@version', e.g. [\"Newtonsoft.Json@13.0.3\", \"Polly\"]")] string[] nugetPack,
        [Description("Also allow prerelease versions")] bool allowPrerelease = false)
    {
        return ToolGuard.Run(() => PackageManager.Install(csprojPath, ParseRequests(nugetPack), allowPrerelease));
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Remove NuGet packages through 'dotnet remove package' (console operation, NOT part of any draft/transaction). " +
        "The report always lists which transitive packages disappear as well: computed first from the local dependency graph " +
        "(build the graph, cut the removed direct packages, compare with the graph before), then re-checked against the restore " +
        "result after the commands ran. There is no dry-run step - 'dotnet remove package' reads the csproj as it is right now.")]
    public static string RemovePackages(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("Package ids to remove")] string[] nugetName)
    {
        return ToolGuard.Run(() => PackageManager.Remove(csprojPath, nugetName));
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
