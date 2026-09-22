namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// nuget 协议处理器：访问本地 NuGet 缓存 API 文档与在线资源。
/// </summary>
public sealed class NuGetProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "nuget";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现（见 TODO.md nuget:// 协议）。
        throw new NotImplementedException("NuGetProtocol 尚未实现。");
    }
}
