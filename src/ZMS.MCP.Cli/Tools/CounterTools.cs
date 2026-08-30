using System.Runtime.CompilerServices;

namespace zms9110750.ZMS_MCP.Cli.Tools;

/// <summary>
/// 计数器工具——用来判断 MCP Server 进程是否在多次调用间保持存活。
/// 每次调用返回自增的数字，归零说明进程重启了。
/// </summary>
[McpServerToolType]
public static partial class CounterTools
{
    // 进程级计数器，每次调用 +1，进程重启归零
    private static int _callCount;

    [McpServerTool(
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Returns a monotonically increasing call count. Each call increments by 1. Resets when the process restarts.")]
    public static string GetCount()
    {
        return Interlocked.Increment(ref _callCount).ToString();
    }
}
