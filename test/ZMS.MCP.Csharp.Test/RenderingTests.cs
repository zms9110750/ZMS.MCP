using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 返回格式规则（需求 3.6 / 3.7 / 3.8）：类型结构视图、重载分组、文档预算与截断。
/// </summary>
public sealed class RenderingTests
{
    private static INamedTypeSymbol TypeOf(string source, string metadataName)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "RenderingTest",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path: "InMemory.cs")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return compilation.GetTypeByMetadataName(metadataName)
            ?? throw new InvalidOperationException($"类型不存在：{metadataName}");
    }

    [Fact]
    public void DocumentationBudget_follows_the_four_tiers()
    {
        // 1 个成员 → 不限行数；2-5 → 200 行；6-10 → 50 行；>10 → 只前 10 个（各 50 行）
        Assert.Equal((1, 0), CodeEditor.DocumentationBudget(1));
        Assert.Equal((3, 200), CodeEditor.DocumentationBudget(3));
        Assert.Equal((7, 50), CodeEditor.DocumentationBudget(7));
        Assert.Equal((10, 50), CodeEditor.DocumentationBudget(51));
    }

    [Fact]
    public void DocumentedMembers_takes_five_per_name_when_several_names_exist()
    {
        INamedTypeSymbol type = TypeOf(
            """
            namespace Demo;
            public class Many
            {
                public void A(int x) { }
                public void A(int x, int y) { }
                public void A(int x, int y, int z) { }
                public void A(int x, int y, int z, int w) { }
                public void A(int x, int y, int z, int w, int v) { }
                public void A(int x, int y, int z, int w, int v, int u) { }
                public void B() { }
            }
            """,
            "Demo.Many");
        List<ISymbol> members = [.. type.GetMembers().Where(member => member is IMethodSymbol && !member.IsImplicitlyDeclared)];

        // 两个不同名字 → 每个名字只给前 5 个（A 有 6 个 → 取 5；B 有 1 个 → 取 1）
        Assert.Equal(6, CodeEditor.DocumentedMembers(members).Count);

        // 只有一个名字时，上限是 10（这里一共 6 个，全都允许）
        List<ISymbol> singleName = [.. members.Where(member => member.Name == "A")];
        Assert.Equal(6, CodeEditor.DocumentedMembers(singleName).Count);
    }

    [Fact]
    public void TruncateLines_cuts_and_reports_the_original_length()
    {
        string text = string.Join('\n', Enumerable.Range(1, 10).Select(index => "line " + index));

        IReadOnlyList<string> lines = CodeEditor.TruncateLines(text, 4);

        Assert.Equal(5, lines.Count);
        Assert.Equal("line 1", lines[0]);
        Assert.Contains("注释共 10 行", lines[^1], StringComparison.Ordinal);
        // 0 表示不截断
        Assert.Equal(10, CodeEditor.TruncateLines(text, 0).Count);
    }

    [Fact]
    public void MemberSignature_hides_bodies_and_shows_initializers_and_accessor_marks()
    {
        INamedTypeSymbol type = TypeOf(
            """
            namespace Demo;
            public class Shape
            {
                private int _count = 3;
                public string Name { get; set; } = "x";
                public int Value { get { return _count; } }
                public void Run(int steps) { _count += steps; }
            }
            """,
            "Demo.Shape");

        // 字段：带初始化器表达式
        Assert.Equal("private int _count = 3;", CodeEditor.MemberSignature(type.GetMembers("_count").Single()));
        // 自动属性：访问器只有 get;/set;，并带初始化器
        Assert.Contains("= \"x\"", CodeEditor.MemberSignature(type.GetMembers("Name").Single()), StringComparison.Ordinal);
        // 有实现的访问器：用 { … } 记号，不展开实现
        Assert.Contains("{ get { … } }", CodeEditor.MemberSignature(type.GetMembers("Value").Single()), StringComparison.Ordinal);
        // 方法：只给签名，没有方法体
        Assert.Equal("public void Run(int steps)", CodeEditor.MemberSignature(type.GetMembers("Run").Single()));
    }

    [Fact]
    public void MemberSignature_shows_every_property_modifier_but_hides_only_the_accessor_bodies()
    {
        INamedTypeSymbol type = TypeOf(
            """
            namespace Demo;
            public class Shape
            {
                private int _reference;
                public ref int Reference { get { return ref _reference; } }
                public ref readonly int CurReference { get { return ref _reference; } }
                public int Value { get; private set; }
                public required string Name { get; init; }
                public override string ToString() { return ""; }
            }
            """,
            "Demo.Shape");

        // 属性本身的 ref / 访问权限 / 访问器访问权限全都在签名里
        Assert.Equal(
            "public ref int Reference { get { … } }",
            CodeEditor.MemberSignature(type.GetMembers("Reference").Single()));
        Assert.Equal(
            "public ref readonly int CurReference { get { … } }",
            CodeEditor.MemberSignature(type.GetMembers("CurReference").Single()));
        Assert.Equal(
            "public int Value { get; private set; }",
            CodeEditor.MemberSignature(type.GetMembers("Value").Single()));
        // override（继承自 object）与 required / init 也要显示
        Assert.Contains("override", CodeEditor.MemberSignature(type.GetMembers("ToString").Single()), StringComparison.Ordinal);
        Assert.Equal(
            "public required string Name { get; init; }",
            CodeEditor.MemberSignature(type.GetMembers("Name").Single()));
    }

    [Fact]
    public void MemberSignature_shows_readonly_on_a_struct_property_and_on_its_accessor()
    {
        INamedTypeSymbol type = TypeOf(
            """
            namespace Demo;
            public struct Point
            {
                private int _x;
                public readonly int X { get { return _x; } }
                public int Y { readonly get { return _x; } set { _x = value; } }
            }
            """,
            "Demo.Point");

        Assert.Equal("public readonly int X { get { … } }", CodeEditor.MemberSignature(type.GetMembers("X").Single()));
        Assert.Equal("public int Y { readonly get { … } set { … } }", CodeEditor.MemberSignature(type.GetMembers("Y").Single()));
    }

    [Fact]
    public void DescribeType_lists_members_without_bodies()
    {
        INamedTypeSymbol type = TypeOf(
            """
            namespace Demo;
            public sealed class Base(int seed) : System.IDisposable
            {
                public void Dispose() { }
            }
            """,
            "Demo.Base");

        string text = CodeEditor.DescribeType(type);

        // 类型头含主构造器参数与接口实现
        Assert.Contains("public sealed class Base(int seed) : System.IDisposable", text, StringComparison.Ordinal);
        // 成员只给签名，不带方法体
        Assert.Contains("- public void Dispose()", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose() { }", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberListRendering_groups_only_after_the_threshold()
    {
        INamedTypeSymbol type = TypeOf(
            """
            namespace Demo;
            public class Overloads
            {
                public void M(int a) { }
                public void M(int a, int b) { }
                public void M(int a, int b, int c) { }
            }
            """,
            "Demo.Overloads");
        List<ISymbol> members =
        [
            .. type.GetMembers()
                .Where(member => member is IMethodSymbol && !member.IsImplicitlyDeclared)
                .OrderBy(member => member.Name, StringComparer.Ordinal),
        ];

        IReadOnlyList<IReadOnlyList<ISymbol>> groups = MemberListRendering.GroupByName(members, member => member);

        Assert.Single(groups);
        // 3 个重载还没到阈值（>10）：照常逐个列名
        Assert.False(MemberListRendering.IsOverloadedGroup(groups[0], member => member));
        Assert.Equal(10, MemberListRendering.OverloadGroupThreshold);
        Assert.Equal("(int)", MemberListRendering.ParameterList(members[0]));
    }

    [Fact]
    public void DescribeOverloads_caps_the_candidate_list()
    {
        INamedTypeSymbol type = TypeOf(
            """
            namespace Demo;
            public class Wide
            {
                public void M(int a) { }
                public void M(int a, int b) { }
                public void M(int a, int b, int c) { }
            }
            """,
            "Demo.Wide");
        List<ISymbol> members = [.. type.GetMembers().Where(member => member is IMethodSymbol && !member.IsImplicitlyDeclared)];

        string text = MemberListRendering.DescribeOverloads(members, limit: 2);

        Assert.Contains("(int)", text, StringComparison.Ordinal);
        Assert.Contains("还有 1 个重载", text, StringComparison.Ordinal);
    }
}
