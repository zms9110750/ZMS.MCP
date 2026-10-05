using System.Reflection;
using System.Text.Json;
using Xunit;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// MSBuild 评估层的测试。
///
/// 仓库根由**编译期**写进程序集元数据（构建输出被重定向到仓库外，运行时靠向上查找定位不到）。
/// 评估类用例要求本仓库已经还原过（有 obj/project.assets.json）；
/// 缓存失效与 JSON 解析的用例只用临时目录里的假文件，不碰仓库里的真实文件。
/// </summary>
public sealed class MsBuildEvaluatorTests
{
    /// <summary>定位仓库根；定位不到就直接失败，不静默跳过。</summary>
    private static string RepositoryRoot()
    {
        string? fromMetadata = typeof(MsBuildEvaluatorTests).Assembly
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

        throw new InvalidOperationException(
            "找不到仓库根（ZMS.MCP.slnx）。构建输出被重定向时依赖程序集元数据 RepositoryRoot。");
    }

    private static string SelfProjectPath()
    {
        return Path.Combine(RepositoryRoot(), "src", "ZMS.MCP.Csharp", "ZMS.MCP.Csharp.csproj");
    }

    /// <summary>在系统临时目录里造一个假项目（只用于缓存与解析的纯逻辑用例，不评估 MSBuild）。</summary>
    private static string NewFakeProject(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "zms-mcp-csharp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "Fake.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        return project;
    }

    // ───────── 缓存失效：源文件指纹 ─────────

    [Fact]
    public void SourceFingerprint_only_follows_the_file_list()
    {
        string project = NewFakeProject(out string directory);
        File.WriteAllText(Path.Combine(directory, "A.cs"), "class A { }");
        string before = MsBuildEvaluator.SourceFingerprint(project);

        // 新增源文件 → 指纹变（MSBuild 的 Compile 项来自 glob，新文件不在"监视文件"里，
        // 不靠指纹的话刚落盘的新类型在本次会话里看不到）
        File.WriteAllText(Path.Combine(directory, "B.cs"), "class B { }");
        string added = MsBuildEvaluator.SourceFingerprint(project);
        Assert.NotEqual(before, added);

        // 删掉又回到原样
        File.Delete(Path.Combine(directory, "B.cs"));
        Assert.Equal(before, MsBuildEvaluator.SourceFingerprint(project));

        // 只改内容 / 只动写入时间 → 指纹**不变**：求值结果里跟 .cs 有关的只有"编译哪些文件"，
        // 内容变化由每次都重读文件的那一层（LoadedProject.Load）负责，不该为此作废整个求值。
        File.WriteAllText(Path.Combine(directory, "A.cs"), "class A { public int Value; }");
        File.SetLastWriteTimeUtc(Path.Combine(directory, "A.cs"), DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(before, MsBuildEvaluator.SourceFingerprint(project));

        // bin / obj 里的 .cs 不算数
        string obj = Path.Combine(directory, "obj");
        Directory.CreateDirectory(obj);
        File.WriteAllText(Path.Combine(obj, "First.cs"), "class G { }");
        string withObj = MsBuildEvaluator.SourceFingerprint(project);
        File.WriteAllText(Path.Combine(obj, "Second.cs"), "class H { }");
        Assert.Equal(withObj, MsBuildEvaluator.SourceFingerprint(project));
    }

    // ───────── 真实项目评估 ─────────

    [Fact(Skip = "停用中，理由见下面的 Obsolete；要恢复请先按那条注释说的改掉它")]
    [Obsolete("靠真跑 dotnet msbuild 才成立：用别人的命令测自己的稳定性，冷启动几秒且与要验的语义无关")]
    public void Evaluate_uses_cache_within_same_input()
    {
        string project = SelfProjectPath();
        MsBuildEvaluation first = MsBuildEvaluator.Evaluate(project, refresh: true);
        MsBuildEvaluation second = MsBuildEvaluator.Evaluate(project, refresh: false);

        Assert.Same(first, second);
    }

    [Fact]
    public void Evaluate_missing_project_throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Missing.csproj");

        Assert.Throws<FileNotFoundException>(() => MsBuildEvaluator.Evaluate(missing));
    }


