using System.Text.Json;
using System.Xml.Linq;
using ZMS.MCP.Csharp.Project;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.NuGet;

/// <summary>图里的一个包。</summary>
public sealed record PackageNode(string Id, string Version)
{
    public override string ToString()
    {
        return Version.Length == 0 ? Id : $"{Id} {Version}";
    }
}

/// <summary>项目的包依赖情况。</summary>
public sealed record PackageGraphResult(
    string ProjectPath,
    IReadOnlyList<PackageNode> Direct,
    IReadOnlyList<PackageNode> Transitive,
    IReadOnlyList<PackageNode> FromProjectReferences,
    string AssetsPath,
    bool AssetsMissing,
    bool MayBeStale);

/// <summary>
/// 包依赖图：**以真实还原结果为准**，不自己造图。
/// 路径取自 MSBuild 评估出的 <c>ReferencePath</c>（它是还原之后的编译引用），
/// 没有可用引用时就退回读 <c>project.assets.json</c>（位置问 MSBuild 的 <c>ProjectAssetsFile</c>，不硬编码 obj/）。
/// </summary>
public static class PackageGraph
{
    /// <summary>依赖图数据比这些输入旧时，标注"可能已过期"。</summary>
    private static readonly string[] Inputs =
    [
        "Directory.Build.props",
        "Directory.Packages.props",
        "Directory.Build.targets",
    ];

    public static PackageGraphResult Build(string csprojPath)
    {
        string fullPath = ProjectViewer.ResolveProjectFile(csprojPath);
        string projectDirectory = Path.GetDirectoryName(fullPath) ?? ".";

        MsBuildSnapshot snapshot = Evaluate(fullPath);
        string assetsPath = snapshot.AssetsPath;

        // 直接引用：项目文件里写了的 PackageReference 名（中央包管理下没有 Version，版本从评估结果对齐）
        IReadOnlyList<string> declared = ReadDeclaredPackages(fullPath);
        Dictionary<string, string> declaredVersions = snapshot.DeclaredVersions;

        Dictionary<string, string> fromReferences = new(StringComparer.OrdinalIgnoreCase);
        foreach (string reference in ReadProjectReferences(fullPath))
        {
            string referencedProject = Path.GetFullPath(Path.Combine(projectDirectory, reference.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(referencedProject))
            {
                continue;
            }

            foreach (string package in ReadDeclaredPackages(referencedProject))
            {
                fromReferences[package] = declaredVersions.TryGetValue(package, out string? version) ? version : "";
            }
        }

        // 传递包：从实际参与编译的引用路径反推包（路径形如 <cache>/<包>/<版本>/...）
        Dictionary<string, string> observed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string referencePath in snapshot.ReferencePaths)
        {
            (string Id, string Version)? parsed = ParsePackagePath(referencePath, NuGetCache.Root());
            if (parsed != null)
            {
                observed[parsed.Value.Id] = parsed.Value.Version;
            }
        }

        if (observed.Count == 0 && assetsPath.Length > 0 && File.Exists(assetsPath))
        {
            observed = ReadFromAssets(assetsPath);
        }

        List<PackageNode> direct = [];
        foreach (string name in declared)
        {
            string version = declaredVersions.TryGetValue(name, out string? found)
                ? found
                : observed.TryGetValue(name, out string? byPath) ? byPath : "";
            direct.Add(new PackageNode(name, version));
        }

        List<PackageNode> transitive = [];
        foreach (KeyValuePair<string, string> pair in observed)
        {
            bool isDirect = declared.Contains(pair.Key, StringComparer.OrdinalIgnoreCase);
            bool isFromReference = fromReferences.ContainsKey(pair.Key);
            if (!isDirect && !isFromReference)
            {
                transitive.Add(new PackageNode(pair.Key, pair.Value));
            }
        }

        List<PackageNode> referencePackages = [];
        foreach (KeyValuePair<string, string> pair in fromReferences)
        {
            bool isDirect = declared.Contains(pair.Key, StringComparer.OrdinalIgnoreCase);
            if (!isDirect)
            {
                string version = observed.TryGetValue(pair.Key, out string? byPath) ? byPath : pair.Value;
                referencePackages.Add(new PackageNode(pair.Key, version));
            }
        }

        direct.Sort(Compare);
        transitive.Sort(Compare);
        referencePackages.Sort(Compare);

        return new PackageGraphResult(
            fullPath,
            direct,
            transitive,
            referencePackages,
            assetsPath,
            assetsPath.Length == 0 || !File.Exists(assetsPath),
            IsStale(fullPath, assetsPath));
    }

    /// <summary>从 <c>ReferencePath</c> 的绝对路径里认出 NuGet 包（含被重定向过的缓存根）。</summary>
    internal static (string Id, string Version)? ParsePackagePath(string referencePath, string cacheRoot)
    {
        string normalized = referencePath.Replace('\\', '/').Trim();
        string root = cacheRoot.Replace('\\', '/').TrimEnd('/') + "/";
        if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] segments = normalized[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        // 至少要 <包名>/<版本>/<其余>，而且第二段必须真的像版本号 —— 否则像 <包>/lib/x.dll 这种也会被当成包
        if (segments.Length < 3 || !ComparableVersion.TryParse(segments[1], out _))
        {
            return null;
        }

        return (segments[0], segments[1]);
    }

