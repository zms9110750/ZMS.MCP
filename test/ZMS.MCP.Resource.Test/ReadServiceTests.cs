using System.Text;
using Xunit;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Test;

public class ReadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zms-resource-" + Guid.NewGuid().ToString("N"));

    public ReadServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string WriteFile(string name, string content, Encoding? encoding = null)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(content));
        return path;
    }

    [Fact]
    public void 完整读给凭据()
    {
        string path = WriteFile("a.txt", "你好");

        string output = ReadService.Run(new LocalAddress(path), 0, 0, 0, 0, null, null);

        Assert.Contains("凭据", output);
        Assert.Contains("你好", output);
    }

    [Fact]
    public void 带范围读不给凭据()
    {
        string path = WriteFile("b.txt", "第一行\n第二行\n第三行");

        string output = ReadService.Run(new LocalAddress(path), 0, 0, 0, 2, null, null);

        Assert.Contains("不给凭据", output);
        Assert.DoesNotContain("凭据：`", output);
    }

    [Fact]
    public void 读不完整份不给凭据()
    {
        string path = WriteFile("big.txt", new string('x', ReadService.DeliveryLimit + 100));

        string output = ReadService.Run(new LocalAddress(path), 0, 0, 0, 0, null, null);

        Assert.Contains("没交付完整份", output);
        Assert.Contains($"全文共 {ReadService.DeliveryLimit + 100} 字符", output);
    }

    [Fact]
    public void 非_utf8_文件能按_gbk_读()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = WriteFile("gbk.txt", "中文内容", Encoding.GetEncoding("gbk"));

        string output = ReadService.Run(new LocalAddress(path), 0, 0, 0, 0, "gbk", null);

        Assert.Contains("中文内容", output);
    }

    [Fact]
    public void 非_utf8_文件不给编码就拒绝()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = WriteFile("gbk2.txt", "中文内容", Encoding.GetEncoding("gbk"));

        Assert.Throws<ArgumentException>(() =>
            ReadService.Run(new LocalAddress(path), 0, 0, 0, 0, null, null));
    }

    [Fact]
    public void 正则命中分级_少于等于五行给上下三行()
    {
        string path = WriteFile("c.txt", "0\n1\n2\n目标\n4\n5\n6");

        string output = ReadService.Run(new LocalAddress(path), 0, 0, 0, 0, null, "目标");

        Assert.Contains("命中 1 处", output);
        Assert.Contains("上下 3 行", output);
    }

    [Fact]
    public void 目录报错()
    {
        Assert.Throws<ArgumentException>(() =>
            ReadService.Run(new LocalAddress(_root), 0, 0, 0, 0, null, null));
    }
}
