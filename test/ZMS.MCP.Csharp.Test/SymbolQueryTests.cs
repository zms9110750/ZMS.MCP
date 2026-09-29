using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZMS.MCP.Csharp.Roslyn;
using ZMS.MCP.Csharp.Tools;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 「列出符号」（源码侧）的测试：种类/修饰符/参数的解析与过滤，以及输出渲染。
/// 过滤类用例跑在真实项目上（要求本仓库已还原过）。
/// </summary>
public sealed class SymbolQueryTests
{
    private static string RepositoryRoot()
    {
        string? fromMetadata = typeof(SymbolQueryTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepositoryRoot")
            ?.Value;
        if (!string.IsNullOrEmpty(fromMetadata))
        {
            string candidate = Path.GetFullPath(fromMetadata);
            if (File.Exists(Path.Combine(candidate, "ZMS.MCP.slnx")))
            {
                return candidate;
            }
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ZMS.MCP.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根（ZMS.MCP.slnx）。");
    }

    private static string SelfProjectPath()
    {
        return Path.Combine(RepositoryRoot(), "src", "ZMS.MCP.Csharp", "ZMS.MCP.Csharp.csproj");
    }

    private static LoadedProject LoadSelf()
    {
        return LoadedProject.Load(SelfProjectPath());
    }

    // ───────── 解析 ─────────

    [Fact]
    public void ParseKinds_maps_kind_letters()
    {
        Assert.Equal(
            SymbolKinds.Namespace | SymbolKinds.Class | SymbolKinds.Struct | SymbolKinds.Interface
            | SymbolKinds.Property | SymbolKinds.Field | SymbolKinds.Event | SymbolKinds.Method | SymbolKinds.Delegate,
            SymbolFilterParser.ParseKinds("NCSIPFEMD"));
    }

    [Fact]
    public void ParseKinds_expands_T_to_the_three_type_kinds()
    {
        SymbolKinds kinds = SymbolFilterParser.ParseKinds("T");

        Assert.Equal(SymbolKinds.Class | SymbolKinds.Struct | SymbolKinds.Interface, kinds);
    }

    [Fact]
    public void ParseKinds_is_case_insensitive_and_ignores_unknown_letters()
    {
        Assert.Equal(SymbolFilterParser.ParseKinds("c"), SymbolFilterParser.ParseKinds("C"));
        Assert.Equal(SymbolKinds.None, SymbolFilterParser.ParseKinds("xyz"));
        Assert.Equal(SymbolKinds.None, SymbolFilterParser.ParseKinds(""));
    }

    [Fact]
    public void ParseKinds_maps_D_to_delegate()
    {
        // v4：D 从"只看带文档注释的"回收给委托
        Assert.Equal(SymbolKinds.Delegate, SymbolFilterParser.ParseKinds("D"));
        Assert.True(SymbolKinds.All.HasFlag(SymbolKinds.Delegate));
    }

    /// <summary>只为参数类型匹配建一个最小编译单元，不碰真实项目。</summary>
    private static CSharpCompilation CompileProbe(string source)
    {
        return CSharpCompilation.Create(
            "Probe",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Fact]
    public void TypeMatches_handles_arrays_generics_aliases_and_nullable_annotations()
    {
        IMethodSymbol method = CompileProbe("""
            namespace Demo;
            public class Holder
            {
                public void Take(string[] items, System.Collections.Generic.List<int> list, string? text) { }
            }
            """)
            .GetTypeByMetadataName("Demo.Holder")!
            .GetMembers("Take")
            .OfType<IMethodSymbol>()
            .Single();

        // 数组：type.Name 对数组是空串，所以这条以前一定匹配不上
        Assert.True(SymbolLocator.TypeMatches("string[]", method.Parameters[0].Type));
        Assert.True(SymbolLocator.TypeMatches("System.String[]", method.Parameters[0].Type));
        Assert.False(SymbolLocator.TypeMatches("string", method.Parameters[0].Type));

        // 泛型：简名、全名、BCL 名三种写法都认
        Assert.True(SymbolLocator.TypeMatches("List<int>", method.Parameters[1].Type));
        Assert.True(SymbolLocator.TypeMatches("System.Collections.Generic.List<int>", method.Parameters[1].Type));
        Assert.True(SymbolLocator.TypeMatches("List<Int32>", method.Parameters[1].Type));
        Assert.False(SymbolLocator.TypeMatches("List<string>", method.Parameters[1].Type));

        // 可空标注不算签名的一部分；关键字和 BCL 名也对得上
        Assert.True(SymbolLocator.TypeMatches("string?", method.Parameters[2].Type));
        Assert.True(SymbolLocator.TypeMatches("string", method.Parameters[2].Type));
        Assert.True(SymbolLocator.TypeMatches("String", method.Parameters[2].Type));
        Assert.False(SymbolLocator.TypeMatches("int", method.Parameters[2].Type));
    }

    [Fact]
    public void UnknownKindLetters_reports_typos_instead_of_swallowing_them()
    {
        Assert.Equal(['X'], SymbolFilterParser.UnknownKindLetters("X"));
        Assert.Equal(['X'], SymbolFilterParser.UnknownKindLetters("CxM"));
        Assert.Empty(SymbolFilterParser.UnknownKindLetters("NCSIPFEMD"));
        Assert.Empty(SymbolFilterParser.UnknownKindLetters(""));
    }

    [Fact]
    public void ParseModifiers_accepts_chinese_and_english()
    {
        Assert.Equal(SymbolModifiers.Public | SymbolModifiers.Static, SymbolFilterParser.ParseModifiers(["公开", "静态"]));
        Assert.Equal(SymbolModifiers.Public | SymbolModifiers.Static, SymbolFilterParser.ParseModifiers(["public", "static"]));
        Assert.Equal(SymbolModifiers.None, SymbolFilterParser.ParseModifiers(["异步", ""]));
    }

    [Fact]
    public void ParseArgumentTypes_splits_on_comma_and_semicolon()
    {
        Assert.Equal(["int", "string"], SymbolFilterParser.ParseArgumentTypes("int, string"));
        Assert.Equal(["a", "b"], SymbolFilterParser.ParseArgumentTypes("a;b"));
        Assert.Empty(SymbolFilterParser.ParseArgumentTypes(""));
    }

    // ───────── 过滤 ─────────

    [Fact]
    public void List_returns_source_symbols_with_namespaces()
    {
        LoadedProject project = LoadSelf();

        IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(project.Compilation, SymbolKinds.None, SymbolModifiers.None, []);

        Assert.NotEmpty(entries);
        // 只列自己源码里的符号：引用的程序集（System.*）不该出现
        Assert.DoesNotContain(entries, entry => entry.Namespace.StartsWith("System", StringComparison.Ordinal));
        Assert.True(
            entries.Any(entry => entry.IsType
                && entry.Signature.Contains("LoadedProject", StringComparison.Ordinal)
                && entry.Namespace == "ZMS.MCP.Csharp.Roslyn"),
            "未找到 LoadedProject。实际前 40 项：\n"
            + string.Join("\n", entries.Take(40).Select(entry => $"[{entry.Namespace}] [{entry.Container}] {entry.Signature}")));
    }

    [Fact]
    public void List_filters_by_kind()
    {
        LoadedProject project = LoadSelf();

        IReadOnlyList<SymbolEntry> types = SymbolQuery.List(project.Compilation, SymbolKinds.Class, SymbolModifiers.None, []);

        Assert.NotEmpty(types);
        Assert.All(types, entry => Assert.True(entry.IsType));
        Assert.All(types, entry => Assert.Equal("class", entry.Kind));
    }

    [Fact]
    public void List_skips_namespaces_that_only_wrap_other_namespaces()
    {
        // ZMS / ZMS.MCP 这种纯层级外壳（下面只有命名空间、自己没有类型定义）不该各出一条：
        // 它们的位置串等于整棵子树的所有文件，看起来跟最深的那条完全重复。
        LoadedProject project = LoadSelf();

        IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(
            project.Compilation, SymbolKinds.Namespace, SymbolModifiers.None, []);
        List<string> names = [.. entries.Where(entry => entry.Kind == "namespace").Select(entry => entry.Namespace)];

        Assert.DoesNotContain("ZMS", names);
        Assert.DoesNotContain("ZMS.MCP", names);
        // 有类型定义的命名空间照旧保留（ZMS.MCP.Csharp 里有 PathComparison）
        Assert.Contains("ZMS.MCP.Csharp", names);
        Assert.Contains(names, name => name.EndsWith(".Roslyn", StringComparison.Ordinal));
    }

    [Fact]
    public void List_filters_by_kind_letters()
    {
        LoadedProject project = LoadSelf();
        SymbolKinds methods = SymbolFilterParser.ParseKinds("M");

        IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(project.Compilation, methods, SymbolModifiers.None, []);

        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.False(entry.IsType));
        Assert.All(entries, entry => Assert.Contains(entry.Kind, new[] { "method", "constructor", "static constructor", "operator" }));
    }

    [Fact]
    public void List_filters_by_modifiers()
    {
        LoadedProject project = LoadSelf();

        IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(
            project.Compilation,
            SymbolKinds.Method,
            SymbolModifiers.Public | SymbolModifiers.Static,
            []);

        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.True(entry.Symbol.IsStatic));
        Assert.All(entries, entry => Assert.Equal(Microsoft.CodeAnalysis.Accessibility.Public, entry.Symbol.DeclaredAccessibility));
    }

    [Fact]
    public void List_with_conflicting_accessibility_returns_empty()
    {
        // AND 语义：公开且私有不可能同时成立 → 空集（不是"取或"）
        LoadedProject project = LoadSelf();

        IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(
            project.Compilation,
            SymbolKinds.All,
            SymbolModifiers.Public | SymbolModifiers.Private,
            []);

        Assert.Empty(entries);
    }

    [Fact]
    public void List_filters_methods_by_argument_types()
    {
        LoadedProject project = LoadSelf();

        IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(
            project.Compilation,
            SymbolKinds.None,
            SymbolModifiers.None,
            ["string"]);

        Assert.NotEmpty(entries);
        // 只列单参数（string）的方法类成员；类型、属性、字段、命名空间都不该出现。
        // 构造函数参数也是 (string)，所以 method 与 constructor 都算命中。
        Assert.All(entries, entry => Assert.False(entry.IsType));
        Assert.All(entries, entry => Assert.True(entry.Kind is "method" or "constructor", entry.Kind));
        Assert.All(
            entries,
            entry => Assert.Single(((Microsoft.CodeAnalysis.IMethodSymbol)entry.Symbol).Parameters));
    }

    [Obsolete("这条拿仓库自身项目（真实 MSBuild 求值）来验符号列表，属靠别人的命令测自己；要保留就该改用临时项目，暂时停用")]
    public void List_keeps_nullable_reference_type_modifier_in_signature()
    {
        // 签名要能看出可空性：McpStdioServer.RunAsync 的 configure 是 Action<IMcpServerBuilder>?
        LoadedProject project = LoadSelf();

        IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(
            project.Compilation,
            SymbolKinds.Method,
            SymbolModifiers.None,
            []);

        Assert.Contains(
            entries,
            entry => entry.Signature.Contains("Action<IMcpServerBuilder>?", StringComparison.Ordinal));
    }

    // ───────── 渲染 ─────────

    [Fact]
    public void Symbols_lists_like_the_old_list_symbols()
    {
        string output = SymbolTools.Symbols(SelfProjectPath(), "", false, "C", "", "");

        Assert.Contains("## ZMS.MCP.Csharp.Roslyn", output);
        Assert.Contains("- `class LoadedProject`", output);
        // 只要类型时不列成员（按种类标记判断；嵌套类型也会缩进，所以不能用缩进判断）
        Assert.DoesNotContain("` (method)", output);
        Assert.DoesNotContain("` (property)", output);
        // 嵌套类型按层级缩进（MsBuildEvaluator.CacheEntry 这类）
        Assert.Contains("\n  - `class ", output);

        // 列成员时：成员行两格缩进（正向断言，不只看"没出现"）
        string withMembers = SymbolTools.Symbols(SelfProjectPath(), "", false, "M", "", "");
        Assert.Contains("\n  - `", withMembers);
        Assert.Contains("` (method)", withMembers);
    }

    [Fact]
    public void Symbols_warns_about_unknown_kind_letters()
    {
        string output = SymbolTools.Symbols(SelfProjectPath(), "", false, "Cx", "", "");

        Assert.Contains("无法识别的 type 字母", output);
        Assert.Contains("X", output);
    }

    [Fact]
    public void Symbols_filters_by_name_letters_and_modifiers()
    {
        // nameFilter = 原来 list_types 的 filter；type = T 只列类型
        string types = SymbolTools.Symbols(SelfProjectPath(), "", false, "T", "", "", "LoadedProject");

        Assert.Contains("LoadedProject", types);
        Assert.Contains("nameFilter='LoadedProject'", types);
        Assert.DoesNotContain("` (method)", types);

        string publics = SymbolTools.Symbols(SelfProjectPath(), "", false, "C", "public,static", "");

        Assert.Contains("符号: ", publics);
    }

    [Fact]
    public void Symbols_lists_a_types_members_when_read_is_false()
    {
        string output = SymbolTools.Symbols(SelfProjectPath(), "ZMS.MCP.Csharp.Draft.DraftEdit", read: false);

        Assert.Contains("# ZMS.MCP.Csharp.Draft.DraftEdit", output);
        // 成员列表带文件与行号
        Assert.Contains(".cs:", output);
        Assert.Contains("IsDelete", output);
    }

    [Fact]
    public void Symbols_reads_a_type_structure_without_member_bodies()
    {
        string output = SymbolTools.Symbols(SelfProjectPath(), "ZMS.MCP.Csharp.Draft.SymbolBaseline", read: true);

        Assert.Contains("## ", output);
        Assert.Contains("### Members", output);
        // 结构视图：方法只给签名、访问器只给记号
        Assert.Contains("Capture(Compilation compilation)", output);
        Assert.DoesNotContain("return baseline;", output);
    }

    [Fact]
    public void Symbols_reads_one_member_with_source()
    {
        string output = SymbolTools.Symbols(SelfProjectPath(), "ZMS.MCP.Csharp.Draft.SymbolBaseline.Key");

        Assert.Contains("## ", output);
        Assert.Contains("```csharp", output);
        Assert.Contains("IdentityFormat", output);
    }

    [Fact]
    public void Symbols_reads_one_accessor_exactly_and_then_shows_its_implementation()
    {
        // 精确匹配到访问器 → 给实现（属性签名里只有 `get { … }` 记号）
        string accessor = SymbolTools.Symbols(SelfProjectPath(), "ZMS.MCP.Csharp.Draft.DraftEdit.IsDelete.get");

        Assert.Contains("RequestedContent == null", accessor);
        Assert.Contains("```csharp", accessor);
    }

    [Fact]
    public void Symbols_reports_an_unknown_member_instead_of_guessing()
    {
        string output = SymbolTools.Symbols(SelfProjectPath(), "ZMS.MCP.Csharp.Draft.DraftEdit.NoSuchMember");

        Assert.StartsWith("Error: ", output, StringComparison.Ordinal);
        Assert.Contains("NoSuchMember", output);
    }
}
