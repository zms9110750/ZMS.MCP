using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;
using Xunit;
using ZMS.MCP.Resource.Archive;

namespace ZMS.MCP.Resource.Test;

[Collection("zms-drafts")]
public class ArchiveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zms-archive-" + Guid.NewGuid().ToString("N"));

    public ArchiveServiceTests()
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

    private string MakeEmpty(string name)
    {
        string path = Under(name);
        using FileStream output = File.Create(path);
        using IWriter writer = WriterFactory.OpenWriter(
            output,
            ArchiveType.Zip,
            new ZipWriterOptions(CompressionType.Deflate));
        return path;
    }

    [Fact]
    public void 认格式()
    {
        Assert.True(ArchiveService.IsSupported("zip"));
        Assert.True(ArchiveService.IsSupported("7Z"));
        Assert.True(ArchiveService.IsSupported("tgz"));
        Assert.True(ArchiveService.IsSupported("ztsd"));
        Assert.False(ArchiveService.IsSupported("rar"));
    }

    [Fact]
    public void ztsd_能落盘也能读回来()
    {
        string archive = Under("压.zst");
        string cookie = DraftStore.Track(archive, "ztsd", 0, 0);

        ArchiveService.Stage(archive, "add", "份/一.txt", Encoding.UTF8.GetBytes("zstd 内容"), null);
        string done = ArchiveService.Apply(cookie, ArchiveService.ApplyCookieFor(cookie));

        Assert.Contains("已落盘", done);
        Assert.True(new FileInfo(archive).Length > 0);

        byte[]? body = ArchiveService.ReadEntry(archive, "份/一.txt");
        Assert.NotNull(body);
        Assert.Equal("zstd 内容", Encoding.UTF8.GetString(body!));
    }

    [Fact]
    public void 还不存在的包能建出来_拟定_落盘_再读回来()    {
        string archive = Under("新建.zip");
        string cookie = DraftStore.Track(archive, "zip", 0, 0);

        string staged = ArchiveService.Stage(archive, "add", "里面/一.txt", Encoding.UTF8.GetBytes("内容"), null);
        Assert.Contains("拟定已更新", staged);

        string preview = ArchiveService.Preview(cookie);
        Assert.Contains("增加 1 条", preview);
        Assert.Contains("里面/一.txt", preview);

        string done = ArchiveService.Apply(cookie, ArchiveService.ApplyCookieFor(cookie));
        Assert.Contains("已落盘", done);

        byte[]? body = ArchiveService.ReadEntry(archive, "里面/一.txt");
        Assert.NotNull(body);
        Assert.Equal("内容", Encoding.UTF8.GetString(body!));
        Assert.Empty(DraftStore.DraftsOf(archive));
    }

    [Fact]
    public void 覆盖包里已有条目要它自己的凭据()
    {
        string archive = MakeEmpty("已有.zip");
        string cookie = DraftStore.Track(
            archive, "zip", new FileInfo(archive).Length, File.GetLastWriteTimeUtc(archive).Ticks);

        ArchiveService.Stage(archive, "add", "a.txt", Encoding.UTF8.GetBytes("甲"), null);
        ArchiveService.Apply(cookie, ArchiveService.ApplyCookieFor(cookie));

        string without = ArchiveService.Stage(archive, "add", "a.txt", Encoding.UTF8.GetBytes("乙"), null);
        Assert.Contains("需要凭据", without);

        string itemCookie = ArchiveService.ListEntries(archive)[0].Cookie;
        string with = ArchiveService.Stage(archive, "add", "a.txt", Encoding.UTF8.GetBytes("乙"), itemCookie);
        Assert.Contains("拟定已更新", with);
    }

    [Fact]
    public void 拟定期间包被改过就拒绝落盘()
    {
        string archive = MakeEmpty("动过.zip");
        string cookie = DraftStore.Track(
            archive, "zip", new FileInfo(archive).Length, File.GetLastWriteTimeUtc(archive).Ticks);

        ArchiveService.Stage(archive, "add", "x.txt", Encoding.UTF8.GetBytes("x"), null);
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddMinutes(5));

        Assert.Throws<ArgumentException>(() =>
            ArchiveService.Apply(cookie, ArchiveService.ApplyCookieFor(cookie)));
    }

    [Fact]
    public void 删除条目也走拟定()
    {
        string archive = MakeEmpty("删.zip");
        string cookie = DraftStore.Track(
            archive, "zip", new FileInfo(archive).Length, File.GetLastWriteTimeUtc(archive).Ticks);

        ArchiveService.Stage(archive, "add", "b.txt", Encoding.UTF8.GetBytes("乙"), null);
        ArchiveService.Apply(cookie, ArchiveService.ApplyCookieFor(cookie));

        string itemCookie = ArchiveService.ListEntries(archive)[0].Cookie;
        ArchiveService.Stage(archive, "delete", "b.txt", null, itemCookie);
        ArchiveService.Apply(cookie, ArchiveService.ApplyCookieFor(cookie));

        Assert.Empty(ArchiveService.ListEntries(archive));
    }

    [Fact]
    public void 没追踪的包查不到()
    {
        Assert.Null(DraftStore.FindByPath(Under("没有.zip")));
    }

    [Fact]
    public void 解除追踪会把拟定一并清掉()
    {
        string archive = MakeEmpty("解除.zip");
        string cookie = DraftStore.Track(
            archive, "zip", new FileInfo(archive).Length, File.GetLastWriteTimeUtc(archive).Ticks);

        ArchiveService.Stage(archive, "add", "c.txt", Encoding.UTF8.GetBytes("丙"), null);
        TrackedArchive tracked = ArchiveService.Require(cookie);
        Assert.Equal(1, DraftStore.Untrack(tracked));
        Assert.Null(DraftStore.FindByPath(archive));
    }
}
