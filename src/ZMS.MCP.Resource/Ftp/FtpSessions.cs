using FluentFTP;

namespace ZMS.MCP.Resource.Ftp;

/// <summary>一个打开的 FTP 会话。</summary>
/// <param name="Handle">句柄，填进 <c>target</c> 的就是它。</param>
/// <param name="Display">给人看的描述 —— **密码不在这里**。</param>
/// <param name="Client">底层连接。</param>
public sealed class FtpSession(string handle, string display, AsyncFtpClient client)
{
    /// <summary>句柄。</summary>
    public string Handle { get; } = handle;

    /// <summary>描述（打码过）。</summary>
    public string Display { get; } = display;

    /// <summary>底层连接。</summary>
    public AsyncFtpClient Client { get; } = client;

    /// <summary>最后一次用它的时刻。</summary>
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// FTP 会话表（<c>login</c> / <c>logout</c>）。
///
/// 句柄形如 <c>ftp:&lt;session&gt;</c>，之后所有操作把它填进 <c>target</c>。
/// **密码只在内存里**：不落盘、不进日志、列表里也不出现（只显示到用户名）。
/// 会话有上限、也有空闲超时，到点自己关。
/// </summary>
public static class FtpSessions
{
    /// <summary>句柄前缀。</summary>
    public const string Prefix = "ftp:";

    /// <summary>默认端口。</summary>
    public const int DefaultPort = 21;

    /// <summary>同时能开多少个会话。</summary>
    public const int Max = 16;

    /// <summary>多久不用就自己关掉。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, FtpSession> Open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>现在开着几个会话。</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Open.Count;
            }
        }
    }

    /// <summary>登录，拿到句柄。</summary>
    public static async Task<string> LoginAsync(string host, int port, string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("host 不能是空的。");
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentException($"port 要在 1–65535 之间，给的是：{port}");
        }

        string account = string.IsNullOrWhiteSpace(user) ? "anonymous" : user!.Trim();
        string secret = password ?? "";

        await CloseIdleAsync().ConfigureAwait(false);

        lock (Gate)
        {
            if (Open.Count >= Max)
            {
                throw new ArgumentException($"会话已经有 {Max} 个了 —— 先 `logout` 关掉一些再登。");
            }
        }

        AsyncFtpClient client = new(host.Trim(), account, secret, port);
        try
        {
            await client.Connect().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            client.Dispose();
            throw new ArgumentException($"连不上 {host.Trim()}:{port} —— {exception.Message}");
        }

        string handle = "s" + Guid.NewGuid().ToString("N")[..8];
        string display = $"{Prefix}{handle}（{account}@{host.Trim()}:{port}）";

        lock (Gate)
        {
            Open[handle] = new FtpSession(handle, display, client);
        }

        return handle;
    }

    /// <summary>关掉一个会话；<paramref name="handle"/> 为空就关掉全部。</summary>
    public static async Task<string> LogoutAsync(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            List<FtpSession> all;
            lock (Gate)
            {
                all = [.. Open.Values];
                Open.Clear();
            }

            foreach (FtpSession session in all)
            {
                await CloseQuietlyAsync(session).ConfigureAwait(false);
            }

            return all.Count == 0 ? "# 已登出\n- 本来就一个会话也没开着。\n" : $"# 已登出\n- 关掉了 {all.Count} 个会话。\n";
        }

        string key = Trim(handle);
        FtpSession? found;
        lock (Gate)
        {
            found = Open.Remove(key, out FtpSession? session) ? session : null;
        }

        if (found == null)
        {
            throw new ArgumentException($"没有这个会话：{handle}（不知道句柄时，`logout` 不带参数可以关掉全部）。");
        }

        await CloseQuietlyAsync(found).ConfigureAwait(false);
        return $"# 已登出\n- {found.Display}\n";
    }

    /// <summary>取出会话；没有就报错（并把现有句柄报出来，好让人接着用）。</summary>
    public static FtpSession Require(string target)
    {
        string key = Trim(target);
        lock (Gate)
        {
            if (Open.TryGetValue(key, out FtpSession? session))
            {
                session.LastUsed = DateTimeOffset.UtcNow;
                return session;
            }
        }

        throw new ArgumentException($"这个会话不在：{target}（用 `login` 开一个，或 `logout` 不带参数看看有哪些）。");
    }

    /// <summary>列出现在开着的会话（密码不出现）。</summary>
    public static IReadOnlyList<string> Descriptions()
    {
        lock (Gate)
        {
            return [.. Open.Values.Select(session => session.Display)];
        }
    }

    /// <summary>只去掉 <c>ftp:</c> 前缀。</summary>
    public static string Trim(string target)
    {
        string value = (target ?? "").Trim();
        return value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? value[Prefix.Length..] : value;
    }

    private static async Task CloseIdleAsync()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow - IdleTimeout;
        List<FtpSession> stale;
        lock (Gate)
        {
            stale = [.. Open.Values.Where(session => session.LastUsed < deadline)];
            foreach (FtpSession session in stale)
            {
                Open.Remove(session.Handle);
            }
        }

        foreach (FtpSession session in stale)
        {
            await CloseQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    private static async Task CloseQuietlyAsync(FtpSession session)
    {
        try
        {
            await session.Client.Disconnect().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 关不掉就算了
        }
        finally
        {
            session.Client.Dispose();
        }
    }
}
