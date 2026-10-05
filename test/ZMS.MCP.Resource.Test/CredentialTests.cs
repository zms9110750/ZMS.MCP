using Xunit;
using ZMS.MCP.Core.Credentials;

namespace ZMS.MCP.Resource.Test;

public class CredentialTests
{
    private static readonly DateTimeOffset When = DateTimeOffset.UnixEpoch;

    [Fact]
    public void 同样的输入算同样的凭据()
    {
        Assert.Equal(Cookie.Of(@"C:\a.txt", 10, When), Cookie.Of(@"C:\a.txt", 10, When));
    }

    [Fact]
    public void 大小变了凭据就变()
    {
        Assert.NotEqual(Cookie.Of(@"C:\a.txt", 10, When), Cookie.Of(@"C:\a.txt", 11, When));
    }

    [Fact]
    public void 修改时间变了凭据就变()
    {
        Assert.NotEqual(
            Cookie.Of(@"C:\a.txt", 10, When),
            Cookie.Of(@"C:\a.txt", 10, When.AddSeconds(1)));
    }

    [Fact]
    public void 读了全文时内容也进指纹()
    {
        Assert.NotEqual(
            Cookie.Of(@"C:\a.txt", 10, When, "甲", "utf-8"),
            Cookie.Of(@"C:\a.txt", 10, When, "乙", "utf-8"));
    }

    [Fact]
    public void 凭据是十六位十六进制()
    {
        string cookie = Cookie.Of(@"C:\a.txt", 10, When);

        Assert.Equal(16, cookie.Length);
        Assert.Matches("^[0-9a-f]{16}$", cookie);
    }

    [Fact]
    public void 比较时忽略空白与大小写()
    {
        Assert.True(Cookie.Matches(" abc123 ", "ABC123"));
        Assert.False(Cookie.Matches("abc123", "abc124"));
        Assert.False(Cookie.Matches(null, "abc123"));
    }

    [Fact]
    public void 路径规范化_去掉尾部分隔符()
    {
        Assert.Equal(
            Cookie.NormalizePath(@"C:\a"),
            Cookie.NormalizePath(@"C:\a\"));
    }

    [Fact]
    public void 读过全文的凭据是两段()
    {
        string cookie = Cookie.Of(@"C:\a.txt", 10, When, "甲", "utf-8");

        Assert.Equal((Cookie.Length * 2) + 1, cookie.Length);
        Assert.Equal(Cookie.Separator, cookie[Cookie.Length]);
        Assert.Matches("^[0-9a-f]{16}-[0-9a-f]{16}$", cookie);
    }

    [Fact]
    public void 两档凭据能互相校验()
    {
        string metadata = Cookie.Of(@"C:\a.txt", 10, When);
        string full = Cookie.Of(@"C:\a.txt", 10, When, "甲", "utf-8");

        Assert.True(Cookie.Matches(full, metadata));
        Assert.True(Cookie.Matches(metadata, full));
    }

    [Fact]
    public void 两边都有内容段时内容不同就对不上()
    {
        Assert.False(Cookie.Matches(
            Cookie.Of(@"C:\a.txt", 10, When, "甲", "utf-8"),
            Cookie.Of(@"C:\a.txt", 10, When, "乙", "utf-8")));
    }
}
