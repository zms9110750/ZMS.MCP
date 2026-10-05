using Xunit;
using ZMS.MCP.Resource.Archive;
using ZMS.MCP.Resource.Targeting;
using ZMS.MCP.Resource.Tools;

namespace ZMS.MCP.Resource.Test;

[Collection("zms-drafts")]
public class ResolveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zms-resolve-" + Guid.NewGuid().ToString("N"));

    public ResolveTests()
    {
        Directory.CreateDirectory(_root);
        DraftStore.OverrideLocation = Path.Combine(_root, "drafts.db");
    }

    public void Dispose()
    {
        DraftStore.OverrideLocation = null;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string Under(string name)
    {
        return Path.Combine(_root, name);
    }

    [Fact]
    public void 追踪_cookie_当_target_就指到那个包()
    {
        string archive = Under("包.zip");
        string cookie = DraftStore.Track(archive, "zip", 0, 0);

        Address address = Resolve.Address(cookie, "里面/一.txt");

        ArchiveAddress into = Assert.IsType<ArchiveAddress>(address);
        Assert.Equal(archive, into.ArchivePath);
        Assert.Equal("里面/一.txt", into.InnerPath);
    }

    [Fact]
    public void 不是追踪_cookie_就照常解析()
    {
        Assert.IsType<LocalAddress>(Resolve.Address(null, Under("普通.txt")));
        Assert.IsType<SessionAddress>(Resolve.Address("ftp:s1234", "/remote/a.txt"));
        Assert.IsType<ArchiveAddress>(Resolve.Address(Under("别的.zip"), "内.txt"));
    }

    [Fact]
    public void 认不出的_target_照样报错()
    {
        Assert.Throws<ArgumentException>(() => Resolve.Address("随便什么东西", "a.txt"));
    }
}
