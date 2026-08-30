using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace zms9110750.ZMS_MCP.Cli.Tools;

/// <summary>
/// HTTP 请求工具：对指定 uri 发起一次 HTTP 请求。
/// 支持 GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS，可自定义请求头与请求体。
/// </summary>
[McpServerToolType]
public static partial class HttpTools
{
    private const int MaxBodyChars = 50_000;
    private static readonly string[] KnownSensitiveHeaders = ["authorization", "cookie", "set-cookie", "proxy-authorization", "x-api-key"];

    [McpServerTool(
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true)]
    [Description(
        "对指定 uri 发起 HTTP 请求。action 为 HTTP 方法（GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS，大小写不敏感）。" +
        "head 为 JSON 对象字符串（如 {\"Content-Type\":\"application/json\"}）作为请求头；body 为可选请求体文本。" +
        "返回状态码、响应头与响应体（超长自动截断）。")]
    public static async Task<string> HttpRequest(
        [Description("目标 URL，必须以 http:// 或 https:// 开头")] string uri,
        [Description("HTTP 方法，如 GET / POST / PUT / DELETE / HEAD / OPTIONS")] string action,
        [Description("请求头，JSON 对象字符串，如 {\"Content-Type\":\"application/json\"}")] string? head = null,
        [Description("请求体文本，GET/HEAD/DELETE 通常为空")] string? body = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(uri) ||
                !(uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                return "uri 必须以 http:// 或 https:// 开头。";
            }

            var method = (action ?? "").Trim().ToUpperInvariant();
            var known = method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS";
            if (!known)
            {
                return $"不支持的 HTTP 方法 '{action}'。支持：GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS";
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var req = new HttpRequestMessage(new HttpMethod(method), uri);

            // 请求体（先建 content，请求头里的 Content-Type 等要挂到 content 上）
            if (!string.IsNullOrEmpty(body))
            {
                if (method is "GET" or "HEAD")
                {
                    return $"方法 {method} 不允许携带请求体。";
                }

                req.Content = new StringContent(body, Encoding.UTF8);
            }

            // 解析请求头
            if (!string.IsNullOrWhiteSpace(head))
            {
                JsonNode? node;
                try { node = JsonNode.Parse(head); }
                catch (JsonException ex) { return $"head 不是合法 JSON: {ex.Message}"; }

                if (node is not JsonObject obj)
                {
                    return "head 必须是 JSON 对象，如 {\"Content-Type\":\"application/json\"}。";
                }

                foreach (var kv in obj)
                {
                    var val = kv.Value?.GetValue<string>() ?? "";
                    if (!req.Headers.TryAddWithoutValidation(kv.Key, val))
                    {
                        // 非请求头（如 Content-Type/Content-Length）挂到 content 上；无 body 时允许无 content 的头
                        req.Content ??= new ByteArrayContent([]);
                        req.Content.Headers.TryAddWithoutValidation(kv.Key, val);
                    }
                }
            }

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var sb = new StringBuilder();
            sb.AppendLine($"# {method} {uri}");
            sb.AppendLine($"Status: {(int)resp.StatusCode} {resp.ReasonPhrase}");
            sb.AppendLine();

            // 响应头（敏感头打码）
            sb.AppendLine("## Headers");
            foreach (var h in resp.Headers)
            {
                var name = h.Key.ToLowerInvariant();
                var value = KnownSensitiveHeaders.Contains(name)
                    ? "***"
                    : string.Join(", ", h.Value);
                sb.AppendLine($"- {h.Key}: {value}");
            }

            foreach (var h in resp.Content.Headers)
            {
                var name = h.Key.ToLowerInvariant();
                var value = KnownSensitiveHeaders.Contains(name)
                    ? "***"
                    : string.Join(", ", h.Value);
                sb.AppendLine($"- {h.Key}: {value}");
            }

            // 响应体
            if (method == "HEAD" || resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                sb.AppendLine();
                sb.AppendLine("(无响应体)");
                return sb.ToString();
            }

            var content = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(content))
            {
                sb.AppendLine();
                sb.AppendLine("(空响应体)");
                return sb.ToString();
            }

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var isText = contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                         contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                         contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                         contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                         contentType.Contains("urlencoded", StringComparison.OrdinalIgnoreCase) ||
                         string.IsNullOrEmpty(contentType);
            if (!isText)
            {
                sb.AppendLine();
                sb.AppendLine($"(二进制响应体 {resp.Content.Headers.ContentLength?.ToString() ?? "?"} bytes，Content-Type: {contentType})");
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine("## Body");
            if (content.Length > MaxBodyChars)
            {
                content = content[..MaxBodyChars] + $"\n...（仅显示前 {MaxBodyChars} 字符，共 {content.Length} 字符）";
            }
            sb.AppendLine(content);
            return sb.ToString();
        }
        catch (TaskCanceledException)
        {
            return "请求超时（30 秒）。";
        }
        catch (HttpRequestException ex)
        {
            return $"请求失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }
}
