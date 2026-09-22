using ZMS.MCP;

// 通用入口（见仓库根 Global.cs）：stdio 传输 + 扫描本程序集注册全部工具
await McpStdioServer.RunAsync(args);
