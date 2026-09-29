using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZMS.MCP.Csharp.Draft;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 追踪基线的语义指纹：**语义不变 → hash 不变**（using / 别名 / 简称只是写法差异），
/// 真被改了才变。注释与空白这些 trivia 也不参与。
/// </summary>
public sealed class SymbolBaselineTests
{
    private static CSharpCompilation Compile(string source)
    {
        return CSharpCompilation.Create(
            "Probe",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>算一个类型的语义 hash。</summary>
    private static string HashOf(string source, string typeName)
    {
        CSharpCompilation compilation = Compile(source);
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(typeName)!;
        return SymbolBaseline.Capture(compilation)[SymbolBaseline.Key(type)];
    }

    [Fact]
    public void The_hash_ignores_how_a_type_is_spelled()
    {
        // 完全限定名 / using 后的简称 / using 别名 —— 三种写法语义相同，hash 必须一样
        string full = HashOf(
            "namespace Demo;\npublic class C { public System.Collections.Generic.List<int> All() { return new System.Collections.Generic.List<int>(); } }",
            "Demo.C");
        string shortName = HashOf(
            "using System.Collections.Generic;\nnamespace Demo;\npublic class C { public List<int> All() { return new List<int>(); } }",
            "Demo.C");
        string alias = HashOf(
            "using L = System.Collections.Generic.List<int>;\nnamespace Demo;\npublic class C { public L All() { return new L(); } }",
            "Demo.C");

        Assert.Equal(full, shortName);
        Assert.Equal(full, alias);
    }

    [Fact]
    public void The_hash_ignores_keyword_versus_bcl_names()
    {
        string keyword = HashOf("namespace Demo;\npublic class C { public int Value; }", "Demo.C");
        string bcl = HashOf("namespace Demo;\npublic class C { public System.Int32 Value; }", "Demo.C");

        Assert.Equal(keyword, bcl);
    }

    [Fact]
    public void The_hash_ignores_comments_and_layout()
    {
        string plain = HashOf("namespace Demo;\npublic class C { public int Value; }", "Demo.C");
        string commented = HashOf(
            "namespace Demo;\n/// <summary>说明</summary>\npublic class C\n{\n    // 注释\n    public int Value;\n}",
            "Demo.C");

        Assert.Equal(plain, commented);
    }

    [Fact]
    public void The_hash_follows_a_really_new_member()
    {
        string before = HashOf("namespace Demo;\npublic class C { public int Value; }", "Demo.C");
        string after = HashOf("namespace Demo;\npublic class C { public int Value; public int Other; }", "Demo.C");

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void The_hash_follows_a_really_changed_body()
    {
        string before = HashOf("namespace Demo;\npublic class C { public int Add(int a, int b) { return a + b; } }", "Demo.C");
        string after = HashOf("namespace Demo;\npublic class C { public int Add(int a, int b) { return a - b; } }", "Demo.C");

        Assert.NotEqual(before, after);
    }
}
