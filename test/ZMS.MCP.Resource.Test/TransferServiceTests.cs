using Xunit;
using ZMS.MCP.Core.Credentials;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Test;

public class TransferServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zms-resource-move-" + Guid.NewGuid().ToString("N"));

    public TransferServiceTests()
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

    private static string FileCookie(string path)
    {
        return Cookie.Of(path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
    }

    private static string DirectoryCookie(string path)
    {
        long size = new DirectoryInfo(path).EnumerateFiles("*", System.IO.SearchOption.AllDirectories).Sum(file => file.Length);
        return Cookie.Of(path, size, Directory.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void 目标不存在直接移动()
    {
        string from = Under("a.txt");
        File.WriteAllText(from, "内容");
        string to = Under("b.txt");

        string output = TransferService.Move(new LocalAddress(from), to, null, null);

        Assert.Contains("已移动", output);
        Assert.False(File.Exists(from));
        Assert.Equal("内容", File.ReadAllText(to));
    }

    [Fact]
    public void 目标不存在直接复制()
    {
        string from = Under("源.txt");
        File.WriteAllText(from, "内容");
        string to = Under("副本.txt");

        string output = TransferService.Copy(new LocalAddress(from), to, null);

        Assert.Contains("已复制", output);
        Assert.True(File.Exists(from));
        Assert.Equal("内容", File.ReadAllText(to));
    }

    [Fact]
    public void 目标已有文件时先要凭据()
    {
        string from = Under("新.txt");
        File.WriteAllText(from, "新的");
        string to = Under("旧.txt");
        File.WriteAllText(to, "旧的");

        string output = TransferService.Copy(new LocalAddress(from), to, null);

        Assert.Contains("目标已有内容（未执行）", output);
        Assert.Contains("会被替换的文件", output);
        Assert.Contains("请确认意图", output);
        Assert.Equal("旧的", File.ReadAllText(to));
    }

    [Fact]
    public void 给了目标凭据就覆盖()
    {
        string from = Under("新2.txt");
        File.WriteAllText(from, "新的");
        string to = Under("旧2.txt");
        File.WriteAllText(to, "旧的");

        string output = TransferService.Copy(new LocalAddress(from), to, [FileCookie(to)]);

        Assert.Contains("已复制", output);
        Assert.Equal("新的", File.ReadAllText(to));
    }

    [Fact]
    public void 合并文件夹要目标目录的凭据()
    {
        Directory.CreateDirectory(Under("源目录"));
        Directory.CreateDirectory(Under("目标目录"));
        File.WriteAllText(Under(@"源目录\内.txt"), "甲");
        File.WriteAllText(Under(@"目标目录\旧.txt"), "乙");

        string blocked = TransferService.Copy(new LocalAddress(Under("源目录")), Under("目标目录"), null);
        Assert.Contains("会合并的目录", blocked);
        Assert.False(File.Exists(Under(@"目标目录\内.txt")));

        string done = TransferService.Copy(
            new LocalAddress(Under("源目录")), Under("目标目录"), [DirectoryCookie(Under("目标目录"))]);

        Assert.Contains("已复制", done);
        Assert.True(File.Exists(Under(@"目标目录\内.txt")));
    }

    [Fact]
    public void 冲突超过十五个带上提示()
    {
        Directory.CreateDirectory(Under("多源"));
        Directory.CreateDirectory(Under("多目标"));
        for (int index = 0; index < 20; index++)
        {
            File.WriteAllText(Under($@"多源\f{index}.txt"), "x");
            File.WriteAllText(Under($@"多目标\f{index}.txt"), "y");
        }

        string output = TransferService.Copy(new LocalAddress(Under("多源")), Under("多目标"), null);

        Assert.Contains("建议由用户手动", output);
    }

    [Fact]
    public void 冲突超过五十个不列凭据()
    {
        Directory.CreateDirectory(Under("大源"));
        Directory.CreateDirectory(Under("大目标"));
        for (int index = 0; index < 55; index++)
        {
            File.WriteAllText(Under($@"大源\g{index}.txt"), "x");
            File.WriteAllText(Under($@"大目标\g{index}.txt"), "y");
        }

        string output = TransferService.Copy(new LocalAddress(Under("大源")), Under("大目标"), null);

        Assert.Contains("有 55 个文件路径冲突", output);
        Assert.Contains("你无法从这个工具得到授权", output);
        Assert.DoesNotContain("cookie: `", output);
    }
}
