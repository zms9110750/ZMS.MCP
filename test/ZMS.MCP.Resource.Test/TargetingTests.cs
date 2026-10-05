using Xunit;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Test;

public class TargetingTests
{
    [Fact]
    public void 空_target_是本地()
    {
        Address address = Address.Parse(null, @"C:\a\b.txt");

        Assert.IsType<LocalAddress>(address);
    }

    [Fact]
    public void ftp_前缀是会话()
    {
        Address address = Address.Parse("ftp:s1", "dir/x.txt");

        Assert.Equal("s1", Assert.IsType<SessionAddress>(address).Session);
    }

    [Fact]
    public void 其余非空_target_当压缩包()
    {
        Address address = Address.Parse(@"D:\a.zip", "dir/x.txt");

        ArchiveAddress archive = Assert.IsType<ArchiveAddress>(address);
        Assert.Equal(@"D:\a.zip", archive.ArchivePath);
        Assert.Equal("dir/x.txt", archive.InnerPath);
    }

    [Fact]
    public void 认不出来的_target_报错_不降级到本地()
    {
        Assert.Throws<ArgumentException>(() => Address.Parse("随便什么", "dir/x.txt"));
    }

    [Fact]
    public void 空的会话句柄报错()
    {
        Assert.Throws<ArgumentException>(() => Address.Parse("ftp:", "dir/x.txt"));
    }

    [Fact]
    public void 压缩包内的路径必须是纯路径()
    {
        Assert.Throws<ArgumentException>(() => Address.Parse(@"D:\a.zip", @"C:\x.txt"));
        Assert.Throws<ArgumentException>(() => Address.Parse(@"D:\a.zip", @"dir\x.txt"));
    }

    [Fact]
    public void path_为空报错()
    {
        Assert.Throws<ArgumentException>(() => Address.Parse(null, "  "));
    }
}
