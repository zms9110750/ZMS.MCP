using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace ZMS.MCP.Csharp.NuGet;

/// <summary>
/// nuget.org 的在线查询：模糊搜索、版本列表、nuspec、readme。
/// **一律走 NuGet 官方协议栈**（NuGet.Protocol）：搜索与版本列表走协议资源；
/// nuspec / readme 则把 .nupkg 下到**内存流**（绝不写盘），再用 PackageArchiveReader 从 zip 里取
/// —— 线上只有 zip，不像本地缓存那样已经解压好。
/// 所有调用都可能因为网络失败，调用方要准备好降级到「只有本地」的结果。
/// </summary>
public static class NuGetOnline
{
    /// <summary>搜索每页条数（与现有语义一致）。</summary>
    public const int PageSize = 15;

    private const string ServiceIndex = "https://api.nuget.org/v3/index.json";

    private static readonly SourceRepository Source = Repository.Factory.GetCoreV3(ServiceIndex);

    /// <summary>模糊搜索包名，返回 (包名, 版本) 列表。</summary>
    public static IReadOnlyList<(string Id, string Version)> SearchPackages(string keyword, int page)
    {
        PackageSearchResource resource = GetResource<PackageSearchResource>();
        SearchFilter filter = new(includePrerelease: false);
        IEnumerable<IPackageSearchMetadata> found = resource.SearchAsync(
            keyword,
            filter,
            Math.Max(page, 0) * PageSize,
            PageSize,
            NullLogger.Instance,
            CancellationToken.None).GetAwaiter().GetResult();

        List<(string, string)> results = [];
        foreach (IPackageSearchMetadata item in found)
        {
            results.Add((item.Identity.Id, item.Identity.Version.ToNormalizedString()));
        }

        return results;
    }

    /// <summary>某个包在 nuget.org 上的全部版本（升序，由协议资源给出）。</summary>
    public static IReadOnlyList<ComparableVersion> Versions(string packageName)
    {
        FindPackageByIdResource resource = GetResource<FindPackageByIdResource>();
        using SourceCacheContext cache = new();
        IEnumerable<NuGetVersion> versions = resource.GetAllVersionsAsync(
            packageName,
            cache,
            NullLogger.Instance,
            CancellationToken.None).GetAwaiter().GetResult();

        List<ComparableVersion> results = [];
        foreach (NuGetVersion version in versions)
        {
            if (ComparableVersion.TryParse(version.ToNormalizedString(), out ComparableVersion? parsed) && parsed != null)
            {
                results.Add(parsed);
            }
        }

        return results;
    }

    /// <summary>取线上包里的 nuspec 原文（下到内存再读）；拿不到返回 null。</summary>
    public static string? FetchNuspec(string packageName, string version)
    {
        return ReadFromPackage(packageName, version, ReadNuspec);
    }

    /// <summary>取线上包里的 readme（下到内存再读 zip）；拿不到返回 null。</summary>
    public static string? FetchReadme(string packageName, string version, string? readmeName)
    {
        // readmeName 只是兜底：nuspec 里声明了 readme 时，GetReadme() 会直接按声明取。
        return ReadFromPackage(packageName, version, reader => ReadReadme(reader, readmeName));
    }

    private static string ReadNuspec(PackageArchiveReader reader)
    {
        using Stream nuspec = reader.GetNuspec();
        using StreamReader text = new(nuspec);
        return text.ReadToEnd();
    }

    private static string? ReadReadme(PackageArchiveReader reader, string? readmeName)
    {
        // nuspec 里声明的 readme（<readme>docs\README.md</readme>）优先，其次用调用方给的兜底名。
        string declared = reader.NuspecReader.GetMetadataValue("readme");
        string wanted = !string.IsNullOrWhiteSpace(declared) ? declared : (readmeName ?? "");
        if (string.IsNullOrWhiteSpace(wanted))
        {
            wanted = "README.md";
        }

        wanted = wanted.Replace('\\', '/').TrimStart('/');
        string wantedFile = Path.GetFileName(wanted);
        foreach (string name in reader.GetFiles())
        {
            string candidate = name.Replace('\\', '/');
            if (!candidate.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(candidate).Equals(wantedFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using Stream stream = reader.GetStream(name);
            using StreamReader text = new(stream);
            return text.ReadToEnd();
        }

        return null;
    }

    /// <summary>
    /// 把 .nupkg 下到**内存流**（不写盘），再交给 <paramref name="read"/> 从 zip 里取内容。
    /// </summary>
    private static string? ReadFromPackage(string packageName, string version, Func<PackageArchiveReader, string?> read)
    {
        if (!NuGetVersion.TryParse(version, out NuGetVersion? parsed) || parsed == null)
        {
            return null;
        }

        FindPackageByIdResource resource = GetResource<FindPackageByIdResource>();
        using SourceCacheContext cache = new();
        using MemoryStream package = new();
        bool copied = resource.CopyNupkgToStreamAsync(
            packageName,
            parsed,
            package,
            cache,
            NullLogger.Instance,
            CancellationToken.None).GetAwaiter().GetResult();
        if (!copied)
        {
            return null;
        }

        package.Position = 0;
        using PackageArchiveReader reader = new(package);
        return read(reader);
    }

    private static T GetResource<T>()
        where T : class, INuGetResource
    {
        return Source.GetResourceAsync<T>().GetAwaiter().GetResult()!;
    }
}
