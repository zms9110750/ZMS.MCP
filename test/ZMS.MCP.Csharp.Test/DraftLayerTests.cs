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

        DraftRecord first = store.GetOrCreate(project);
        DraftRecord again = store.GetOrCreate(project);

        // MCP 重启（新进程/新连接）也拿到同一个 cookit
        Assert.Equal(first.Cookit, again.Cookit);
        Assert.True(Guid.TryParse(first.Cookit, out _));

        store.Append(project, "Demo.A", "Run", "void Run() { }", "modified", "Demo.A.Run()", "snapshot1");
        store.Append(project, "Demo.A", "Stop", "void Stop() { }", "modified", "Demo.A.Stop()", "snapshot2");

        DraftRecord? record = store.Find(project);
        Assert.NotNull(record);
        Assert.Equal([1, 2], record!.Edits.Select(edit => edit.Sequence));
        // 拟定只记「符号 + 意图 + 首次编辑时的符号文本」：没有文件路径、没有整文件内容、没有文件快照
        Assert.Equal("Demo.A.Stop()", record.Edits[1].SymbolKey);
        Assert.Equal("snapshot2", record.Edits[1].SymbolSnapshot);
        Assert.All(record.Edits, edit => Assert.False(edit.IsDelete));
    }

    // ───────── 追踪记录（符号级基线） ─────────

    [Fact]
    public void SymbolBaseline_does_not_count_a_file_move_as_change()
    {
        // 同一份代码、不同文件路径（改名 / 移动）→ 基线一致
        CSharpCompilation first = Compile(
            "namespace Demo;\npublic class A\n{\n    public void Run() { }\n}\n",
            @"C:\one\A.cs");
        CSharpCompilation second = Compile(
            "namespace Demo;\npublic class A\n{\n    public void Run() { }\n}\n",
            @"C:\two\Renamed.cs");

        BaselineDiff diff = SymbolBaseline.Compare(SymbolBaseline.Capture(first), SymbolBaseline.Capture(second));

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void SymbolBaseline_reports_changed_added_and_removed()
    {
        CSharpCompilation before = Compile(
            "namespace Demo;\npublic class A\n{\n    public void Run() { }\n}\n",
            "A.cs");
        CSharpCompilation after = Compile(
            "namespace Demo;\npublic class A\n{\n    public void Run() { }\n    public int Value { get; set; }\n}\npublic class B { }\n",
            "A.cs");

        BaselineDiff diff = SymbolBaseline.Compare(SymbolBaseline.Capture(before), SymbolBaseline.Capture(after));

        Assert.Contains(diff.Added, key => key.Contains("Value", StringComparison.Ordinal));
        Assert.Contains(diff.Added, key => key.Contains("B", StringComparison.Ordinal));
        Assert.Contains(diff.Changed, key => key.Contains("A", StringComparison.Ordinal));
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void DraftStore_tracking_record_survives_and_is_cleared_with_the_draft()
    {
        string root = NewTempDirectory();
        DraftStore store = new(Path.Combine(root, "drafts.db"));
        string project = Path.Combine(root, "Demo.csproj");

        TrackingRecord saved = store.SaveTracking(project, "track-cookie", new Dictionary<string, string> { ["Demo.A"] = "H1" });
        Assert.Equal("track-cookie", saved.TrackingCookie);
        Assert.Equal("H1", store.GetTracking(project)!.Baseline["Demo.A"]);

        store.Append(project, "Demo.A", "Run", "void Run() { }", "added");
        Assert.NotNull(store.Find(project));

        // 取消追踪：追踪记录与拟定一起清掉
        store.ClearTracking(project);
        Assert.Null(store.GetTracking(project));
        Assert.Null(store.Find(project));
        Assert.False(store.HasPendingJournal(project));
    }

    [Fact]
    public void Opening_an_existing_database_again_is_idempotent()
    {
        string root = NewTempDirectory();
        string database = Path.Combine(root, "drafts.db");
        string project = Path.Combine(root, "Demo.csproj");

        DraftStore first = new(database);
        DraftRecord created = first.GetOrCreate(project);

        // 再开一次：新表与新列都已存在，补列迁移必须幂等（不抛异常）
        DraftStore second = new(database);
        DraftRecord again = second.GetOrCreate(project);

        Assert.Equal(created.Cookit, again.Cookit);
    }

    [Fact]
    public void DraftStore_records_deletes_as_null_content()
    {
        string root = NewTempDirectory();
        DraftStore store = new(Path.Combine(root, "drafts.db"));
        string project = Path.Combine(root, "Demo.csproj");

        store.Append(project, "Demo.A", "Run", null, "removed");

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

        store.Append(firstProject, "Demo.A", "Run", "void Run() { }", "modified");
        store.Append(secondProject, "Demo.B", "Run", "void Run() { }", "modified");

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

        store.RecordJournal("cookie-1", [new DraftStore.JournalEntry(first, "a", "H1", "utf-8"), new DraftStore.JournalEntry(second, "b", "H2", "utf-8")]);

        // 写前 hash 与编码也要原样往返（前滚的三态判断靠它们）
        Assert.Equal(
            [
                new DraftStore.JournalEntry(first, "a", "H1", "utf-8"),
                new DraftStore.JournalEntry(second, "b", "H2", "utf-8"),
            ],
            store.ReadJournal("cookie-1"));

        store.ClearJournal("cookie-1");
        Assert.Empty(store.ReadJournal("cookie-1"));
    }

    [Fact]
    public void JournalCookits_lists_every_unfinished_journal()
    {
        string root = NewTempDirectory();
        DraftStore store = new(Path.Combine(root, "drafts.db"));

        store.RecordJournal("cookie-1", [new DraftStore.JournalEntry(Path.Combine(root, "A.cs"), "a", "", "")]);
        store.RecordJournal("cookie-2", [new DraftStore.JournalEntry(Path.Combine(root, "B.cs"), "b", "", "")]);

        Assert.Equal(new[] { "cookie-1", "cookie-2" }, store.JournalCookits());

        store.ClearJournal("cookie-1");
        Assert.Equal(new[] { "cookie-2" }, store.JournalCookits());
    }

    // ───────── 变更分类（增加 / 删除 / 修改） ─────────

    [Fact]
    public void CategoryOf_separates_add_delete_and_modify()
    {
        Assert.Equal("增加", DraftService.CategoryOf(Edit("added")));
        Assert.Equal("增加", DraftService.CategoryOf(Edit("新建类型")));
        Assert.Equal("增加", DraftService.CategoryOf(Edit("新建内部类")));
        Assert.Equal("增加", DraftService.CategoryOf(Edit("补 partial")));
        Assert.Equal("删除", DraftService.CategoryOf(Edit("removed")));
        Assert.Equal("修改", DraftService.CategoryOf(Edit("replaced")));
    }

    // ───────── 前滚：把上次没写完的落盘补齐 ─────────

    [Fact]
    public void RecoverPendingWrites_replays_the_journal_then_clears_it()
    {
        string root = NewTempDirectory();
        string database = Path.Combine(root, "drafts.db");
        string first = Path.Combine(root, "A.cs");
        string second = Path.Combine(root, "Sub", "B.cs");
        DraftStore store = new(database);
        // 模拟"多文件落盘中途崩溃"：日志留着，盘上什么都没有
        store.RecordJournal("cookie-1", [new DraftStore.JournalEntry(first, "class A { }", "", ""), new DraftStore.JournalEntry(second, "class B { }", "", "")]);
        Assert.False(File.Exists(first));

        string report = DraftService.RecoverPendingWrites(database);

        Assert.Contains("前滚", report);
        Assert.Equal("class A { }", File.ReadAllText(first));
        Assert.Equal("class B { }", File.ReadAllText(second));
        // 补齐之后日志要清掉：第二次启动不该重复干活
        Assert.Empty(store.ReadJournal("cookie-1"));
        Assert.Empty(store.JournalCookits());
        Assert.Equal("", DraftService.RecoverPendingWrites(database));
    }

    // ───────── 许可（内存） ─────────

    [Fact]
    public void PermitStore_grants_checks_invalidates_and_isolates_by_symbol()
    {
        string project = @"C:\demo\Demo.csproj";
        Dictionary<string, SymbolPermitSnapshot> snapshot = new(StringComparer.Ordinal)
        {
            ["Demo.A"] = new SymbolPermitSnapshot(@"C:\demo\A.cs", "HASH"),
        };

        string cookie = PermitStore.GrantApply(project, snapshot);

        // cookie 对得上才给快照
        IReadOnlyDictionary<string, SymbolPermitSnapshot>? checkedSnapshot = PermitStore.CheckApply(project, cookie);
        Assert.NotNull(checkedSnapshot);
        Assert.Equal(@"C:\demo\A.cs", checkedSnapshot!["Demo.A"].FilePath);
        Assert.Null(PermitStore.CheckApply(project, "wrong-cookie"));

        // 选择许可按 (项目, 符号) 隔离：不同符号互不影响
        string selectA = PermitStore.GrantSelect(project, "Demo.A");
        string selectB = PermitStore.GrantSelect(project, "Demo.B");
        Assert.NotNull(PermitStore.TakeSelect(project, "Demo.A", selectA));
        Assert.NotNull(PermitStore.TakeSelect(project, "Demo.B", selectB));
        Assert.Null(PermitStore.TakeSelect(project, "Demo.A", selectA));   // 取过即用掉
        Assert.Null(PermitStore.TakeSelect(project, "Demo.B", "wrong"));

        // 作废：落盘许可立刻失效
        PermitStore.Invalidate(project);
        Assert.Null(PermitStore.CheckApply(project, cookie));
    }

    [Fact]
    public void ReplaceSymbol_keeps_only_one_entry_per_symbol_and_RemoveSymbol_drops_it()
    {
        string root = NewTempDirectory();
        DraftStore store = new(Path.Combine(root, "drafts.db"));
        string project = Path.Combine(root, "Demo.csproj");

        store.ReplaceSymbol(project, "Demo.A", "Run()", "void Run1() { }", "Demo.A.Run()", "snapshot", "added");
        store.ReplaceSymbol(project, "Demo.B", "Value", "public int Value { get; set; }", "Demo.B.Value", "", "added");
        store.ReplaceSymbol(project, "Demo.A", "Run()", "void Run2() { }", "Demo.A.Run()", "snapshot", "replaced");

        DraftRecord record = store.Find(project)!;
        // 同一符号只留一条：A.Run() 是替换，不是追加
        Assert.Equal(2, record.Edits.Count);
        Assert.Equal("void Run2() { }", record.Edits.Single(edit => edit.SymbolKey == "Demo.A.Run()").RequestedContent);
        Assert.Equal("snapshot", record.Edits.Single(edit => edit.SymbolKey == "Demo.A.Run()").SymbolSnapshot);

        Assert.Equal(1, store.RemoveSymbol(project, "Demo.A.Run()"));
        Assert.Single(store.Find(project)!.Edits);
    }

    private static DraftEdit Edit(string action)
    {
        return new DraftEdit(1, 1, "Demo.A", "Run", "void Run() { }", action);
    }

    // ───────── 追踪服务 ─────────

    private static string SelfProjectPath()
    {
        string? fromMetadata = typeof(DraftLayerTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepositoryRoot")
            ?.Value;
        if (!string.IsNullOrEmpty(fromMetadata))
        {
            string candidate = Path.GetFullPath(fromMetadata);
            if (File.Exists(Path.Combine(candidate, "ZMS.MCP.slnx")))
            {
                return Path.Combine(candidate, "src", "ZMS.MCP.Csharp", "ZMS.MCP.Csharp.csproj");
            }
        }

        throw new InvalidOperationException("找不到仓库根（ZMS.MCP.slnx）。");
    }

    [Fact]
    public void TrackingService_start_then_resume_keeps_the_same_cookie()
    {
        string root = NewTempDirectory();
        string database = Path.Combine(root, "drafts.db");
        string project = SelfProjectPath();

        string first = TrackingService.Track(project, database);
        Assert.Contains("追踪已开始", first);
        Assert.Contains("## 现在已经有这些拟定写", first);
        Assert.Contains("## 有这些未追踪更改", first);

        string second = TrackingService.Track(project, database);
        Assert.Contains("已在追踪", second);

        // 恢复追踪必须沿用同一个 cookie
        int start = first.IndexOf('`') + 1;
        string cookie = first.Substring(start, first.IndexOf('`', start) - start);
        Assert.Contains(cookie, second);
    }

    [Fact]
    public void TrackingService_untrack_needs_the_cookie_and_clears_tracking()
    {
        string root = NewTempDirectory();
        string database = Path.Combine(root, "drafts.db");
        string project = SelfProjectPath();
        string started = TrackingService.Track(project, database);
        int start = started.IndexOf('`') + 1;
        string cookie = started.Substring(start, started.IndexOf('`', start) - start);

        // cookie 不对：拒绝，且什么都不改
        Assert.Throws<InvalidOperationException>(() => TrackingService.Untrack(project, "wrong-cookie", database));
        Assert.NotNull(new DraftStore(database).GetTracking(project));

        string report = TrackingService.Untrack(project, cookie, database);

        Assert.Contains("取消追踪", report);
        Assert.Contains("已删除拟定", report);
        Assert.Null(new DraftStore(database).GetTracking(project));
    }

    // ───────── 冲突模型（基线 hash vs 现状 hash） ─────────

    [Fact]
    public void ConflictService_reports_changed_added_and_removed_as_unresolved()
    {
        Dictionary<string, string> baseline = new(StringComparer.Ordinal)
        {
            ["Demo.A"] = "H1",
            ["Demo.A.Run()"] = "H2",
            ["Demo.Gone"] = "H3",
        };
        Dictionary<string, string> current = new(StringComparer.Ordinal)
        {
            ["Demo.A"] = "H1-CHANGED",
            ["Demo.A.Run()"] = "H2",
            ["Demo.New"] = "H4",
        };
        DraftEdit drafted = new(1, 1, "Demo.New", "", "class New { }", "新建类型", "Demo.New", "");

        IReadOnlyList<SymbolConflict> conflicts = ConflictService.Unresolved(baseline, current, [drafted]);

        Assert.Contains(conflicts, item => item.SymbolKey == "Demo.A" && item.Kind == ConflictKind.Changed);
        // 新增的、没有拟定的符号也算冲突
        Assert.Contains(conflicts, item => item.SymbolKey == "Demo.New" && item.Kind == ConflictKind.Added && item.HasDraft);
        Assert.Contains(conflicts, item => item.SymbolKey == "Demo.Gone" && item.Kind == ConflictKind.Removed);
        Assert.DoesNotContain(conflicts, item => item.SymbolKey == "Demo.A.Run()");
    }

    [Fact]
    public void ConflictService_IsUnresolved_needs_both_sides_aligned()
    {
        Dictionary<string, string> baseline = new(StringComparer.Ordinal) { ["Demo.A"] = "H1" };

        Assert.False(ConflictService.IsUnresolved(baseline, new Dictionary<string, string> { ["Demo.A"] = "H1" }, "Demo.A"));
        Assert.True(ConflictService.IsUnresolved(baseline, new Dictionary<string, string> { ["Demo.A"] = "H9" }, "Demo.A"));
        // 基线里没有、现状里有 = 新增冲突
        Assert.True(ConflictService.IsUnresolved(baseline, new Dictionary<string, string> { ["Demo.A"] = "H1", ["Demo.B"] = "H9" }, "Demo.B"));
        // 基线里有、现状没有 = 消失冲突
        Assert.True(ConflictService.IsUnresolved(baseline, new Dictionary<string, string>(), "Demo.A"));
    }

    [Fact]
    public void ConflictService_IsRelated_covers_the_container_chain()
    {
        Assert.True(ConflictService.IsRelated("Demo.Class1.Add(int, int)", "Demo.Class1.Add(int, int)"));
        // 成员与它所属的类型视为一体
        Assert.True(ConflictService.IsRelated("Demo.Class1", "Demo.Class1.Add(int, int)"));
        // 兄弟成员不受影响，名字前缀相似的类型也不受影响
        Assert.False(ConflictService.IsRelated("Demo.Class1.Other()", "Demo.Class1.Add(int, int)"));
        Assert.False(ConflictService.IsRelated("Demo.Class12", "Demo.Class1"));
    }

    [Fact]
    public void ConflictService_AcceptCurrent_updates_that_hash_and_keeps_the_other_conflicts()
    {
        string root = NewTempDirectory();
        DraftStore store = new(Path.Combine(root, "drafts.db"));
        string project = Path.Combine(root, "Demo.csproj");
        store.SaveTracking(project, "track-cookie", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Demo.A"] = "OLD-A",
            ["Demo.B"] = "OLD-B",
        });
        Dictionary<string, string> current = new(StringComparer.Ordinal)
        {
            ["Demo.A"] = "NOW-A",
            ["Demo.B"] = "NOW-B",
        };

        ConflictService.AcceptCurrent(project, "Demo.A", current, store.DatabasePath);

        TrackingRecord after = store.GetTracking(project)!;
        Assert.Equal("NOW-A", after.Baseline["Demo.A"]);   // 解决过的 → 更新为现在的 hash
        Assert.Equal("OLD-B", after.Baseline["Demo.B"]);   // 别的冲突原样保留
        Assert.Equal("track-cookie", after.TrackingCookie);
    }

    [Fact]
    public void DraftStore_finds_a_tracking_record_by_its_cookie()
    {
        string root = NewTempDirectory();
        DraftStore store = new(Path.Combine(root, "drafts.db"));
        string project = Path.Combine(root, "Demo.csproj");
        store.SaveTracking(project, "track-cookie", new Dictionary<string, string> { ["Demo.A"] = "H1" });

        Assert.Equal(project, store.GetTrackingByCookie("track-cookie")!.ProjectPath);
        Assert.Null(store.GetTrackingByCookie("nope"));
        Assert.Null(store.GetTrackingByCookie(""));
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


    [Fact]
    public void BuildChanges_merges_partial_and_added_member_into_one_change()
    {
        string root = NewTempDirectory();
        string project = Path.Combine(root, "Demo.csproj");
        File.WriteAllText(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string file = Path.Combine(root, "Class1.cs");
        File.WriteAllText(file, "namespace Demo;\n\npublic class Class1\n{\n}\n");

        // 没还原过的项目会降级，但源码照样能解析 —— 这正是拟定要面对的常态
        LoadedProject loaded = LoadedProject.Load(project);
        List<CodeChange> changes = DraftService.BuildChanges(
            loaded,
            loaded.Compilation,
            "Demo.Class1",
            "Add(int,int)",
            "public static int Add(int a, int b) { return a + b; }",
            out _);

        // 落盘时每个文件只取最后一条改动，所以这里必须只剩一条 —— 且它同时含 partial 与新成员
        CodeChange change = Assert.Single(changes);
        Assert.Equal(file, change.FilePath);
        Assert.Contains("partial", change.NewContent, StringComparison.Ordinal);
        Assert.Contains("Add(int a, int b)", change.NewContent, StringComparison.Ordinal);
    }

    // ───────── 新类型：文件放到项目目录（不因缺省 RootNamespace 多一层） ─────────

    [Fact]
    public void CreateNewType_uses_the_project_name_when_root_namespace_is_not_declared()
    {
        string root = NewTempDirectory();
        string project = Path.Combine(root, "Demo.csproj");
        // 没写 RootNamespace：MSBuild 的默认值就是项目名，不该因此多出一层 Demo\Demo\ 目录
        File.WriteAllText(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
        ProjectFileInfo info = ProjectFileInfo.Read(project);
        CSharpCompilation compilation = Compile("namespace Demo;\npublic class Class1 { }\n", Path.Combine(root, "Class1.cs"));

        CodeChange change = DraftService.CreateNewType(info, compilation, "Demo.NewType", "public int Value { get; set; }");

        Assert.Equal("NewType.cs", Path.GetFileName(change.FilePath));
        Assert.Equal(
            Path.GetFullPath(root),
            Path.GetFullPath(Path.GetDirectoryName(change.FilePath)!));
        Assert.Contains("public class NewType", change.NewContent, StringComparison.Ordinal);
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
        Assert.Equal(Path.Combine(root, "Outer.Inner.cs"), change.FilePath);
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

        // confirm_draft 只认追踪 cookie（不再要 csprojPath）；cookie 无效就抛错（由 ToolGuard 转成 Error: 文本）
        InvalidOperationException invalid = Assert.Throws<InvalidOperationException>(
            () => DraftService.Confirm("no-such-cookie", "", apply: true));
        Assert.Contains("track_project", invalid.Message);

        // 有追踪但没有任何拟定：同样拒绝（服务层与默认库打交道，所以这里也用默认库，收尾清掉）
        DraftStore store = new();
        try
        {
            store.SaveTracking(project, "track-cookie", new Dictionary<string, string>());
            string report = DraftService.Confirm("track-cookie", "", apply: true);
            Assert.Contains("拟定", report);
        }
        finally
        {
            store.ClearTracking(project);
        }
    }
}
