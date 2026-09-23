using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZMS.MCP.Csharp.Draft;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 拟定层：sqlite 拟定状态、写前日志、诊断配对、语法树重放、partial 补全、新类型文件定位。
/// 不跑 dotnet format、不真的落盘（落盘链路要真实项目，另行人工验证）。
/// </summary>
public sealed class DraftLayerTests
{
    private static string NewTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zms-mcp-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static CSharpCompilation Compile(string source, string path)
    {
        return CSharpCompilation.Create(
            "DraftTest",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path: path)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    // ───────── 拟定状态（sqlite） ─────────

    [Fact]
    public void DraftStore_keeps_one_cookit_and_appends_edits_in_order()
    {
        string root = NewTempDirectory();
        DraftStore store = new(Path.Combine(root, "drafts.db"));
        string project = Path.Combine(root, "Demo.csproj");
        string file = Path.Combine(root, "A.cs");

        DraftRecord first = store.GetOrCreate(project);
        DraftRecord again = store.GetOrCreate(project);

        // MCP 重启（新进程/新连接）也拿到同一个 cookit
        Assert.Equal(first.Cookit, again.Cookit);
        Assert.True(Guid.TryParse(first.Cookit, out _));

        store.Append(project, "Demo.A", "Run", "void Run() { }", file, "hash_original", "content1", "modified");
        store.Append(project, "Demo.A", "Stop", "void Stop() { }", file, "hash_original", "content2", "modified");

        DraftRecord? record = store.Find(project);
        Assert.NotNull(record);
        Assert.Equal([1, 2], record!.Edits.Select(edit => edit.Sequence));
        Assert.Equal("content2", record.Edits[1].ResultContent);
        // 同一文件的两条编辑共用最初那份基线 hash
        Assert.All(record.Edits, edit => Assert.Equal("hash_original", edit.BaselineHash));
        Assert.All(record.Edits, edit => Assert.False(edit.IsDelete));
    }

    [Fact]
    public void DraftStore_records_deletes_as_null_content()
    {
        string root = NewTempDirectory();
        DraftStore store = new(Path.Combine(root, "drafts.db"));
        string project = Path.Combine(root, "Demo.csproj");

        store.Append(project, "Demo.A", "Run", null, Path.Combine(root, "A.cs"), "hash", "content", "removed");

        DraftRecord? record = store.Find(project);
        Assert.True(record!.Edits[0].IsDelete);
        Assert.Null(record.Edits[0].RequestedContent);
    }

    [Fact]
    public void DraftStore_clears_one_project_without_touching_another()
    {
        string root = NewTempDirectory();
        DraftStore store = new(Path.Combine(root, "drafts.db"));
        string firstProject = Path.Combine(root, "First.csproj");
        string secondProject = Path.Combine(root, "Second.csproj");
        string file = Path.Combine(root, "A.cs");

        store.Append(firstProject, "Demo.A", "Run", "void Run() { }", file, "h", "c", "modified");
        store.Append(secondProject, "Demo.B", "Run", "void Run() { }", file, "h", "c", "modified");

        store.Clear(firstProject);

        Assert.Null(store.Find(firstProject));
        Assert.NotNull(store.Find(secondProject));
        Assert.Single(store.ListAll());
    }

    [Fact]
    public void DraftStore_round_trips_the_write_journal()
    {
        string root = NewTempDirectory();
        DraftStore store = new(Path.Combine(root, "drafts.db"));
        string first = Path.Combine(root, "A.cs");
        string second = Path.Combine(root, "B.cs");

        store.RecordJournal("cookie-1", [new(first, "a"), new(second, "b")]);

        Assert.Equal([new KeyValuePair<string, string>(first, "a"), new KeyValuePair<string, string>(second, "b")], store.ReadJournal("cookie-1"));

        store.ClearJournal("cookie-1");
        Assert.Empty(store.ReadJournal("cookie-1"));
    }

    // ───────── 诊断配对 ─────────

    [Fact]
    public void DiagnosticKey_pairs_by_code_message_and_file_ignoring_line_numbers()
    {
        DiagnosticKey first = new("CS0246", "找不到类型", @"C:\a\A.cs", 10);
        DiagnosticKey second = new("CS0246", "找不到类型", @"C:\a\A.cs", 42);

        // 改动会挪行号，行号不能参与配对
        Assert.Equal(first.PairingKey, second.PairingKey);
        Assert.NotEqual(first.PairingKey, new DiagnosticKey("CS0246", "找不到类型", @"C:\a\B.cs", 10).PairingKey);
        Assert.NotEqual(first.PairingKey, new DiagnosticKey("CS0103", "找不到类型", @"C:\a\A.cs", 10).PairingKey);
    }

    [Fact]
    public void Analyze_separates_errors_from_warnings()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Bad.cs");
        string source = "namespace Demo;\npublic class Bad\n{\n    public void Run() { int x = \"text\"; }\n}\n";
        File.WriteAllText(file, source);

        DiagnosticSnapshot snapshot = DraftService.Analyze(Compile(source, file));

        Assert.Contains(snapshot.Errors, item => item.Code == "CS0029");
        Assert.All(snapshot.Errors, item => Assert.Equal(file, item.File));
    }

    [Fact]
    public void Analyze_is_clean_for_valid_source()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Good.cs");
        string source = "namespace Demo;\npublic class Good\n{\n    public void Run() { }\n}\n";
        File.WriteAllText(file, source);

        DiagnosticSnapshot snapshot = DraftService.Analyze(Compile(source, file));

