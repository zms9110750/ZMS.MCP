namespace ZMS.MCP.Resource.Tools;

/// <summary>
/// 把异常统一转成工具可读的文本结果。
///
/// 失败一律以 <see cref="McpStdioServer.FailurePrefix"/> 开头 —— 那是"这次调用失败了"的约定：
/// 过滤器看到它就把结果的 <c>IsError</c> 置为 true，调用方不必靠读文本猜。
/// </summary>
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
            return McpStdioServer.FailurePrefix + exception.Message;
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
            return McpStdioServer.FailurePrefix + exception.Message;
        }
    }
}
