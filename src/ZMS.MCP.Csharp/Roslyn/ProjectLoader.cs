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
        HashSet<string> files = new(PathComparison.Comparer);

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

        return files.OrderBy(file => file, PathComparison.Comparer).ToList();
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
    /// <summary>
    /// 元数据引用缓存的上限。正常情况下引用集有限（框架目录 + NuGet 包），
    /// 但 MCP 服务是长驻进程、会反复加载不同项目，所以给一个兜底上限：
    /// 超了就把整张表清空重建，代价远小于内存无界增长。
    /// </summary>
    private const int ReferenceCacheLimit = 512;

    private static readonly ConcurrentDictionary<string, MetadataReference> ReferenceCache = new(StringComparer.OrdinalIgnoreCase);

    public ProjectFileInfo Info { get; }

    /// <summary>由项目源文件构建出的编译对象。</summary>
    public CSharpCompilation Compilation { get; }

    /// <summary>本次加载走的是评估还是降级。</summary>
    public LoadMode Mode { get; }

    /// <summary>降级原因（短摘要，<see cref="LoadMode.Evaluated"/> 时为空）。</summary>
    public string FallbackReason { get; }

    private LoadedProject(ProjectFileInfo info, CSharpCompilation compilation, LoadMode mode, string fallbackReason)
    {
        Info = info;
        Compilation = compilation;
        Mode = mode;
        FallbackReason = fallbackReason;
    }

    /// <summary>
    /// 会话级编译缓存：同一份源码状态只装配一次编译。
    /// 键是项目路径，值是「编译 + 项目目录下所有 .cs 的文件数 / 最新写入时间」——
    /// 时间戳一变就重装配，所以别人在拟定期间改了文件也看得见（不会拿着旧编译往下走）。
    /// </summary>
    private static readonly ConcurrentDictionary<string, (LoadedProject Project, string Stamp)> SessionCache =
        new(PathComparison.Comparer);

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
        string stamp = SourceStamp(info);

        if (SessionCache.TryGetValue(info.ProjectPath, out (LoadedProject Project, string Stamp) cached)
            && string.Equals(cached.Stamp, stamp, StringComparison.Ordinal))
        {
            return cached.Project;
        }

        LoadedProject loaded = LoadCore(info);
        SessionCache[info.ProjectPath] = (loaded, stamp);
        return loaded;
    }

    private static LoadedProject LoadCore(ProjectFileInfo info)
    {
        MsBuildEvaluation evaluation;
        try
        {
            evaluation = MsBuildEvaluator.Evaluate(info.ProjectPath);
        }
        catch (Exception exception) when (IsFallbackWorthy(exception))
        {
            return Fallback(info, Summarize(exception));
        }

        return Evaluated(info, evaluation);
    }

    /// <summary>项目目录下所有 <c>.cs</c> 的"文件数 + 最新写入时间"，用来判断会话缓存还能不能用。</summary>
    private static string SourceStamp(ProjectFileInfo info)
    {
        int count = 0;
        long latest = 0;
        foreach (string file in MsBuildEvaluator.EnumerateSourceFiles(info.ProjectDirectory))
        {
            count++;
            try
            {
                latest = Math.Max(latest, File.GetLastWriteTimeUtc(file).Ticks);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 读不到就只记数，不影响"有变化就重装配"的判断
            }
        }

        return $"{count}:{latest}";
    }

    /// <summary>
    /// 哪些异常才值得降级：MSBuild 评估本身的契约失败（未还原、SDK 不匹配、超时、IO）。
    /// 其余（<see cref="OutOfMemoryException"/>、<see cref="OperationCanceledException"/>、编程错误）
    /// 照实抛出 —— 降级只会把真问题藏成"简化模式"。
    /// </summary>
    internal static bool IsFallbackWorthy(Exception exception)
    {
        return exception is InvalidOperationException
            or TimeoutException
            or IOException
            or UnauthorizedAccessException
            // dotnet 不在 PATH、找不到可执行文件时 Process.Start 抛的是它，同样属于"评估失败"该降级
            or System.ComponentModel.Win32Exception;
    }

    /// <summary>MSBuild / NuGet / 编译器的错误码形态，如 MSB1003、NETSDK1004、CS0246、NU1101。</summary>
    private static readonly Regex ErrorCodeRegex = new("[A-Z]{2,10}\\d{3,5}", RegexOptions.Compiled);

    /// <summary>
    /// 把异常压成一行短摘要（MSBuild 原文可能上千字符，不能整段带回工具输出）。
    /// 多行时优先挑带错误码的那一行 —— 那才是根因所在。
    /// </summary>
    private static string Summarize(Exception exception)
    {
        string[] lines = exception.Message.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string line = lines.FirstOrDefault(candidate => ErrorCodeRegex.IsMatch(candidate))
            ?? lines.FirstOrDefault()
            ?? exception.Message.Trim();

        const int limit = 240;
        if (line.Length > limit)
        {
            line = line[..limit] + "…";
        }

        return $"{exception.GetType().Name}: {line}";
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
        // 降级是有意放宽：真实的语言版本 / 可空性拿不到，宁可少报错，
        // 也别用不可信的设置刷出一片假错误（提示见 ModeNotice）。
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
    internal static LanguageVersion ParseLanguageVersion(string value)
    {
        return LanguageVersionFacts.TryParse(value, out LanguageVersion version) ? version : LanguageVersion.Default;
    }

    /// <summary>可空性：MSBuild 里"没设置"等价于 disable，不是 enable。</summary>
    internal static NullableContextOptions ParseNullable(string value)
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
            if (ReferenceCache.Count >= ReferenceCacheLimit)
            {
                // 兜底：清空重建。MCP 服务是长驻进程，反复加载不同项目时这张表会一直涨
                ReferenceCache.Clear();
            }

            ReferenceCache[path] = created;
            return created;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
