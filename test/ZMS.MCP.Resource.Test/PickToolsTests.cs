using Xunit;
using ZMS.MCP.Resource.Tools;

namespace ZMS.MCP.Resource.Test;

public class PickToolsTests
{
    [Fact]
    public void 抽出的项数与点数和都落在约束里()
    {
        for (int round = 0; round < 50; round++)
        {
            string output = PickTools.PickRandom("(2,4)[5,8]{A:3,B:2,C:4}", single: false);

            string[] parts = output.Split(" = ", StringSplitOptions.TrimEntries);
            int items = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
            int total = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);

            Assert.InRange(items, 2, 4);
            Assert.InRange(total, 5, 8);
        }
    }

    [Fact]
    public void 不放回模式下同一个项不会抽两次()
    {
        string output = PickTools.PickRandom("(3)[7]{A:3,B:2,C:2}", single: true);

        string[] names = output.Split(" = ")[0].Split(',', StringSplitOptions.TrimEntries);
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void 有放回模式下可以重复抽中同一个项()
    {
        // 池子里只有一个能凑出点数的项：必然抽到它多次
        string output = PickTools.PickRandom("(3)[9]{唯:3}", single: false);

        Assert.StartsWith("唯, 唯, 唯 = 9", output, StringComparison.Ordinal);
    }

    [Fact]
    public void 池子空或格式不对会被挑出来()
    {
        Assert.Throws<ArgumentException>(() => PickTools.PickRandom("(2,4)[5,8]{ }", null));
        Assert.Throws<ArgumentException>(() => PickTools.PickRandom("胡写的", null));
        Assert.Throws<ArgumentException>(() => PickTools.PickRandom("(2,4)[5,8]{file://根本没有这个文件.json}", null));
    }

    [Fact]
    public void 全角逗号与冒号也认()
    {
        string output = PickTools.PickRandom("(2)[4]{甲：3，乙：2}", single: false);

        Assert.Contains("= 4", output, StringComparison.Ordinal);
    }
}
