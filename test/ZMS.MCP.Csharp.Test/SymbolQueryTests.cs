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

    private static LoadedProject LoadSelf()
    {
        return LoadedProject.Load(Path.Combine(RepositoryRoot(), "src", "ZMS.MCP.Csharp", "ZMS.MCP.Csharp.csproj"));
    }

    // ───────── 解析 ─────────

    [Fact]
    public void ParseKinds_maps_kind_letters()
    {
        SymbolKinds kinds = SymbolFilterParser.ParseKinds("NCSIPFEMD");

        Assert.True(kinds.HasFlag(SymbolKinds.Namespace));
        Assert.True(kinds.HasFlag(SymbolKinds.Class));
        Assert.True(kinds.HasFlag(SymbolKinds.Struct));
        Assert.True(kinds.HasFlag(SymbolKinds.Interface));
        Assert.True(kinds.HasFlag(SymbolKinds.Property));
        Assert.True(kinds.HasFlag(SymbolKinds.Field));
        Assert.True(kinds.HasFlag(SymbolKinds.Event));
        Assert.True(kinds.HasFlag(SymbolKinds.Method));
        Assert.True(kinds.HasFlag(SymbolKinds.Document));
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
    public void List_filters_methods_by_argument_types()
    {
        LoadedProject project = LoadSelf();

        IReadOnlyList<SymbolEntry> entries = SymbolQuery.List(
            project.Compilation,
            SymbolKinds.None,
            SymbolModifiers.None,
            ["string"]);

        Assert.NotEmpty(entries);
        // 只列单参数（string）的方法；类型、属性、字段都不该出现
        Assert.All(entries, entry => Assert.False(entry.IsType));
        Assert.All(entries, entry => Assert.Equal("method", entry.Kind));
        Assert.All(
            entries,
            entry => Assert.Single(((Microsoft.CodeAnalysis.IMethodSymbol)entry.Symbol).Parameters));
    }

    // ───────── 渲染 ─────────

    [Fact]
    public void ListSymbols_renders_namespace_sections_and_members()
    {
        string project = Path.Combine(RepositoryRoot(), "src", "ZMS.MCP.Csharp", "ZMS.MCP.Csharp.csproj");

        string output = ProjectTools.ScanProjects(RepositoryRoot(), 4, "");
        Assert.Contains("ZMS.MCP.Csharp", output);

        output = SymbolTools.ListSymbols(project, "C", "", "");
        Assert.Contains("## ZMS.MCP.Csharp.Roslyn", output);
        Assert.Contains("`class LoadedProject`", output);
        // 只要类型时不该出现成员行（成员行以两个空格缩进 + 反引号开头）
        Assert.DoesNotContain("\n  - `", output);
    }
}