    internal static IReadOnlyList<string> ReadDeclaredPackages(string csprojPath)
    {
        try
        {
            XDocument document = XDocument.Load(csprojPath);
            return document.Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<string> ReadProjectReferences(string csprojPath)
    {
        try
        {
            XDocument document = XDocument.Load(csprojPath);
            return document.Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static int Compare(PackageNode left, PackageNode right)
    {
        return string.Compare(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStale(string csprojPath, string assetsPath)
    {
        string[] candidates = [csprojPath];
        List<string> paths = [.. candidates];
        foreach (string input in Inputs)
        {
            string? nearest = ProjectViewer.FindNearestUpwards(Path.GetDirectoryName(csprojPath) ?? ".", input);
            if (nearest != null)
            {
                paths.Add(nearest);
            }
        }

        if (assetsPath.Length == 0 || !File.Exists(assetsPath))
        {
            return true;
        }

        DateTime assetsTime = File.GetLastWriteTimeUtc(assetsPath);
        return paths.Any(path => File.Exists(path) && File.GetLastWriteTimeUtc(path) > assetsTime);
    }

    private static MsBuildSnapshot Evaluate(string csprojPath)
    {
        string workingDirectory = Path.GetDirectoryName(csprojPath) ?? ".";
        List<string> arguments =
        [
            "msbuild",
            csprojPath,
            "-nologo",
            "-v:q",
            "-t:ResolveReferences",
            "-getProperty:ProjectAssetsFile,TargetFramework",
            "-getItem:ReferencePath,PackageReference",
        ];

        try
        {
            CommandResult result = CommandRunner.Run("dotnet", arguments, workingDirectory, 180);
            if (!result.Succeeded)
            {
                return MsBuildSnapshot.Empty;
            }

            using JsonDocument document = MsBuildEvaluator.ExtractJsonDocument(result.Output);
            JsonElement root = document.RootElement;

            string assetsPath = GetString(root, "ProjectAssetsFile") ?? "";
            List<string> referencePaths = [];
            Dictionary<string, string> declaredVersions = new(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("Items", out JsonElement items))
            {
                referencePaths.AddRange(ReadItems(items, "ReferencePath", "Identity"));

                foreach (JsonElement item in ReadItemElements(items, "PackageReference"))
                {
                    string? name = GetString(item, "Identity") ?? GetString(item, "FullPath");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string version = GetString(item, "Version") ?? GetString(item, "VersionOverride") ?? "";
                    declaredVersions[name] = version;
                }
            }

            return new MsBuildSnapshot(assetsPath, referencePaths, declaredVersions);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or JsonException)
        {
            return MsBuildSnapshot.Empty;
        }
    }

    private static IEnumerable<string> ReadItems(JsonElement items, string name, string field)
    {
        foreach (JsonElement item in ReadItemElements(items, name))
        {
            string? value = GetString(item, field);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static List<JsonElement> ReadItemElements(JsonElement items, string name)
    {
        List<JsonElement> result = [];
        if (items.ValueKind != JsonValueKind.Object
            || !items.TryGetProperty(name, out JsonElement array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            result.Add(item);
        }

        return result;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    /// <summary>
    /// 兜底：从 <c>project.assets.json</c> 取包。
    /// 只认**真正参与编译**的包（在 targets 里有 compile 资产），
    /// 否则只被 runtime 引用的包也会被算进"依赖传递包"。
    /// </summary>
    private static Dictionary<string, string> ReadFromAssets(string assetsPath)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assetsPath));
            if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries)
                || libraries.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            HashSet<string> compiled = ReadCompileLibraries(document.RootElement);
            foreach (JsonProperty library in libraries.EnumerateObject())
            {
                string? type = GetString(library.Value, "type");
                if (type == null || !type.Equals("package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!compiled.Contains(library.Name))
                {
                    continue;
                }

                int slash = library.Name.IndexOf('/');
                if (slash <= 0)
                {
                    continue;
                }

                // 同一个包出现多个版本时只留一条（展示用；真实版本仍以还原结果为准）
                result[library.Name[..slash]] = library.Name[(slash + 1)..];
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return result;
        }

        return result;
    }

    /// <summary>targets 里带 compile 资产的库（形如 <c>Newtonsoft.Json/13.0.3</c>）。</summary>
    private static HashSet<string> ReadCompileLibraries(JsonElement root)
    {
        HashSet<string> compiled = new(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Object)
        {
            return compiled;
        }

        foreach (JsonProperty target in targets.EnumerateObject())
        {
            if (target.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if (library.Value.TryGetProperty("compile", out JsonElement compile) && compile.ValueKind == JsonValueKind.Object)
                {
                    compiled.Add(library.Name);
                }
            }
        }

        return compiled;
    }

    private sealed record MsBuildSnapshot(string AssetsPath, IReadOnlyList<string> ReferencePaths, Dictionary<string, string> DeclaredVersions)
    {
        public static MsBuildSnapshot Empty { get; } = new("", [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}
