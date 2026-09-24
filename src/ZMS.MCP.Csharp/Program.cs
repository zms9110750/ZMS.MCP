using ZMS.MCP;
using ZMS.MCP.Csharp.Draft;

// 启动维护：前滚未完成的落盘 → 按现状刷新追踪基线 → 清理孤儿拟定（见 docs/Csharp-拟定流程v3.md 第六节）。
// 日志写 stderr，stdout 留给 MCP 协议；维护失败也不能把服务拦在门外。
try
{
    string maintenance = DraftService.StartupMaintenance();
    if (maintenance.Length > 0)
    {
        Console.Error.WriteLine(maintenance);
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine("启动维护失败（不影响服务启动）：" + exception.Message);
}

// 通用入口（见仓库根 Global.cs）：stdio 传输 + 扫描本程序集注册全部工具
await McpStdioServer.RunAsync(args);
