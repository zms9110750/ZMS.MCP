using System.IO.Compression;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using ZMS.MCP.Csharp.Project;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.NuGet;

/// <summary>一个包在本地缓存里的元数据（nuspec + readme）。</summary>
public sealed record PackageMetadata(
    string Id,
    string Version,
    string? Description,
    string? ProjectUrl,
    string? RepositoryUrl,
    string? Readme,
    string NuspecXml);

/// <summary>
/// 本地 NuGet 包缓存：包名、版本列表、nuspec、readme。
/// 在线的部分在 <see cref="NuGetOnline"/>。
/// </summary>
public static class NuGetCache
{
    /// <summary>缓存根（与 <c>NUGET_PACKAGES</c> 一致）。</summary>
    public static string Root()
    {
        return NuGetXmlDocumentation.CacheRoot();
    }

    /// <summary>按关键字找本地已缓存的包（包目录名包含关键字，不区分大小写）。</summary>
    public static IReadOnlyList<string> SearchPackages(string keyword, bool exactOnly = false)
    {
        string root = Root();
        if (!Directory.Exists(root))
        {
            return [];
        }

        string trimmed = (keyword ?? "").Trim();
        IEnumerable<string> names = Directory.GetDirectories(root).Select(Path.GetFileName).Where(name => name != null).Select(name => name!);
        if (trimmed.Length == 0)
        {
            return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (exactOnly)
        {
            return names
                .Where(name => name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return names
            .Where(name => name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 本地缓存的版本列表（降序）。取数走 NuGet 官方协议资源 ——
    /// LocalV3FindPackageByIdResource 直接认全局包文件夹这种 &lt;id&gt;/&lt;version&gt;/ 布局，纯离线，不联网。
    /// </summary>
    public static IReadOnlyList<ComparableVersion> Versions(string packageName)
    {
        List<ComparableVersion> versions = [];
        try
        {
            using SourceCacheContext cache = new();
            IEnumerable<NuGetVersion> found = LocalFinder
                .GetAllVersionsAsync(packageName, cache, NullLogger.Instance, CancellationToken.None)
                .GetAwaiter().GetResult();
            foreach (NuGetVersion version in found)
            {
                if (ComparableVersion.TryParse(version.ToNormalizedString(), out ComparableVersion? parsed) && parsed != null)
                {
                    versions.Add(parsed);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return [];
        }

        versions.Sort();
        versions.Reverse();
        return versions;
    }

    private static readonly FindPackageByIdResource LocalFinder =
        new LocalV3FindPackageByIdResource(new PackageSource(Root(), "local-cache"));

    /// <summary>包目录（小写包名）。</summary>
    public static string PackageDirectory(string packageName)
    {
        return Path.Combine(Root(), (packageName ?? "").Trim().ToLowerInvariant());
    }

    /// <summary>nuspec 路径（找不到返回 null）。</summary>
    public static string? NuspecPath(string packageName, string version)
    {
        string directory = Path.Combine(PackageDirectory(packageName), version);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        // 正常是 <包名>.nuspec；有的包用了不同大小写，兜一下
        string expected = Path.Combine(directory, packageName.Trim().ToLowerInvariant() + ".nuspec");
        if (File.Exists(expected))
        {
            return expected;
        }

        return Directory.GetFiles(directory, "*.nuspec").OrderBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>读本地元数据（含 readme）；本地没有这个版本时返回 null。</summary>
    public static PackageMetadata? ReadMetadata(string packageName, string version)
    {
        string? nuspecPath = NuspecPath(packageName, version);
        if (nuspecPath == null)
        {
            return null;
        }

        string nuspecXml = File.ReadAllText(nuspecPath);
        XElement? metadata = XDocument.Parse(nuspecXml).Descendants().FirstOrDefault(element => element.Name.LocalName == "metadata");
        if (metadata == null)
        {
            return null;
        }

        string id = Value(metadata, "id") ?? packageName;
        string resolvedVersion = Value(metadata, "version") ?? version;
        string? readmeName = Value(metadata, "readme");
        string? readme = null;
        if (!string.IsNullOrWhiteSpace(readmeName))
        {
            string packageRoot = Path.GetDirectoryName(nuspecPath) ?? ".";
            string readmePath = Path.Combine(packageRoot, readmeName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(readmePath))
            {
                readme = ReadText(readmePath);
            }
        }

        return new PackageMetadata(
            id,
            resolvedVersion,
            Value(metadata, "description"),
            Value(metadata, "projectUrl"),
            RepositoryUrl(metadata),
            readme,
            nuspecXml);
    }

    /// <summary>从 nupkg（本地没有就下载）里取 readme 文本。</summary>
    internal static string? ReadReadmeFromPackage(string packageName, string version)
    {
        string? nuspecPath = NuspecPath(packageName, version);
        string readmeName = "README.md";
        if (nuspecPath != null)
        {
            try
            {
                XElement? metadata = XDocument.Parse(File.ReadAllText(nuspecPath)).Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "metadata");
                string? declared = metadata == null ? null : Value(metadata, "readme");
                if (!string.IsNullOrWhiteSpace(declared))
                {
                    readmeName = declared;
                }
            }
            catch (System.Xml.XmlException)
            {
                // nuspec 坏了就按默认名试
            }
        }

        string nupkg = Path.Combine(PackageDirectory(packageName), version, packageName.ToLowerInvariant() + "." + version + ".nupkg");
        if (!File.Exists(nupkg))
        {
            return null;
        }

        using ZipArchive archive = ZipFile.OpenRead(nupkg);
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(
            candidate => candidate.FullName.Replace('\\', '/').Equals(readmeName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            return null;
        }

        using Stream stream = entry.Open();
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string? RepositoryUrl(XElement metadata)
    {
        XElement? repository = metadata.Elements().FirstOrDefault(element => element.Name.LocalName == "repository");
        return repository?.Attribute("url")?.Value;
    }

    private static string? Value(XElement metadata, string name)
    {
        XElement? element = metadata.Elements().FirstOrDefault(candidate => candidate.Name.LocalName == name);
        string? value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string ReadText(string path)
    {
        try
        {
            return FileWriter.ReadAllText(path, out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return File.ReadAllText(path, System.Text.Encoding.Latin1);
        }
    }
}
