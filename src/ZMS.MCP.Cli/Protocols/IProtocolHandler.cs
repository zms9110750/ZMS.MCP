namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// 协议处理器接口。每个协议实现一个，
/// 由 <see cref="UriResourceTools"/> 按 uri 的 scheme 从字典中取出并分发调用。
/// </summary>
public interface IProtocolHandler
{
    /// <summary>
    /// 本处理器负责的 scheme（小写），同时作为字典分发的 key。
    /// </summary>
    string Scheme { get; }

    /// <summary>
    /// 处理一次 uri 资源操作请求。
    /// </summary>
    /// <param name="ctx">解析后的请求上下文（含 scheme/userinfo/host/port/path/query/fragment、action、head、body）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>面向 AI 的 Markdown 文本结果（成功内容或错误说明）。</returns>
    Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default);
}
