using System.Reflection;
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
            | SymbolKinds.Property | SymbolKinds.Field | SymbolKinds.Event | SymbolKinds.Method | SymbolKinds.Document,
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
        // 只列单参数（string）的方法；类型、属性、字段、命名空间都不该出现
        Assert.All(entries, entry => Assert.False(entry.IsType));
        Assert.All(entries, entry => Assert.Equal("method", entry.Kind));
        Assert.All(
            entries,
            entry => Assert.Single(((Microsoft.CodeAnalysis.IMethodSymbol)entry.Symbol).Parameters));
    }

    [Fact]
    public void List_documented_only_lists_symbols_with_xml_docs()
    {
        // D：只列带 XML 文档注释的符号（Program.cs 的顶层语句没有注释，不该出现）
        LoadedProject project = LoadSelf();

        IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(
            project.Compilation,
            SymbolKinds.Document,
            SymbolModifiers.None,
            []);

        Assert.NotEmpty(entries);
        Assert.All(
            entries,
            entry => Assert.False(string.IsNullOrWhiteSpace(entry.Symbol.GetDocumentationCommentXml())));
    }

    [Fact]
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
    public void ListSymbols_renders_namespace_sections_types_and_indented_members()
    {
        string output = SymbolTools.ListSymbols(SelfProjectPath(), "C", "", "");

        Assert.Contains("## ZMS.MCP.Csharp.Roslyn", output);
        Assert.Contains("- `class LoadedProject`", output);
        // 只要类型时不列成员（按种类标记判断；嵌套类型也会缩进，所以不能用缩进判断）
        Assert.DoesNotContain("` (method)", output);
        Assert.DoesNotContain("` (property)", output);
        // 嵌套类型按层级缩进（MsBuildEvaluator.CacheEntry 这类）
        Assert.Contains("\n  - `class ", output);

        // 列成员时：成员行两格缩进（正向断言，不只看"没出现"）
        string withMembers = SymbolTools.ListSymbols(SelfProjectPath(), "M", "", "");
        Assert.Contains("\n  - `", withMembers);
        Assert.Contains("` (method)", withMembers);
    }

    [Fact]
    public void ListSymbols_warns_about_unknown_kind_letters()
    {
        string output = SymbolTools.ListSymbols(SelfProjectPath(), "Cx", "", "");

        Assert.Contains("无法识别的 kind 字母", output);
        Assert.Contains("X", output);
    }
}
