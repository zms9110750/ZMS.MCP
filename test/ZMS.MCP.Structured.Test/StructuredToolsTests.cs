using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using ZMS.MCP.Structured.Tools;

namespace ZMS.MCP.Structured.Test;

/// <summary>
/// 两个工具的行为：五种格式的读/改/插/删、cookie 是结构指纹、depth 截断、空文件、同值跳过、
/// 编码判定与显式编码、各种拒绝路径。每个用例自己开一个临时目录，不碰仓库。
/// </summary>
public sealed class StructuredToolsTests
{
    static StructuredToolsTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void Json_change_insert_delete()
    {
        string file = Sample("sample.json", """
            {
              "logging": { "level": "info" },
              "hosts": "*"
            }
            """);

        Assert.Contains("改写 $.logging.level", Apply(file, "$.logging.level", "\"debug\"", update: true), StringComparison.Ordinal);
        Assert.Contains("插入 $.logging.extra", Apply(file, "$.logging.extra", "\"x\"", insert: true), StringComparison.Ordinal);
        Assert.Contains("删除 $.hosts", Apply(file, "$.hosts", null, remove: true), StringComparison.Ordinal);

        string text = File.ReadAllText(file);
        Assert.Contains("\"level\": \"debug\"", text, StringComparison.Ordinal);
        Assert.Contains("\"extra\": \"x\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hosts", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_change_insert_delete()
    {
        string file = Sample("sample.xml", """
            <config>
              <logging level="info" />
              <hosts>*</hosts>
            </config>
            """);

        Assert.Contains("改写 /config/logging/@level", Apply(file, "/config/logging/@level", "debug", update: true), StringComparison.Ordinal);
        Assert.Contains("插入 /config/extra", Apply(file, "/config/extra", "<extra>x</extra>", insert: true), StringComparison.Ordinal);
        Assert.Contains("删除 /config/hosts", Apply(file, "/config/hosts", null, remove: true), StringComparison.Ordinal);

        string text = File.ReadAllText(file);
        Assert.Contains("level=\"debug\"", text, StringComparison.Ordinal);
        Assert.Contains("<extra>x</extra>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hosts", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_change_insert_delete()
    {
        string file = Sample("sample.yaml", """
            logging:
              level: info
            hosts: "*"
            """);

        Assert.Contains("改写 $.logging.level", Apply(file, "$.logging.level", "debug", update: true), StringComparison.Ordinal);
        Assert.Contains("插入 $.logging.extra", Apply(file, "$.logging.extra", "x", insert: true), StringComparison.Ordinal);
        Assert.Contains("删除 $.hosts", Apply(file, "$.hosts", null, remove: true), StringComparison.Ordinal);

        string text = File.ReadAllText(file);
        Assert.Contains("level: debug", text, StringComparison.Ordinal);
        Assert.Contains("extra: x", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hosts", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Toml_change_insert_delete()
    {
        string file = Sample("sample.toml", """
            hosts = "*"

            [logging]
            level = "info"
            """);

        Assert.Contains("改写 $.logging.level", Apply(file, "$.logging.level", "\"debug\"", update: true), StringComparison.Ordinal);
        Assert.Contains("插入 $.logging.extra", Apply(file, "$.logging.extra", "\"x\"", insert: true), StringComparison.Ordinal);
        Assert.Contains("删除 $.hosts", Apply(file, "$.hosts", null, remove: true), StringComparison.Ordinal);

        string text = File.ReadAllText(file);
        Assert.Contains("level = \"debug\"", text, StringComparison.Ordinal);
        Assert.Contains("extra = \"x\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hosts", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ini_change_insert_delete()
    {
        string file = Sample("sample.ini", """
            hosts=*

            [logging]
            level=info
            """);

        Assert.Contains("改写 $.logging.level", Apply(file, "$.logging.level", "debug", update: true), StringComparison.Ordinal);
        Assert.Contains("插入 $.logging.extra", Apply(file, "$.logging.extra", "x", insert: true), StringComparison.Ordinal);
        Assert.Contains("删除 $.hosts", Apply(file, "$.hosts", null, remove: true), StringComparison.Ordinal);

        string text = File.ReadAllText(file);
        Assert.Contains("level=debug", text, StringComparison.Ordinal);
        Assert.Contains("extra=x", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hosts", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_the_whole_document_twice_gives_the_same_cookie()
    {
        string file = Sample("same.json", """{"a": 1}""");

        Assert.Equal(Cookie(file), Cookie(file));
    }

    [Fact]
    public void Reading_one_location_gives_no_cookie()
    {
        string file = Sample("one.json", """{"a": 1}""");

        string output = StructuredTools.ReadStructured(file, point: "$.a", length: 100);

        Assert.Equal("1", output);
    }

    [Fact]
    public void Depth_hides_deeper_levels_and_says_how_much()
    {
        string file = Sample("deep.json", """{"a": {"b": {"c": 1}}}""");

        string shallow = StructuredTools.ReadStructured(file, depth: 1, length: 900);
        string full = StructuredTools.ReadStructured(file, depth: 0, length: 900);

        Assert.Contains("未展开", shallow, StringComparison.Ordinal);
        Assert.DoesNotContain("未展开", full, StringComparison.Ordinal);
        Assert.Contains("\"c\": 1", full, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_file_reads_as_an_empty_document()
    {
        string file = Sample("empty.json", "");

        string output = StructuredTools.ReadStructured(file);

        Assert.Contains("空文档", output, StringComparison.Ordinal);
        Assert.Contains("cookie：`", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewriting_a_value_with_the_same_content_writes_nothing()
    {
        string file = Sample("same.json", """{"a": 1}""");

        Assert.Equal("值没有变化，未写入。", Apply(file, "$.a", "1", update: true));
    }

    [Fact]
    public void A_switch_that_is_off_reports_the_matches_instead_of_writing()
    {
        string file = Sample("off.json", """{"a": 1}""");

        string output = Apply(file, "$.a", "2");

        Assert.Contains("没有写入", output, StringComparison.Ordinal);
        Assert.Contains("update 没开", output, StringComparison.Ordinal);
        Assert.Contains("1", output, StringComparison.Ordinal);
        Assert.Contains("\"a\": 1", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void A_cookie_from_an_older_state_is_refused()
    {
        string file = Sample("stale.json", """{"a": 1}""");
        string stale = Cookie(file);

        Apply(file, "$.a", "2", update: true);

        Assert.Contains("重新 read_structured", Apply(stale, file, "$.a", "3", update: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_only_the_layout_is_allowed()
    {
        string file = Sample("layout.json", """{"b": 1, "a": 2}""");
        string cookie = Cookie(file);

        File.WriteAllText(file, "{\n  \"a\": 2,\n  \"b\": 1\n}\n");

        Assert.Contains("已落盘", StructuredTools.EditStructured(cookie, file, "$.a", "3", update: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Several_matches_need_the_multi_switch()
    {
        string file = Sample("multi.json", """{"a": {"v": 1}, "b": {"v": 2}}""");

        Assert.Contains("multi 没开", Apply(file, "$..v", "9", update: true), StringComparison.Ordinal);
        Assert.Contains("（2 处）", Apply(file, "$..v", "9", update: true, multi: true), StringComparison.Ordinal);

        string text = File.ReadAllText(file);
        Assert.DoesNotContain("\"v\": 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"v\": 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Inserting_into_a_missing_parent_is_refused()
    {
        string file = Sample("missing.json", """{"a": 1}""");

        Assert.Contains("要先定位父容器", Apply(file, "$.nope.extra", "1", insert: true), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_extension_needs_an_explicit_format()
    {
        string file = Sample("config.conf", """{"a": 1}""");

        Assert.Contains("看不懂扩展名", StructuredTools.ReadStructured(file), StringComparison.Ordinal);
        Assert.Contains("cookie：`", StructuredTools.ReadStructured(file, format: "json"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_plain_utf8_reports_its_encoding()
    {
        string file = Sample("plain.json", """{"a": 1}""");

        Assert.Contains("编码：utf-8", StructuredTools.ReadStructured(file), StringComparison.Ordinal);
        Assert.DoesNotContain("带 BOM", StructuredTools.ReadStructured(file), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_utf8_is_refused()
    {
        string file = Write("gbk.ini", "gb18030", "[名称]\r\n键=中文值\r\n");

        Assert.Contains("不是 UTF-8", StructuredTools.ReadStructured(file), StringComparison.Ordinal);
        Assert.Equal(0, CookieOrNothing(file).Length);
    }

    [Fact]
    public void An_explicit_encoding_lets_a_gbk_file_through_and_writes_it_back_as_gbk()
    {
        string file = Write("gbk.ini", "gb18030", "[名称]\r\n键=中文值\r\n");

        string output = StructuredTools.ReadStructured(file, encoding: "gb18030", length: 300);
        Assert.Contains("编码：gb18030", output, StringComparison.Ordinal);

        string cookie = Regex.Match(output, "cookie：`([0-9a-f]+)`").Groups[1].Value;
        Assert.Contains(
            "已落盘",
            StructuredTools.EditStructured(cookie, file, "$.名称.键", "新值", encoding: "gb18030", update: true),
            StringComparison.Ordinal);

        Encoding codec = Encoding.GetEncoding("gb18030");
        string written = codec.GetString(File.ReadAllBytes(file));
        Assert.Contains("新值", written, StringComparison.Ordinal);
        Assert.Contains("名称", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_utf8_file_with_a_bom_keeps_its_bom()
    {
        string file = Write("bom.json", "utf-8", """{"a": 1}""");

        Assert.Contains("编码：utf-8（带 BOM）", StructuredTools.ReadStructured(file), StringComparison.Ordinal);

        Assert.Contains("已落盘", StructuredTools.EditStructured(Cookie(file), file, "$.a", "2", update: true), StringComparison.Ordinal);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(file)[..3]);
    }

    [Fact]
    public void A_toml_date_keeps_its_type()
    {
        string file = Sample("date.toml", "name = \"demo\"\nreleased = 2024-01-31\nclock = 09:30:00\n");

        Apply(file, "$.name", "\"demo2\"", update: true);

        string text = File.ReadAllText(file);
        Assert.Contains("released = 2024-01-31", text, StringComparison.Ordinal);
        Assert.Contains("clock = 09:30:00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"2024-01-31\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_toml_nested_table_gets_no_empty_parent_header()
    {
        string file = Sample("nested.toml", "[tool.black]\nline-length = 88\n");

        Apply(file, "$.tool.black.line-length", "100", update: true);

        string text = File.ReadAllText(file);
        Assert.Contains("[tool.black]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[tool]\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_yaml_file_with_anchors_is_refused()
    {
        string file = Sample("anchor.yaml", "base: &b\n  x: 1\nchild:\n  <<: *b\n");

        Assert.Contains("锚点", StructuredTools.ReadStructured(file), StringComparison.Ordinal);
    }

    [Fact]
    public void An_xml_declaration_and_comments_survive_a_write()
    {
        string file = Sample(
            "declared.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- 顶部 -->\n<config>\n  <!-- 里面 -->\n  <a>1</a>\n</config>\n");

        Apply(file, "/config/a", "2", update: true);

        string text = File.ReadAllText(file);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text, StringComparison.Ordinal);
        Assert.Contains("<!-- 顶部 -->", text, StringComparison.Ordinal);
        Assert.Contains("<!-- 里面 -->", text, StringComparison.Ordinal);
    }

    private static string Apply(
        string file,
        string point,
        string? value,
        bool insert = false,
        bool remove = false,
        bool update = false,
        bool multi = false)
    {
        return Apply(Cookie(file), file, point, value, insert, remove, update, multi);
    }

    private static string Apply(
        string cookie,
        string file,
        string point,
        string? value,
        bool insert = false,
        bool remove = false,
        bool update = false,
        bool multi = false)
    {
        return StructuredTools.EditStructured(
            cookie,
            file,
            point,
            value,
            insert: insert,
            remove: remove,
            update: update,
            multi: multi);
    }

    private static string Cookie(string file)
    {
        string output = StructuredTools.ReadStructured(file, length: 300);
        Match match = Regex.Match(output, "cookie：`([0-9a-f]+)`");
        Assert.True(match.Success, "读整份没有给出 cookie：\n" + output);

        return match.Groups[1].Value;
    }

    private static string CookieOrNothing(string file)
    {
        Match match = Regex.Match(StructuredTools.ReadStructured(file, length: 300), "cookie：`([0-9a-f]+)`");

        return match.Success ? match.Groups[1].Value : "";
    }

    private static string Sample(string name, string body)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "zms-structured-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, name);
        File.WriteAllText(file, body);

        return file;
    }

    private static string Write(string name, string encoding, string body)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "zms-structured-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, name);
        Encoding codec = Encoding.GetEncoding(encoding);
        File.WriteAllBytes(file, [.. codec.GetPreamble(), .. codec.GetBytes(body)]);

        return file;
    }
}
