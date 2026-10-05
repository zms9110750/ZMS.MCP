using System.Text;
using Xunit;
using ZMS.MCP.Core.Credentials;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Test;

public class WriteServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zms-resource-write-" + Guid.NewGuid().ToString("N"));

    public WriteServiceTests()
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

    private string Under(string name)
    {
        return Path.Combine(_root, name);
    }

    private string CookieOf(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        (string text, Encoding used) = EncodingRules.Decode(bytes, null);
        return Cookie.Of(path, bytes.LongLength, File.GetLastWriteTimeUtc(path), text, used.WebName);
    }

    [Fact]
    public void 新建不要凭据()
    {
        string path = Under("新文件.txt");

        string output = WriteService.Write(new LocalAddress(path), "内容", null, null);

        Assert.Contains("已写入（新建）", output);
        Assert.Equal("内容", File.ReadAllText(path, new UTF8Encoding(false)));
    }

    [Fact]
    public void 覆写没凭据只出影响清单()
    {
        string path = Under("已有.txt");
        File.WriteAllText(path, "旧内容");

        string output = WriteService.Write(new LocalAddress(path), "新内容", null, null);

        Assert.Contains("会发生什么（未执行）", output);
        Assert.Contains("不算失败", output);
        Assert.Equal("旧内容", File.ReadAllText(path));
    }

    [Fact]
    public void 覆写凭据对不上就不写()
    {
        string path = Under("变了.txt");
        File.WriteAllText(path, "旧内容");

        string output = WriteService.Write(new LocalAddress(path), "新内容", null, "0123456789abcdef");

        Assert.Contains("凭据对不上", output);
        Assert.Equal("旧内容", File.ReadAllText(path));
    }

    [Fact]
    public void 覆写凭据对得上就写并给下一个凭据()
    {
        string path = Under("照写.txt");
        File.WriteAllText(path, "旧内容");

        string output = WriteService.Write(new LocalAddress(path), "新内容", null, CookieOf(path));

        Assert.Contains("已写入", output);
        Assert.Contains("下一个凭据", output);
        Assert.Equal("新内容", File.ReadAllText(path));
    }

    [Fact]
    public void 替换字面命中一处()
    {
        string path = Under("替换.txt");
        File.WriteAllText(path, "甲和乙");

        string output = WriteService.Replace(new LocalAddress(path), "甲", "丙", regex: false, null, CookieOf(path));

        Assert.Contains("已替换", output);
        Assert.Equal("丙和乙", File.ReadAllText(path));
    }

    [Fact]
    public void 替换命中多处不执行()
    {
        string path = Under("多处.txt");
        File.WriteAllText(path, "甲甲甲");

        string output = WriteService.Replace(new LocalAddress(path), "甲", "丙", regex: false, null, CookieOf(path));

        Assert.Contains("命中多处", output);
        Assert.Equal("甲甲甲", File.ReadAllText(path));
    }

    [Fact]
    public void 替换没命中的是错误()
    {
        string path = Under("没命中.txt");
        File.WriteAllText(path, "甲");

        Assert.Throws<ArgumentException>(() =>
            WriteService.Replace(new LocalAddress(path), "丁", "丙", regex: false, null, CookieOf(path)));
    }

    [Fact]
    public void 替换可以用正则和捕获组()
    {
        string path = Under("正则.txt");
        File.WriteAllText(path, "name=zms");

        string output = WriteService.Replace(new LocalAddress(path), @"name=(\w+)", "name[$1]", regex: true, null, CookieOf(path));

        Assert.Contains("已替换", output);
        Assert.Equal("name[zms]", File.ReadAllText(path));
    }

    [Fact]
    public void 替换也要凭据()
    {
        string path = Under("要凭据.txt");
        File.WriteAllText(path, "甲");

        string output = WriteService.Replace(new LocalAddress(path), "甲", "丙", regex: false, null, "0123456789abcdef");

        Assert.Contains("凭据对不上", output);
        Assert.Equal("甲", File.ReadAllText(path));
    }
}
