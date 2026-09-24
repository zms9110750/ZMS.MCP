namespace ZMS.MCP.Csharp.Draft;

/// <summary>许可快照里的一项：符号 → 落盘现场算出来的文件路径 + 当时的文件字节 hash。</summary>
public sealed record SymbolPermitSnapshot(string FilePath, string FileHash);

/// <summary>选择许可：某个符号在"被查看时"的三方内容。</summary>
public sealed record SelectPermit(string Cookie, string SymbolKey, string Snapshot, string Draft, string Disk);

/// <summary>
/// 落盘许可与选择许可：**只放内存，不持久化**（要立刻用；MCP 重启即全部失效）。
/// 见 `docs/Csharp-拟定流程v3.md` 第二节与第四节。
/// </summary>
public static class PermitStore
{
    private static readonly object Gate = new();

    /// <summary>落盘许可：project_path → （cookie + 许可快照）。</summary>
    private static readonly Dictionary<string, ApplyPermit> Applies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>选择许可：`project_path\u0001symbol` → 选择许可。</summary>
    private static readonly Dictionary<string, SelectPermit> Selects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>拟定变了 / 落盘了 / 取消追踪了 → 该项目的两种许可都作废。</summary>
    public static void Invalidate(string projectPath)
    {
        string key = Normalize(projectPath);
        lock (Gate)
        {
            Applies.Remove(key);
            foreach (string selectKey in Selects.Keys
                .Where(item => item.StartsWith(key + "\u0001", StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                Selects.Remove(selectKey);
            }
        }
    }

    /// <summary>该项目是否有落盘许可（用来区分"忘了预检"和"预检已过期"两种报错）。</summary>
    public static bool HasApply(string projectPath)
    {
        lock (Gate)
        {
            return Applies.ContainsKey(Normalize(projectPath));
        }
    }

    /// <summary>记录（或轮换）落盘许可，返回新的 applyCookie。</summary>
    public static string GrantApply(string projectPath, IReadOnlyDictionary<string, SymbolPermitSnapshot> snapshot)
    {
        string key = Normalize(projectPath);
        string cookie = Guid.NewGuid().ToString("D");
        lock (Gate)
        {
            Applies[key] = new ApplyPermit(cookie, snapshot);
        }

        return cookie;
    }

    /// <summary>校验落盘许可：cookie 对得上就返回许可快照，否则 null。</summary>
    public static IReadOnlyDictionary<string, SymbolPermitSnapshot>? CheckApply(string projectPath, string cookie)
    {
        string key = Normalize(projectPath);
        lock (Gate)
        {
            if (!Applies.TryGetValue(key, out ApplyPermit? permit))
            {
                return null;
            }

            return string.Equals(permit.Cookie, (cookie ?? "").Trim(), StringComparison.Ordinal)
                ? permit.Snapshot
                : null;
        }
    }

    /// <summary>落盘成功后清掉该项目的所有许可。</summary>
    public static void Clear(string projectPath)
    {
        Invalidate(projectPath);
    }

    /// <summary>为某个符号发选择许可（`get_member` 遇到冲突时），返回新 cookie。</summary>
    public static string GrantSelect(string projectPath, string symbolKey, string snapshot, string draft, string disk)
    {
        string key = SelectKey(projectPath, symbolKey);
        string cookie = Guid.NewGuid().ToString("D");
        lock (Gate)
        {
            Selects[key] = new SelectPermit(cookie, symbolKey, snapshot, draft, disk);
        }

        return cookie;
    }

    /// <summary>取选择许可：cookie 与符号都对得上才给（取完即用掉）。</summary>
    public static SelectPermit? TakeSelect(string projectPath, string symbolKey, string cookie)
    {
        string key = SelectKey(projectPath, symbolKey);
        lock (Gate)
        {
            if (!Selects.TryGetValue(key, out SelectPermit? permit))
            {
                return null;
            }

            if (!string.Equals(permit.Cookie, (cookie ?? "").Trim(), StringComparison.Ordinal))
            {
                return null;
            }

            Selects.Remove(key);
            return permit;
        }
    }

    private static string SelectKey(string projectPath, string symbolKey)
    {
        return Normalize(projectPath) + "\u0001" + symbolKey;
    }

    private static string Normalize(string projectPath)
    {
        return Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
    }

    private sealed record ApplyPermit(string Cookie, IReadOnlyDictionary<string, SymbolPermitSnapshot> Snapshot);
}
