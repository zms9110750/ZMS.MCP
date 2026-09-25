using System.ComponentModel;

namespace ZMS.MCP.Csharp.Tools;

/// <summary>把异常统一转成工具可读的文本结果。</summary>
public static class ToolGuard
{
    public static string Run(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            return "Error: " + Describe(exception);
        }
    }
    /// <summary>异步版：把异常同样转成工具可读的文本结果（供 async 工具用）。</summary>
    public static async Task<string> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception)
        {
            return "Error: " + Describe(exception);
        }
    }

    /// <summary>
    /// 已知的环境类异常给一句可操作的提示；其余原样返回消息。
    /// </summary>
    private static string Describe(Exception exception)
    {
        if (exception is Win32Exception)
        {
            return $"{exception.Message}（找不到可执行文件：确认 .NET SDK 已安装，且 dotnet 在 PATH 中）";
        }

        if (exception is TimeoutException)
        {
            return $"{exception.Message}（操作超时，可稍后重试；进程已终止）";
        }

        return exception.Message;
    }
}
