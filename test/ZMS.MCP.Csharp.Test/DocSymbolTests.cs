using Xunit;
using ZMS.MCP.Csharp.Roslyn;
using ZMS.MCP.Csharp.Tools;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 「列出文档注释符号」的测试：XML 条目解析 + 精度推断 + 参数消歧。
/// 解析类用例用临时目录里的手写 XML，不依赖机器上装了什么包。
/// </summary>
public sealed class DocSymbolTests
{
    private const string SampleXml = """
        <?xml version="1.0"?>
        <doc>
          <members>
            <member name="T:Demo.Outer"><summary>Outer type.</summary></member>
            <member name="T:Demo.Outer.Inner"><summary>Nested type.</summary></member>
            <member name="M:Demo.Outer.Foo"><summary>No args.</summary></member>
            <member name="M:Demo.Outer.Foo(System.Int32)"><summary>One int.</summary></member>
            <member name="M:Demo.Outer.Foo(System.Int32,System.String)"><summary>Two args.</summary></member>
            <member name="M:Demo.Outer.Inner.Bar"><summary>Nested member.</summary></member>
            <member name="P:Demo.Outer.Value"><summary>Property.</summary></member>
            <member name="F:Demo.Outer.Field"><summary>Field.</summary></member>
            <member name="E:Demo.Outer.Changed"><summary>Event.</summary></member>
            <member name="M:Demo.Outer.#ctor"><summary>Ctor.</summary></member>
            <member name="M:Demo.Outer.ByRef(System.Int32@)"><summary>Ref param.</summary></member>
            <member name="!:Demo.Broken"><summary>Broken.</summary></member>
            <member name="T:Demo.Other"><summary>Other.</summary></member>
          </members>
        </doc>
        """;

