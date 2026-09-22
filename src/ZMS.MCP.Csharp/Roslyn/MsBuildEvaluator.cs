using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>
/// 一次 MSBuild 评估的结果：属性、编译项、引用路径。
/// </summary>
public sealed class MsBuildEvaluation
{
    public IReadOnlyDictionary<string, string> Properties { get; }

    /// <summary>编译项（评估后）：含 <c>Directory.Build.props</c> 注入的、以及生成的源文件。</summary>
    public IReadOnlyList<string> CompileItems { get; }

    /// <summary>引用路径（<c>ResolveReferences</c> 之后）：含传递依赖包与框架引用。</summary>
    public IReadOnlyList<string> ReferencePaths { get; }

    public MsBuildEvaluation(
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyList<string> compileItems,
        IReadOnlyList<string> referencePaths)
    {
        Properties = properties;
        CompileItems = compileItems;
        ReferencePaths = referencePaths;
    }

    public string GetProperty(string name)
    {
        return Properties.TryGetValue(name, out string? value) ? value : "";
    }
}

/// <summary>
/// 借 MSBuild 做一次项目评估，取回"项目的事实"：
/// 属性（<c>DefineConstants</c> / <c>TargetFramework</c> / <c>AssemblyName</c> …）、
/// 编译项（含 props 注入与生成的文件）、引用路径（含传递依赖与框架引用）。
///
/// 不把 MSBuild 拉进进程 —— 起一次 <c>dotnet msbuild</c>，结果按输入文件时间戳缓存。
/// 评估失败（未还原、SDK 不匹配等）由调用方决定降级，本类只负责如实抛出。
/// </summary>
public static class MsBuildEvaluator
{
    private const int TimeoutSeconds = 180;

    /// <summary>会参与项目声明的文件；它们比缓存新就说明缓存过期。</summary>
    private static readonly string[] WatchedFileNames =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "Directory.Build.props.user",
    ];

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(MsBuildEvaluation Evaluation, DateTime ValidatedAtUtc);

    /// <summary>
    /// 求值一个项目。结果带缓存，输入文件变动后自动失效。
    /// </summary>
    /// <param name="projectPath">.csproj 路径。</param>
    /// <param name="refresh">true 时跳过缓存。</param>
    public static MsBuildEvaluation Evaluate(string projectPath, bool refresh = false)
    {
        string fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Project file not found: {fullPath}");
        }

        if (!refresh && Cache.TryGetValue(fullPath, out CacheEntry? cached) && !IsStale(fullPath, cached))
        {
            return cached.Evaluation;
        }

        MsBuildEvaluation evaluation = Run(fullPath);
        Cache[fullPath] = new CacheEntry(evaluation, DateTime.UtcNow);
        return evaluation;
    }

    /// <summary>清空缓存（测试或强制重新评估时用）。</summary>
    public static void ClearCache()
    {
        Cache.Clear();
    }

    private static MsBuildEvaluation Run(string projectPath)
    {
        string[] arguments =
        [
            "msbuild",
            projectPath,
            "-nologo",
            "-v:q",
            "-t:ResolveReferences",
            "-getProperty:TargetFramework,DefineConstants,AssemblyName,RootNamespace,Nullable,ProjectAssetsFile",
            "-getItem:Compile,ReferencePath",
        ];

        (int exitCode, string output) = RunDotnet(arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"MSBuild evaluation of '{projectPath}' failed with exit code {exitCode}: {Truncate(output)}");
        }

        return Parse(output);
    }

    private static (int ExitCode, string Output) RunDotnet(string[] arguments)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'dotnet'. Is the .NET SDK on PATH?");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeoutSeconds * 1000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能已退出，忽略
            }

            return (-1, $"dotnet msbuild timed out after {TimeoutSeconds}s.");
        }

        string output = stdout.GetAwaiter().GetResult() + Environment.NewLine + stderr.GetAwaiter().GetResult();
        return (process.ExitCode, output);
    }

    private static MsBuildEvaluation Parse(string output)
    {
        string json = ExtractJson(output);
        using JsonDocument document = JsonDocument.Parse(json);

        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("Properties", out JsonElement propertyElement))
        {
            foreach (JsonProperty property in propertyElement.EnumerateObject())
            {
                properties[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.ToString();
            }
        }

        return new MsBuildEvaluation(
            properties,
            ReadItems(document, "Compile"),
            ReadItems(document, "ReferencePath"));
    }

    /// <summary>
    /// 从输出里截出 JSON 对象。MSBuild 会在结果前后夹带提示信息（如 NETSDK1057 预览版提示），
    /// 所以取第一个 <c>{</c> 到最后一个 <c>}</c>。
    /// </summary>
    private static string ExtractJson(string output)
    {
        string text = output.Replace("\uFEFF", "").Trim();
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException($"MSBuild output is not JSON: {Truncate(output)}");
        }

        return text[start..(end + 1)];
    }

    private static List<string> ReadItems(JsonDocument document, string itemName)
    {
        List<string> values = [];
        if (!document.RootElement.TryGetProperty("Items", out JsonElement itemsElement) ||
            !itemsElement.TryGetProperty(itemName, out JsonElement arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (JsonElement item in arrayElement.EnumerateArray())
        {
            string? path = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("FullPath", out JsonElement fullPath) => fullPath.GetString(),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(path))
            {
                values.Add(path);
            }
        }

        return values;
    }

    private static bool IsStale(string projectPath, CacheEntry entry)
    {
        foreach (string file in CandidateInputFiles(projectPath))
        {
            try
            {
                if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > entry.ValidatedAtUtc)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 读不到时间戳就当没过期，下一轮再看
            }
        }

        return false;
    }

    /// <summary>项目文件 + 逐级向上的 props/targets/packages 文件。</summary>
    private static IEnumerable<string> CandidateInputFiles(string projectPath)
    {
        yield return projectPath;

        DirectoryInfo? directory = new FileInfo(projectPath).Directory;
        while (directory != null)
        {
            foreach (string name in WatchedFileNames)
            {
                yield return Path.Combine(directory.FullName, name);
            }

            directory = directory.Parent;
        }
    }

    private static string Truncate(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 2000 ? trimmed : trimmed[..2000] + "…";
    }
}
