using System.Text;
using System.Xml.Linq;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Project;

/// <summary>一个"参与项目声明"的文件。</summary>
public sealed record DeclarationFile(string Role, string Path, string Content);

/// <summary>
/// 「查看项目」：返回 csproj 原文，再附加所有向上能找到、参与项目声明的文件。
/// </summary>
public static class ProjectViewer
{
    /// <summary>向上找时最多上升几层（防呆，正常项目到盘根就停了）。</summary>
    private const int MaxAncestorDepth = 64;

    /// <summary>按文档顺序列出参与项目声明的文件。</summary>
    public static IReadOnlyList<DeclarationFile> CollectDeclarationFiles(string csprojPath)
    {
        string fullPath = Path.GetFullPath(csprojPath);
        string projectDirectory = Path.GetDirectoryName(fullPath) ?? ".";
        string projectName = Path.GetFileName(fullPath);
        List<DeclarationFile> files =
        [
            new DeclarationFile("项目文件", fullPath, ReadText(fullPath)),
        ];

        // 目录级：各取最近一份（MSBuild 语义：Directory.Build.props 找到即停，除非它自己 Import 更上层）
        AddNearest(files, "目录级（MSBuild 自动导入，取最近一份）", projectDirectory, "Directory.Build.props");
        AddNearest(files, "目录级（中央包管理版本）", projectDirectory, "Directory.Packages.props");
        AddNearest(files, "目录级（MSBuild 自动导入，取最近一份）", projectDirectory, "Directory.Build.targets");
        AddNearest(files, "SDK 版本固定", projectDirectory, "global.json");
        AddNearest(files, "NuGet 源配置", projectDirectory, "NuGet.config");

        // 生成物：还原时产生的 props/targets
        string intermediate = Path.Combine(projectDirectory, "obj");
        AddIfExists(files, "还原生成（属性）", Path.Combine(intermediate, projectName + ".nuget.g.props"));
        AddIfExists(files, "还原生成（目标）", Path.Combine(intermediate, projectName + ".nuget.g.targets"));

        return files;
    }

    /// <summary>查看项目：项目文件 + 参与声明的文件，逐份给原文。</summary>
    public static string View(string csprojPath, string? relativeTo = null)
    {
        string fullPath = ResolveProjectFile(csprojPath);
        StringBuilder builder = new();
        builder.AppendLine($"# {Display(fullPath, relativeTo)}");

        IReadOnlyList<DeclarationFile> files = CollectDeclarationFiles(fullPath);
        foreach (DeclarationFile file in files)
        {
            builder.AppendLine();
            builder.AppendLine($"## {file.Role}");
            builder.AppendLine(Display(file.Path, relativeTo));
            builder.AppendLine("```xml");
            builder.AppendLine(file.Content.TrimEnd());
            builder.AppendLine("```");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 定位项目文件：路径存在就直接用；否则当成项目名，向上找解决方案并在其中找**唯一**同名项目。
    /// 重名时必须给完整相对路径。
    /// </summary>
    public static string ResolveProjectFile(string csprojPath)
    {
        if (string.IsNullOrWhiteSpace(csprojPath))
        {
            throw new ArgumentException("csprojPath 不能为空。");
        }

        string trimmed = csprojPath.Trim();
        if (File.Exists(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        if (trimmed.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException($"项目文件不存在：{trimmed}");
        }

        string baseDirectory = Environment.CurrentDirectory;
        string? solution = FindNearestUpwards(baseDirectory, ".slnx") ?? FindNearestUpwards(baseDirectory, ".sln");
        if (solution == null)
        {
            throw new FileNotFoundException(
                $"'{trimmed}' 不是已存在的项目文件，也没能从 {baseDirectory} 向上找到解决方案，无法按项目名定位。请给完整路径。");
        }

        List<string> matches = SolutionExplorer.ReadProjects(solution)
            .Where(project => project.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(project => project.AbsolutePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException($"解决方案 {Path.GetFileName(solution)} 里没有名为 '{trimmed}' 的项目。"),
            _ => throw new InvalidOperationException(
                $"解决方案 {Path.GetFileName(solution)} 里有 {matches.Count} 个叫 '{trimmed}' 的项目，请给完整相对路径："
                + string.Join("; ", matches)),
        };
    }

    private static void AddNearest(List<DeclarationFile> files, string role, string startDirectory, string fileName)
    {
        string? path = FindNearestUpwards(startDirectory, fileName);
        if (path != null)
        {
            files.Add(new DeclarationFile($"{role} — {fileName}", path, ReadText(path)));
        }
    }

    private static void AddIfExists(List<DeclarationFile> files, string role, string path)
    {
        if (File.Exists(path))
        {
            files.Add(new DeclarationFile(role, path, ReadText(path)));
        }
    }

    private static string ReadText(string path)
    {
        try
        {
            return FileWriter.ReadAllText(path, out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"（读取失败：{exception.Message}）";
        }
    }

    private static string Display(string path, string? relativeTo)
    {
        if (string.IsNullOrWhiteSpace(relativeTo))
        {
            return path;
        }

        return Path.GetRelativePath(relativeTo, path);
    }

    /// <summary>从某目录向上找最近的一个同名文件。</summary>
    internal static string? FindNearestUpwards(string startDirectory, string fileName)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(startDirectory));
        int depth = 0;
        while (directory != null && depth < MaxAncestorDepth)
        {
            string[] found = Directory.GetFiles(directory.FullName, "*" + fileName, SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase)
                    || (fileName.StartsWith('.') && Path.GetExtension(path).Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (found.Length > 0)
            {
                return found.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First();
            }

            directory = directory.Parent;
            depth++;
        }

        return null;
    }

    /// <summary>检验内容是不是能当 csproj 用（XML 合法 + 根元素是 Project）。</summary>
    public static void EnsureValidProjectXml(string content)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(content);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new InvalidOperationException($"XML 语法不通过：{exception.Message}");
        }

        if (document.Root == null || document.Root.Name.LocalName != "Project")
        {
            throw new InvalidOperationException(
                $"最低 csproj 语法检查不通过：根元素必须是 <Project>，实际是 <{document.Root?.Name.LocalName ?? "(空)"}>");
        }
    }
}
