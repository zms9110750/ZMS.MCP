using System.Reflection;
using System.Text;
using Xunit;
using ZMS.MCP.Csharp.Project;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 项目层：查看 slnx（虚拟文件夹树）/ 查看 csproj（原文 + 向上找声明文件）/ 编辑元数据 / 写文件通则。
/// 全部在临时目录上跑，不碰真实项目。
/// </summary>
public sealed class WorkspaceLayerTests
{
    private static string NewTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zms-mcp-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Write(string directory, string relativePath, string content)
    {
        string path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string RepositoryRoot()
    {
        // 构建输出被重定向到仓库外（见测试 csproj 的 AssemblyMetadata），所以先看编译期写进来的根
        string? fromMetadata = typeof(WorkspaceLayerTests).Assembly
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

    // ───────── 向上找 ─────────

    [Fact]
    public void FindNearestUpwards_stops_at_the_closest_match()
    {
        string root = NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src", "Deep", "Deeper"));
        Write(root, "Directory.Build.props", "<Project />");
        Write(root, "src/Directory.Build.props", "<Project />");

        string? found = ProjectViewer.FindNearestUpwards(Path.Combine(root, "src", "Deep", "Deeper"), "Directory.Build.props");

        // 「找到即停」：拿最近的一份，不是最上面那份
        Assert.Equal(Path.Combine(root, "src", "Directory.Build.props"), found);
    }

    [Fact]
    public void FindNearestUpwards_returns_null_when_nothing_matches()
    {
        Assert.Null(ProjectViewer.FindNearestUpwards(NewTempDirectory(), "NuGet.config"));
    }

    // ───────── 查看 csproj ─────────

    [Fact]
    public void CollectDeclarationFiles_lists_project_then_directory_level_files()
    {
        string root = NewTempDirectory();
        string project = Write(root, "Demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(root, "Directory.Build.props", "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");
        Write(root, "Directory.Packages.props", "<Project />");
        Write(root, "global.json", "{ \"sdk\": { \"version\": \"10.0.100\" } }");

        IReadOnlyList<DeclarationFile> files = ProjectViewer.CollectDeclarationFiles(project);
        List<string> names = [.. files.Select(file => Path.GetFileName(file.Path))];

        Assert.Equal("Demo.csproj", names[0]);
        Assert.Contains("Directory.Build.props", names);
        Assert.Contains("Directory.Packages.props", names);
        Assert.Contains("global.json", names);
        // 不存在的就不列
        Assert.DoesNotContain("Directory.Build.targets", names);
        Assert.DoesNotContain("NuGet.config", names);
        // 内容也要一起带出来
        Assert.Contains(files, file => file.Content.Contains("<Nullable>enable</Nullable>", StringComparison.Ordinal));
    }

    [Fact]
    public void CollectDeclarationFiles_reports_when_the_obj_location_is_unknown()
    {
        // 这个 csproj 没有 TargetFramework，MSBuild 评估必然失败：此时**不能**硬编码 <项目目录>/obj/，
        // 而要说明"问不到，所以不列"
        string root = NewTempDirectory();
        string project = Write(root, "Demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(root, "obj/Demo.csproj.nuget.g.props", "<Project />");
        Write(root, "obj/Demo.csproj.nuget.g.targets", "<Project />");

        IReadOnlyList<DeclarationFile> files = ProjectViewer.CollectDeclarationFiles(project);

        Assert.DoesNotContain(files, file => file.Path.Contains("nuget.g.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, file => file.Path == "(obj 的位置未知)");
    }

    [Obsolete("靠真跑 dotnet msbuild 才成立：用别人的命令测自己的稳定性，冷启动几秒且与要验的语义无关")]
    public void ResolveIntermediateDirectory_asks_msbuild_instead_of_hard_coding_obj()
    {
        // 真实项目：obj 的位置必须来自 MSBuild（本仓库的中间输出目录本身就被重定向过）
        string directory = ProjectViewer.ResolveIntermediateDirectory(SelfProjectPath(), out string source);

        Assert.NotEmpty(directory);
        Assert.True(Directory.Exists(directory), directory);
        Assert.Contains("MSBuild", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectDeclarationFiles_follows_an_import_of_the_nearest_props()
    {
        string root = NewTempDirectory();
        string project = Write(root, "src/Demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string upper = Write(root, "Directory.Build.props", "<Project><PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup></Project>");
        // 最近的 Directory.Build.props 自己 Import 了更上层那份 —— 那份同样参与项目声明
        Write(root, "src/Directory.Build.props", "<Project><Import Project=\"../Directory.Build.props\" /></Project>");

        IReadOnlyList<DeclarationFile> files = ProjectViewer.CollectDeclarationFiles(project);
        List<string> paths = [.. files.Select(file => Path.GetFullPath(file.Path))];

        Assert.Contains(Path.Combine(root, "src", "Directory.Build.props"), paths);
        Assert.Contains(Path.GetFullPath(upper), paths);
    }

    [Fact]
    public void ResolveImportPath_expands_msbuild_this_file_directory_and_refuses_unknowns()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zms-import");

        string? resolved = ProjectViewer.ResolveImportPath("$(MSBuildThisFileDirectory)../Shared.props", directory);

        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "..", "Shared.props")), resolved);
        // 未知属性 / 通配符：解析不出来就不猜
        Assert.Null(ProjectViewer.ResolveImportPath("$(UnknownDir)/A.props", directory));
        Assert.Null(ProjectViewer.ResolveImportPath("*.props", directory));
    }

    // ───────── 编辑解决方案的格式限制 ─────────

    [Fact]
    public void RequireSlnx_accepts_slnx_and_refuses_sln()
    {
        string root = NewTempDirectory();
        string sln = Write(root, "Hello.sln", "");
        string slnx = Write(root, "Hello.slnx", "<Solution />");

        Assert.Equal(slnx, SolutionExplorer.RequireSlnx(slnx));
        Assert.Throws<InvalidOperationException>(() => SolutionExplorer.RequireSlnx(sln));
        Assert.Throws<FileNotFoundException>(() => SolutionExplorer.RequireSlnx(Path.Combine(root, "Nope.slnx")));
    }

    [Fact]
    public void View_returns_project_xml_and_declaration_files()
    {
        string root = NewTempDirectory();
        string project = Write(root, "Demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(root, "Directory.Build.props", "<Project><PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup></Project>");

        string view = ProjectViewer.View(project, root);

        Assert.Contains("Demo.csproj", view);
        Assert.Contains("<Project Sdk=\"Microsoft.NET.Sdk\" />", view);
        Assert.Contains("Directory.Build.props", view);
        Assert.Contains("<LangVersion>latest</LangVersion>", view);
    }

    [Fact]
    public void ResolveProjectFile_accepts_an_existing_path_and_throws_for_a_missing_one()
    {
        string root = NewTempDirectory();
        string project = Write(root, "Demo.csproj", "<Project />");

        Assert.Equal(Path.GetFullPath(project), ProjectViewer.ResolveProjectFile(project));
        Assert.Throws<FileNotFoundException>(() => ProjectViewer.ResolveProjectFile(Path.Combine(root, "NoSuch.csproj")));
    }

    // ───────── 编辑元数据 ─────────

    [Fact]
    public void EditMetadata_refuses_broken_xml_and_wrong_root_element()
    {
        string root = NewTempDirectory();
        string project = Write(root, "Demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.Throws<InvalidOperationException>(() => ProjectEditor.EditMetadata(project, "<Project><PropertyGroup>"));
        Assert.Throws<InvalidOperationException>(() => ProjectEditor.EditMetadata(project, "<NotProject />"));
        // 检查没过就绝不落盘
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\" />", File.ReadAllText(project));
    }

    [Fact]
    public void EditMetadata_dry_run_reports_but_does_not_write()
    {
        string root = NewTempDirectory();
        string project = Write(root, "Demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        string report = ProjectEditor.EditMetadata(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>",
            dryRun: true);

        Assert.Contains("预演", report);
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\" />", File.ReadAllText(project));
    }

    [Fact]
    public void EditMetadata_writes_valid_content_back_with_the_original_encoding()
    {
        string root = NewTempDirectory();
        string project = Path.Combine(root, "Demo.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />", new UTF8Encoding(true));

        string report = ProjectEditor.EditMetadata(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");

        Assert.Contains("已写入", report);
        Assert.Contains("utf-8", report, StringComparison.OrdinalIgnoreCase);
        byte[] bytes = File.ReadAllBytes(project);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    // ───────── 查看 slnx ─────────

    [Fact]
    public void ViewTree_matches_the_spec_layout()
    {
        string root = NewTempDirectory();
        string slnx = Write(root, "Hello.slnx", """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj" />
                <Project Path="src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj" />
              </Folder>
              <Folder Name="/test/">
                <Project Path="test/ZMS.MCP.Test/ZMS.MCP.Test.csproj" />
              </Folder>
            </Solution>
            """);

        string tree = SolutionViewer.ViewTree(slnx);

        string[] expected =
        [
            "Hello.slnx",
            "├─src",
            "│  ├─src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj",
            "│  └─src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj",
            "└─test",
            "   └─test/ZMS.MCP.Test/ZMS.MCP.Test.csproj",
            "",
        ];
        Assert.Equal(string.Join(Environment.NewLine, expected), tree);
    }

    [Fact]
    public void ViewTree_nests_sub_folders_and_keeps_projects_at_the_root()
    {
        string root = NewTempDirectory();
        string slnx = Write(root, "Root.slnx", """
            <Solution>
              <Project Path="Loose.csproj" />
              <Folder Name="/src/sub/">
                <Project Path="src/sub/Deep.csproj" />
              </Folder>
            </Solution>
            """);

        string tree = SolutionViewer.ViewTree(slnx);

        Assert.Contains($"Loose.csproj", tree);
        Assert.Contains("└─src", tree);
        // 树前缀统一 3 列宽（"│  " 或 "   "），和 "├─" 的宽度对齐
        Assert.Contains("   └─sub", tree);
        Assert.Contains("      └─src/sub/Deep.csproj", tree);
    }

    [Fact]
    public void ViewTree_rejects_sln_because_editing_only_supports_slnx()
    {
        string root = NewTempDirectory();
        string sln = Write(root, "Hello.sln", "");

        Assert.Throws<InvalidOperationException>(() => SolutionViewer.ViewTree(sln));
    }

    [Fact]
    public void ResolveSolutionFile_prefers_slnx_over_sln()
    {
        string root = NewTempDirectory();
        Write(root, "Hello.sln", "");
        string slnx = Write(root, "Hello.slnx", "<Solution />");

        Assert.Equal(slnx, SolutionViewer.ResolveSolutionFile(root));
    }

    [Fact]
    public void MigrateToSlnx_rejects_a_solution_that_is_already_slnx()
    {
        string root = NewTempDirectory();
        string slnx = Write(root, "Hello.slnx", "<Solution />");

        Assert.Throws<InvalidOperationException>(() => SolutionViewer.MigrateToSlnx(slnx));
    }

    // ───────── 写文件通则：编码 ─────────

    [Fact]
    public void DetectEncoding_reads_the_byte_order_mark()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Bom.cs");
        File.WriteAllText(file, "class A { }", new UTF8Encoding(true));

        DetectedEncoding encoding = FileWriter.DetectEncoding(file);

        Assert.Equal(EncodingSource.ByteOrderMark, encoding.Source);
        Assert.NotEmpty(encoding.Encoding.GetPreamble());
    }

    [Fact]
    public void DetectEncoding_falls_back_to_strict_utf8()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Plain.cs");
        File.WriteAllText(file, "// 中文注释", new UTF8Encoding(false));

        DetectedEncoding encoding = FileWriter.DetectEncoding(file);

        Assert.Equal(EncodingSource.StrictUtf8, encoding.Source);
        Assert.Empty(encoding.Encoding.GetPreamble());
    }

    [Fact]
    public void DetectEncoding_prefers_editorconfig_charset()
    {
        string root = NewTempDirectory();
        Write(root, ".editorconfig", "root = true\n\n[*.cs]\ncharset = utf-8-bom\n");
        string file = Path.Combine(root, "Plain.cs");
        File.WriteAllText(file, "class A { }", new UTF8Encoding(false));

        DetectedEncoding encoding = FileWriter.DetectEncoding(file);

        Assert.Equal(EncodingSource.EditorConfig, encoding.Source);
        Assert.NotEmpty(encoding.Encoding.GetPreamble());
    }

    [Fact]
    public void DetectEncoding_refuses_undecidable_bytes_instead_of_guessing()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Gbk.cs");
        // GBK 的「中文」：无 BOM 且不是合法 UTF-8
        File.WriteAllBytes(file, [0xD6, 0xD0, 0xCE, 0xC4]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => FileWriter.DetectEncoding(file));
        Assert.Contains("拒绝写入", exception.Message);
    }

    [Fact]
    public void WriteAtomic_keeps_the_original_encoding_and_leaves_no_temp_file()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Bom.cs");
        File.WriteAllText(file, "class A { }", new UTF8Encoding(true));

        FileWriter.WriteAtomic(file, "class B { }");

        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Contains("class B", File.ReadAllText(file, Encoding.UTF8), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(root, "*.zms-tmp-*"));
    }

    [Fact]
    public void Probe_reports_writable_busy_and_read_only()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, "class A { }");

        // 正常文件：可写
        Assert.Equal(WriteProbe.Writable, FileWriter.Probe(file));

        // 被独占占用 → Busy
        using (FileStream _ = new(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(WriteProbe.Busy, FileWriter.Probe(file));
        }

        // 只读属性 → ReadOnly
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Assert.Equal(WriteProbe.ReadOnly, FileWriter.Probe(file));
        File.SetAttributes(file, FileAttributes.Normal);

        // 新文件：目录可写 → Writable
        Assert.Equal(WriteProbe.Writable, FileWriter.Probe(Path.Combine(root, "New.cs")));
    }

    [Fact]
    public void WriteAtomic_skips_the_write_when_content_is_unchanged()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Same.cs");
        File.WriteAllText(file, "class A { }");
        DateTime before = File.GetLastWriteTimeUtc(file);

        bool written = FileWriter.WriteAtomic(file, "class A { }");

        Assert.False(written);
        // 没写：时间戳保持原样，也不留临时文件
        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
        Assert.Empty(Directory.GetFiles(root, "*.zms-tmp-*"));
    }

    [Fact]
    public void WriteAtomic_retries_a_busy_file_then_reports_it_and_cleans_up()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Locked.cs");
        File.WriteAllText(file, "class A { }");

        // 独占占住目标文件，模拟"编辑器 / 杀软 / 索引器正拿着它"
        using (FileStream _ = new(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => FileWriter.WriteAtomic(file, "class B { }", encoding: null, retryDelays: [1, 1, 1]));
            Assert.Contains("写入失败", exception.Message, StringComparison.Ordinal);
            Assert.Contains("占用", exception.Message, StringComparison.Ordinal);
        }

        // 失败也不能留下临时文件，且原内容不动
        Assert.Empty(Directory.GetFiles(root, "*.zms-tmp-*"));
        Assert.Equal("class A { }", File.ReadAllText(file));
    }

    [Fact]
    public void WriteAtomic_creates_new_files_as_utf8_without_bom()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "Nested", "New.cs");

        FileWriter.WriteAtomic(file, "class New { }");

        byte[] bytes = File.ReadAllBytes(file);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("class New { }", File.ReadAllText(file, new UTF8Encoding(false)));
    }

    // ───────── 写文件通则：基线与前滚日志 ─────────

    [Fact]
    public void ComputeHash_changes_with_content_and_is_empty_for_missing_files()
    {
        string root = NewTempDirectory();
        string file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, "one");
        string before = FileWriter.ComputeHash(file);

        File.WriteAllText(file, "two");

        Assert.NotEqual(before, FileWriter.ComputeHash(file));
        Assert.Equal("", FileWriter.ComputeHash(Path.Combine(root, "Missing.cs")));
    }
}
