using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>NuGet 漏洞审计的警告码：NU1901–NU1904。</summary>
    private static readonly Regex AuditWarningRegex = new("NU190[1-4]", RegexOptions.Compiled);

    public static string Install(
        string csprojPath,
        IReadOnlyList<PackageRequest> requests,
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
            VersionChoice choice = ResolveVersion(request.Name, request.Version, allowPrerelease, vulnerabilities);
            direct[request.Name] = choice.Version;
            if (choice.Vulnerable)
            {
                vulnerableDirect.Add($"{request.Name} {choice.Version}");
            }
            else if (choice.IndexUnavailable)
            {
                // 索引没拿到：这个版本没做过漏洞核对，必须让人看见，不能默默当"安全"
                vulnerableDirect.Add($"{request.Name} {choice.Version}（漏洞索引不可用，未经核对）");
            }
        }

        Dictionary<string, string> graph = BuildGraph(direct, allowPrerelease, vulnerabilities, out List<string> upgraded);

        // 参数里要了、但被**别的参数包**的依赖传递满足的包：这一包不直接引入。
        // （需求：只有出现在参数里的包才可能进「因为被引用而未直接引入」这一类）
        Dictionary<string, HashSet<string>> introducedBy = BuildIntroductions(direct, allowPrerelease, vulnerabilities);
        List<string> coveredByOthers = [];
        foreach (string name in direct.Keys.ToList())
        {
            bool covered = introducedBy.Any(pair =>
                !pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                && pair.Value.Contains(name)
                && VersionSatisfied(graph, direct, name));
            if (covered)
            {
                coveredByOthers.Add(name);
                direct.Remove(name);
            }
        }

        coveredByOthers.Sort(StringComparer.OrdinalIgnoreCase);

        // 只对顶级包进行引入（= 调用方要的那几个）。传递包不单独引入，最终版本以真实还原结果为准；
        // 只有当依赖图要求的版本**比参数更高**时，才把参数版本抬上去（否则还原会降级失败）。
        List<string> pinned = [];
        foreach (KeyValuePair<string, string> pair in graph)
        {
            if (!direct.TryGetValue(pair.Key, out string? requested) || requested.Length == 0)
            {
                continue;
            }

            if (ComparableVersion.TryParse(requested, out ComparableVersion? requestedVersion)
                && requestedVersion != null
                && ComparableVersion.TryParse(pair.Value, out ComparableVersion? graphVersion)
                && graphVersion != null
                && graphVersion.CompareTo(requestedVersion) > 0)
            {
                pinned.Add($"{pair.Key}：参数要 {requested}，依赖图要 {pair.Value} → 采用 {pair.Value}");
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
        Append(builder, coveredByOthers);
        builder.AppendLine();
        builder.AppendLine("# 以下包因为漏洞被换成了别的版本");
        Append(builder, upgraded);
        builder.AppendLine();
        builder.AppendLine("# 以下包被本次传递引入");
        Append(builder, graph.Keys.Where(name =>
            !direct.ContainsKey(name)
            && !coveredByOthers.Contains(name, StringComparer.OrdinalIgnoreCase)
            && !alreadyPresent.Contains(name, StringComparer.OrdinalIgnoreCase)));
        builder.AppendLine();
        builder.AppendLine("# 以下传递引入包原本就存在");
        Append(builder, graph.Keys.Where(name =>
            !coveredByOthers.Contains(name, StringComparer.OrdinalIgnoreCase)
            && alreadyPresent.Contains(name, StringComparer.OrdinalIgnoreCase)));
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

        // 索引有滞后：落盘后再用 restore 的漏洞审计（NU1901–NU1904）核一遍。
        // 传递依赖要覆盖就得带 NuGetAuditMode=all —— 文档说"这一步不能省"。
        builder.AppendLine();
        builder.AppendLine("## 落盘后核对（NU1901–NU1904，NuGetAuditMode=all）");
        AppendAudit(builder, fullPath, workingDirectory);
        return builder.ToString();
    }

    /// <summary>
    /// 移除：**先建图**（本地已有依赖图）→ 从图里切掉要移除的直接包、算出不再被需要的传递包 → 与移除前的图比对；
    /// 执行命令行之后，再用**还原结果**复核一次（切图是推算，实际以还原为准）。
    /// </summary>
    public static string Remove(string csprojPath, IReadOnlyList<string> packageNames)
    {
        if (packageNames.Count == 0)
        {
            throw new ArgumentException("至少要给一个包名。");
        }

        string fullPath = ProjectViewer.ResolveProjectFile(csprojPath);
        string workingDirectory = Path.GetDirectoryName(fullPath) ?? ".";
        PackageGraphResult before = PackageGraph.Build(fullPath);

        // 先按本地依赖图切图算一遍（建图 → 切掉要移除的直接包 → 与移除前的图比对）
        List<string> disappeared = [.. ComputeDisappearingByGraph(before, packageNames)];

        foreach (string name in packageNames)
        {
            CommandResult result = CommandRunner.Run("dotnet", ["remove", fullPath, "package", name], workingDirectory);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"dotnet remove package {name} 失败（退出码 {result.ExitCode}）：\n{result.Output}");
            }
        }

        // 跑过之后用**还原结果**复核一遍：切图是推算，实际以还原为准
        PackageGraphResult after = PackageGraph.Build(fullPath);
        HashSet<string> beforeNames = [.. before.Direct.Select(node => node.Id), .. before.Transitive.Select(node => node.Id)];
        HashSet<string> afterNames = [.. after.Direct.Select(node => node.Id), .. after.Transitive.Select(node => node.Id)];
        HashSet<string> removedDirectly = new(packageNames, StringComparer.OrdinalIgnoreCase);
        disappeared =
        [
            .. beforeNames
                .Where(name => !afterNames.Contains(name) && !removedDirectly.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
        ];

        StringBuilder builder = new();
        builder.AppendLine("# 本次移除包");
        Append(builder, packageNames);
        builder.AppendLine();
        builder.AppendLine("# 本次移除的依赖传递包");
        // 被直接移除的顶级包已经列在「本次移除包」里，不再重复出现在这里（间接移除就是间接移除）
        Append(builder, disappeared);

        return builder.ToString();
    }

    /// <summary>按本地依赖图切图：保留「没被移除的直接包 + 项目引用带来的顶级包」，看哪些包再也到不了。</summary>
    internal static IReadOnlyList<string> ComputeDisappearingByGraph(
        PackageGraphResult before,
        IReadOnlyList<string> removedNames)
    {
        List<PackageNode> retainedRoots =
        [
            .. before.Direct.Where(node => !removedNames.Contains(node.Id, StringComparer.OrdinalIgnoreCase)),
        ];
        retainedRoots.AddRange(before.FromProjectReferences);
        List<PackageNode> known = [.. before.Direct, .. before.Transitive];
        return ComputeDisappearingPackages(retainedRoots, known, removedNames, ReadNuspecDependencies);
    }

    /// <summary>
    /// 切图的核心（纯函数，便于单测）：从保留的根出发按依赖闭包扩张，凡与「移除前的图」比对后不再可达、
    /// 又不属于「本次直接移除」的包，就是会一并消失的传递包。
    /// </summary>
    /// <param name="retainedRoots">移除之后仍然保留的顶点（未被移除的直接包 + 项目引用带来的顶级包）。</param>
    /// <param name="knownPackages">移除前的图里出现过的包（含版本；版本用来定位 nuspec）。</param>
    /// <param name="removedNames">本次要移除的包名（它们本身不算「依赖传递包消失」）。</param>
    /// <param name="readDependencies">读某个包的依赖（生产里读本地 nuspec；测试可注入假图）。</param>
    internal static IReadOnlyList<string> ComputeDisappearingPackages(
        IReadOnlyList<PackageNode> retainedRoots,
        IReadOnlyList<PackageNode> knownPackages,
        IReadOnlyList<string> removedNames,
        Func<string, string, IReadOnlyList<(string Id, string Range)>> readDependencies)
    {
        Dictionary<string, string> known = new(StringComparer.OrdinalIgnoreCase);
        foreach (PackageNode node in knownPackages)
        {
            known[node.Id] = node.Version;
        }

        HashSet<string> reachable = new(StringComparer.OrdinalIgnoreCase);
        Queue<(string Id, string Version)> queue = new();
        foreach (PackageNode root in retainedRoots)
        {
            // 保留的根自己当然还在
            reachable.Add(root.Id);
            queue.Enqueue((root.Id, root.Version));
        }

        while (queue.Count > 0)
        {
            (string id, string version) = queue.Dequeue();
            foreach ((string dependencyId, string _) in readDependencies(id, version))
            {
                if (!reachable.Add(dependencyId))
                {
                    continue;
                }

                queue.Enqueue((dependencyId, known.TryGetValue(dependencyId, out string? found) ? found : ""));
            }
        }

        return
        [
            .. known.Keys
                .Where(id => !removedNames.Contains(id, StringComparer.OrdinalIgnoreCase) && !reachable.Contains(id))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>选版本的结果：版本号 + 是否已知有漏洞 + 是否因为索引不可用而没核对过。</summary>
    internal sealed record VersionChoice(string Version, bool Vulnerable, bool IndexUnavailable);

    /// <summary>
    /// 选版本：显式给了就用它（<c>*</c> 表示要最新的）；没给就先用本地缓存最新，
    /// 本地没有这个包再去看线上；最后过一遍漏洞索引，有漏洞就换成最新安全版。
    /// </summary>
    internal static VersionChoice ResolveVersion(
        string packageName,
        string requested,
        bool allowPrerelease,
        VulnerabilityIndexData vulnerabilities)
    {
        string trimmed = (requested ?? "").Trim();
        bool wantsLatest = trimmed.Length == 0 || trimmed == "*";
        if (!wantsLatest)
        {
            string explicitVersion = trimmed.TrimStart('^', '~');
            bool vulnerable = ComparableVersion.TryParse(explicitVersion, out ComparableVersion? parsed)
                && parsed != null
                && VulnerabilityIndex.IsVulnerable(vulnerabilities, packageName, parsed);
            return new VersionChoice(explicitVersion, vulnerable, IndexUnavailable: !vulnerabilities.Available);
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

        // 按审查规则收窄候选：以**候选里最高的那个**的大版本为下界（往上至少没漏洞；
        // 往下换更低版本入不敷出），且不含预览版。
        local.Sort();
        ComparableVersion highest = local[^1];
        List<ComparableVersion> selectable = VulnerabilityIndex.SelectableCandidates(highest, local);

        ComparableVersion? safe = VulnerabilityIndex.PickLatestSafe(vulnerabilities, packageName, selectable);
        if (safe == null)
        {
            // 不退回有漏洞的版本：直接报错，要装就由调用方显式指定版本。
            string reason = vulnerabilities.Available ? "" : "（漏洞索引不可用，无法核对安全版本）";
            throw new InvalidOperationException(
                $"{packageName} 在 {highest.Numbers[0]}.x 及以上找不到无漏洞的正式版{reason}。"
                + $"要装就显式指定版本，例如 {packageName}@{highest.Original}。");
        }

        return new VersionChoice(safe.Original, Vulnerable: false, IndexUnavailable: false);
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

    /// <summary>
    /// 每个参数包的依赖闭包（不含它自己）。用来判断"参数里要了、但被别的参数包带进来"的包 ——
    /// 那些包不直接引入，归入「因为被引用而未直接引入」。
    /// </summary>
    private static Dictionary<string, HashSet<string>> BuildIntroductions(
        Dictionary<string, string> direct,
        bool allowPrerelease,
        VulnerabilityIndexData vulnerabilities)
    {
        Dictionary<string, HashSet<string>> introduced = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> root in direct)
        {
            HashSet<string> closure = new(StringComparer.OrdinalIgnoreCase);
            Queue<(string Id, string Version, int Depth)> queue = new();
            queue.Enqueue((root.Key, root.Value, 0));
            while (queue.Count > 0)
            {
                (string id, string version, int depth) = queue.Dequeue();
                if (depth >= MaxGraphDepth)
                {
                    continue;
                }

                foreach ((string dependencyId, string range) in ReadDependencies(id, version))
                {
                    string resolved = ResolveDependencyVersion(dependencyId, range, allowPrerelease, vulnerabilities, out _);
                    if (!closure.Add(dependencyId))
                    {
                        continue;
                    }

                    queue.Enqueue((dependencyId, resolved, depth + 1));
                }
            }

            introduced[root.Key] = closure;
        }

        return introduced;
    }

    /// <summary>依赖图给的版本是否已经满足参数要求的版本（没指定版本时，图里的版本就算满足）。</summary>
    private static bool VersionSatisfied(Dictionary<string, string> graph, Dictionary<string, string> requested, string name)
    {
        if (!requested.TryGetValue(name, out string? wanted) || wanted.Length == 0)
        {
            return true;
        }

        if (!graph.TryGetValue(name, out string? resolved) || resolved.Length == 0)
        {
            return false;
        }

        if (!ComparableVersion.TryParse(wanted.TrimStart('^', '~'), out ComparableVersion? wantedVersion) || wantedVersion == null)
        {
            // "*" 这类"要最新的"：依赖图给什么就用什么
            return true;
        }

        return ComparableVersion.TryParse(resolved, out ComparableVersion? resolvedVersion)
            && resolvedVersion != null
            && resolvedVersion.CompareTo(wantedVersion) >= 0;
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
    internal static IEnumerable<(string Id, string Range)> ReadDependencies(string packageName, string version)
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

    /// <summary>切图时要的依赖读取委托：包名 + 版本 → 该包的依赖列表。</summary>
    internal static IReadOnlyList<(string Id, string Range)> ReadNuspecDependencies(string packageName, string version)
    {
        return [.. ReadDependencies(packageName, version)];
    }

    /// <summary>
    /// 落盘后核对漏洞警告：<c>dotnet restore</c> 带 <c>NuGetAuditMode=all</c>（覆盖传递依赖），
    /// 把 NU1901–NU1904 摘出来。漏洞索引可能滞后，所以这一步不能省。
    /// </summary>
    private static void AppendAudit(StringBuilder builder, string projectPath, string workingDirectory)
    {
        CommandResult result;
        try
        {
            result = CommandRunner.Run(
                "dotnet",
                ["restore", projectPath, "-p:NuGetAuditMode=all", "--nologo"],
                workingDirectory,
                300);
        }
        catch (Exception exception) when (exception is TimeoutException or System.ComponentModel.Win32Exception)
        {
            builder.AppendLine($"- 核对未执行：{exception.Message}");
            return;
        }

        IReadOnlyList<string> warnings = ExtractAuditWarnings(result.Output);
        if (warnings.Count == 0)
        {
            builder.AppendLine(result.Succeeded
                ? "- 未发现 NU1901–NU1904（已带 NuGetAuditMode=all，覆盖传递依赖）。"
                : $"- restore 未成功（退出码 {result.ExitCode}），本次核对不完整。");
            return;
        }

        foreach (string warning in warnings)
        {
            builder.AppendLine("- " + warning);
        }
    }

    /// <summary>从 restore 输出里摘出 NU1901–NU1904 的行（纯函数，便于单测）。</summary>
    internal static IReadOnlyList<string> ExtractAuditWarnings(string output)
    {
        return
        [
            .. output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => AuditWarningRegex.IsMatch(line))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private static void Append(StringBuilder builder, IEnumerable<string> lines)    {
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
