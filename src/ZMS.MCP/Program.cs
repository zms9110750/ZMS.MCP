using System.Reflection;
using ZMS.MCP;

// 聚合入口：一个进程同时提供四组工具。
//
// 四个项目各自仍是可独立启动的 exe（`dotnet run --project src/ZMS.MCP.X`），
// 这一份只是把它们挂到一起 —— 四组工具共用一个进程、一份依赖。
//
// 工具分散在被引用的四个程序集里，所以要把它们逐个报给入口（留空的话只扫本程序集，
// 而本程序集里一个工具都没有）。
await McpStdioServer.RunAsync(
    args,
    configure: null,
    toolAssemblies:
    [
        typeof(ZMS.MCP.Csharp.Tools.SymbolTools).Assembly,
        typeof(ZMS.MCP.Structured.Tools.StructuredTools).Assembly,
        typeof(ZMS.MCP.Resource.Tools.ReadTools).Assembly,
        typeof(ZMS.MCP.Workflow.Tools.WorkflowTools).Assembly,
    ]);