        Assert.Empty(snapshot.Errors);
    }

    // ───────── 语法树重放 ─────────

    [Fact]
    public void Replay_replaces_the_edited_tree_and_adds_new_files()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, "namespace Demo;\npublic class A { }\n");
        CSharpCompilation compilation = Compile(File.ReadAllText(file), file);
        string added = Path.Combine(root, "B.cs");

        DraftEdit edit = new(1, 1, "Demo.A", "", null, file, "", "namespace Demo;\npublic class A\n{\n    public void Run() { }\n}\n", "modified");
        DraftEdit created = new(2, 2, "Demo.B", "", null, added, "", "namespace Demo;\npublic class B { }\n", "新建类型");

        CSharpCompilation replayed = DraftService.Replay(compilation, [edit, created]);

        Assert.Equal(2, replayed.SyntaxTrees.Count());
        Assert.Contains("Run", replayed.SyntaxTrees.First(tree => tree.FilePath == file).ToString(), StringComparison.Ordinal);
        Assert.NotNull(replayed.SyntaxTrees.FirstOrDefault(tree => tree.FilePath == added));
    }

    // ───────── partial 补全 ─────────

    [Fact]
    public void EnsurePartial_inserts_partial_after_the_access_modifier()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Outer.cs");
        string source = "namespace Demo;\npublic class Outer\n{\n}\n";
        File.WriteAllText(file, source);
        INamedTypeSymbol type = Compile(source, file).GetTypeByMetadataName("Demo.Outer")!;

        CodeChange change = Assert.Single(DraftService.EnsurePartial(type));

        Assert.Equal(file, change.FilePath);
        Assert.Contains("public partial class Outer", change.NewContent, StringComparison.Ordinal);
        // 关键字不能被插错位置（不能变成 "public class partial"）
        Assert.DoesNotContain("class partial", change.NewContent, StringComparison.Ordinal);
        Assert.Equal("补 partial", change.Action);
    }

    [Fact]
    public void EnsurePartial_skips_already_partial_types()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Already.cs");
        string source = "namespace Demo;\npublic partial class Already\n{\n}\n";
        File.WriteAllText(file, source);
        INamedTypeSymbol type = Compile(source, file).GetTypeByMetadataName("Demo.Already")!;

        Assert.Empty(DraftService.EnsurePartial(type));
    }

    [Fact]
    public void EnsurePartial_handles_types_without_an_access_modifier()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Plain.cs");
        string source = "namespace Demo;\nclass Plain\n{\n}\n";
        File.WriteAllText(file, source);
        INamedTypeSymbol type = Compile(source, file).GetTypeByMetadataName("Demo.Plain")!;

        CodeChange change = Assert.Single(DraftService.EnsurePartial(type));

        Assert.Contains("partial class Plain", change.NewContent, StringComparison.Ordinal);
    }

    // ───────── 新建类型的文件定位 ─────────

    [Fact]
    public void CreateNewType_places_the_file_by_namespace_after_root_namespace()
    {
        string root = NewTempDirectory();
        string project = Path.Combine(root, "Demo.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>Demo</RootNamespace>
              </PropertyGroup>
            </Project>
            """);
        string existing = Path.Combine(root, "Existing.cs");
        string source = "namespace Demo;\npublic class Existing { }\n";
        File.WriteAllText(existing, source);
        ProjectFileInfo info = ProjectFileInfo.Read(project);

        CodeChange change = DraftService.CreateNewType(info, Compile(source, existing), "Demo.Feature.Widget", "public void Run() { }");

        // RootNamespace 之后的部分变成目录层级
        Assert.Equal(Path.Combine(root, "Feature", "Widget.cs"), change.FilePath);
        Assert.Contains("namespace Demo.Feature;", change.NewContent, StringComparison.Ordinal);
        Assert.Contains("public class Widget", change.NewContent, StringComparison.Ordinal);
        Assert.Contains("public void Run() { }", change.NewContent, StringComparison.Ordinal);
        Assert.Equal("新建类型", change.Action);
    }

    [Fact]
    public void CreateNewType_uses_outer_inner_file_and_partial_outer_for_a_nested_type()
    {
        string root = NewTempDirectory();
        string project = Path.Combine(root, "Demo.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Demo</RootNamespace></PropertyGroup></Project>");
        string outerFile = Path.Combine(root, "Outer.cs");
        string source = "namespace Demo;\npublic class Outer\n{\n}\n";
        File.WriteAllText(outerFile, source);
        ProjectFileInfo info = ProjectFileInfo.Read(project);

        CodeChange change = DraftService.CreateNewType(info, Compile(source, outerFile), "Demo.Outer.Inner", "public void Run() { }");

        // 内部类用 Outer.Inner.cs，外层在新文件里以 partial 出现
        Assert.Equal(Path.Combine(root, "Inner.cs"), change.FilePath);
        Assert.Contains("public partial class Outer", change.NewContent, StringComparison.Ordinal);
        Assert.Contains("public class Inner", change.NewContent, StringComparison.Ordinal);
        Assert.Equal("新建内部类", change.Action);
    }

    [Fact]
    public void CreateNewType_refuses_when_no_content_is_given()
    {
        string root = NewTempDirectory();
        string project = Path.Combine(root, "Demo.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        ProjectFileInfo info = ProjectFileInfo.Read(project);
        CSharpCompilation compilation = Compile("namespace Demo;\npublic class Existing { }\n", Path.Combine(root, "Existing.cs"));

        Assert.Throws<InvalidOperationException>(() => DraftService.CreateNewType(info, compilation, "Demo.New", null));
    }

    // ───────── 确认的入口行为 ─────────

    [Fact]
    public void Confirm_reports_nothing_when_there_is_no_draft()
    {
        string root = NewTempDirectory();
        string project = Path.Combine(root, "Demo.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        string report = DraftService.Confirm(project, "", apply: true);

        Assert.Contains("没有未完成的拟定", report);
    }
}
