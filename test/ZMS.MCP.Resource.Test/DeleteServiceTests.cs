using System.Text;
using Xunit;
using ZMS.MCP.Resource.Credentials;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Test;

public class DeleteServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zms-resource-delete-" + Guid.NewGuid().ToString("N"));

    public DeleteServiceTests()
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

    [Fact]
    public void 凭据对不上就不删()
    {
        string path = Under("留着.txt");
        File.WriteAllText(path, "别删我");

        string output = DeleteService.Run(new LocalAddress(path), "0123456789abcdef");

        Assert.Contains("凭据对不上", output);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void 不存在的对象报错()
    {
        Assert.Throws<ArgumentException>(() =>
            DeleteService.Run(new LocalAddress(Under("没有这个.txt")), "0123456789abcdef"));
    }

    [Fact]
    public void 凭据对得上就删进回收站()
    {
        string path = Under("删掉.txt");
        File.WriteAllText(path, "再见");
        byte[] bytes = File.ReadAllBytes(path);
        (string text, Encoding used) = EncodingRules.Decode(bytes, null);
        string cookie = Cookie.Of(path, bytes.LongLength, File.GetLastWriteTimeUtc(path), text, used.WebName);

        string output = DeleteService.Run(new LocalAddress(path), cookie);

        Assert.Contains("已删除（回收站）", output);
        Assert.False(File.Exists(path));
    }
}
