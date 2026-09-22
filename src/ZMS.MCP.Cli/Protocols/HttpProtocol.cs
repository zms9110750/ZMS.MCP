namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// http/https 协议处理器：访问网页与 REST API。
/// </summary>
public sealed class HttpProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "http";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现（见 TODO.md http/https 协议）。
        throw new NotImplementedException("HttpProtocol 尚未实现。");
    }
}
