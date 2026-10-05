using Xunit;
using ZMS.MCP.Resource.Browser;

namespace ZMS.MCP.Resource.Test;

/// <summary>
/// 只测参数校验那一层 —— 这些分支在启动浏览器**之前**就返回，所以不会在测试里弹出窗口。
/// 真正的渲染要现场有 Edge/Chrome，测试里跑不了。
/// </summary>
public class BrowseServiceTests
{
    [Fact]
    public async Task 不是_http_地址就报错()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            BrowseService.RunAsync("file:///C:/a.html", null, null, 60));
    }

    [Fact]
    public async Task 空的_url_就报错()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            BrowseService.RunAsync("", null, null, 60));
    }

    [Fact]
    public async Task 认不出的_action_就报错()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            BrowseService.RunAsync("https://example.com", "scroll", null, 60));
    }

    [Fact]
    public async Task timeout_必须大于零()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            BrowseService.RunAsync("https://example.com", null, null, 0));
    }

    [Fact]
    public void 默认超时是六十秒()
    {
        Assert.Equal(60, BrowseService.DefaultTimeoutSeconds);
        Assert.Equal(60, BrowseService.VerificationSeconds);
    }
}