    private static IReadOnlyList<DocEntry> SampleEntries()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zms-mcp-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "Sample.xml");
        File.WriteAllText(file, SampleXml);
        return NuGetXmlDocumentation.Read(file);
    }

    // ───────── 解析 ─────────

    [Fact]
    public void Read_parses_kinds_prefixes_and_parameters()
    {
        IReadOnlyList<DocEntry> entries = SampleEntries();

        // ! 开头的条目在读入时丢弃
        Assert.DoesNotContain(entries, entry => entry.MemberName.StartsWith("!:", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Kind == 'T' && entry.FullName == "Demo.Outer");

        DocEntry withParams = entries.First(entry => entry.MemberName == "M:Demo.Outer.Foo(System.Int32,System.String)");
        Assert.Equal("Demo.Outer.Foo", withParams.Prefix);
        Assert.Equal("System.Int32,System.String", withParams.Parameters);

        DocEntry withoutParams = entries.First(entry => entry.MemberName == "M:Demo.Outer.Foo");
        Assert.Equal("", withoutParams.Parameters);
        Assert.Equal("One int.".Length > 0 ? "No args." : "", withoutParams.Summary);
    }

    // ───────── 精度推断 ─────────

    [Fact]
    public void Query_exact_type_infers_PFME_and_excludes_nested_members()
    {
        DocQueryResult result = DocSymbolQuery.Query(SampleEntries(), "Demo.Outer", [], "");

        Assert.Equal("PFME", result.EffectiveKinds);
        Assert.True(result.KindsWereInferred);
        Assert.Contains(result.Entries, entry => entry.MemberName == "P:Demo.Outer.Value");
        Assert.Contains(result.Entries, entry => entry.MemberName == "F:Demo.Outer.Field");
        Assert.Contains(result.Entries, entry => entry.MemberName == "E:Demo.Outer.Changed");
        Assert.Contains(result.Entries, entry => entry.MemberName == "M:Demo.Outer.#ctor");
        // 嵌套类型（Demo.Outer.Inner）的成员不能混进来
        Assert.DoesNotContain(result.Entries, entry => entry.MemberName.Contains("Inner", StringComparison.Ordinal));
    }

    [Fact]
    public void Query_single_member_infers_D()
    {
        DocQueryResult result = DocSymbolQuery.Query(SampleEntries(), "Demo.Outer.Value", [], "");

        Assert.Equal("D", result.EffectiveKinds);
        DocEntry only = Assert.Single(result.Entries);
        Assert.Equal("P:Demo.Outer.Value", only.MemberName);
    }

    [Fact]
    public void Query_overloads_infers_M_and_can_be_disambiguated_by_args()
    {
        IReadOnlyList<DocEntry> entries = SampleEntries();

        DocQueryResult overloads = DocSymbolQuery.Query(entries, "Demo.Outer.Foo", [], "");
        Assert.Equal("M", overloads.EffectiveKinds);
        Assert.Equal(3, overloads.Entries.Count);

        // 别名（int）与完全限定名都能消歧
        DocQueryResult alias = DocSymbolQuery.Query(entries, "Demo.Outer.Foo", ["int"], "");
        Assert.Equal("D", alias.EffectiveKinds);
        Assert.Equal("M:Demo.Outer.Foo(System.Int32)", Assert.Single(alias.Entries).MemberName);

        DocQueryResult full = DocSymbolQuery.Query(entries, "Demo.Outer.Foo", ["System.Int32", "System.String"], "");
        Assert.Equal("M:Demo.Outer.Foo(System.Int32,System.String)", Assert.Single(full.Entries).MemberName);
    }

    [Fact]
    public void Query_matches_ref_parameters_despite_at_suffix()
    {
        // XML 里 ref/out 参数带 @ 后缀，匹配时要忽略
        DocQueryResult result = DocSymbolQuery.Query(SampleEntries(), "Demo.Outer.ByRef", ["int"], "");

        Assert.Equal("M:Demo.Outer.ByRef(System.Int32@)", Assert.Single(result.Entries).MemberName);
    }

    [Fact]
    public void Query_namespace_prefix_lists_types()
    {
        DocQueryResult result = DocSymbolQuery.Query(SampleEntries(), "Demo", [], "");

        Assert.Equal("T", result.EffectiveKinds);
        Assert.Contains(result.Entries, entry => entry.FullName == "Demo.Outer");
        Assert.Contains(result.Entries, entry => entry.FullName == "Demo.Outer.Inner");
        Assert.Contains(result.Entries, entry => entry.FullName == "Demo.Other");
        Assert.NotEmpty(result.Note);
    }

    [Fact]
    public void Query_nested_type_is_a_type_on_its_own()
    {
        DocQueryResult result = DocSymbolQuery.Query(SampleEntries(), "Demo.Outer.Inner", [], "");

        Assert.Equal("PFME", result.EffectiveKinds);
        Assert.Contains(result.Entries, entry => entry.MemberName == "M:Demo.Outer.Inner.Bar");
        // 外层类型的成员不能出现在嵌套类型的查询里
        Assert.DoesNotContain(result.Entries, entry => entry.MemberName == "P:Demo.Outer.Value");
    }

    [Fact]
    public void Query_explicit_kinds_override_inference()
    {
        DocQueryResult result = DocSymbolQuery.Query(SampleEntries(), "Demo.Outer", [], "F");

        Assert.Equal("F", result.EffectiveKinds);
        Assert.False(result.KindsWereInferred);
        Assert.Equal("F:Demo.Outer.Field", Assert.Single(result.Entries).MemberName);
    }

    [Fact]
    public void Query_unknown_path_throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => DocSymbolQuery.Query(SampleEntries(), "Demo.Nothing", [], ""));
    }

    // ───────── 本地缓存定位 ─────────

    [Fact]
    public void Locate_finds_cached_package_xml()
    {
        // 本机缓存里一定有这个包（本项目自己就引用它）
        DocSource source = NuGetXmlDocumentation.Locate("microsoft.codeanalysis.csharp", "", "");

        Assert.NotEmpty(source.Version);
        Assert.StartsWith("net", source.TargetFramework, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(source.XmlPaths);
        Assert.All(source.XmlPaths, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void Locate_missing_package_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => NuGetXmlDocumentation.Locate("this-package-does-not-exist-xyz", "", ""));
    }

    // ───────── 工具渲染 ─────────

    [Fact]
    public void ListDocSymbols_goes_through_the_real_cache_end_to_end()
    {
        // 真实缓存包走完整链路（ParseText 本身有重载，所以这里只锁"链路通、给出生效 type"）
        string output = DocSymbolTools.ListDocSymbols(
            "microsoft.codeanalysis.csharp",
            path: "Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText");

        Assert.Contains("microsoft.codeanalysis.csharp", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CSharpSyntaxTree.ParseText", output);
        Assert.Contains("生效 type: `", output);
    }

    [Fact]
    public void Render_outputs_raw_xml_fragment_for_single_member()
    {
        // D（唯一成员）分支：直接喂一个条目，不依赖真实包有没有重载
        DocEntry entry = SampleEntries().First(item => item.MemberName == "P:Demo.Outer.Value");
        DocSource source = new("Demo", "1.0.0", "net10.0", ["Demo.xml"]);
        DocQueryResult result = new("Demo.Outer.Value", "D", true, [entry], "");

        string output = DocSymbolTools.Render(source, result);

        Assert.Contains("```xml", output);
        Assert.Contains("P:Demo.Outer.Value", output);
        Assert.Contains("Demo 1.0.0 (net10.0)", output);
    }

    [Fact]
    public void ListDocSymbols_without_path_lists_types()
    {
        string output = DocSymbolTools.ListDocSymbols("microsoft.codeanalysis.csharp");

        Assert.Contains("生效 type: `T`", output);
        Assert.Contains("T:Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree", output);
    }
}
