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
/// 不把 MSBuild 拉进进程 —— 起一次 <c>dotnet msbuild</c>，结果按输入文件快照缓存。
/// 评估失败（未还原、SDK 不匹配等）由调用方决定降级，本类只负责如实抛出。
/// </summary>
public static class MsBuildEvaluator
{
    private const int TimeoutSeconds = 180;

    /// <summary>向上查找 props 链的最大层数，避免在深层目录里一路扫到盘根。</summary>
    private const int MaxAncestorDepth = 16;

    /// <summary>缓存条目上限；评估很贵、条目很少，超限就整体丢弃。</summary>
    private const int MaxCacheEntries = 64;

    /// <summary>解析 JSON 时最多尝试的候选边界数（起点与终点各这么多，组合起来逐个试）。</summary>
    private const int MaxJsonBoundaryCandidates = 8;

    /// <summary>会参与项目声明的文件；它们出现、消失或变动都说明缓存过期。</summary>
    private static readonly string[] WatchedFileNames =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Build.props.user",
        "Directory.Packages.props",
        "global.json",
        "NuGet.config",
        "nuget.config",
    ];

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(MsBuildEvaluation Evaluation, IReadOnlyDictionary<string, DateTime> InputTimestamps);

    /// <summary>
    /// 求值一个项目。结果带缓存，输入文件出现 / 消失 / 变动后自动失效。
    /// </summary>
    /// <param name="projectPath">.csproj 路径。</param>
    /// <param name="refresh">true 时跳过缓存。</param>
    /// <exception cref="FileNotFoundException">项目文件不存在。</exception>
    /// <exception cref="TimeoutException">评估超时。</exception>
    /// <exception cref="InvalidOperationException">评估失败（未还原、SDK 不匹配、输出无法解析等）。</exception>
    public static MsBuildEvaluation Evaluate(string projectPath, bool refresh = false)
    {
        string fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Project file not found: {fullPath}");
        }

        if (!refresh &&
            Cache.TryGetValue(fullPath, out CacheEntry? cached) &&
            !IsStale(fullPath, cached.InputTimestamps))
        {
            return cached.Evaluation;
        }

        MsBuildEvaluation evaluation = Run(fullPath);
        if (Cache.Count >= MaxCacheEntries)
        {
            Cache.Clear();
        }

        Cache[fullPath] = new CacheEntry(evaluation, SnapshotInputs(fullPath));
        return evaluation;
    }

    /// <summary>清空缓存（测试或强制重新评估时用）。</summary>
    public static void ClearCache()
    {
        Cache.Clear();
    }

    /// <summary>给候选输入文件拍一份时间戳快照（只记存在的文件）。</summary>
    internal static Dictionary<string, DateTime> SnapshotInputs(string projectPath)
    {
        Dictionary<string, DateTime> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in CandidateInputFiles(projectPath))
        {
            try
            {
                if (File.Exists(file))
                {
                    snapshot[file] = File.GetLastWriteTimeUtc(file);
                }
            }
            catch (Exception)
            {
                // 读不到就跳过
            }
        }

        return snapshot;
    }

    /// <summary>
    /// 与快照比对：候选文件"出现 / 消失 / 时间戳变动"都算过期。
    /// 已知限制：文件系统粒度粗（FAT 2 秒、部分网络盘）时，同一时间片内的改动可能看不出
    /// mtime 变化而漏判 —— 这里不做时间容差，因为容差会把"没变"也判成脏，导致缓存永不命中。
    /// </summary>
    internal static bool IsStale(string projectPath, IReadOnlyDictionary<string, DateTime> snapshot)
    {
        foreach (string file in CandidateInputFiles(projectPath))
        {
            bool recorded = snapshot.TryGetValue(file, out DateTime timestamp);
            bool exists;
            DateTime current;
            try
            {
                exists = File.Exists(file);
                current = exists ? File.GetLastWriteTimeUtc(file) : default;
            }
            catch (Exception)
            {
                continue;
            }

            if (exists != recorded)
            {
                // 监视文件出现或消失
                return true;
            }

            if (exists && current != timestamp)
            {
                return true;
            }
        }

        return false;
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

            // 杀完把已捕获的输出带回来，方便定位卡在哪一步
            string captured = Observe(stdout) + Environment.NewLine + Observe(stderr);
            throw new TimeoutException(
                $"dotnet msbuild timed out after {TimeoutSeconds}s: {string.Join(' ', arguments)}" +
                $"{Environment.NewLine}{Truncate(captured)}");
        }

        string output = stdout.GetAwaiter().GetResult() + Environment.NewLine + stderr.GetAwaiter().GetResult();
        return (process.ExitCode, output);
    }

    /// <summary>进程已被杀，等读取任务落地（避免留下未观察的任务），并尽量带回已捕获的输出。</summary>
    private static string Observe(Task<string> task)
    {
        try
        {
            return task.Wait(TimeSpan.FromSeconds(5)) ? task.Result : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static MsBuildEvaluation Parse(string output)
    {
        using JsonDocument document = ExtractJsonDocument(output);

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
    /// 从输出里找出 JSON 对象。MSBuild 会在结果前后夹带提示信息（如 NETSDK1057 预览版提示），
    /// 提示里也可能出现大括号，所以按候选边界逐个尝试解析，而不是简单地"第一个 { 到最后一个 }"。
    /// </summary>
    internal static JsonDocument ExtractJsonDocument(string output)
    {
        string text = output.Replace("\uFEFF", "").Trim();
        List<int> starts = CollectPositions(text, '{', MaxJsonBoundaryCandidates, fromEnd: false);
        List<int> ends = CollectPositions(text, '}', MaxJsonBoundaryCandidates, fromEnd: true);

        foreach (int start in starts)
        {
            foreach (int end in ends)
            {
                if (end <= start)
                {
                    continue;
                }

                try
                {
                    return JsonDocument.Parse(text[start..(end + 1)]);
                }
                catch (JsonException)
                {
                    // 换下一组候选边界
                }
            }
        }

        throw new InvalidOperationException(
            $"MSBuild output does not contain a parsable JSON object: {Truncate(output)}");
    }

    private static List<int> CollectPositions(string text, char value, int limit, bool fromEnd)
    {
        List<int> positions = [];
        int index = fromEnd ? text.LastIndexOf(value) : text.IndexOf(value);
        while (index >= 0 && positions.Count < limit)
        {
            positions.Add(index);
            if (fromEnd)
            {
                if (index == 0)
                {
                    // LastIndexOf 的 startIndex 必须 >= 0
                    break;
                }

                index = text.LastIndexOf(value, index - 1);
            }
            else
            {
                index = text.IndexOf(value, index + 1);
            }
        }

        return positions;
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

    /// <summary>项目文件 + 逐级向上的 props/targets/packages 文件（层数有上限）。</summary>
    private static IEnumerable<string> CandidateInputFiles(string projectPath)
    {
        yield return projectPath;

        DirectoryInfo? directory = new FileInfo(projectPath).Directory;
        int depth = 0;
        while (directory != null && depth < MaxAncestorDepth)
        {
            foreach (string name in WatchedFileNames)
            {
                yield return Path.Combine(directory.FullName, name);
            }

            directory = directory.Parent;
            depth++;
        }
    }

    private static string Truncate(string text)
    {
        const int limit = 2000;
        string trimmed = text.Trim();
        if (trimmed.Length <= limit)
        {
            return trimmed;
        }

        int cut = limit;
        if (char.IsHighSurrogate(trimmed[cut - 1]))
        {
            // 不要从代理对中间切开
            cut--;
        }

        return trimmed[..cut] + "…";
    }
}
