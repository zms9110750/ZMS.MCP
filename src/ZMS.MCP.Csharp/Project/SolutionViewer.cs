using System.Text;
using System.Xml.Linq;

namespace ZMS.MCP.Csharp.Project;

/// <summary>slnx 树里的一个节点（虚拟文件夹或项目）。</summary>
public sealed class SolutionNode
{
    public string Label { get; }

    public bool IsFolder { get; }

    public List<SolutionNode> Children { get; } = [];

    public SolutionNode(string label, bool isFolder)
    {
        Label = label;
        IsFolder = isFolder;
    }

    public SolutionNode GetOrAddFolder(string label)
    {
        SolutionNode? existing = Children.FirstOrDefault(child => child.IsFolder && child.Label == label);
        if (existing != null)
        {
            return existing;
        }

        SolutionNode created = new(label, isFolder: true);
        Children.Add(created);
        return created;
    }
}

/// <summary>
/// 「查看 slnx」与「迁移解决方案为 slnx」。
/// 编辑解决方案只能对 slnx 格式进行，所以查看也只支持 slnx。
/// </summary>
public static class SolutionViewer
{
    /// <summary>查看 slnx：树状图，包含虚拟文件夹信息。</summary>
    public static string ViewTree(string solutionPath)
    {
        string fullPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"解决方案不存在：{fullPath}");
        }

        if (!fullPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"只能查看 .slnx：{Path.GetFileName(fullPath)}。请先用「迁移解决方案为 slnx」把它迁过来。");
        }

        SolutionNode root = ParseTree(fullPath);
        StringBuilder builder = new();
        builder.AppendLine(Path.GetFileName(fullPath));
        AppendChildren(builder, root, "");
        return builder.ToString();
    }

    /// <summary>
    /// sln → slnx，走命令行（<c>dotnet sln &lt;文件&gt; migrate</c>）。
    /// 参数封闭（只有解决方案路径 + 可选的强制覆盖），所以做成工具；但**命令行改盘不进事务**。
    /// </summary>
    public static string MigrateToSlnx(string path, bool force = false)
    {
        string solution = ResolveSolutionFile(path);
        if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{Path.GetFileName(solution)} 已经是 slnx 了。");
        }

        string target = Path.ChangeExtension(solution, ".slnx");
        if (File.Exists(target) && !force)
        {
            throw new InvalidOperationException(
                $"目标已存在：{target}。确认要覆盖请带 force=true（dotnet sln migrate 需要 --force）。");
        }

        List<string> arguments = ["sln", solution, "migrate"];
        if (force)
        {
            arguments.Add("--force");
        }

        CommandResult result = CommandRunner.Run("dotnet", arguments, Path.GetDirectoryName(solution) ?? ".");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet sln migrate 失败（退出码 {result.ExitCode}）：\n{result.Output}");
        }

        StringBuilder builder = new();
        builder.AppendLine("# 迁移完成（命令行改盘，不进事务）");
        builder.AppendLine($"- 源：{solution}");
        builder.AppendLine($"- 目标：{target}");
        if (result.Output.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine(result.Output.TrimEnd());
        }

        return builder.ToString();
    }

    /// <summary>path 可以是文件夹（找其中的 .sln/.slnx）或文件本身。</summary>
    internal static string ResolveSolutionFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("path 不能为空。");
        }

        string full = Path.GetFullPath(path.Trim());
        if (File.Exists(full))
        {
            return full;
        }

        if (!Directory.Exists(full))
        {
            throw new FileNotFoundException($"路径不存在：{full}");
        }

        string? slnx = Directory.GetFiles(full, "*.slnx").OrderBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        string? sln = Directory.GetFiles(full, "*.sln").OrderBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return slnx ?? sln ?? throw new FileNotFoundException($"这个文件夹里没有解决方案文件：{full}");
    }

    /// <summary>解析 slnx 成树（虚拟文件夹按 Name 的路径层级展开）。</summary>
    internal static SolutionNode ParseTree(string slnxPath)
    {
        XDocument document = XDocument.Load(slnxPath);
        XElement? root = document.Root;
        if (root == null || root.Name.LocalName != "Solution")
        {
            throw new InvalidOperationException($"{Path.GetFileName(slnxPath)} 的根元素不是 <Solution>。");
        }

        SolutionNode tree = new(Path.GetFileName(slnxPath), isFolder: false);
        AppendElements(tree, root.Elements(), slnxPath);
        return tree;
    }

    private static void AppendElements(SolutionNode parent, IEnumerable<XElement> elements, string slnxPath)
    {
        foreach (XElement element in elements)
        {
            if (element.Name.LocalName.Equals("Folder", StringComparison.OrdinalIgnoreCase))
            {
                string name = element.Attribute("Name")?.Value ?? "";
                SolutionNode folder = Descend(parent, name);
                AppendElements(folder, element.Elements(), slnxPath);
                continue;
            }

            if (element.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("File", StringComparison.OrdinalIgnoreCase))
            {
                // 照实描述 slnx 里写的路径（哪怕是绝对路径）
                parent.Children.Add(new SolutionNode(element.Attribute("Path")?.Value ?? "(缺 Path)", isFolder: false));
            }
        }
    }

    /// <summary><c>/src/sub/</c> → 从 parent 起一层层建出 src、sub。</summary>
    private static SolutionNode Descend(SolutionNode parent, string folderName)
    {
        string normalized = folderName.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return parent;
        }

        SolutionNode current = parent;
        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.GetOrAddFolder(segment);
        }

        return current;
    }

    private static void AppendChildren(StringBuilder builder, SolutionNode node, string prefix)
    {
        for (int index = 0; index < node.Children.Count; index++)
        {
            SolutionNode child = node.Children[index];
            bool isLast = index == node.Children.Count - 1;
            builder.AppendLine($"{prefix}{(isLast ? "└─" : "├─")}{child.Label}");
            AppendChildren(builder, child, prefix + (isLast ? "   " : "│  "));
        }
    }
}
