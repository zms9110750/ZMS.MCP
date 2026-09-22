namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// ftp 协议处理器：访问远程 FTP 服务器。
/// </summary>
public sealed class FtpProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "ftp";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现（见 TODO.md ftp 协议）。
        throw new NotImplementedException("FtpProtocol 尚未实现。");
    }
}
