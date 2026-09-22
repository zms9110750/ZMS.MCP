using System.Text.Json.Nodes;

namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// 解析后的 uri 资源请求上下文。由 <see cref="UriResourceTools"/> 从原始
/// uri/action/head/body 参数解析而来，分发给各 <see cref="IProtocolHandler"/>。
/// </summary>
public sealed class UriRequestContext
{
    /// <summary>协议 scheme（小写）。</summary>
    public required string Scheme { get; init; }

    /// <summary>
    /// userinfo 段（@ 前的内容，小写）。各协议自定义语义：
    /// http/ftp = 账号[:密码]；webdav = 类型断言；nuget = 包名。
    /// </summary>
    public string? UserInfo { get; init; }

    /// <summary>host 段。各协议自定义语义（http 主机名 / ftp 主机名 / cmd 程序名 / nuget 包名）。</summary>
    public string? Host { get; init; }

    /// <summary>端口。缺省时各协议按 scheme 默认（http=80/https=443/ftp=21）。</summary>
    public int Port { get; init; }

    /// <summary>path 段（解码后）。本地协议为本地路径；远程协议为资源路径；cmd 为命令树（每段一个纯单词子命令）。</summary>
    public required string Path { get; init; }

    /// <summary>query 段解析结果（已解码，key 小写）。全选填，各协议给默认值。</summary>
    public Dictionary<string, string> Query { get; init; } = [];

    /// <summary>fragment 段（解码后）。各协议自定义：http=下载路径；webdav file@=文件内文本搜索、dir@=文件名搜索、archive@=容器内路径；structured=定位路径；cmd 不用（并入 body）。</summary>
    public string? Fragment { get; init; }

    /// <summary>HTTP 方法（原样，如 GET / PUT / PROPFIND / MOVE）。cmd 协议固定 POST（命令只有一个作用）。</summary>
    public required string Action { get; init; }

    /// <summary>请求头（JSON 对象）。http 用；cmd 用 head.path 放程序绝对路径；其他协议可能取 Depth/Destination 等。</summary>
    public JsonObject? Head { get; init; }

    /// <summary>请求体。PUT 写内容、MOVE/COPY 放目标路径、cmd 放所有参数（flag/值/作用目标）。</summary>
    public string? Body { get; init; }
}
