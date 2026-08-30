using System.Text.Json.Nodes;
using zms9110750.ZMS_MCP.Cli.Protocols;

namespace zms9110750.ZMS_MCP.Cli.Tools;

/// <summary>
/// 统一资源访问工具：按 uri 的 scheme 分发到对应协议处理器。
/// 支持协议：http/https、webdav（本地文件/目录/压缩容器，file@/dir@/archive@ 断言）、
/// ftp、structured（json/xml/yaml/ini/toml/csharp）、sqlite、cmd（白名单命令）、nuget。
/// </summary>
[McpServerToolType]
public static partial class UriResourceTools
{
    /// <summary>
    /// 协议分发字典：scheme（小写）→ 处理器实例。
    /// 新增协议时在此注册即可，分发逻辑无需改动。
    /// </summary>
    private static readonly Dictionary<string, IProtocolHandler> Handlers = new(StringComparer.OrdinalIgnoreCase) {
        ["http"] = new HttpProtocol(),
        ["https"] = new HttpProtocol(),
        ["webdav"] = new WebDavProtocol(),
        ["ftp"] = new FtpProtocol(),
        ["structured"] = new StructuredProtocol(),
        ["cmd"] = new CmdProtocol(),
        ["nuget"] = new NuGetProtocol(),
    };

    /// <summary>当前支持的全部 scheme 列表（用于帮助文本）。</summary>
    private static string SupportedSchemes => string.Join(", ", Handlers.Keys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s));

    [McpServerTool(
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true)]
    [Description(
        "统一资源访问工具。uri 格式: scheme://[userinfo@]host[:port]/path?query#fragment。\n" +
        "action 为 HTTP 方法枚举（GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS/TRACE/CONNECT/QUERY 及 WebDAV 的 PROPFIND/MKCOL/MOVE/COPY/LOCK/UNLOCK；cmd 协议固定 POST）。\n" +
        "head 为 JSON 对象字符串请求头；body 为请求体/内容/参数（自由文本参数一律放 body，避免 ?#% 空格转义地狱）。\n" +
        "支持协议: http/https（网页/API，fragment=下载路径）、webdav（本地文件/目录/压缩容器/SQLite，userinfo 断言 file@/dir@/archive@/sqlite@，\n" +
        "  file@ 的 #=文件内文本搜索（正则+行号），dir@ 的 #=文件名搜索（GetFiles pattern），sqlite@ 的 #=SQL 查询/表名）、\n" +
        "ftp（远程 FTP，默认匿名）、structured（json/xml/yaml/ini/toml/csharp 文档，#=定位路径；host=cache/nuget）、\n" +
        "cmd（白名单命令，命令树在 path，参数在 body，固定 POST）。\n" +
        "query 全选填：文件读取用 line/offset/max；目录列出用 depth/limit/meta；结构化文档用 depth/max。\n" +
        "破坏性操作（DELETE/写/命令）统一走 cookie 两阶段确认：先返回影响清单+cookie，带 cookie 且清单未变才执行。")]
    public static async Task<string> UriResource(
        [Description("目标 URI，如 https://example.com/a?b=1、webdav://file@/C:/data.txt、ftp://user:pass@host/dir、structured://json@/C:/a.json#$.a.b、nuget://fusioncache@last/")] string uri,
        [Description("HTTP 方法，如 GET / POST / PUT / DELETE / PROPFIND / MOVE / MKCOL")] string action,
        [Description("请求头，JSON 对象字符串，如 {\"Content-Type\":\"application/json\"}")] string? head = null,
        [Description("请求体/内容/目标路径，按协议和 action 使用")] string? body = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return "uri 不能为空。";
            }

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return $"无法解析 uri: '{uri}'。";
            }

            var scheme = parsed.Scheme.ToLowerInvariant();
            if (!Handlers.TryGetValue(scheme, out var handler))
            {
                return $"不支持的协议 '{scheme}'。支持: {SupportedSchemes}。\n" +
                       "也可用 action=OPTIONS 查看各协议能力说明。";
            }

            // 组装请求上下文
            var ctx = new UriRequestContext {
                Scheme = scheme,
                UserInfo = string.IsNullOrEmpty(parsed.UserInfo) ? null : Uri.UnescapeDataString(parsed.UserInfo),
                Host = string.IsNullOrEmpty(parsed.Host) ? null : parsed.Host,
                Port = parsed.Port,
                Path = Uri.UnescapeDataString(parsed.AbsolutePath),
                Query = ParseQuery(parsed.Query),
                Fragment = string.IsNullOrEmpty(parsed.Fragment) ? null : Uri.UnescapeDataString(parsed.Fragment),
                Action = (action ?? "").Trim(),
                Head = ParseHead(head),
                Body = body,
            };

            return await handler.HandleAsync(ctx);
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }

    /// <summary>解析查询串 ?a=1&amp;b=2 为字典（key 小写，已解码）。</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0)
            {
                result[Uri.UnescapeDataString(pair)] = "";
            }
            else
            {
                result[Uri.UnescapeDataString(pair[..idx])] = Uri.UnescapeDataString(pair[(idx + 1)..]);
            }
        }
        return result;
    }

    /// <summary>解析 head JSON 字符串为 JsonObject；空/非法返回 null（非法时由 handler 决定）。</summary>
    private static JsonObject? ParseHead(string? head)
    {
        if (string.IsNullOrWhiteSpace(head))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(head) as JsonObject;
        }
        catch
        {
            return null;
        }
    }
}
