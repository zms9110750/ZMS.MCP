namespace ZMS.MCP.Resource.Targeting;

/// <summary>
/// 寻址：**在哪个存储/容器上操作**（<c>target</c>）+ **它里面的路径**（<c>path</c>）。
///
/// - <c>target</c> 空 → 本地文件系统，<c>path</c> 是全称（可带盘符）；
/// - <c>ftp:</c> 开头 → FTP 会话，<c>path</c> 是远端纯路径；
/// - 其余非空 → 压缩包，<c>path</c> 是压缩包内的纯路径（用 <c>/</c> 分隔）。
///
/// **认不出来就报错，绝不降级到本地** —— 一降级就会落到同名的本地文件上。
/// </summary>
public abstract record Address(string InnerPath)
{
    /// <summary>会话句柄的前缀。</summary>
    public const string SessionPrefix = "ftp:";

    /// <summary>压缩包的扩展名（<c>target</c> 非空且不是会话时，必须命中其中之一）。</summary>
    private static readonly string[] ArchiveExtensions =
        [".zip", ".7z", ".tgz", ".tar", ".gz", ".zstd", ".ztsd", ".nupkg"];

    /// <summary>解析一次寻址。</summary>
    /// <exception cref="ArgumentException">认不出 <paramref name="target"/>，或路径形态不对。</exception>
    public static Address Parse(string? target, string path)
    {
        string inner = (path ?? "").Trim();
        if (inner.Length == 0)
        {
            throw new ArgumentException("path 不能为空。");
        }

        string text = (target ?? "").Trim();
        if (text.Length == 0)
        {
            return new LocalAddress(inner);
        }

        if (IsSession(text))
        {
            string session = text[SessionPrefix.Length..].Trim();
            if (session.Length == 0)
            {
                throw new ArgumentException($"会话句柄是空的：{text}（应该形如 {SessionPrefix}<session>）。");
            }

            return new SessionAddress(session, inner);
        }

        if (!IsArchivePath(text))
        {
            throw new ArgumentException(
                $"认不出这个 target：{text} —— 空 = 本地，{SessionPrefix} 开头 = FTP 会话，"
                + $"其余必须是压缩包路径（{string.Join(" / ", ArchiveExtensions)}）。");
        }

        if (inner.Contains(':') || inner.Contains('\\'))
        {
            throw new ArgumentException($"压缩包内的路径必须是纯路径（用 / 分隔、不带盘符）：{inner}");
        }

        return new ArchiveAddress(text, inner);
    }

    /// <summary>这个值是不是压缩包路径。</summary>
    public static bool IsArchivePath(string target)
    {
        return ArchiveExtensions.Any(extension =>
            target.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>这个值是不是 FTP 会话句柄。</summary>
    public static bool IsSession(string target)
    {
        return target.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>本地文件系统上的对象。<see cref="Address.InnerPath"/> 是全称路径。</summary>
public sealed record LocalAddress(string InnerPath) : Address(InnerPath);

/// <summary>FTP 会话里的对象。<see cref="Address.InnerPath"/> 是远端纯路径。</summary>
public sealed record SessionAddress(string Session, string InnerPath) : Address(InnerPath);

/// <summary>
/// 压缩包里的对象。<see cref="ArchivePath"/> 是压缩包文件本身，
/// <see cref="Address.InnerPath"/> 是压缩包内的纯路径。
/// </summary>
public sealed record ArchiveAddress(string ArchivePath, string InnerPath) : Address(InnerPath);
