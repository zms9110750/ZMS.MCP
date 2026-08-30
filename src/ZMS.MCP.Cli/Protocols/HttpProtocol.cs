namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// http/https 协议处理器：访问网页与 REST API。
///
/// 职责（待实现）：
/// 1. action = HTTP 方法（GET/HEAD/POST/PUT/PATCH/DELETE/MOVE/COPY，通过 HttpMethod string 构造器）。
/// 2. userinfo = user:pass → 转 Authorization: Basic 头；host:port 缺省 80/443。
/// 3. path/query 拼成请求 URL（query 透传）。
/// 4. head（JSON 对象）→ 请求头；body → 请求体（GET/HEAD 禁止带 body）。
/// 5. fragment 非空 → 下载到本地路径（必须是本地绝对路径，且没有文件/文件夹），走限流池
///    （token bucket：容量 1M，恢复 1M/分钟，最大储存 10 分钟，不足则等待或报"剩余 X，需等 Y 分钟"）。
/// 6. 响应：状态码 + 状态短语 → 响应头（敏感头 authorization/cookie 打码）→
///    响应体（超长截断，>5k 说明还有 xx 被截断；二进制按 Content-Type 识别只报大小）。
/// 7. 自动缓存：无 fragment 时自动缓存结果，额外在开头返回缓存 key（12 位 hash）。
/// 8. 超时 30s；错误转友好中文消息，不抛异常给客户端。
/// </summary>
public sealed class HttpProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "http";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现 HTTP 访问逻辑（见类注释）。
        throw new NotImplementedException("HttpProtocol 尚未实现。");
    }
}
