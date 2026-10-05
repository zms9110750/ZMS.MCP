using Xunit;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Test;

public class ListServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zms-resource-list-" + Guid.NewGuid().ToString("N"));

    public ListServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "子目录"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "甲");
        File.WriteAllText(Path.Combine(_root, "子目录", "b.txt"), "乙");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 完整穷举给目录树凭据()
    {
        string output = ListService.Run(new LocalAddress(_root), depth: 3, limit: 0, type: null, meta: null, regex: null);

        Assert.Contains("目录树凭据：`", output);
        Assert.Contains("条目总数：", output);
        Assert.Contains("总大小：", output);
    }

    [Fact]
    public void depth_为_0_只列当前层()
    {
        string output = ListService.Run(new LocalAddress(_root), depth: 0, limit: 0, type: null, meta: null, regex: null);

        Assert.Contains("a.txt", output);
        Assert.DoesNotContain("b.txt", output);
    }

    [Fact]
    public void 截断就不给目录树凭据()
    {
        string output = ListService.Run(new LocalAddress(_root), depth: 3, limit: 1, type: null, meta: null, regex: null);

        Assert.DoesNotContain("目录树凭据：`", output);
        Assert.Contains("不给目录树凭据", output);
    }

    [Fact]
    public void 指到文件给元数据和凭据()
    {
        string file = Path.Combine(_root, "a.txt");

        string output = ListService.Run(new LocalAddress(file), depth: 0, limit: 0, type: null, meta: null, regex: null);

        Assert.Contains("类型：文件", output);
        Assert.Contains("凭据：`", output);
    }

    [Fact]
    public void limit_按层递减()
    {
        Assert.Equal(200, ListService.LimitForLevel(0, 0));
        Assert.Equal(100, ListService.LimitForLevel(1, 0));
        Assert.Equal(50, ListService.LimitForLevel(2, 0));
        Assert.Equal(20, ListService.LimitForLevel(9, 0));
        Assert.Equal(200, ListService.LimitForLevel(0, 999));
        Assert.Equal(1, ListService.LimitForLevel(0, 1));
    }

    [Fact]
    public void 认不出的_meta_报错()
    {
        Assert.Throws<ArgumentException>(() =>
            ListService.Run(new LocalAddress(_root), depth: 0, limit: 0, type: null, meta: "体积", regex: null));
    }

    [Fact]
    public void 只列文件或只要目录()
    {
        string files = ListService.Run(new LocalAddress(_root), depth: 1, limit: 0, type: "file", meta: null, regex: null);
        Assert.Contains("a.txt", files);
        Assert.DoesNotContain("子目录/", files);

        string dirs = ListService.Run(new LocalAddress(_root), depth: 1, limit: 0, type: "dir", meta: null, regex: null);
        Assert.Contains("子目录/", dirs);
        Assert.DoesNotContain("a.txt", dirs);
    }
}
