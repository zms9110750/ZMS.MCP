using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>
/// .csproj 的静态快照：目标框架、源文件、项目引用与包引用。
/// 只做 XML 解析，不启动 MSBuild。
/// </summary>
public sealed class ProjectFileInfo
{
    public string ProjectPath { get; }

    public string ProjectDirectory { get; }

    public string TargetFramework { get; }

    public IReadOnlyList<string> SourceFiles { get; }

    public IReadOnlyList<string> ProjectReferences { get; }

    public IReadOnlyList<string> PackageReferences { get; }

    public ProjectFileInfo(
        string projectPath,
        string projectDirectory,
        string targetFramework,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> projectReferences,
        IReadOnlyList<string> packageReferences)
    {
        ProjectPath = projectPath;
        ProjectDirectory = projectDirectory;
        TargetFramework = targetFramework;
        SourceFiles = sourceFiles;
        ProjectReferences = projectReferences;
        PackageReferences = packageReferences;
    }

    public static ProjectFileInfo Read(string projectPath)
    {
        string fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Project file not found: {fullPath}");
        }

        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        XDocument document = XDocument.Load(fullPath);
        XElement root = document.Root ?? throw new InvalidDataException($"Empty project file: {fullPath}");

        string targetFramework = ReadProperty(root, "TargetFramework");
        if (string.IsNullOrEmpty(targetFramework))
        {
            targetFramework = ReadProperty(root, "TargetFrameworks")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "net10.0";
        }

        List<string> sourceFiles = CollectSourceFiles(root, directory);
        List<string> projectReferences = ReadItems(root, "ProjectReference")
            .Select(relative => Path.GetFullPath(Path.Combine(directory, relative)))
            .ToList();
        List<string> packageReferences = ReadItems(root, "PackageReference");

        return new ProjectFileInfo(fullPath, directory, targetFramework, sourceFiles, projectReferences, packageReferences);
    }

    internal static string ReadProperty(XElement root, string name)
    {
        foreach (XElement group in root.Elements().Where(element => element.Name.LocalName == "PropertyGroup"))
        {
            foreach (XElement property in group.Elements().Where(element => element.Name.LocalName == name))
            {
                return property.Value.Trim();
            }
        }

        return "";
    }

    internal static List<string> ReadItems(XElement root, string itemName)
    {
        List<string> values = [];
        foreach (XElement group in root.Elements().Where(element => element.Name.LocalName == "ItemGroup"))
        {
            foreach (XElement item in group.Elements().Where(element => element.Name.LocalName == itemName))
            {
                string? include = item.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                {
                    values.Add(include.Trim());
                }
            }
        }

        return values;
    }

    private static List<string> ReadItemPatterns(XElement root, string attribute)
    {
        List<string> values = [];
        foreach (XElement group in root.Elements().Where(element => element.Name.LocalName == "ItemGroup"))
        {
            foreach (XElement item in group.Elements().Where(element => element.Name.LocalName == "Compile"))
            {
                string? value = item.Attribute(attribute)?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value.Trim());
                }
            }
        }

        return values;
    }

    private static List<string> CollectSourceFiles(XElement root, string directory)
    {
        bool useDefaultItems = !string.Equals(ReadProperty(root, "EnableDefaultCompileItems"), "false", StringComparison.OrdinalIgnoreCase);
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);

        if (useDefaultItems)
        {
            foreach (string file in EnumerateSourceFiles(directory))
            {
                files.Add(file);
            }
        }

        foreach (string pattern in ReadItemPatterns(root, "Include"))
        {
            ApplyPattern(directory, pattern, files, remove: false);
        }

        foreach (string pattern in ReadItemPatterns(root, "Remove"))
        {
            ApplyPattern(directory, pattern, files, remove: true);
        }

        return files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ApplyPattern(string directory, string pattern, HashSet<string> files, bool remove)
    {
        string normalized = pattern.Replace('\\', '/');
        if (!normalized.Contains('*') && !normalized.Contains('?'))
        {
            string fullPath = Path.GetFullPath(Path.Combine(directory, normalized));
            if (remove)
            {
                files.Remove(fullPath);
            }
            else if (File.Exists(fullPath))
            {
                files.Add(fullPath);
            }

            return;
        }

        Regex regex = new(GlobToRegex(normalized), RegexOptions.IgnoreCase);
        foreach (string file in EnumerateSourceFiles(directory))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (!regex.IsMatch(relative))
            {
                continue;
            }

            if (remove)
            {
                files.Remove(file);
            }
            else
            {
                files.Add(file);
            }
        }
    }

    private static string GlobToRegex(string glob)
    {
        StringBuilder builder = new("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char current = glob[i];
            if (current == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (current == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(current.ToString()));
            }
        }

        builder.Append('$');
        return builder.ToString();
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        Stack<string> pending = new();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string subdirectory in subdirectories)
            {
                string name = Path.GetFileName(subdirectory);
                if (name is "bin" or "obj" or ".git" or ".vs" or "node_modules")
                {
                    continue;
                }

                pending.Push(subdirectory);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(current, "*.cs");
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string file in files)
            {
                yield return Path.GetFullPath(file);
            }
        }
    }
}

/// <summary>
/// 已加载的项目：.csproj 快照 + 由源文件构建出的 <see cref="CSharpCompilation"/>。
/// </summary>
public sealed class LoadedProject
{
    private static readonly ConcurrentDictionary<string, MetadataReference> ReferenceCache = new(StringComparer.OrdinalIgnoreCase);

    public ProjectFileInfo Info { get; }

    public CSharpCompilation Compilation { get; }

    private LoadedProject(ProjectFileInfo info, CSharpCompilation compilation)
    {
        Info = info;
        Compilation = compilation;
    }

    public static LoadedProject Load(string projectPath)
    {
        ProjectFileInfo info = ProjectFileInfo.Read(projectPath);
        CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
        List<SyntaxTree> trees = [];
        foreach (string file in info.SourceFiles)
        {
            string text;
            try
            {
                text = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception)
            {
                continue;
            }

            SourceText source = SourceText.From(text, Encoding.UTF8);
            trees.Add(CSharpSyntaxTree.ParseText(source, parseOptions, path: file));
        }

        CSharpCompilationOptions options = new(
            outputKind: OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable);

        CSharpCompilation compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(info.ProjectPath),
            trees,
            RuntimeReferences(),
            options);

        return new LoadedProject(info, compilation);
    }

    private static IEnumerable<MetadataReference> RuntimeReferences()
    {
        string? trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(trusted))
        {
            yield break;
        }

        foreach (string path in trusted.Split(Path.PathSeparator))
        {
            if (path.Length == 0 || !File.Exists(path))
            {
                continue;
            }

            MetadataReference? reference = GetReference(path);
            if (reference != null)
            {
                yield return reference;
            }
        }
    }

    private static MetadataReference? GetReference(string path)
    {
        if (ReferenceCache.TryGetValue(path, out MetadataReference? cached))
        {
            return cached;
        }

        try
        {
            MetadataReference created = MetadataReference.CreateFromFile(path);
            ReferenceCache[path] = created;
            return created;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
