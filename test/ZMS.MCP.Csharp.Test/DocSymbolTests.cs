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
        return ReadXml(SampleXml);
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
        Assert.Equal("No args.", withoutParams.Summary);
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

    // ───────── 泛型、D/N 语义、TFM 与版本排序 ─────────

    [Fact]
    public void Query_matches_generic_names_without_arity_suffix()
    {
        // XML 里泛型是 T:Demo.Box`1 / M:Demo.Box`1.Add``1(...)，调用方只写裸名
        IReadOnlyList<DocEntry> entries = ReadXml("""
            <?xml version="1.0"?>
            <doc><members>
              <member name="T:Demo.Box`1"><summary>Box.</summary></member>
              <member name="P:Demo.Box`1.Value"><summary>Value.</summary></member>
              <member name="M:Demo.Box`1.Add``1(``0)"><summary>Generic method.</summary></member>
            </members></doc>
            """);

        DocQueryResult type = DocSymbolQuery.Query(entries, "Demo.Box", [], "");
        Assert.Contains(type.Entries, entry => entry.MemberName == "P:Demo.Box`1.Value");

        DocQueryResult method = DocSymbolQuery.Query(entries, "Demo.Box.Add", [], "");
        Assert.Equal("M:Demo.Box`1.Add``1(``0)", Assert.Single(method.Entries).MemberName);
    }

    [Fact]
    public void Query_generic_nested_type_members_do_not_leak_into_outer_type()
    {
        // 嵌套类型自己也可能泛型：T:Demo.Outer`1.Inner`1
        IReadOnlyList<DocEntry> entries = ReadXml("""
            <?xml version="1.0"?>
            <doc><members>
              <member name="T:Demo.Outer`1"><summary>Outer.</summary></member>
              <member name="T:Demo.Outer`1.Inner`1"><summary>Nested generic.</summary></member>
              <member name="P:Demo.Outer`1.Value"><summary>Outer value.</summary></member>
              <member name="M:Demo.Outer`1.Inner`1.Bar"><summary>Nested member.</summary></member>
            </members></doc>
            """);

        DocQueryResult outer = DocSymbolQuery.Query(entries, "Demo.Outer", [], "");

        Assert.Contains(outer.Entries, entry => entry.MemberName == "P:Demo.Outer`1.Value");
        Assert.DoesNotContain(outer.Entries, entry => entry.MemberName.Contains("Inner", StringComparison.Ordinal));
    }

    [Fact]
    public void Query_disambiguates_arguments_with_generic_commas()
    {
        // 泛型实参里的逗号不是参数分隔符
        IReadOnlyList<DocEntry> entries = ReadXml("""
            <?xml version="1.0"?>
            <doc><members>
              <member name="T:Demo.Outer"><summary>Outer.</summary></member>
              <member name="M:Demo.Outer.Map(System.Collections.Generic.Dictionary{System.String,System.Int32})"><summary>Map dict.</summary></member>
              <member name="M:Demo.Outer.Map(System.Collections.Generic.Dictionary{System.String,System.Int32},System.Boolean)"><summary>Map dict + flag.</summary></member>
            </members></doc>
            """);

        DocQueryResult result = DocSymbolQuery.Query(
            entries,
            "Demo.Outer.Map",
            ["System.Collections.Generic.Dictionary{System.String,System.Int32}"],
            "");

        Assert.Equal(
            "M:Demo.Outer.Map(System.Collections.Generic.Dictionary{System.String,System.Int32})",
            Assert.Single(result.Entries).MemberName);
    }

    [Fact]
    public void SplitParameters_ignores_commas_inside_brackets()
    {
        // 调用方可能按 C# 习惯写尖括号，尖括号里的逗号同样不是分隔符
        Assert.Equal(
            ["System.Collections.Generic.Dictionary<System.String,System.Int32>", "System.Boolean"],
            DocSymbolQuery.SplitParameters("System.Collections.Generic.Dictionary<System.String,System.Int32>,System.Boolean"));
        Assert.Equal(["int"], DocSymbolQuery.SplitParameters("int"));
        Assert.Empty(DocSymbolQuery.SplitParameters(""));
    }

    [Fact]
    public void Query_with_explicit_namespace_kind_lists_types_instead_of_throwing()
    {
        // N 不能当成"不含 T 就找不到"：XML 里没有 N: 条目，N 就是"按前缀列类型"
        DocQueryResult result = DocSymbolQuery.Query(SampleEntries(), "Demo", [], "N");

        Assert.Equal("N", result.EffectiveKinds);
        Assert.False(result.KindsWereInferred);
        Assert.Contains(result.Entries, entry => entry.FullName == "Demo.Outer");
        Assert.Contains("N:", result.Note);
    }

    [Fact]
    public void Query_with_explicit_D_returns_raw_fragments()
    {
        // D 是输出开关，不是条目种类：它不该把命中结果过滤成空
        DocQueryResult result = DocSymbolQuery.Query(SampleEntries(), "Demo.Outer.Foo", [], "D");

        Assert.Equal("D", result.EffectiveKinds);
        Assert.Equal(3, result.Entries.Count);
        Assert.All(result.Entries, entry => Assert.Contains("<summary>", entry.Xml));

        string output = DocSymbolTools.Render(new DocSource("Demo", "1.0.0", "net10.0", ["Demo.xml"]), result, 12);

        Assert.Contains("```xml", output);
        Assert.Contains("条目总数: 12", output);
    }

    [Fact]
    public void TargetFrameworkScore_prefers_modern_over_legacy()
    {
        // net48 里的 "48" 是 4.8，不能被当成版本 48 压过 net10.0
        Assert.True(NuGetXmlDocumentation.TargetFrameworkScore("net10.0") > NuGetXmlDocumentation.TargetFrameworkScore("net48"));
        Assert.True(NuGetXmlDocumentation.TargetFrameworkScore("net8.0") > NuGetXmlDocumentation.TargetFrameworkScore("net481"));
        Assert.True(NuGetXmlDocumentation.TargetFrameworkScore("net48") > NuGetXmlDocumentation.TargetFrameworkScore("net472"));
        Assert.True(NuGetXmlDocumentation.TargetFrameworkScore("net48") > NuGetXmlDocumentation.TargetFrameworkScore("netstandard2.0"));
        Assert.True(NuGetXmlDocumentation.TargetFrameworkScore("net8.0") > NuGetXmlDocumentation.TargetFrameworkScore("netcoreapp3.1"));
    }

    [Fact]
    public void TargetFrameworkScore_tolerates_platform_suffix()
    {
        // net8.0-windows 这类带平台后缀的也要按 net8.0 分档
        Assert.True(NuGetXmlDocumentation.TargetFrameworkScore("net8.0-windows") > NuGetXmlDocumentation.TargetFrameworkScore("net48"));
        Assert.Equal(
            NuGetXmlDocumentation.TargetFrameworkScore("net8.0"),
            NuGetXmlDocumentation.TargetFrameworkScore("net8.0-windows7.0"));
    }

    [Fact]
    public void VersionComparer_prefers_release_over_prerelease()
    {
        IComparer<string> comparer = NuGetXmlDocumentation.VersionComparer;

        Assert.True(comparer.Compare("1.0.0", "1.0.0-beta") > 0);
        Assert.True(comparer.Compare("1.10.0", "1.9.0") > 0);
        Assert.True(comparer.Compare("2.0.0", "1.9.9") > 0);
    }

    [Fact]
    public void Query_prefers_exact_type_over_generic_sibling()
    {
        // Tuple 与 Tuple`1 这类同名泛型/非泛型类型能同时存在，查裸名要给非泛型的那个
        IReadOnlyList<DocEntry> entries = ReadXml("""
            <?xml version="1.0"?>
            <doc><members>
              <member name="T:Demo.Tuple"><summary>Non generic.</summary></member>
              <member name="P:Demo.Tuple.Item"><summary>Non generic member.</summary></member>
              <member name="T:Demo.Tuple`1"><summary>Generic.</summary></member>
              <member name="P:Demo.Tuple`1.Item"><summary>Generic member.</summary></member>
            </members></doc>
            """);

        DocQueryResult result = DocSymbolQuery.Query(entries, "Demo.Tuple", [], "T");

        Assert.Equal("T:Demo.Tuple", Assert.Single(result.Entries).MemberName);

        // 带反引号写全名时命中泛型那个
        DocQueryResult generic = DocSymbolQuery.Query(entries, "Demo.Tuple`1", [], "T");
        Assert.Equal("T:Demo.Tuple`1", Assert.Single(generic.Entries).MemberName);
    }

    /// <summary>把一段 XML 写进临时文件再读，用于验证解析/查询细节。</summary>
    private static IReadOnlyList<DocEntry> ReadXml(string xml)
    {
        string directory = Path.Combine(Path.GetTempPath(), "zms-mcp-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "Sample.xml");
        File.WriteAllText(file, xml);
        return NuGetXmlDocumentation.Read(file);
    }
}
