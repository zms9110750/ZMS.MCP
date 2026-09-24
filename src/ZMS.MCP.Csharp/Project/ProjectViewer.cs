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

    /// <summary>沿着 Directory.Build.props 自己的 Import 往上追的最大层数（防环）。</summary>
    private const int MaxImportedAncestors = 8;

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
        string? nearestProps = FindNearestUpwards(projectDirectory, "Directory.Build.props");
        if (nearestProps != null)
        {
            files.Add(new DeclarationFile(
                "目录级（MSBuild 自动导入，取最近一份） — Directory.Build.props",
                nearestProps,
                ReadText(nearestProps)));
            AddImportedAncestors(files, nearestProps);
        }

        AddNearest(files, "目录级（中央包管理版本）", projectDirectory, "Directory.Packages.props");
        AddNearest(files, "目录级（MSBuild 自动导入，取最近一份）", projectDirectory, "Directory.Build.targets");
        AddNearest(files, "SDK 版本固定", projectDirectory, "global.json");
        AddNearest(files, "NuGet 源配置", projectDirectory, "NuGet.config");

        // 生成物：还原时产生的 props/targets。obj 的位置问 MSBuild（可能被 BaseIntermediateOutputPath 重定向），
        // 问不到就不猜 —— 宁可少列，也不给一个可能错的路径
        AddGeneratedFiles(files, fullPath, projectName);

        return files;
    }

    /// <summary>
    /// 最近的 <c>Directory.Build.props</c> 自己 <c>Import</c> 了更上层的文件时，那份同样参与项目声明，
    /// 也一并列出（逐级向上；防环 + 深度上限）。
    /// </summary>
    private static void AddImportedAncestors(List<DeclarationFile> files, string startFile)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(startFile) };
        string current = startFile;
        for (int depth = 0; depth < MaxImportedAncestors; depth++)
        {
            string? imported = FindImportedAncestor(current);
            if (imported == null || !seen.Add(imported))
            {
                break;
            }

            files.Add(new DeclarationFile(
                "目录级（被上一份的 Import 引入）",
                imported,
                ReadText(imported)));
            current = imported;
        }
    }

    /// <summary>
    /// 读一份 props/targets 里的 <c>Import</c> 元素，取**更上层**且确实存在的第一个。
    /// 路径里带未知 MSBuild 属性（<c>$(...)</c>）或通配符时跳过：解析不出来就不猜。
    /// </summary>
    internal static string? FindImportedAncestor(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        XDocument document;
        try
        {
            document = XDocument.Load(fullPath);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        DirectoryInfo? parent = Directory.GetParent(directory);
        if (parent == null)
        {
            return null;
        }

        foreach (XElement element in document.Descendants().Where(item => item.Name.LocalName == "Import"))
        {
            string? resolved = ResolveImportPath(element.Attribute("Project")?.Value, directory);
            // 只要"更上层"的：落在当前文件所在目录的父目录之下
            if (resolved == null || !SolutionExplorer.IsUnder(parent.FullName, resolved) || !File.Exists(resolved))
            {
                continue;
            }

            return resolved;
        }

        return null;
    }

    /// <summary>解析 <c>Import</c> 的 Project 值：支持 <c>$(MSBuildThisFileDirectory)</c>、绝对路径与相对路径。</summary>
    internal static string? ResolveImportPath(string? value, string directory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string withSeparator = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        string expanded = value
            .Replace("$(MSBuildThisFileDirectory)", withSeparator, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFileFullPath)", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (expanded.Length == 0 || expanded.Contains("$(", StringComparison.Ordinal) || expanded.Contains('*'))
        {
            return null;
        }

        string normalized = expanded
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(directory, normalized));
    }

    /// <summary>
    /// 还原生成物 <c>&lt;obj&gt;/&lt;项目&gt;.csproj.nuget.g.props|targets</c>。
    /// obj 的真实位置问 MSBuild；问不到时不列（不硬编码 <c>&lt;项目目录&gt;/obj/</c>）。
    /// </summary>
    private static void AddGeneratedFiles(List<DeclarationFile> files, string csprojPath, string projectName)
    {
        string intermediate = ResolveIntermediateDirectory(csprojPath, out string source);
        if (intermediate.Length == 0)
        {
            files.Add(new DeclarationFile(
                "还原生成（属性/目标） — 未列出",
                "(obj 的位置未知)",
                $"问不到 MSBuild（{source}），因此不按默认值猜 obj 的位置。"));
            return;
        }

        AddIfExists(files, $"还原生成（属性） — obj 位置取自 {source}", Path.Combine(intermediate, projectName + ".nuget.g.props"));
        AddIfExists(files, $"还原生成（目标） — obj 位置取自 {source}", Path.Combine(intermediate, projectName + ".nuget.g.targets"));
    }

    /// <summary>
    /// 问 MSBuild 要中间输出目录（<c>obj</c> 的真实位置）：
    /// 先 <c>MSBuildProjectExtensionsPath</c>，再 <c>ProjectAssetsFile</c> 所在目录。
    /// </summary>
    internal static string ResolveIntermediateDirectory(string csprojPath, out string source)
    {
        source = "";
        try
        {
            MsBuildEvaluation evaluation = MsBuildEvaluator.Evaluate(csprojPath);
            string extensions = evaluation.GetProperty("MSBuildProjectExtensionsPath");
            if (!string.IsNullOrWhiteSpace(extensions))
            {
                source = "MSBuild 的 MSBuildProjectExtensionsPath";
                return Path.GetFullPath(extensions);
            }

            string assets = evaluation.GetProperty("ProjectAssetsFile");
            if (!string.IsNullOrWhiteSpace(assets))
            {
                source = "MSBuild 的 ProjectAssetsFile 所在目录";
                return Path.GetDirectoryName(Path.GetFullPath(assets)) ?? "";
            }

            source = "MSBuild 没有给出这两个属性";
            return "";
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or TimeoutException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            source = exception.Message;
            return "";
        }
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
