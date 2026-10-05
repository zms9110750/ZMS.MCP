using Xunit;
using ZMS.MCP.Resource.Ftp;

namespace ZMS.MCP.Resource.Test;

public class FtpSessionsTests
{
    [Fact]
    public void 去前缀()
    {
        Assert.Equal("s1234", FtpSessions.Trim("ftp:s1234"));
        Assert.Equal("s1234", FtpSessions.Trim("s1234"));
        Assert.Equal("S1234", FtpSessions.Trim("FTP:S1234"));
        Assert.Equal("", FtpSessions.Trim("ftp:"));
    }

    [Fact]
    public void 不知道的会话就报错()
    {
        Assert.Throws<ArgumentException>(() => FtpSessions.Require("ftp:nope"));
    }

    [Fact]
    public async Task 端口超范围就报错()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => FtpSessions.LoginAsync("example.com", 0, null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => FtpSessions.LoginAsync("example.com", 70000, null, null));
    }

    [Fact]
    public async Task 空主机就报错()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => FtpSessions.LoginAsync("   ", 21, null, null));
    }

    [Fact]
    public async Task 一个会话都没开时全部登出也能用()
    {
        string output = await FtpSessions.LogoutAsync(null);

        Assert.Contains("本来就一个会话也没开着", output);
    }

    [Fact]
    public async Task 登出不知道的会话就报错()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => FtpSessions.LogoutAsync("ftp:nope"));
    }

    [Fact]
    public void 默认端口是二十一()
    {
        Assert.Equal(21, FtpSessions.DefaultPort);
        Assert.Equal("ftp:", FtpSessions.Prefix);
    }
}
