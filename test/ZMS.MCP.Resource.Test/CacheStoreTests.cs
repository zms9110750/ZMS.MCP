using Xunit;
using ZMS.MCP.Resource.Net;

namespace ZMS.MCP.Resource.Test;

[Collection("zms-cache")]
public class CacheStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zms-cache-" + Guid.NewGuid().ToString("N"));

    public CacheStoreTests()
    {
        CacheStore.OverrideRoot = _root;
    }

    public void Dispose()
    {
        CacheStore.OverrideRoot = null;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 存了能取回来()
    {
        string key = CacheStore.Put("fetch", "https://example.com", "内容甲", "正常");

        Assert.Equal("内容甲", CacheStore.Read(key));
        Assert.Equal(1, CacheStore.Count());
    }

    [Fact]
    public void 没这条就返回空()
    {
        Assert.Null(CacheStore.Read("00000000-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void 列表分页每页五十条()
    {
        for (int index = 0; index < 55; index++)
        {
            CacheStore.Put("fetch", $"第 {index}", "x", "正常");
        }

        Assert.Equal(50, CacheStore.Page(0).Count);
        Assert.Equal(5, CacheStore.Page(1).Count);
        Assert.Equal(55, CacheStore.Count());
    }

    [Fact]
    public void 落盘后目标必须不存在()
    {
        string key = CacheStore.Put("fetch", "https://example.com", "内容乙", "正常");
        string where = Path.Combine(_root, "落盘.txt");

        string saved = CacheStore.Save(key, where);

        Assert.Equal("内容乙", File.ReadAllText(saved));
        Assert.Throws<ArgumentException>(() => CacheStore.Save(key, where));
    }

    [Fact]
    public void 过期的会被扫掉()
    {
        string key = CacheStore.Put("fetch", "https://example.com", "旧内容", "正常");
        string entry = Path.Combine(_root, key + ".json");

        // 把条目的时间戳推到 TTL 之外
        long stale = (DateTimeOffset.UtcNow - CacheStore.Ttl - TimeSpan.FromDays(1)).UtcTicks;
        string json = File.ReadAllText(entry);
        File.WriteAllText(entry, System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"Time\":-?[0-9]+",
            $"\"Time\":{stale}"));

        CacheStore.Sweep();

        Assert.Null(CacheStore.Read(key));
        Assert.Equal(0, CacheStore.Count());
    }

    [Fact]
    public void 超出容量按最旧的淘汰()
    {
        // 每条 1 MB，塞 70 条 —— 超过 64 MB 上限
        string big = new('x', 1024 * 1024);
        for (int index = 0; index < 70; index++)
        {
            CacheStore.Put("fetch", $"第 {index}", big, "正常");
        }

        Assert.True(CacheStore.Count() < 70, $"应该淘汰掉一些，实际还剩 {CacheStore.Count()} 条");
    }
}
