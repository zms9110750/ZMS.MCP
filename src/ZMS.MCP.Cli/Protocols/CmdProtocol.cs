namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// cmd 协议处理器：执行命令。
/// </summary>
public sealed class CmdProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "cmd";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现（见 TODO.md cmd 协议）。
        throw new NotImplementedException("CmdProtocol 尚未实现。");
    }
}
