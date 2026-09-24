using Xunit;
using ZMS.MCP.Csharp.Draft;
using ZMS.MCP.Csharp.Project;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 新流程（追踪 → 编辑 → 预检 → 落盘）的端到端测试，跑在临时项目上。
/// 见 `docs/Csharp-拟定流程v3.md`。
/// </summary>
public sealed class DraftFlowTests
{
    /// <summary>造一个最小项目（csproj + 一个带 Add 的类）。</summary>
    private static string NewProject(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "zms-mcp-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "Demo.csproj");
        File.WriteAllText(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(directory, "Class1.cs"),
            "namespace Demo;\n\npublic class Class1\n{\n    public static int Add(int a, int b) { return a + b; }\n}\n");
        return project;
    }

    private static string TakeCookie(string text)
    {
        int start = text.IndexOf('`') + 1;
        return text.Substring(start, text.IndexOf('`', start) - start);
    }

    [Fact]
    public void Stage_requires_an_active_tracking_session()
    {
        string project = NewProject(out _);
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }"));

            Assert.Contains("track_project", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Track_stage_precheck_then_apply_writes_the_file()
    {
        string project = NewProject(out string directory);
        string source = Path.Combine(directory, "Class1.cs");
        try
        {
            Assert.Contains("追踪已开始", TrackingService.Track(project));

            string staged = DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            Assert.Contains("Demo.Class1.Add(int, int)", staged);

            string precheck = DraftService.Confirm(project, "", apply: true);
            Assert.Contains("落盘 cookie", precheck);
            Assert.Contains("Class1.cs", precheck);

            string applied = DraftService.Confirm(project, TakeCookie(precheck), apply: true);

            Assert.Contains("已落盘", applied);
            Assert.Contains("a + b + 1", File.ReadAllText(source));
            // 落盘后拟定清空
            Assert.Contains("没有未完成的拟定", DraftService.Confirm(project, "", apply: true));
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Stage_keeps_one_entry_per_symbol()
    {
        string project = NewProject(out _);
        try
        {
            TrackingService.Track(project);
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 2; }");

            DraftRecord record = new DraftStore().Find(project)!;

            Assert.Single(record.Edits);
            Assert.Contains("a + b + 2", record.Edits[0].RequestedContent!, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Precheck_reports_a_conflict_when_the_symbol_was_edited_outside()
    {
        string project = NewProject(out string directory);
        string source = Path.Combine(directory, "Class1.cs");
        try
        {
            TrackingService.Track(project);
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");

            // 模拟别人手改同一个符号
            File.WriteAllText(source, "namespace Demo;\n\npublic class Class1\n{\n    public static int Add(int a, int b) { return a + b + 100; }\n}\n");

            string precheck = DraftService.Confirm(project, "", apply: true);

            Assert.Contains("冲突", precheck);
            Assert.Contains("预检查未通过", precheck);
            Assert.DoesNotContain("落盘 cookie：`", precheck);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Apply_refuses_when_the_file_changed_after_the_precheck()
    {
        string project = NewProject(out string directory);
        string source = Path.Combine(directory, "Class1.cs");
        try
        {
            TrackingService.Track(project);
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            string cookie = TakeCookie(DraftService.Confirm(project, "", apply: true));

            // 发许可之后文件被外部改过 → 文件字节校验必须拦住
            File.WriteAllText(source, "namespace Demo;\n\npublic class Class1\n{\n    public static int Add(int a, int b) { return a + b + 7; }\n}\n");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Confirm(project, cookie, apply: true));
            Assert.Contains("不一致", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Select_drop_removes_the_symbol_from_the_draft()
    {
        string project = NewProject(out _);
        try
        {
            TrackingService.Track(project);
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");

            const string symbolKey = "Demo.Class1.Add(int, int)";
            string cookie = PermitStore.GrantSelect(project, symbolKey, "snapshot", "draft", "disk");

            Assert.Contains("取消拟定", DraftService.Select(project, symbolKey, cookie, "drop"));
            Assert.Contains("没有未完成的拟定", DraftService.List(project));
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Select_draft_snapshot_and_disk_rewrite_the_entry()
    {
        string project = NewProject(out _);
        try
        {
            const string symbolKey = "Demo.Class1.Add(int, int)";
            const string drafted = "public static int Add(int a, int b) { return a + b + 1; }";

            TrackingService.Track(project);
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", drafted);

            // draft：坚持拟定内容（条目内容不变，这是正确语义）
            string draftCookie = PermitStore.GrantSelect(project, symbolKey, "SNAPSHOT", "DRAFT-IGNORED", "DISK");
            Assert.Contains("用拟定内容", DraftService.Select(project, symbolKey, draftCookie, "draft"));
            Assert.Equal(drafted, new DraftStore().Find(project)!.Edits[0].RequestedContent);
            Assert.Equal("DISK", new DraftStore().Find(project)!.Edits[0].SymbolSnapshot);   // 快照 = 当前磁盘文本

            // snapshot：回到编辑前
            string snapshotCookie = PermitStore.GrantSelect(project, symbolKey, "SNAPSHOT", drafted, "DISK");
            Assert.Contains("用快照内容", DraftService.Select(project, symbolKey, snapshotCookie, "snapshot"));
            Assert.Equal("SNAPSHOT", new DraftStore().Find(project)!.Edits[0].RequestedContent);

            // disk：取当前磁盘内容（等于不改这个符号）
            string diskCookie = PermitStore.GrantSelect(project, symbolKey, "SNAPSHOT", drafted, "DISK");
            Assert.Contains("用现状内容", DraftService.Select(project, symbolKey, diskCookie, "disk"));
            Assert.Equal("DISK", new DraftStore().Find(project)!.Edits[0].RequestedContent);

            // cookie 用过就不能再用
            Assert.Throws<InvalidOperationException>(() => DraftService.Select(project, symbolKey, diskCookie, "disk"));
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Precheck_refuses_when_a_new_file_target_already_exists()
    {
        string project = NewProject(out string directory);
        try
        {
            TrackingService.Track(project);
            // 新建类型：目标文件此刻还不存在
            DraftService.Stage(project, "Demo.NewType", "", "public class NewType { }");
            Assert.Contains("落盘 cookie", DraftService.Confirm(project, "", apply: true));

            // 外部抢先创建了同名类型（写进同一个文件）
            File.WriteAllText(Path.Combine(directory, "NewType.cs"), "namespace Demo;\n\npublic class NewType { }\n");

            string precheck = DraftService.Confirm(project, "", apply: true);

            Assert.Contains("已被外部创建", precheck);
            Assert.DoesNotContain("落盘 cookie：`", precheck);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Stage_can_add_a_member_to_an_existing_file()
    {
        string project = NewProject(out string directory);
        string source = Path.Combine(directory, "Class1.cs");
        try
        {
            TrackingService.Track(project);
            // 往既有类里加成员：文件已存在，但这不是"新建文件"，不该被拦
            DraftService.Stage(project, "Demo.Class1", "Sub(int,int)", "public static int Sub(int a, int b) { return a - b; }");

            string precheck = DraftService.Confirm(project, "", apply: true);

            Assert.Contains("落盘 cookie", precheck);
            Assert.DoesNotContain("新建目标文件已存在", precheck);

            Assert.Contains("已落盘", DraftService.Confirm(project, TakeCookie(precheck), apply: true));

            string text = File.ReadAllText(source);
            Assert.Contains("public static int Sub(int a, int b)", text, StringComparison.Ordinal);
            Assert.Contains("public static int Add(int a, int b)", text, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Apply_refuses_when_a_symbol_disappears_between_precheck_and_apply()
    {
        string project = NewProject(out string directory);
        string source = Path.Combine(directory, "Class1.cs");
        try
        {
            TrackingService.Track(project);
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            string cookie = TakeCookie(DraftService.Confirm(project, "", apply: true));

            // 窗口期内把符号整个删掉：不能静默少写一部分
            File.WriteAllText(source, "namespace Demo;\n\npublic class Class1\n{\n}\n");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Confirm(project, cookie, apply: true));
            Assert.Contains("定位不到", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Probe_reports_a_missing_directory_as_creatable()
    {
        string root = Path.Combine(Path.GetTempPath(), "zms-mcp-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 目录不存在 → 能写，但如实报告（提示级，不是 not-writable）
            Assert.Equal(WriteProbe.DirectoryWillBeCreated, FileWriter.Probe(Path.Combine(root, "Sub", "A.cs")));

            Directory.CreateDirectory(root);
            Assert.Equal(WriteProbe.Writable, FileWriter.Probe(Path.Combine(root, "A.cs")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Select_refuses_an_ambiguous_member_path()
    {
        string project = NewProject(out string directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "Other.cs"),
                "namespace Demo;\n\npublic class Other\n{\n    public static int Add(int a, int b) { return a + b; }\n}\n");
            TrackingService.Track(project);
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            DraftService.Stage(project, "Demo.Other", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 2; }");

            string cookie = PermitStore.GrantSelect(project, "Demo.Class1.Add(int, int)", "S", "D", "K");

            // 成员名 "Add(int,int)" 同时命中两个类型 → 不猜，要求完整符号键
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Select(project, "Add(int,int)", cookie, "drop"));

            Assert.Contains("命中 2 条", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Precheck_lists_the_diagnostics_the_draft_would_add()
    {
        string project = NewProject(out _);
        try
        {
            TrackingService.Track(project);
            // 引用一个不存在的成员：拟定本身语法合法，但应用后会新增一个编译错误
            DraftService.Stage(project, "Demo.Class1", "Bad()", "public static int Bad() { return NotExist.Value; }");

            string precheck = DraftService.Confirm(project, "", apply: true);

            Assert.Contains("## 诊断对比", precheck);
            Assert.Contains("CS", precheck);                  // 报出了具体的编译错误码
            Assert.DoesNotContain("新增：无", precheck);        // 不再是恒空的"无"
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Stage_creates_a_nested_type_and_marks_the_outer_type_partial()
    {
        string project = NewProject(out string directory);
        string outer = Path.Combine(directory, "Class1.cs");
        try
        {
            TrackingService.Track(project);
            // 新建内部类：外层类要自动补 partial，内部类落到 Class1.Inner.cs
            // content 是**类型体**（成员列表），不是完整类型声明
            DraftService.Stage(project, "Demo.Class1.Inner", "", "public int V;");

            Assert.Contains("已落盘", DraftService.Confirm(project, TakeCookie(DraftService.Confirm(project, "", apply: true)), apply: true));

            Assert.Contains("partial class Class1", File.ReadAllText(outer), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(directory, "Class1.Inner.cs")));
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Precheck_refuses_a_file_whose_encoding_cannot_be_determined()
    {
        string project = NewProject(out string directory);
        string source = Path.Combine(directory, "Class1.cs");
        try
        {
            // 无 BOM、也不是合法 UTF-8（尾部两个非法字节）→ 按需求"不猜、拒绝写并报告"
            List<byte> bytes =
            [
                .. System.Text.Encoding.UTF8.GetBytes(
                    "namespace Demo;\n\npublic class Class1\n{\n    public static int Add(int a, int b) { return a + b; }\n}\n// "),
                0xC3,
                0x28,
            ];
            File.WriteAllBytes(source, [.. bytes]);

            TrackingService.Track(project);
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");

            string precheck = DraftService.Confirm(project, "", apply: true);

            Assert.Contains("无法判定编码", precheck);
            Assert.DoesNotContain("落盘 cookie：`", precheck);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Untrack_invalidates_the_in_memory_apply_cookie()
    {
        string project = NewProject(out _);
        try
        {
            TrackingService.Track(project);
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            string cookie = TakeCookie(DraftService.Confirm(project, "", apply: true));

            TrackingService.Untrack(project, new DraftStore().GetTracking(project)!.TrackingCookie);

            // 同一项目再开一轮，但不重新预检：旧 applyCookie 必须已经失效
            TrackingService.Track(project);
            DraftService.Stage(project, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 3; }");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Confirm(project, cookie, apply: true));

            Assert.Contains("没有有效的落盘许可", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }
}
