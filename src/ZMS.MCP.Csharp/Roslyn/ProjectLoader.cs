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
/// 只做 XML 解析，不启动 MSBuild —— 它是评估失败时的降级依据。
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

/// <summary>项目加载方式。</summary>
public enum LoadMode
{
    /// <summary>走 MSBuild 评估：编译项、引用集、宏都来自真实评估结果。</summary>
    Evaluated,

    /// <summary>降级：只解析 csproj 并扫目录，引用只有宿主运行时程序集，诊断可能夹带假错误。</summary>
    Fallback,
}

/// <summary>
/// 已加载的项目：csproj 快照 + 由源文件构建出的 <see cref="CSharpCompilation"/>。
/// </summary>
public sealed class LoadedProject
{
    private static readonly ConcurrentDictionary<string, MetadataReference> ReferenceCache = new(StringComparer.OrdinalIgnoreCase);

    public ProjectFileInfo Info { get; }

    /// <summary>由项目源文件构建出的编译对象。</summary>
    public CSharpCompilation Compilation { get; }

    /// <summary>本次加载走的是评估还是降级。</summary>
    public LoadMode Mode { get; }

    /// <summary>降级原因（<see cref="LoadMode.Evaluated"/> 时为空）。</summary>
    public string FallbackReason { get; }

    private LoadedProject(ProjectFileInfo info, CSharpCompilation compilation, LoadMode mode, string fallbackReason)
    {
        Info = info;
        Compilation = compilation;
        Mode = mode;
        FallbackReason = fallbackReason;
    }

    /// <summary>
    /// 加载项目：先问 MSBuild 要"项目的事实"（编译项 / 引用集 / 宏 / 语言选项），拿不到就降级。
    ///
    /// 只有"评估本身"失败才降级 —— 评估成功后装配 compilation 出错属于真问题，照实抛出，
    /// 否则会把真错误伪装成"简化模式"。降级时用 <see cref="ModeNotice"/> 取提示。
    /// </summary>
    /// <exception cref="FileNotFoundException">项目文件不存在。</exception>
    public static LoadedProject Load(string projectPath)
    {
        ProjectFileInfo info = ProjectFileInfo.Read(projectPath);

        MsBuildEvaluation evaluation;
        try
        {
            evaluation = MsBuildEvaluator.Evaluate(info.ProjectPath);
        }
        catch (Exception exception)
        {
            return Fallback(info, $"{exception.GetType().Name}: {exception.Message}");
        }

        return Evaluated(info, evaluation);
    }

    /// <summary>
    /// 加载模式提示：评估模式返回空串；降级时提醒"引用集与宏可能不全，诊断可能夹带假错误"。
    /// </summary>
    public string ModeNotice()
    {
        if (Mode == LoadMode.Evaluated)
        {
            return "";
        }

        return $"⚠ 简化模式：MSBuild 评估失败（{FallbackReason}）—— 引用集与宏可能不全，诊断可能夹带假错误。";
    }

    private static LoadedProject Evaluated(ProjectFileInfo info, MsBuildEvaluation evaluation)
    {
        CSharpParseOptions parseOptions = new(
            languageVersion: ParseLanguageVersion(evaluation.GetProperty("LangVersion")),
            preprocessorSymbols: SplitSymbols(evaluation.GetProperty("DefineConstants")));

        List<SyntaxTree> trees = ParseTrees(evaluation.CompileItems, parseOptions);

        string assemblyName = evaluation.GetProperty("AssemblyName");
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            assemblyName = Path.GetFileNameWithoutExtension(info.ProjectPath);
        }

        CSharpCompilationOptions options = new(
            outputKind: OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            nullableContextOptions: ParseNullable(evaluation.GetProperty("Nullable")));

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            References(evaluation.ReferencePaths),
            options);

        return new LoadedProject(info, compilation, LoadMode.Evaluated, "");
    }

    private static LoadedProject Fallback(ProjectFileInfo info, string reason)
    {
        CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
        List<SyntaxTree> trees = ParseTrees(info.SourceFiles, parseOptions);

        CSharpCompilationOptions options = new(
            outputKind: OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable);

        CSharpCompilation compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(info.ProjectPath),
            trees,
            RuntimeReferences(),
            options);

        return new LoadedProject(info, compilation, LoadMode.Fallback, reason);
    }

    private static List<SyntaxTree> ParseTrees(IEnumerable<string> files, CSharpParseOptions parseOptions)
    {
        List<SyntaxTree> trees = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || !seen.Add(file) || !File.Exists(file))
            {
                continue;
            }

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

        return trees;
    }

    private static IEnumerable<MetadataReference> References(IEnumerable<string> paths)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path) || !File.Exists(path))
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

    /// <summary>
    /// 语言版本：缺失或不可解析时用 <see cref="LanguageVersion.Default"/>。
    /// 不能用 Preview —— 那会接受实验语法，把"该报错的代码"放过去（假阴性）。
    /// </summary>
    private static LanguageVersion ParseLanguageVersion(string value)
    {
        return LanguageVersionFacts.TryParse(value, out LanguageVersion version) ? version : LanguageVersion.Default;
    }

    /// <summary>可空性：MSBuild 里"没设置"等价于 disable，不是 enable。</summary>
    private static NullableContextOptions ParseNullable(string value)
    {
        if (string.Equals(value, "enable", StringComparison.OrdinalIgnoreCase))
        {
            return NullableContextOptions.Enable;
        }

        if (string.Equals(value, "warnings", StringComparison.OrdinalIgnoreCase))
        {
            return NullableContextOptions.Warnings;
        }

        if (string.Equals(value, "annotations", StringComparison.OrdinalIgnoreCase))
        {
            return NullableContextOptions.Annotations;
        }

        return NullableContextOptions.Disable;
    }

    private static string[] SplitSymbols(string value)
    {
        return value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
