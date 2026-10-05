using System.Net;
using System.Text;
using Xunit;
using ZMS.MCP.Resource.Net;

namespace ZMS.MCP.Resource.Test;

[Collection("zms-cache")]
public class FetchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zms-fetch-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _before;

    public FetchServiceTests()
    {
        Directory.CreateDirectory(_root);
        CacheStore.OverrideRoot = Path.Combine(_root, "cache");
        _before = FetchService.Client;
        DownloadBudget.Reset();
    }

    public void Dispose()
    {
        FetchService.Client = _before;
        CacheStore.OverrideRoot = null;
        DownloadBudget.Reset();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(answer(request));
        }
    }

    private void Use(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        FetchService.Client = new HttpClient(new FakeHandler(answer));
    }

    private static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, new UTF8Encoding(false), "text/plain"),
        };
    }

    [Fact]
    public async Task 取内容会全额进缓存并给_key()
    {
        string long_ = new('甲', FetchService.ResponseLimit + 500);
        Use(_ => Text(long_));

        string output = await FetchService.RunAsync("https://example.com/a", null, null, null, null, null);

        Assert.Contains("- key：`", output);
        Assert.Contains($"全文 {long_.Length} 字符", output);
        Assert.Equal(1, CacheStore.Count());
    }

    [Fact]
    public async Task 下载会落到磁盘且不进内容缓存()
    {
        Use(_ => Text("下下来的东西"));
        string where = Path.Combine(_root, "下载.txt");

        string output = await FetchService.RunAsync("https://example.com/b", null, null, null, where, null);

        Assert.Contains("# 已下载", output);
        Assert.Equal("下下来的东西", File.ReadAllText(where));

        // 只留一条**元数据**记录：条数有 1，但内容为空（原件已经在磁盘上了）
        Assert.Equal(1, CacheStore.Count());
        Assert.Equal("", CacheStore.Read(CacheStore.Page(0)[0].Key));
    }

    [Fact]
    public async Task 下载目标已有文件时要凭据()
    {
        Use(_ => Text("新的"));
        string where = Path.Combine(_root, "已有.txt");
        File.WriteAllText(where, "旧的");

        string output = await FetchService.RunAsync("https://example.com/c", null, null, null, where, null);

        Assert.Contains("会发生什么（未执行）", output);
        Assert.Equal("旧的", File.ReadAllText(where));
    }

    [Fact]
    public async Task Head_不给内容()
    {
        Use(_ => Text(""));
        string output = await FetchService.RunAsync("https://example.com/d", "HEAD", null, null, null, null);
        Assert.Contains("# HEAD", output);
    }

    [Fact]
    public async Task 不是_http_地址就报错()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            FetchService.RunAsync("file:///C:/a.txt", null, null, null, null, null));
    }

    [Fact]
    public async Task 认不出的方法就报错()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            FetchService.RunAsync("https://example.com", "TRACE", null, null, null, null));
    }

    [Fact]
    public async Task head_必须是_json_对象()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            FetchService.RunAsync("https://example.com", null, "不是json", null, null, null));
    }

    [Fact]
    public void 额度扣得动就扣_扣不动就报还要等多久()
    {
        DownloadBudget.Reset();

        Assert.True(DownloadBudget.TryTake(DownloadBudget.Capacity, out TimeSpan none));
        Assert.Equal(TimeSpan.Zero, none);

        Assert.False(DownloadBudget.TryTake(1024 * 1024, out TimeSpan waitFor));
        Assert.True(waitFor > TimeSpan.Zero);
    }
}
