using Xunit;
using ZMS.MCP.Csharp.Draft;
using ZMS.MCP.Csharp.Project;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 新流程（追踪 → 编辑 → 预检 → 落盘）的端到端测试，跑在临时项目上。
/// 编辑一律先追踪：stage_draft / confirm_draft 只认 track_project 发出来的追踪 cookie。
/// </summary>
public sealed class DraftFlowTests
{
    private const string AddSymbolKey = "Demo.Class1.Add(int, int)";

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

    /// <summary>从工具输出里取出第一个反引号里的 cookie。</summary>
    private static string TakeCookie(string text)
    {
        int start = text.IndexOf('`') + 1;
        return text.Substring(start, text.IndexOf('`', start) - start);
    }

    /// <summary>追踪并返回追踪 cookie。</summary>
    private static string TrackAndTakeCookie(string project)
    {
        return TakeCookie(TrackingService.Track(project));
    }

    [Fact]
    public void Stage_requires_an_active_tracking_session()
    {
        string project = NewProject(out _);
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Stage("not-a-cookie", "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }"));

            Assert.Contains("track_project", exception.Message, StringComparison.Ordinal);
            Assert.Contains("cookie", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Track_reports_the_cookie_and_its_two_uses()
    {
        string project = NewProject(out _);
        try
        {
            string started = TrackingService.Track(project);

            Assert.Contains("追踪已开始", started);
            Assert.Contains("stage_draft", started);
            Assert.Contains("confirm_draft", started);
            // 同一个 cookie 也用来解除追踪
            Assert.Contains("track_project", started);
            Assert.Contains("## 有这些未追踪更改", started);
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
            string cookie = TrackAndTakeCookie(project);

            string staged = DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            Assert.Contains("Demo.Class1.Add(int, int)", staged);

            string precheck = DraftService.Confirm(cookie, "");
            Assert.Contains("落盘 cookie", precheck);
            Assert.Contains("Class1.cs", precheck);

            string applied = DraftService.Confirm(cookie, TakeCookie(precheck));

            Assert.Contains("已落盘", applied);
            // 落盘汇报不再提 git
            Assert.DoesNotContain("git", applied, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("a + b + 1", File.ReadAllText(source));
            // 落盘后拟定清空
            Assert.Contains("没有未完成的拟定", DraftService.Confirm(cookie, ""));
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
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 2; }");

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
    public void Precheck_reports_an_unresolved_conflict_when_the_symbol_was_edited_outside()
    {
        string project = NewProject(out string directory);
        string source = Path.Combine(directory, "Class1.cs");
        try
        {
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");

            // 模拟别人手改同一个符号
            File.WriteAllText(source, "namespace Demo;\n\npublic class Class1\n{\n    public static int Add(int a, int b) { return a + b + 100; }\n}\n");

            string precheck = DraftService.Confirm(cookie, "");

            Assert.Contains("未解决的冲突", precheck);
            Assert.Contains(AddSymbolKey, precheck);
            Assert.Contains("预检查未通过", precheck);
            Assert.DoesNotContain("落盘 cookie：`", precheck);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Precheck_treats_a_symbol_added_outside_as_a_conflict_even_without_a_draft()
    {
        string project = NewProject(out string directory);
        try
        {
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");

            // 外部新增了一个符号：它没有拟定，但也算冲突
            File.WriteAllText(
                Path.Combine(directory, "Outside.cs"),
                "namespace Demo;\n\npublic class CameFromOutside\n{\n    public void Run() { }\n}\n");

            string precheck = DraftService.Confirm(cookie, "");

            Assert.Contains("未解决的冲突", precheck);
            Assert.Contains("Demo.CameFromOutside", precheck);
            Assert.Contains("新出现", precheck);
            Assert.DoesNotContain("落盘 cookie：`", precheck);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Select_keep_resolves_the_conflict_and_updates_the_saved_hash()
    {
        string project = NewProject(out string directory);
        string source = Path.Combine(directory, "Class1.cs");
        try
        {
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            File.WriteAllText(source, "namespace Demo;\n\npublic class Class1\n{\n    public static int Add(int a, int b) { return a + b + 100; }\n}\n");
            Assert.DoesNotContain("落盘 cookie：`", DraftService.Confirm(cookie, ""));

            // 保持拟定（坚持本次写法）：该符号的 hash 更新为现在的 hash → 冲突解除
            string selectCookie = PermitStore.GrantSelect(project, AddSymbolKey);
            Assert.Contains("保持拟定", DraftService.Select(project, AddSymbolKey, selectCookie, "keep"));

            string precheck = DraftService.Confirm(cookie, "");
            Assert.Contains("落盘 cookie", precheck);

            // 落盘后文件里是拟定内容
            Assert.Contains("已落盘", DraftService.Confirm(cookie, TakeCookie(precheck)));
            Assert.Contains("a + b + 1", File.ReadAllText(source));
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Select_drop_removes_the_draft_and_accepts_the_current_state()
    {
        string project = NewProject(out string directory);
        string source = Path.Combine(directory, "Class1.cs");
        try
        {
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            File.WriteAllText(source, "namespace Demo;\n\npublic class Class1\n{\n    public static int Add(int a, int b) { return a + b + 100; }\n}\n");

            string selectCookie = PermitStore.GrantSelect(project, AddSymbolKey);
            Assert.Contains("移除拟定", DraftService.Select(project, AddSymbolKey, selectCookie, "drop"));

            // 拟定被移掉了
            Assert.Contains("没有未完成的拟定", DraftService.List(project));
            // 冲突也解除了：再拟定一次就能正常预检
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 200; }");
            Assert.Contains("落盘 cookie", DraftService.Confirm(cookie, ""));
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Select_drop_accepts_an_added_symbol_that_has_no_draft()
    {
        string project = NewProject(out string directory);
        try
        {
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            File.WriteAllText(
                Path.Combine(directory, "Outside.cs"),
                "namespace Demo;\n\npublic class CameFromOutside\n{\n    public void Run() { }\n}\n");

            string selectCookie = PermitStore.GrantSelect(project, "Demo.CameFromOutside");
            Assert.Contains("移除拟定", DraftService.Select(project, "Demo.CameFromOutside", selectCookie, "drop"));

            Assert.Contains("落盘 cookie", DraftService.Confirm(cookie, ""));
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Select_no_longer_offers_snapshot_and_needs_a_valid_cookie()
    {
        string project = NewProject(out _);
        try
        {
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");

            // snapshot 已经取消
            Assert.Throws<ArgumentException>(
                () => DraftService.Select(project, AddSymbolKey, PermitStore.GrantSelect(project, AddSymbolKey), "snapshot"));

            // 用过的 cookie 不能再用
            string used = PermitStore.GrantSelect(project, AddSymbolKey);
            DraftService.Select(project, AddSymbolKey, used, "keep");
            Assert.Throws<InvalidOperationException>(() => DraftService.Select(project, AddSymbolKey, used, "keep"));

            // keep 需要该符号有拟定
            string other = PermitStore.GrantSelect(project, "Demo.NotDrafted");
            Assert.Throws<InvalidOperationException>(() => DraftService.Select(project, "Demo.NotDrafted", other, "keep"));
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
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            string applyCookie = TakeCookie(DraftService.Confirm(cookie, ""));

            // 发许可之后文件被外部改过 → 文件字节校验必须拦住
            File.WriteAllText(source, "namespace Demo;\n\npublic class Class1\n{\n    public static int Add(int a, int b) { return a + b + 7; }\n}\n");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Confirm(cookie, applyCookie));
            Assert.Contains("不一致", exception.Message, StringComparison.Ordinal);
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
            string cookie = TrackAndTakeCookie(project);
            // 新建类型：目标文件此刻还不存在
            DraftService.Stage(cookie, "Demo.NewType", "", "public class NewType { }");
            Assert.Contains("落盘 cookie", DraftService.Confirm(cookie, ""));

            // 外部抢先创建了同名类型（写进同一个文件）
            File.WriteAllText(Path.Combine(directory, "NewType.cs"), "namespace Demo;\n\npublic class NewType { }\n");

            string precheck = DraftService.Confirm(cookie, "");

            Assert.Contains("未解决的冲突", precheck);
            Assert.Contains("Demo.NewType", precheck);
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
            string cookie = TrackAndTakeCookie(project);
            // 往既有类里加成员：文件已存在，但这不是"新建文件"，不该被拦。
            // memberName 用**裸名**（新成员的名字）—— 带上参数表就等于声明"我指的是这个已有签名"，
            // 匹配不上会直接报错。
            DraftService.Stage(cookie, "Demo.Class1", "Sub", "public static int Sub(int a, int b) { return a - b; }");

            string precheck = DraftService.Confirm(cookie, "");

            Assert.Contains("落盘 cookie", precheck);
            Assert.DoesNotContain("新建目标文件已存在", precheck);

            Assert.Contains("已落盘", DraftService.Confirm(cookie, TakeCookie(precheck)));

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
    public void Stage_refuses_a_parameter_list_that_matches_nothing()
    {
        // 带了参数表 = "我指的就是这个签名"，匹配不上就别当新增（那会写出第二份同名成员，CS0111）
        string project = NewProject(out _);
        try
        {
            string cookie = TrackAndTakeCookie(project);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Stage(cookie, "Demo.Class1", "Add(int,long)", "public static int Add(int a, long b) { return 0; }"));

            Assert.Contains("定位不到符号", exception.Message, StringComparison.Ordinal);
            // 名字对得上、签名对不上的那些要摆出来，好让人看出是参数表写错了
            Assert.Contains("Add", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Stage_reports_the_diagnostics_this_edit_would_add()
    {
        // 每加一条就在内存里报"这条让诊断多了什么" —— 不等 confirm_draft 才攒出来
        string project = NewProject(out _);
        try
        {
            string cookie = TrackAndTakeCookie(project);
            string clean = DraftService.Stage(cookie, "Demo.Class1", "Sub", "public static int Sub(int a, int b) { return a - b; }");

            Assert.Contains("这条改动带来的诊断变化", clean, StringComparison.Ordinal);

            // 第二条引用一个不存在的成员：当场就该报出编译错误，而不是等预检
            string broken = DraftService.Stage(cookie, "Demo.Class1", "Bad", "public static int Bad() { return NotExist.Value; }");

            Assert.Contains("这条改动带来的诊断变化", broken, StringComparison.Ordinal);
            Assert.Contains("CS", broken, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Load_reuses_the_same_compilation_while_the_sources_are_unchanged()
    {
        // "拟定全程在内存里做"的前提：源码没变就复用同一份编译；
        // 磁盘上的 .cs 一旦变了（哪怕只动时间戳）就重装配。
        string project = NewProject(out string directory);
        try
        {
            LoadedProject first = LoadedProject.Load(project);
            Assert.Same(first, LoadedProject.Load(project));

            File.SetLastWriteTimeUtc(Path.Combine(directory, "Class1.cs"), DateTime.UtcNow.AddMinutes(5));
            Assert.NotSame(first, LoadedProject.Load(project));
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Stage_adds_a_class_under_a_namespace()
    {
        // typePath 指向**命名空间** → 在那个命名空间下加一个类（类名写在 content 里）
        string project = NewProject(out string directory);
        try
        {
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo", "", "public class Widget\n{\n    public int Value;\n}\n");

            string precheck = DraftService.Confirm(cookie, "");
            Assert.Contains("落盘 cookie", precheck);
            Assert.Contains("已落盘", DraftService.Confirm(cookie, TakeCookie(precheck)));

            string widget = Path.Combine(directory, "Widget.cs");
            Assert.True(File.Exists(widget), "新的类该落在命名空间对应的目录里");
            string text = File.ReadAllText(widget);
            Assert.Contains("namespace Demo;", text, StringComparison.Ordinal);
            Assert.Contains("public class Widget", text, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }

    [Fact]
    public void Removing_a_field_leaves_no_shell_behind()
    {
        // 字段的声明节点是**一个声明符**（VariableDeclarator），直接删它会留下
        // "private static readonly T;" 这种壳（CS1519）
        string project = NewProject(out string directory);
        string source = Path.Combine(directory, "Class1.cs");
        try
        {
            File.WriteAllText(
                source,
                "namespace Demo;\n\npublic class Class1\n{\n    private static readonly System.Collections.Generic.Dictionary<string, int> Cache = new();\n\n    public static int Add(int a, int b) { return a + b; }\n}\n");

            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Cache", null);

            string precheck = DraftService.Confirm(cookie, "");
            Assert.Contains("已落盘", DraftService.Confirm(cookie, TakeCookie(precheck)));

            string text = File.ReadAllText(source);
            Assert.DoesNotContain("Cache", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private static readonly", text, StringComparison.Ordinal);
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
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            string applyCookie = TakeCookie(DraftService.Confirm(cookie, ""));

            // 窗口期内把符号整个删掉：不能静默少写一部分
            File.WriteAllText(source, "namespace Demo;\n\npublic class Class1\n{\n}\n");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Confirm(cookie, applyCookie));
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
            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            DraftService.Stage(cookie, "Demo.Other", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 2; }");

            string selectCookie = PermitStore.GrantSelect(project, AddSymbolKey);

            // 成员名 "Add(int,int)" 同时命中两个类型 → 不猜，要求完整符号键
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Select(project, "Add(int,int)", selectCookie, "drop"));

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
            string cookie = TrackAndTakeCookie(project);
            // 引用一个不存在的成员：拟定本身语法合法，但应用后会新增一个编译错误
            DraftService.Stage(cookie, "Demo.Class1", "Bad", "public static int Bad() { return NotExist.Value; }");

            string precheck = DraftService.Confirm(cookie, "");

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
            string cookie = TrackAndTakeCookie(project);
            // 新建内部类：外层类要自动补 partial，内部类落到 Class1.Inner.cs
            // content 是**类型体**（成员列表），不是完整类型声明
            DraftService.Stage(cookie, "Demo.Class1.Inner", "", "public int V;");

            Assert.Contains("已落盘", DraftService.Confirm(cookie, TakeCookie(DraftService.Confirm(cookie, ""))));

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

            string cookie = TrackAndTakeCookie(project);
            DraftService.Stage(cookie, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");

            string precheck = DraftService.Confirm(cookie, "");

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
            string first = TrackAndTakeCookie(project);
            DraftService.Stage(first, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 1; }");
            string applyCookie = TakeCookie(DraftService.Confirm(first, ""));

            TrackingService.Untrack(project, new DraftStore().GetTracking(project)!.TrackingCookie);

            // 同一项目再开一轮，但不重新预检：旧 applyCookie 必须已经失效
            string second = TrackAndTakeCookie(project);
            DraftService.Stage(second, "Demo.Class1", "Add(int,int)", "public static int Add(int a, int b) { return a + b + 3; }");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => DraftService.Confirm(second, applyCookie));

            Assert.Contains("没有有效的落盘许可", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            new DraftStore().ClearTracking(project);
        }
    }
}
