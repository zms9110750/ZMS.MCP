using System.Runtime.CompilerServices;
using ZMS.MCP.Csharp.Draft;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 测试进程启动时跑一次：把落盘流程里的 dotnet format 换成 no-op。
///
/// 端到端用例验的是「落盘语义」（写前日志、窗口期字节校验、原子写、前滚），
/// 不该为一次**真实格式化**等几十秒 —— 这正是测试总耗时的大头。
/// 生产代码默认仍然真跑 dotnet format，这里只覆盖测试进程。
/// </summary>
internal static class TestSetup
{
    private const string SkipNote = "（测试：已跳过 dotnet format）";

    [ModuleInitializer]
    internal static void Initialize()
    {
        DraftService.FormatRunner = SkipFormat;
    }

    private static (bool Succeeded, string Log) SkipFormat(string projectPath, IReadOnlyList<string> files)
    {
        return (true, SkipNote);
    }
}
