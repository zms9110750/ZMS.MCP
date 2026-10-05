using System.Text.Json;

namespace ZMS.MCP.Resource.Net;

/// <summary>
/// 取 / 下载：<c>fetch</c>。
///
/// 不给 <c>targetPath</c> = **取内容**：把响应取回来，**全额进缓存**（哪怕交给你的那份被截断——
/// http 没有偏移，一次只给得了这么多，超了后半段就再也要不到，只有缓存里留着完整的）。
/// 给了 <c>targetPath</c> = **下载**：落到那个路径；**不进内容缓存**，只留一条元数据记录，
/// 而且这一路**受额度约束**。
///
/// **拦截不做判定**：<c>2xx</c> 就是成功，内容照给。
/// </summary>
public static class FetchService
{
    /// <summary>超时。</summary>
    public const int TimeoutSeconds = 15;

    /// <summary>重试上限。</summary>
    public const int MaxAttempts = 3;

    /// <summary>响应截断（字符）。</summary>
    public const int ResponseLimit = 16000;

    private static readonly string[] AllowedMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    private static readonly HttpClient DefaultClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
    };

    /// <summary>测试可以换掉它。</summary>
    public static HttpClient Client { get; set; } = DefaultClient;

    /// <summary>取内容或下载。</summary>
    public static async Task<string> RunAsync(
        string url,
        string? method,
        string? head,
        string? body,
        string? targetPath,
        string? cookie)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"url 必须是 http:// 或 https:// 开头的绝对地址：{url}");
        }

        string verb = (method ?? "GET").Trim().ToUpperInvariant();
        if (!AllowedMethods.Contains(verb))
        {
            throw new ArgumentException($"method 只认 {string.Join(" / ", AllowedMethods)}，给的是：{method}");
        }

        using HttpRequestMessage request = new(new HttpMethod(verb), target);
        if (!string.IsNullOrWhiteSpace(body))
        {
            request.Content = new StringContent(body!, new UTF8Encoding(false));
        }

        if (!string.IsNullOrWhiteSpace(head))
        {
            ApplyHeaders(request, head!);
        }

        return string.IsNullOrWhiteSpace(targetPath)
            ? await FetchContentAsync(request, url).ConfigureAwait(false)
            : await DownloadAsync(request, url, targetPath!, cookie).ConfigureAwait(false);
    }

    private static async Task<string> FetchContentAsync(HttpRequestMessage request, string url)
    {
        using HttpResponseMessage response = await SendAsync(request).ConfigureAwait(false);
        string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        int status = (int)response.StatusCode;

        string shown = content.Length <= ResponseLimit ? content : content[..ResponseLimit];
        string result = content.Length <= ResponseLimit
            ? "正常"
            : $"截断 {content.Length - ResponseLimit} 字符";

        if (request.Method != HttpMethod.Head)
        {
            string key = CacheStore.Put("fetch", url, content, result);

            StringBuilder withKey = new();
            withKey.AppendLine($"# {request.Method.Method} {url}");
            withKey.AppendLine($"- 状态：{status}");
            withKey.AppendLine($"- 全文 {content.Length} 字符，这里给 {(content.Length <= ResponseLimit ? "全文" : $"前 {ResponseLimit} 字符")}");
            withKey.AppendLine($"- key：`{key}`");
            withKey.AppendLine("- 要看后半段：用 `cache(key=..., path=...)` 落盘，再用 `read` 的范围参数读它。");
            withKey.AppendLine();
            withKey.AppendLine("```");
            withKey.AppendLine(shown);
            withKey.AppendLine("```");
            return withKey.ToString();
        }

        StringBuilder headOnly = new();
        headOnly.AppendLine($"# HEAD {url}");
        headOnly.AppendLine($"- 状态：{status}");
        foreach (var header in response.Headers)
        {
            headOnly.AppendLine($"- {header.Key}: {string.Join(", ", header.Value)}");
        }

        return headOnly.ToString();
    }

    private static async Task<string> DownloadAsync(
        HttpRequestMessage request,
        string url,
        string targetPath,
        string? cookie)
    {
        string full = Path.GetFullPath(targetPath);
        if (Directory.Exists(full))
        {
            throw new ArgumentException($"那是个目录：{full}");
        }

        if (File.Exists(full) && string.IsNullOrWhiteSpace(cookie))
        {
            return $"# 会发生什么（未执行）\n- 目标已经有文件：{full}\n"
                + "- 这次会把它覆盖掉。先 `list` 它拿凭据，再来。\n"
                + "- （这不算失败：缺凭据是两段式的第一段。）\n";
        }

        FileStream? sink = null;
        long written = 0;
        try
        {
            using HttpResponseMessage response = await SendAsync(request).ConfigureAwait(false);
            int status = (int)response.StatusCode;

            if (!Directory.Exists(Path.GetDirectoryName(full)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            }

            sink = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
            await using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

            byte[] buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (!DownloadBudget.TryTake(read, out TimeSpan waitFor))
                {
                    sink.Dispose();
                    sink = null;
                    File.Delete(full);
                    throw new ArgumentException(
                        $"下载额度不够，没有下：{DownloadBudget.Describe(read)}"
                        + $"（这一路受额度约束；取内容不受。）");
                }

                await sink.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                written += read;
            }

            sink.Dispose();
            sink = null;

            CacheStore.Put("fetch", $"{url} → {full}", "", $"下载 {written} 字节");

            StringBuilder done = new();
            done.AppendLine("# 已下载");
            done.AppendLine($"- {url}");
            done.AppendLine($"- 目标：{full}");
            done.AppendLine($"- 大小：{written} 字节（状态 {status}）");
            done.AppendLine($"- 额度还剩：{DownloadBudget.Available / (1024.0 * 1024.0):F2} MB");
            done.AppendLine("- 原件已经在磁盘上了，去读原件（这条不进内容缓存）。");
            return done.ToString();
        }
        finally
        {
            sink?.Dispose();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                // 重试要重新造一份，请求体只能用一次。
                // 这一份**不能** dispose：响应是流式读的，提前销毁请求会把流一起带走。
                HttpRequestMessage copy = await CloneAsync(request).ConfigureAwait(false);
                return await Client.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                last = exception;
            }
            catch (TaskCanceledException exception)
            {
                last = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt)).ConfigureAwait(false);
        }

        throw new ArgumentException($"试了 {MaxAttempts} 次都没成功：{last?.Message}");
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        HttpRequestMessage copy = new(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content != null)
        {
            string body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            copy.Content = new StringContent(body, new UTF8Encoding(false));
            foreach (var header in request.Content.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return copy;
    }

    private static void ApplyHeaders(HttpRequestMessage request, string head)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(head);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"head 要是合法的 JSON 对象：{exception.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("head 要是 JSON **对象**，例如 '{\"Accept\":\"text/plain\"}'。");
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                string value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : property.Value.ToString();

                if (!request.Headers.TryAddWithoutValidation(property.Name, value))
                {
                    request.Content?.Headers.TryAddWithoutValidation(property.Name, value);
                }
            }
        }
    }
}
