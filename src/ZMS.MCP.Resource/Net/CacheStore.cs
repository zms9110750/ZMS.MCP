using System.Text.Json;

namespace ZMS.MCP.Resource.Net;

/// <summary>缓存里的一条。</summary>
/// <param name="Key">条目标识（随机 GUID）—— 不是凭据。</param>
/// <param name="Tool">哪次调用落下的。</param>
/// <param name="Args">那次调用的参数（给人看的）。</param>
/// <param name="Time">落下的时刻（UTC 刻度）。</param>
/// <param name="Result">这次落下了什么，例如 <c>正常</c>、<c>截断 N 字符</c>。</param>
/// <param name="Length">内容有多少字符。</param>
public sealed record CacheEntry(
    string Key,
    string Tool,
    string Args,
    long Time,
    string Result,
    int Length);

/// <summary>
/// 缓存：把**取回来的东西**（http 全文、FTP 读取）存起来，之后用 <c>key</c> 取。
///
/// **进缓存 ≠ 命中缓存** —— 这个库只负责存与取，没有任何工具会自动hitting它。
/// 条目是磁盘上的两个文件：<c>&lt;key&gt;.json</c>（元数据）与 <c>&lt;key&gt;.bin</c>（内容），
/// 落点在 <c>%LOCALAPPDATA%\ZMS.MCP\cache</c>（环境变量 <c>ZMS_MCP_CACHE</c> 可覆盖）。
/// 过期按 TTL 扫、超量按最旧淘汰。
/// </summary>
public static class CacheStore
{
    /// <summary>列缓存时一页多少条。</summary>
    public const int PageSize = 50;

    /// <summary>取一条缓存内容时，一次最多给这么多字符。</summary>
    public const int ReadLimit = 5000;

    /// <summary>缓存存活时间：到点就删。</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    /// <summary>容量上限：超了按最旧的淘汰。</summary>
    public const long Capacity = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = false };

    private static int _swept;

    /// <summary>测试用：把缓存落点指到别处。</summary>
    public static string? OverrideRoot { get; set; }

    /// <summary>缓存根目录。</summary>
    public static string Root
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OverrideRoot))
            {
                return OverrideRoot!;
            }

            string? custom = Environment.GetEnvironmentVariable("ZMS_MCP_CACHE");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ZMS.MCP",
                    "cache")
                : custom!;
        }
    }

    /// <summary>存一条，返回它的 <c>key</c>。</summary>
    public static string Put(string tool, string args, string content, string result)
    {
        SweepInBackground();
        Directory.CreateDirectory(Root);

        string key = Guid.NewGuid().ToString("D");
        CacheEntry entry = new(key, tool, args, DateTimeOffset.UtcNow.UtcTicks, result, content.Length);

        File.WriteAllText(EntryPath(key), JsonSerializer.Serialize(entry, Format), new UTF8Encoding(false));
        File.WriteAllText(ContentPath(key), content, new UTF8Encoding(false));
        EnforceCapacity();
        return key;
    }

    /// <summary>读一条的内容；没有就返回 <c>null</c>。</summary>
    public static string? Read(string key)
    {
        string path = ContentPath(key);
        return File.Exists(path) ? File.ReadAllText(path, new UTF8Encoding(false)) : null;
    }

    /// <summary>第 <paramref name="page"/> 页的条目（新的在前）。</summary>
    public static IReadOnlyList<CacheEntry> Page(int page)
    {
        return [.. All().Skip(page * PageSize).Take(PageSize)];
    }

    /// <summary>一共多少条。</summary>
    public static int Count()
    {
        return All().Count;
    }

    /// <summary>把这一条落盘到 <paramref name="path"/>；路径已存在就报错。</summary>
    public static string Save(string key, string path)
    {
        string? content = Read(key) ?? throw new ArgumentException($"没有这条缓存：{key}");
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new ArgumentException($"目标已经有东西了：{path}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>删掉过期的（TTL）与超量的（最旧优先）。</summary>
    public static void Sweep()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow - Ttl;
        foreach (CacheEntry entry in All())
        {
            if (new DateTimeOffset(entry.Time, TimeSpan.Zero) < deadline)
            {
                Remove(entry.Key);
            }
        }

        EnforceCapacity();
    }

    private static void SweepInBackground()
    {
        if (Interlocked.Exchange(ref _swept, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Sweep();
                }
                catch (IOException)
                {
                    // 扫不动就算了：下次 Put 还会来
                }
            });
        }
    }

    private static void EnforceCapacity()
    {
        List<CacheEntry> entries = [.. All()];
        long total = entries.Sum(entry => entry.Length);
        int index = entries.Count - 1;   // 最旧的先淘汰（Page/All 是新的在前）

        while (total > Capacity && index >= 0)
        {
            total -= entries[index].Length;
            Remove(entries[index].Key);
            index--;
        }
    }

    private static List<CacheEntry> All()
    {
        List<CacheEntry> entries = [];
        if (!Directory.Exists(Root))
        {
            return entries;
        }

        foreach (string file in Directory.EnumerateFiles(Root, "*.json", System.IO.SearchOption.TopDirectoryOnly))
        {
            try
            {
                CacheEntry? entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file, new UTF8Encoding(false)));
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // 坏的条目当不存在
            }
            catch (IOException)
            {
                // 正被别人动着，跳过
            }
        }

        entries.Sort((left, right) => right.Time.CompareTo(left.Time));
        return entries;
    }

    private static void Remove(string key)
    {
        TryDelete(EntryPath(key));
        TryDelete(ContentPath(key));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删不掉就留着，下次再说
        }
    }

    private static string EntryPath(string key)
    {
        return Path.Combine(Root, key + ".json");
    }

    private static string ContentPath(string key)
    {
        return Path.Combine(Root, key + ".bin");
    }
}
