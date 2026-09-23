using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

namespace ZMS.MCP.Csharp.NuGet;

/// <summary>
/// nuget.org 的在线查询：模糊搜索、版本列表、nuspec、readme（从 nupkg 里取）。
/// 所有调用都可能因为网络失败，调用方要准备好降级到"只有本地"的结果。
/// </summary>
public static class NuGetOnline
{
    /// <summary>nuget.org 搜索 API 的每页条数（与现有语义一致）。</summary>
    public const int PageSize = 15;

    private const string SearchEndpoint = "https://azuresearch-usnc.nuget.org/query";
    private const string FlatContainerEndpoint = "https://api.nuget.org/v3-flatcontainer";

    private static readonly HttpClient Client = CreateClient();

    /// <summary>模糊搜索包名，返回 (包名, 最新版本) 列表。</summary>
    public static IReadOnlyList<(string Id, string Version)> SearchPackages(string keyword, int page)
    {
        int skip = Math.Max(page, 0) * PageSize;
        string url = $"{SearchEndpoint}?q={Uri.EscapeDataString(keyword)}&skip={skip}&take={PageSize}&prerelease=false";
        using JsonDocument document = GetJson(url);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<(string, string)> results = [];
        foreach (JsonElement item in data.EnumerateArray())
        {
            string? id = item.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() : null;
            string? version = item.TryGetProperty("version", out JsonElement versionElement) ? versionElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id))
            {
                results.Add((id, version ?? ""));
            }
        }

        return results;
    }

    /// <summary>某个包在 nuget.org 上的全部版本（升序，和 flat container 一致）。</summary>
    public static IReadOnlyList<ComparableVersion> Versions(string packageName)
    {
        string url = $"{FlatContainerEndpoint}/{Uri.EscapeDataString(packageName.ToLowerInvariant())}/index.json";
        using JsonDocument document = GetJson(url);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("versions", out JsonElement versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<ComparableVersion> results = [];
        foreach (JsonElement item in versions.EnumerateArray())
        {
            string? text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text) && ComparableVersion.TryParse(text, out ComparableVersion? parsed) && parsed != null)
            {
                results.Add(parsed);
            }
        }

        return results;
    }

    /// <summary>取线上 nuspec 原文；拿不到返回 null。</summary>
    public static string? FetchNuspec(string packageName, string version)
    {
        string lowered = packageName.ToLowerInvariant();
        string url = $"{FlatContainerEndpoint}/{Uri.EscapeDataString(lowered)}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(lowered)}.nuspec";
        return GetStringOrNull(url);
    }

    /// <summary>取线上包里的 readme（下载 nupkg 再解出来）；拿不到返回 null。</summary>
    public static string? FetchReadme(string packageName, string version, string? readmeName)
    {
        string lowered = packageName.ToLowerInvariant();
        string url = $"{FlatContainerEndpoint}/{Uri.EscapeDataString(lowered)}/{Uri.EscapeDataString(version)}/"
            + $"{Uri.EscapeDataString(lowered)}.{Uri.EscapeDataString(version)}.nupkg";

        byte[]? bytes = GetBytesOrNull(url);
        if (bytes == null)
        {
            return null;
        }

        string wanted = string.IsNullOrWhiteSpace(readmeName) ? "README.md" : readmeName;
        try
        {
            using MemoryStream buffer = new(bytes);
            using ZipArchive archive = new(buffer);
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(
                candidate => candidate.FullName.Replace('\\', '/').Equals(wanted.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                ?? archive.Entries.FirstOrDefault(
                    candidate => Path.GetFileName(candidate.FullName).Equals("README.md", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return null;
            }

            using Stream stream = entry.Open();
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static JsonDocument GetJson(string url)
    {
        using HttpResponseMessage response = Client.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using Stream stream = response.Content.ReadAsStream();
        return JsonDocument.Parse(stream);
    }

    private static string? GetStringOrNull(string url)
    {
        using HttpResponseMessage response = Client.GetAsync(url).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static byte[]? GetBytesOrNull(string url)
    {
        using HttpResponseMessage response = Client.GetAsync(url).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZMS.MCP.Csharp/1.0");
        return client;
    }
}
