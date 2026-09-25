using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ZMS.MCP.Csharp.NuGet;

namespace ZMS.MCP.Csharp.Tools;

/// <summary>
/// nuget 层：查询包 / 查询版本 / 查询元数据。
/// local 与 web 可以同时打开 —— 那时会额外描述"本地也存在"。
/// </summary>
[McpServerToolType]
public static class NuGetTools
{
    /// <summary>搜索 API 每页条数。</summary>
    private const int PageSize = NuGetOnline.PageSize;

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Search package ids by keyword. local = search the local NuGet cache, web = search nuget.org. " +
        "When both are on, packages that exist locally are marked with []. page starts at 0.")]
    public static string SearchPackages(
        [Description("Keyword (empty lists local packages)")] string packName,
        [Description("Page number, starts at 0 (web only)")] int page = 0,
        [Description("Search the local NuGet cache")] bool local = true,
        [Description("Search nuget.org")] bool web = false)
    {
        return ToolGuard.Run(() =>
        {
            if (!local && !web)
            {
                throw new InvalidOperationException("local 与 web 至少要开一个（两个都关就没有可查的来源）。");
            }

            // 只在开了 local 时才读本地缓存（Description 说 local = search the local cache）
            HashSet<string> localNames = local
                ? new HashSet<string>(NuGetCache.SearchPackages(packName), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> names = [];
            if (local)
            {
                names.AddRange(localNames);
            }

            StringBuilder builder = new();
            if (web)
            {
                IReadOnlyList<(string Id, string Version)> online = NuGetOnline.SearchPackages(packName, page);
                List<string> onlineNames = [.. online.Select(item => item.Id)];
                if (local)
                {
                    builder.AppendLine($"本页查询到 {onlineNames.Count} 个，[] 标记为本地也存在");
                    builder.AppendLine();
                    foreach (string name in onlineNames)
                    {
                        builder.AppendLine(localNames.Contains(name) ? $"[{name}]" : name);
                    }

                    return builder.ToString();
                }

                builder.AppendLine($"# 线上查询到 {onlineNames.Count} 个（第 {page + 1} 页，每页 {PageSize} 条）");
                builder.AppendLine();
                foreach ((string id, string version) in online)
                {
                    builder.AppendLine(version.Length == 0 ? id : $"{id} {version}");
                }

                return builder.ToString();
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            builder.AppendLine($"# 本地查询到 {names.Count} 个");
            builder.AppendLine();
            foreach (string name in names)
            {
                builder.AppendLine(name);
            }

            return builder.ToString();
        });
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "List versions of one exact package id. local = local cache, web = nuget.org. " +
        "verRange is a NuGet version range (parsed by the NuGet VersionRange API) and EVERY version inside it is listed, never just one. " +
        "empty = release versions only; '*' = all release versions (same as [0.0.0,9999.9999.9999]); '*-*' = include prerelease too; " +
        "range syntax such as '[13.0,14.0)' or a floating form such as '1.2.*' works as well. " +
        "The header reports how many prerelease versions were left out.")]
    public static string ListPackageVersions(
        [Description("Exact package id")] string packName,
        [Description("NuGet version range; empty = release only, '*' = all release, '*-*' = include prerelease")] string verRange = "",
        [Description("Query the local NuGet cache")] bool local = true,
        [Description("Query nuget.org")] bool web = false)
    {
        return ToolGuard.Run(() =>
        {
            VersionRangeFilter filter;
            try
            {
                filter = VersionRangeFilter.Parse(verRange);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException($"Bad version range: {exception.Message}");
            }

            if (!local && !web)
            {
                throw new InvalidOperationException("local 与 web 至少要开一个（两个都关就没有可查的来源）。");
            }

            // 只在开了 local 时才读本地缓存（Description 说 local = local cache）
            List<ComparableVersion> localVersions = local ? [.. NuGetCache.Versions(packName)] : [];
            List<ComparableVersion> versions = [.. localVersions];
            string source = "本地缓存";
            if (web)
            {
                try
                {
                    versions = [.. NuGetOnline.Versions(packName)];
                    source = local ? "本地缓存 + nuget.org" : "nuget.org";
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    source = $"本地缓存（线上取失败：{exception.Message}）";
                }
            }

            // 范围里符合的**全部**版本；预览版是否列出由 filter 决定
            List<ComparableVersion> inRange = [.. versions.Where(filter.MatchesRange)];
            List<ComparableVersion> shown = filter.IncludePrerelease
                ? inRange
                : [.. inRange.Where(version => !version.IsPrerelease)];
            shown.Sort();
            shown.Reverse();

            StringBuilder builder = new();
            builder.AppendLine($"# {packName}（{source}）");
            builder.AppendLine($"> 范围 {filter.Description}：共列出 {shown.Count} 个版本");
            if (!filter.IncludePrerelease)
            {
                // 挡掉的预览版要说出来，否则"少了几个"看起来像工具算错了
                int leftOut = inRange.Count - shown.Count;
                if (leftOut > 0)
                {
                    builder.AppendLine($"（另有 {leftOut} 个预览版落在范围内，未列出；verRange 用 `*-*` 才会列出）");
                }
            }

            builder.AppendLine();
            foreach (ComparableVersion version in shown)
            {
                bool existsLocally = localVersions.Any(localVersion => localVersion.Equals(version));
                builder.AppendLine(local && web && existsLocally ? $"[{version}]" : version.Original);
            }

            if (local && web)
            {
                builder.AppendLine();
                builder.AppendLine("（[] = 本地缓存里也存在）");
            }

            return builder.ToString();
        });
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Show a package's readme and nuspec. Looks in the local cache first, then nuget.org. " +
        "When neither has it, reports 'not found' together with the nuspec's projectUrl / repositoryUrl so you can look yourself.")]
    public static string GetPackageMetadata(
        [Description("Exact package id")] string packName,
        [Description("Version; empty = highest cached (or latest online)")] string ver = "")
    {
        return ToolGuard.Run(() =>
        {
            string version = ver.Trim();
            if (version.Length == 0)
            {
                IReadOnlyList<ComparableVersion> cached = NuGetCache.Versions(packName);
                version = cached.Count > 0 ? cached[0].Original : "";
            }

            PackageMetadata? local = version.Length == 0 ? null : NuGetCache.ReadMetadata(packName, version);
            if (local != null)
            {
                return Render(packName, version, "本地缓存", local.NuspecXml, local.Readme ?? NuGetCache.ReadReadmeFromPackage(packName, version), local.ProjectUrl, local.RepositoryUrl);
            }

            string? onlineNuspec = null;
            string? readmeName = null;
            try
            {
                onlineNuspec = NuGetOnline.FetchNuspec(packName, version);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                onlineNuspec = null;
            }

            if (onlineNuspec != null)
            {
                (string? project, string? repository, string? readme) = ReadNuspecHints(onlineNuspec);
                readmeName = readme;
                string? onlineReadme = null;
                try
                {
                    onlineReadme = NuGetOnline.FetchReadme(packName, version, readmeName);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    onlineReadme = null;
                }

                return Render(packName, version, "nuget.org", onlineNuspec, onlineReadme, project, repository);
            }

            return $"# {packName} {version}\n未找到（本地缓存与 nuget.org 都没有）。";
        });
    }

    private static string Render(
        string packName,
        string version,
        string source,
        string nuspecXml,
        string? readme,
        string? projectUrl,
        string? repositoryUrl)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# {packName} {version}（{source}）");
        builder.AppendLine();
        builder.AppendLine("## nuspec");
        builder.AppendLine("```xml");
        builder.AppendLine(nuspecXml.Trim());
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("## readme");
        if (string.IsNullOrWhiteSpace(readme))
        {
            builder.AppendLine("未找到 readme。可以自己去这里看：");
            builder.AppendLine($"- projectUrl: {projectUrl ?? "(nuspec 里没有)"}");
            builder.AppendLine($"- repositoryUrl: {repositoryUrl ?? "(nuspec 里没有)"}");
        }
        else
        {
            builder.AppendLine(readme.Trim());
        }

        return builder.ToString();
    }

    private static (string? ProjectUrl, string? RepositoryUrl, string? Readme) ReadNuspecHints(string nuspecXml)
    {
        try
        {
            System.Xml.Linq.XElement? metadata = System.Xml.Linq.XDocument.Parse(nuspecXml)
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "metadata");
            if (metadata == null)
            {
                return (null, null, null);
            }

            string? project = metadata.Elements().FirstOrDefault(element => element.Name.LocalName == "projectUrl")?.Value.Trim();
            string? repository = metadata.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "repository")?.Attribute("url")?.Value;
            string? readme = metadata.Elements().FirstOrDefault(element => element.Name.LocalName == "readme")?.Value.Trim();
            return (
                string.IsNullOrEmpty(project) ? null : project,
                string.IsNullOrEmpty(repository) ? null : repository,
                string.IsNullOrEmpty(readme) ? null : readme);
        }
        catch (System.Xml.XmlException)
        {
            return (null, null, null);
        }
    }
}
