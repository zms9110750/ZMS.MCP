using System.IO.Compression;
using System.Text;
using Xunit;
using zms9110750.ZMS_MCP.Cli.Tools;

namespace ZMS.MCP.Test;

public sealed class PickTests
{
    [Fact]
    public void Pick_SimpleExpression_ReturnsResult()
    {
        var result = PickTools.PickRandom("(1,2)[3,6]{A:3,B:2,C:1}", null);
        Assert.False(string.IsNullOrEmpty(result));
        Assert.DoesNotContain("Error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pick_EmptyPool_ReturnsResult()
    {
        var result = PickTools.PickRandom("(1,1)[1,1]{X:1}", null);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void Pick_SingleMode_ReturnsResult()
    {
        var result = PickTools.PickRandom("(1,2)[3,6]{A:3,B:2,C:1}", true);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void Pick_NoReplacement_CountMatch()
    {
        var result = PickTools.PickRandom("(2,3)[4,8]{A:3,B:2,C:1,D:5}", true);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Theory]
    [InlineData("(1,1)[1,1]{X:1}", false)]
    [InlineData("(2,3)[10,20]{A:10,B:5,C:3,D:7}", true)]
    [InlineData("(1,2)[1,3]{a:1,b:2}", false)]
    public void Pick_VariousExpressions_NoError(string expr, bool? single)
    {
        var result = PickTools.PickRandom(expr, single);
        Assert.False(string.IsNullOrEmpty(result));
        Assert.DoesNotContain("Error", result, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DataQueryTests
{
    [Fact]
    public async Task Query_SimpleJson_ReturnsValue()
    {
        var result = await DataQueryTools.QueryData("""{"name":"hello","value":42}""", "$.name", 500);
        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task Query_JsonArrayIndex_ReturnsNth()
    {
        var result = await DataQueryTools.QueryData("""{"items":["a","b","c"]}""", "$.items[0]", 500);
        Assert.Contains("a", result);
    }

    [Fact]
    public async Task Query_JsonRecursive_ReturnsMatches()
    {
        var result = await DataQueryTools.QueryData("""{"a":{"name":"x"},"b":{"name":"y"}}""", "$..name", 500);
        Assert.Contains("x", result);
        Assert.Contains("y", result);
    }

    [Fact]
    public async Task Query_JsonRoot_ReturnsFull()
    {
        var result = await DataQueryTools.QueryData("""{"x":1,"y":{"z":2}}""", "$", 500);
        Assert.Contains("x", result);
    }

    [Fact]
    public async Task Query_JsonNestedProperty_ReturnsValue()
    {
        var result = await DataQueryTools.QueryData("""{"a":{"b":{"c":"deep"}}}""", "$.a.b.c", 500);
        Assert.Contains("deep", result);
    }

    [Fact]
    public async Task Query_XmlSimple_ReturnsNode()
    {
        var result = await DataQueryTools.QueryData("<root><item id=\"1\">text</item></root>", "/root/item", 500);
        Assert.Contains("text", result);
    }

    [Fact]
    public async Task Query_MaxLengthTruncation_ReturnsStructure()
    {
        var sb = new StringBuilder();
        sb.Append("{\"data\":{");
        for (int i = 0; i < 50; i++)
        {
            sb.Append($"\"k{i}\":\"v{i}\",");
        }

        sb.Length--; sb.Append("}}");
        var result = await DataQueryTools.QueryData(sb.ToString(), "$.data", 100);
        Assert.Contains("object", result);
    }

    [Theory]
    [InlineData("""{"a":1}""", "$.a", "1")]
    [InlineData("""{"a":{"b":true}}""", "$.a.b", "true")]
    [InlineData("""{"list":[10,20,30]}""", "$.list[2]", "30")]
    public async Task Query_VariousJsonPaths_ReturnsCorrect(string json, string path, string expected)
    {
        var result = await DataQueryTools.QueryData(json, path, 500);
        Assert.Contains(expected, result);
    }

    [Theory]
    [InlineData("<r><x v=\"1\"/></r>", "/r/x/@v", "1")]
    [InlineData("<r><x>abc</x></r>", "/r/x/text()", "abc")]
    public async Task Query_VariousXPaths_ReturnsCorrect(string xml, string xpath, string expected)
    {
        var result = await DataQueryTools.QueryData(xml, xpath, 500);
        Assert.Contains(expected, result);
    }

    [Fact]
    public async Task Query_InvalidSource_ReturnsError()
    {
        var result = await DataQueryTools.QueryData("not valid source", "$", 500);
        Assert.True(result.Contains("无法识别") || result.Contains("Error"));
    }
}

public sealed class ArchiveTests
{
    private string CreateTestZip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MCPTest_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "Hello");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "World");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "c.txt"), "Nested");
            var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
            ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Optimal, false);
            return zip;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Archive_ListContents_ReturnsTree()
    {
        var zip = CreateTestZip();
        try
        {
            var result = await ArchiveTools.Archive(zip, null, null);
            Assert.Contains("a.txt", result);
            Assert.Contains("b.txt", result);
        }
        finally { try { File.Delete(zip); } catch { } }
    }

    [Fact]
    public async Task Archive_ReadFile_ReturnsContent()
    {
        var zip = CreateTestZip();
        try
        {
            var result = await ArchiveTools.Archive(zip, "a.txt", null);
            Assert.Contains("Hello", result);
        }
        finally { try { File.Delete(zip); } catch { } }
    }

    [Fact]
    public async Task Archive_ReadNestedFile_ReturnsContent()
    {
        var zip = CreateTestZip();
        try
        {
            var result = await ArchiveTools.Archive(zip, "sub/c.txt", null);
            Assert.Contains("Nested", result);
        }
        finally { try { File.Delete(zip); } catch { } }
    }

    [Fact]
    public async Task Archive_ExtractToDirectory_FilesExtracted()
    {
        var zip = CreateTestZip();
        var extractDir = Path.Combine(Path.GetTempPath(), "MCPExtract_" + Guid.NewGuid());
        try
        {
            var result = await ArchiveTools.Archive(zip, null, extractDir);
            Assert.True(File.Exists(Path.Combine(extractDir, "a.txt")));
            Assert.True(File.Exists(Path.Combine(extractDir, "sub", "c.txt")));
        }
        finally
        {
            try { File.Delete(zip); } catch { }
            try { Directory.Delete(extractDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Archive_FileNotFound_ReturnsError()
    {
        var zip = CreateTestZip();
        try
        {
            var result = await ArchiveTools.Archive(zip, "nonexistent.txt", null);
            Assert.True(result.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                        result.Contains("未找到"));
        }
        finally { try { File.Delete(zip); } catch { } }
    }

    [Fact]
    public async Task Archive_InvalidArchivePath_ReturnsError()
    {
        var result = await ArchiveTools.Archive(@"C:\nonexistent.zip", null, null);
        Assert.True(result.Contains("不存在") || result.Contains("not exist", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class SearchNuGetTests
{
    [Fact]
    public async Task Search_OnlineFuzzy_FindsResults()
    {
        var result = await NuGetTools.SearchNuGet("Newtonsoft.Json", null, true);
        Assert.False(string.IsNullOrEmpty(result));
        Assert.DoesNotContain("Error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_OnlineAllVersions_ReturnsList()
    {
        var result = await NuGetTools.SearchNuGet("Newtonsoft.Json", "*", true);
        Assert.False(string.IsNullOrEmpty(result));
        Assert.DoesNotContain("Error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_LocalCache_ReturnsVersions()
    {
        var result = await NuGetTools.SearchNuGet("Newtonsoft.Json", null, false);
        Assert.False(string.IsNullOrEmpty(result));
    }
}

public sealed class CounterTests
{
    [Fact]
    public void Counter_Increments()
    {
        var first = CounterTools.GetCount();
        Assert.True(int.Parse(first) > 0);
        var second = CounterTools.GetCount();
        Assert.Equal(int.Parse(first) + 1, int.Parse(second));
    }
}
