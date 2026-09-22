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
            return "Error: " + exception.Message;
        }
    }
}
