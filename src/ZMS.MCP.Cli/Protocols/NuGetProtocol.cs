namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// nuget 协议处理器：访问本地 NuGet 包缓存，读取包内 XML 文档注释。
/// 复用现有 NuGetTools.InspectNuGetXml 的逻辑。
///
/// 职责（待实现）：
/// 1. host = 包名；path = 对象路径（点分名）。
/// 2. query：version（版本，缺省最新）/ tfm（目标框架，缺省自动找可用项）/ type（TPFMED，缺省 auto）。
/// 3. type 缺省时由 path 精确度决定：精确类型→TPFME、精确成员→D、非精确→T；
///    显式 type 可组合；找命名空间下所有类型用 ?type=T。
/// 4. fragment（#）= 重载消歧：参数签名段，完全匹配；不带 # 返回全部同名重载。
/// 5. 包级资源（path 留空）：?readme / ?nupkg（+version 缺省最新）。
/// 6. 在线资源（path 留空）：?online（+version/readme/nupkg/page），映射 SearchNuGet(online=true)。
/// 7. 只读协议（无写操作）；输出 Markdown 类型树/成员表/文档详情。
/// 8. 缓存根：NUGET_PACKAGES 环境变量或 ~/.nuget/packages。
/// </summary>
public sealed class NuGetProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "nuget";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现 NuGet 包文档访问（见类注释），复用 NuGetTools 解析逻辑。
        throw new NotImplementedException("NuGetProtocol 尚未实现。");
    }
}
