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
///
/// 每个请求都有**统一超时**（<see cref="TimeoutSeconds"/> 秒）与**下载上限**（<see cref="MaxPackageBytes"/> 字节）：
/// 超时抛 <see cref="TimeoutException"/>、超上限抛 <see cref="InvalidOperationException"/>，
/// 都由工具层如实报给调用方 —— 不静默降级成"没有 readme"。
/// </summary>
public static class NuGetOnline
{
    /// <summary>搜索每页条数（与现有语义一致）。</summary>
    public const int PageSize = 15;

    /// <summary>单次线上请求的超时（秒）。</summary>
    public const int TimeoutSeconds = 30;

    /// <summary>单个 .nupkg 允许下载的最大字节数（50 MiB）。</summary>
    public const long MaxPackageBytes = 50L * 1024 * 1024;

    private const string ServiceIndex = "https://api.nuget.org/v3/index.json";

    private static readonly SourceRepository Source = Repository.Factory.GetCoreV3(ServiceIndex);

    /// <summary>模糊搜索包名，返回 (包名, 版本) 列表。</summary>
    public static IReadOnlyList<(string Id, string Version)> SearchPackages(string keyword, int page)
    {
        PackageSearchResource resource = GetResource<PackageSearchResource>();
        SearchFilter filter = new(includePrerelease: false);
        IEnumerable<IPackageSearchMetadata> found = Run(token => resource.SearchAsync(
            keyword,
            filter,
            Math.Max(page, 0) * PageSize,
            PageSize,
            NullLogger.Instance,
            token));

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
        IEnumerable<NuGetVersion> versions = Run(token => resource.GetAllVersionsAsync(
            packageName,
            cache,
            NullLogger.Instance,
            token));

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

    /// <summary>取线上包里的 nuspec 原文（下到内存再读）；解析不了版本时返回 null。</summary>
    public static string? FetchNuspec(string packageName, string version)
    {
        return ReadFromPackage(packageName, version, ReadNuspec);
    }

    /// <summary>取线上包里的 readme（下到内存再读 zip）；解析不了版本时返回 null。</summary>
    public static string? FetchReadme(string packageName, string version, string? readmeName)
    {
        // readmeName 只是兜底：nuspec 里声明了 readme 时，按 nuspec 声明取。
        return ReadFromPackage(packageName, version, reader => ReadReadme(reader, readmeName));
    }

    /// <summary>
    /// 所有线上调用都经这里：统一超时，并把取消转成明确的"超时"错误。
    /// </summary>
    private static T Run<T>(Func<CancellationToken, Task<T>> action)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            return action(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"nuget.org 请求超过 {TimeoutSeconds} 秒未完成，已取消。",
                exception);
        }
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
            // zip 里的条目名大小写不保证与 nuspec 声明一致（打包工具各异），这里按不敏感比较更稳。
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
    /// 把 .nupkg 下到**内存流**（不写盘，且限制最大字节数），再交给 <paramref name="read"/> 从 zip 里取内容。
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
        SizeLimitedStream limited = new(package, MaxPackageBytes);
        bool copied = Run(token => resource.CopyNupkgToStreamAsync(
            packageName,
            parsed,
            limited,
            cache,
            NullLogger.Instance,
            token));
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

    /// <summary>
    /// 写入总量超过上限就中止的流包装（.nupkg 只往下写，写满上限即失败）。
    /// </summary>
    private sealed class SizeLimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _written;

        public SizeLimitedStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get { return _inner.Position; }
            set { _inner.Position = value; }
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _written += count;
            if (_written > _limit)
            {
                throw new InvalidOperationException(
                    $"包体积超过上限 {_limit / (1024 * 1024)} MiB，已中止下载（{_limit / (1024 * 1024)} MiB 是本项目的硬上限）。");
            }

            _inner.Write(buffer, offset, count);
        }
    }
}
