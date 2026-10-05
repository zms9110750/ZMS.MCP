using System.ComponentModel;
using FluentFTP;
using ModelContextProtocol.Server;
using ZMS.MCP.Resource.Ftp;

namespace ZMS.MCP.Resource.Tools;

/// <summary>FTP 会话：登录拿句柄，之后把它填进 <c>target</c>。</summary>
[McpServerToolType]
public static class FtpTools
{
    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description(
        "Open an FTP session and get its handle back - put that handle into target of the other tools. The password is " +
        "kept in memory only: never written to disk, never logged, and never shown in listings (they show the account " +
        "and host). There is a limit on how many sessions can be open at once, and idle sessions close themselves. " +
        "Note that an archive on FTP cannot be tracked.")]
    public static Task<string> Login(
        [Description("Host name or address, e.g. 'ftp.example.com'.")] string host,
        [Description("Port. Default 21.")] int port = FtpSessions.DefaultPort,
        [Description("User name. Empty = anonymous.")] string? user = null,
        [Description("Password. Empty = anonymous. Kept in memory only.")] string? password = null)
    {
        return ToolGuard.RunAsync(async () =>
        {
            string handle = await FtpSessions.LoginAsync(host, port, user, password).ConfigureAwait(false);

            StringBuilder builder = new();
            builder.AppendLine("# 已登录");
            builder.AppendLine($"- `{FtpSessions.Prefix}{handle}`");
            builder.AppendLine($"- 现在开着 {FtpSessions.Count} 个会话（上限 {FtpSessions.Max}）");
            builder.AppendLine("- 把这个句柄填进 `target`，例如 `target = 'ftp:" + handle + "'`。");
            return builder.ToString();
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Close an FTP session. With a session handle only that one closes; with nothing, every open session closes and " +
        "the reply says how many - that is the only call that works when you no longer know the handles.")]
    public static Task<string> Logout(
        [Description("Session handle, e.g. 'ftp:s1a2b3c4d'. Empty = close every session.")] string? session = null)
    {
        return ToolGuard.RunAsync(() => FtpSessions.LogoutAsync(session));
    }

    /// <summary>给别的工具用：按会话句柄要一条打开的连接。</summary>
    public static AsyncFtpClient Client(string session)
    {
        return FtpSessions.Require(session).Client;
    }
}
