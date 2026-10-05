namespace ZMS.MCP.Resource.Net;

/// <summary>
/// 下载的额度桶：额度 <see cref="Capacity"/>，每分钟恢复 <see cref="PerMinute"/>。
///
/// **只作用于下载**（<c>fetch</c> 给了 <c>targetPath</c> 的那一路）—— 取内容不受它管。
/// 目的不是替对方站点限速，而是别让 AI 一口气掏个巨物下来，把磁盘和带宽吃干。
/// 只活在进程内存里，不落盘。
/// </summary>
public static class DownloadBudget
{
    /// <summary>一共这么多字节可下。</summary>
    public const long Capacity = 10L * 1024 * 1024;

    /// <summary>每分钟恢复这么多字节。</summary>
    public const long PerMinute = 1L * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static double _available = Capacity;
    private static DateTimeOffset _lastRefill = DateTimeOffset.UtcNow;

    /// <summary>现在还剩多少字节。</summary>
    public static long Available
    {
        get
        {
            lock (Gate)
            {
                Refill();
                return (long)_available;
            }
        }
    }

    /// <summary>尝试扣掉这么多字节。</summary>
    /// <param name="bytes">要下多少。</param>
    /// <param name="waitFor">扣不动时，还要等多久才有这么多。</param>
    /// <returns>扣得动就是 <c>true</c>。</returns>
    public static bool TryTake(long bytes, out TimeSpan waitFor)
    {
        lock (Gate)
        {
            Refill();

            if (bytes <= _available)
            {
                _available -= bytes;
                waitFor = TimeSpan.Zero;
                return true;
            }

            double missing = bytes - _available;
            waitFor = TimeSpan.FromMinutes(missing / PerMinute);
            return false;
        }
    }

    /// <summary>桶里还剩多少、还要等多久才能凑出 <paramref name="bytes"/>（给人看的文案）。</summary>
    public static string Describe(long bytes)
    {
        lock (Gate)
        {
            Refill();
            if (bytes <= _available)
            {
                return $"额度还够（剩 {_available / (1024.0 * 1024.0):F2} MB）。";
            }

            double missing = bytes - _available;
            return $"额度不够：要 {bytes / (1024.0 * 1024.0):F2} MB，只剩 {_available / (1024.0 * 1024.0):F2} MB，"
                + $"大约还要等 {missing / PerMinute:F1} 分钟。";
        }
    }

    /// <summary>测试用：把桶灌满。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _available = Capacity;
            _lastRefill = DateTimeOffset.UtcNow;
        }
    }

    private static void Refill()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        double minutes = (now - _lastRefill).TotalMinutes;
        if (minutes <= 0)
        {
            return;
        }

        _available = Math.Min(Capacity, _available + (minutes * PerMinute));
        _lastRefill = now;
    }
}
