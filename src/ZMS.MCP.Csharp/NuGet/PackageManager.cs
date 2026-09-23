using System.Text;
using System.Xml.Linq;
using ZMS.MCP.Csharp.Project;

namespace ZMS.MCP.Csharp.NuGet;

/// <summary>一个要安装的包请求（版本为空表示自动选）。</summary>
public sealed record PackageRequest(string Name, string Version);

/// <summary>
/// 安装 / 移除 NuGet 包。
/// 决定用什么版本靠**自建图 + 漏洞索引**（落盘前就能算），
/// 但最终版本以真实还原结果为准（见 <see cref="PackageGraph"/>），
/// 真正改盘一律走命令行：项目文件该怎么改只有 MSBuild 知道。
/// </summary>
public static class PackageManager
{
    /// <summary>依赖图递归的最大深度（防环 + 防呆）。</summary>
    private const int MaxGraphDepth = 24;

    public static string Install(
        string csprojPath,
        IReadOnlyList<PackageRequest> requests,
        bool dryRun = false,
        bool allowPrerelease = false)
    {
        if (requests.Count == 0)
        {
            throw new ArgumentException("至少要给一个包。");
        }

        string fullPath = ProjectViewer.ResolveProjectFile(csprojPath);
        string workingDirectory = Path.GetDirectoryName(fullPath) ?? ".";
        VulnerabilityIndexData vulnerabilities = VulnerabilityIndex.Load();

        Dictionary<string, string> direct = new(StringComparer.OrdinalIgnoreCase);
        List<string> vulnerableDirect = [];
        foreach (PackageRequest request in requests)
        {
            string version = ResolveVersion(request.Name, request.Version, allowPrerelease, vulnerabilities);
            direct[request.Name] = version;
            if (ComparableVersion.TryParse(version, out ComparableVersion? parsed)
                && parsed != null
                && VulnerabilityIndex.IsVulnerable(vulnerabilities, request.Name, parsed))
            {
                vulnerableDirect.Add($"{request.Name} {version}");
            }
        }

        Dictionary<string, string> graph = BuildGraph(direct, allowPrerelease, vulnerabilities, out List<string> upgraded);

        // 只引顶级包；传递包与参数要求的版本不一致时把它提升为直接引用（用直接依赖钉住版本）
        List<string> pinned = [];
        foreach (KeyValuePair<string, string> pair in graph)
        {
            if (direct.TryGetValue(pair.Key, out string? requested))
            {
                if (requested.Length > 0 && !requested.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    pinned.Add($"{pair.Key}：参数要 {requested}，依赖图给 {pair.Value}（保留参数要求的版本）");
                }

                continue;
            }

            if (IsPromotable(pair.Key))
            {
                pinned.Add($"{pair.Key} {pair.Value}（传递依赖，版本与参数不一致，提升为直接引用）");
                direct[pair.Key] = pair.Value;
            }
        }

        List<string> alreadyPresent = PackageGraph.Build(fullPath).Transitive.Select(node => node.Id).ToList();

        StringBuilder builder = new();
        builder.AppendLine("# 以下直接引入包是漏洞的");
        Append(builder, vulnerableDirect);
        builder.AppendLine();
        builder.AppendLine("# 引入以下包");
        Append(builder, direct.Select(pair => pair.Key + (pair.Value.Length == 0 ? "" : " " + pair.Value)));
        builder.AppendLine();
        builder.AppendLine("# 以下包因为被引用而未直接引入");
        Append(builder, graph.Keys.Where(name => !direct.ContainsKey(name)));
        builder.AppendLine();
        builder.AppendLine("# 以下包因为漏洞被自动升级引入");
        Append(builder, upgraded);
        builder.AppendLine();
        builder.AppendLine("# 以下包被本次传递引入");
        Append(builder, graph.Keys.Where(name => !direct.ContainsKey(name) && !alreadyPresent.Contains(name, StringComparer.OrdinalIgnoreCase)));
        builder.AppendLine();
        builder.AppendLine("# 以下传递引入包原本就存在");
        Append(builder, graph.Keys.Where(name => alreadyPresent.Contains(name, StringComparer.OrdinalIgnoreCase)));
        if (pinned.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("# 版本冲突被钉住的包");
            Append(builder, pinned);
        }

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine("以下用命令行改盘，**不进事务**（立即生效、不可回滚）：");
        foreach (KeyValuePair<string, string> pair in direct)
        {
            string versionArgument = pair.Value.Length == 0 ? "" : $" --version {pair.Value}";
            builder.AppendLine($"- dotnet add \"{Path.GetFileName(fullPath)}\" package {pair.Key}{versionArgument}");
            if (dryRun)
            {
                continue;
            }

            List<string> arguments = ["add", fullPath, "package", pair.Key];
            if (pair.Value.Length > 0)
            {
                arguments.Add("--version");
                arguments.Add(pair.Value);
            }

            CommandResult result = CommandRunner.Run("dotnet", arguments, workingDirectory);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"dotnet add package {pair.Key} 失败（退出码 {result.ExitCode}）：\n{result.Output}");
            }
        }

        if (dryRun)
        {
            builder.AppendLine();
            builder.AppendLine("（预演，未真正执行。）");
        }

        return builder.ToString();
    }

    /// <summary>移除前建图 → 命令行移除 → 移除后再建图 → 报告差异。</summary>
    public static string Remove(string csprojPath, IReadOnlyList<string> packageNames, bool dryRun = false)
    {
        if (packageNames.Count == 0)
        {
            throw new ArgumentException("至少要给一个包名。");
        }

        string fullPath = ProjectViewer.ResolveProjectFile(csprojPath);
        string workingDirectory = Path.GetDirectoryName(fullPath) ?? ".";
        PackageGraphResult before = PackageGraph.Build(fullPath);

        if (!dryRun)
        {
            foreach (string name in packageNames)
            {
                CommandResult result = CommandRunner.Run("dotnet", ["remove", fullPath, "package", name], workingDirectory);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException($"dotnet remove package {name} 失败（退出码 {result.ExitCode}）：\n{result.Output}");
                }
            }
        }

        PackageGraphResult after = dryRun ? before : PackageGraph.Build(fullPath);
        HashSet<string> beforeNames = [.. before.Direct.Select(node => node.Id), .. before.Transitive.Select(node => node.Id)];
        HashSet<string> afterNames = [.. after.Direct.Select(node => node.Id), .. after.Transitive.Select(node => node.Id)];

        StringBuilder builder = new();
        builder.AppendLine("# 本次移除包");
        Append(builder, packageNames);
        builder.AppendLine();
        builder.AppendLine("# 本次移除的依赖传递包");
        Append(builder, beforeNames.Where(name => !afterNames.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        if (dryRun)
        {
            builder.AppendLine();
            builder.AppendLine("（预演，未真正执行；上面「依赖传递包」一栏按实际移除后的图算，预演时为 0。）");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 选版本：显式给了就用它（<c>*</c> 表示要最新的）；没给就先用本地缓存最新，
    /// 本地没有这个包再去看线上；最后过一遍漏洞索引，有漏洞就换成最新安全版。
    /// </summary>
    internal static string ResolveVersion(
        string packageName,
        string requested,
        bool allowPrerelease,
        VulnerabilityIndexData vulnerabilities)
    {
        string trimmed = (requested ?? "").Trim();
        bool wantsLatest = trimmed.Length == 0 || trimmed == "*";
        if (!wantsLatest)
        {
            return trimmed.TrimStart('^', '~');
        }

        List<ComparableVersion> local = [.. NuGetCache.Versions(packageName)];
        if (!allowPrerelease)
        {
            local = [.. local.Where(version => !version.IsPrerelease)];
        }

        if (local.Count == 0)
        {
            try
            {
                local = [.. NuGetOnline.Versions(packageName).Where(version => allowPrerelease || !version.IsPrerelease)];
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                throw new InvalidOperationException($"本地缓存没有 {packageName}，也无法从 nuget.org 取版本：{exception.Message}");
            }
        }

        if (local.Count == 0)
        {
            throw new InvalidOperationException($"找不到 {packageName} 的任何可用版本。");
        }

        ComparableVersion? safe = VulnerabilityIndex.PickLatestSafe(vulnerabilities, packageName, local);
        if (safe != null)
        {
            return safe.Original;
        }

        local.Sort();
        return local[^1].Original;
    }

    /// <summary>
    /// 自建图：从直接引用出发，按 nuspec 里的依赖一层层展开，只用于**决策与展示**。
    /// 依赖组不区分目标框架（本地 nuspec 里各组的依赖会被合并考虑），必要时取版本更高的一条。
    /// </summary>
    private static Dictionary<string, string> BuildGraph(
        Dictionary<string, string> direct,
        bool allowPrerelease,
        VulnerabilityIndexData vulnerabilities,
        out List<string> upgraded)
    {
        Dictionary<string, string> graph = new(direct, StringComparer.OrdinalIgnoreCase);
        upgraded = [];
        Queue<(string Id, string Version, int Depth)> queue = new();
        foreach (KeyValuePair<string, string> pair in direct)
        {
            queue.Enqueue((pair.Key, pair.Value, 0));
        }

        while (queue.Count > 0)
        {
            (string id, string version, int depth) = queue.Dequeue();
            if (depth >= MaxGraphDepth)
            {
                continue;
            }

            foreach ((string dependencyId, string range) in ReadDependencies(id, version))
            {
                string resolved = ResolveDependencyVersion(dependencyId, range, allowPrerelease, vulnerabilities, out bool wasUpgraded);
                if (wasUpgraded)
                {
                    upgraded.Add($"{dependencyId} → {resolved}");
                }

                if (graph.TryGetValue(dependencyId, out string? existing)
                    && ComparableVersion.TryParse(existing, out ComparableVersion? existingVersion)
                    && ComparableVersion.TryParse(resolved, out ComparableVersion? resolvedVersion)
                    && existingVersion != null
                    && resolvedVersion != null
                    && existingVersion.CompareTo(resolvedVersion) >= 0)
                {
                    continue;
                }

                graph[dependencyId] = resolved;
                queue.Enqueue((dependencyId, resolved, depth + 1));
            }
        }

        return graph;
    }

    private static string ResolveDependencyVersion(
        string dependencyId,
        string range,
        bool allowPrerelease,
        VulnerabilityIndexData vulnerabilities,
        out bool wasUpgraded)
    {
        wasUpgraded = false;
        IReadOnlyList<ComparableVersion> local = NuGetCache.Versions(dependencyId);
        List<ComparableVersion> candidates = [.. local.Where(version => allowPrerelease || !version.IsPrerelease)];

        VersionRange parsed;
        try
        {
            parsed = VersionRange.Parse(range);
        }
        catch (ArgumentException)
        {
            // 依赖里写的区间读不懂时，放宽成"任意版本"（只是自建图，最终以还原结果为准）
            parsed = VersionRange.Any(allowPrerelease);
        }

        List<ComparableVersion> matching = [.. candidates.Where(parsed.Contains)];
        List<ComparableVersion> pool = matching.Count > 0 ? matching : candidates;
        if (pool.Count == 0)
        {
            return range.Trim('[', ']', '(', ')').Split(',')[0].Trim();
        }

        pool.Sort();
        ComparableVersion highest = pool[^1];
        ComparableVersion? safe = VulnerabilityIndex.PickLatestSafe(vulnerabilities, dependencyId, pool);
        if (safe == null)
        {
            // 候选里没有安全版本（或有未知风险）：不乱升级，保留依赖要的最高版本
            return highest.Original;
        }

        wasUpgraded = safe.CompareTo(highest) != 0 || VulnerabilityIndex.IsVulnerable(vulnerabilities, dependencyId, highest);
        return safe.Original;
    }

    /// <summary>读本地 nuspec 里的依赖；本地没有就返回空（宁可少算，也不瞎猜）。</summary>
    private static IEnumerable<(string Id, string Range)> ReadDependencies(string packageName, string version)
    {
        string? nuspecPath = NuGetCache.NuspecPath(packageName, version);
        if (nuspecPath == null)
        {
            yield break;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(nuspecPath);
        }
        catch (System.Xml.XmlException)
        {
            yield break;
        }

        foreach (XElement dependency in document.Descendants().Where(element => element.Name.LocalName == "dependency"))
        {
            string? id = dependency.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            yield return (id, dependency.Attribute("version")?.Value ?? "");
        }
    }

    /// <summary>只提升真正的主包：SDK/分析器/构建包会改变项目行为，不自动加进 csproj。</summary>
    private static bool IsPromotable(string packageName)
    {
        return !packageName.EndsWith(".sdk", StringComparison.OrdinalIgnoreCase)
            && !packageName.Contains("analyzers", StringComparison.OrdinalIgnoreCase)
            && !packageName.Contains("build", StringComparison.OrdinalIgnoreCase);
    }

    private static void Append(StringBuilder builder, IEnumerable<string> lines)
    {
        bool any = false;
        foreach (string line in lines)
        {
            builder.AppendLine(line);
            any = true;
        }

        if (!any)
        {
            builder.AppendLine("（无）");
        }
    }
}
