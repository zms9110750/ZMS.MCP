namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// webdav 协议处理器：操作本地一切资源。
/// </summary>
public sealed class WebDavProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "webdav";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现（见 TODO.md webdav 协议）。
        throw new NotImplementedException("WebDavProtocol 尚未实现。");
    }
}
