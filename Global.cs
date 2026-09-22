// 所有 MCP 服务项目共享的基础设施：
// 1) MCP 相关的 global using；
// 2) 通用入口——扫描本程序集注册全部工具，日志走 stderr（stdout 留给协议）。
global using System.ComponentModel;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
global using ModelContextProtocol.Server;

namespace ZMS.MCP;

/// <summary>
/// MCP stdio 服务的通用入口。各 MCP 项目只需：
/// <code>await McpStdioServer.RunAsync(args);</code>
/// </summary>
public static class McpStdioServer
{
    /// <summary>
    /// 构建并运行 MCP stdio 服务。
    /// </summary>
    /// <param name="args">命令行参数，透传给 <see cref="Host.CreateApplicationBuilder(string[])"/>。</param>
    /// <param name="configure">可选：追加自定义配置，如请求/消息过滤器。</param>
    public static async Task RunAsync(string[] args, Action<IMcpServerBuilder>? configure = null)
    {
        await Build(args, configure).RunAsync();
    }

    /// <summary>
    /// 构建 Host（未启动）。默认已注册：stdio 传输 + 扫描本程序集里的全部 MCP 工具。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="configure">可选：追加自定义配置，如请求/消息过滤器。</param>
    public static IHost Build(string[] args, Action<IMcpServerBuilder>? configure = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // MCP 走 stdout，日志一律写 stderr，避免污染协议流
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        IMcpServerBuilder mcp = builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        configure?.Invoke(mcp);

        return builder.Build();
    }
}