    [Fact]
    public void IsStale_returns_false_when_inputs_unchanged()
    {
        string project = NewFakeProject(out string directory);
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project></Project>");

        Dictionary<string, DateTime> snapshot = MsBuildEvaluator.SnapshotInputs(project);

        Assert.False(MsBuildEvaluator.IsStale(project, snapshot));
    }

    [Fact]
    public void IsStale_detects_touched_project_file()
    {
        string project = NewFakeProject(out string directory);
        Dictionary<string, DateTime> snapshot = MsBuildEvaluator.SnapshotInputs(project);

        // 推到未来，保证跨过时间戳容差
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddSeconds(5));

        Assert.True(MsBuildEvaluator.IsStale(project, snapshot));
        Assert.NotNull(directory);
    }

    [Fact]
    public void IsStale_detects_touched_watched_file()
    {
        string project = NewFakeProject(out string directory);
        string props = Path.Combine(directory, "Directory.Build.props");
        File.WriteAllText(props, "<Project></Project>");
        Dictionary<string, DateTime> snapshot = MsBuildEvaluator.SnapshotInputs(project);

        File.SetLastWriteTimeUtc(props, DateTime.UtcNow.AddSeconds(5));

        Assert.True(MsBuildEvaluator.IsStale(project, snapshot));
    }

    [Fact]
    public void IsStale_detects_deleted_watched_file()
    {
        string project = NewFakeProject(out string directory);
        string props = Path.Combine(directory, "Directory.Build.props");
        File.WriteAllText(props, "<Project></Project>");
        Dictionary<string, DateTime> snapshot = MsBuildEvaluator.SnapshotInputs(project);

        // 删的是本用例刚创建的临时文件
        File.Delete(props);

        Assert.True(MsBuildEvaluator.IsStale(project, snapshot));
    }

    [Fact]
    public void IsStale_detects_appeared_watched_file()
    {
        string project = NewFakeProject(out string directory);
        Dictionary<string, DateTime> snapshot = MsBuildEvaluator.SnapshotInputs(project);

        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project></Project>");

        Assert.True(MsBuildEvaluator.IsStale(project, snapshot));
    }

    // ───────── MSBuild 输出解析 ─────────

    [Fact]
    public void ExtractJsonDocument_parses_json_with_surrounding_noise()
    {
        string output = "NETSDK1057: 你正在使用 .NET 的预览版。\n{\"Properties\":{\"A\":\"1\"}}\n生成成功。";

        using JsonDocument document = MsBuildEvaluator.ExtractJsonDocument(output);

        Assert.Equal("1", document.RootElement.GetProperty("Properties").GetProperty("A").GetString());
    }

    [Fact]
    public void ExtractJsonDocument_skips_brace_in_noise()
    {
        // 噪声里先出现一个 '{'，真正的 JSON 在后面
        string output = "警告: 无法解析 \"{bad\"\n{\"Properties\":{\"A\":\"2\"}}";

        using JsonDocument document = MsBuildEvaluator.ExtractJsonDocument(output);

        Assert.Equal("2", document.RootElement.GetProperty("Properties").GetProperty("A").GetString());
    }

    [Fact]
    public void ExtractJsonDocument_throws_when_no_json()
    {
        string output = "这里没有任何 JSON 对象";

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MsBuildEvaluator.ExtractJsonDocument(output));

        Assert.Contains("does not contain a parsable JSON object", exception.Message);
    }

    [Fact]
    public void ExtractJsonDocument_handles_output_starting_with_closing_brace()
    {
        // 输出以 '}' 开头：候选边界收集不能越界（回归 CollectPositions 的 index == 0 守卫）
        string output = "}\n{\"Properties\":{\"A\":\"3\"}}";

        using JsonDocument document = MsBuildEvaluator.ExtractJsonDocument(output);

        Assert.Equal("3", document.RootElement.GetProperty("Properties").GetProperty("A").GetString());
    }
}
