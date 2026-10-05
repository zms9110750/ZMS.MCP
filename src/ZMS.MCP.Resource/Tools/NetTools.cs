using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Resource.Net;

namespace ZMS.MCP.Resource.Tools;

/// <summary>网络：取回来 / 下载，以及回看缓存里存下的东西。</summary>
[McpServerToolType]
public static class NetTools
{
    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description(
        "Fetch a URL, or download it to a local path. Without targetPath the response is fetched as content: it always " +
        "goes into the cache in full, even when the part handed to you is truncated - HTTP has no offset, so a truncated " +
        "reply could never be resumed otherwise; the reply carries a key, and you read the rest through cache. With " +
        "targetPath the bytes land on that path instead, nothing enters the content cache (only a metadata record is " +
        "kept) and that path is subject to the download budget - fetching content is not. An existing target file needs " +
        "its cookie. No interception checks are made: any 2xx counts as success.")]
    public static Task<string> Fetch(
        [Description("Absolute URL, must start with http:// or https://.")] string url,
        [Description("HTTP method: GET / POST / PUT / PATCH / DELETE / HEAD / OPTIONS. Empty = GET.")] string? method = null,
        [Description("Request headers as a JSON object, e.g. '{\"Accept\":\"text/plain\"}'.")] string? head = null,
        [Description("Request body.")] string? body = null,
        [Description("Local absolute path to download to. When given, the bytes go to disk instead of the reply.")] string? targetPath = null,
        [Description("Cookie for the existing target file, required only when targetPath already holds a file.")] string? cookie = null)
    {
        return FetchService.RunAsync(url, method, head, body, targetPath, cookie);
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Look at what was cached. With no key it lists the entries one page at a time (tool | args | time | key | " +
        "result). With a key it hands back that entry's content, at most 5000 characters - enough to see what it is; to " +
        "read it properly, give key together with path to write it to disk and then read that file with a range. " +
        "Nothing ever hits the cache automatically: it is a place you inspect on purpose.")]
    public static string Cache(
        [Description("Cache entry key. Empty = list the cache instead of reading one entry.")] string? key = null,
        [Description("Local absolute path to write the entry to; it must not exist yet. Only meaningful together with key.")] string? path = null,
        [Description("Page number when listing. Default 0. 50 entries per page.")] int page = 0)
    {
        if (page < 0)
        {
            throw new ArgumentException("page 不能是负数。");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return ListPage(page);
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            string saved = CacheStore.Save(key!.Trim(), path!);
            return $"# 已落盘\n- 缓存：`{key.Trim()}`\n- 落点：{saved}\n"
                + "- 之后就能用 `read` 的范围参数去看它的其他部分。\n";
        }

        string? content = CacheStore.Read(key!.Trim());
        if (content == null)
        {
            throw new ArgumentException($"没有这条缓存：{key}（用不带 key 的调用列出来看看）。");
        }

        string shown = content.Length <= CacheStore.ReadLimit ? content : content[..CacheStore.ReadLimit];
        StringBuilder builder = new();
        builder.AppendLine($"# 缓存 `{key.Trim()}`");
        builder.AppendLine($"- 共 {content.Length} 字符，这里给 {(content.Length <= CacheStore.ReadLimit ? "全部" : $"前 {CacheStore.ReadLimit} 字符")}");
        builder.AppendLine("- 要看全文：用 `key` + `path` 落盘，再 `read` 它的范围。");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(shown);
        builder.AppendLine("```");
        return builder.ToString();
    }

    private static string ListPage(int page)
    {
        IReadOnlyList<CacheEntry> entries = CacheStore.Page(page);
        int total = CacheStore.Count();

        StringBuilder builder = new();
        builder.AppendLine($"# 缓存（共 {total} 条）");
        builder.AppendLine($"- 第 {page} 页，每页 {CacheStore.PageSize} 条");
        builder.AppendLine($"- 落点：{CacheStore.Root}");
        builder.AppendLine($"- 过期：{CacheStore.Ttl.TotalDays:F0} 天；上限：{CacheStore.Capacity / (1024 * 1024)} MB");
        builder.AppendLine();

        if (entries.Count == 0)
        {
            builder.AppendLine(page == 0 ? "（里面是空的）" : "（这一页没有东西了）");
            return builder.ToString();
        }

        builder.AppendLine("tool | args | time | key | 结果");
        builder.AppendLine("---|---|---|---|---");

        foreach (CacheEntry entry in entries)
        {
            string time = new DateTimeOffset(entry.Time, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            builder.AppendLine($"{entry.Tool} | {entry.Args} | {time} | `{entry.Key}` | {entry.Result}");
        }

        if (total > (page + 1) * CacheStore.PageSize)
        {
            builder.AppendLine();
            builder.AppendLine($"还有更多：`page={page + 1}`");
        }

        return builder.ToString();
    }
}
