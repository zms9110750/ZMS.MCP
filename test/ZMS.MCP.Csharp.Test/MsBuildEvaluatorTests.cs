using System.Reflection;
using Xunit;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// MSBuild 评估层的测试。
///
/// 仓库根由**编译期**写进程序集元数据（构建输出被重定向到仓库外，运行时靠向上查找定位不到）。
/// 这些用例要求本仓库已经还原过（有 obj/project.assets.json）。
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

    [Fact]
    public void Evaluate_self_project_returns_target_framework()
    {
        MsBuildEvaluation evaluation = MsBuildEvaluator.Evaluate(SelfProjectPath());

        Assert.Equal("net10.0", evaluation.GetProperty("TargetFramework"));
        // 程序集名来自 Directory.Build.props（zms9110750.<项目名>），不写死全名以免 props 重构即碎
        Assert.EndsWith("ZMS.MCP.Csharp", evaluation.GetProperty("AssemblyName"));
    }

    [Fact]
    public void Evaluate_self_project_returns_reference_paths()
    {
        MsBuildEvaluation evaluation = MsBuildEvaluator.Evaluate(SelfProjectPath());

        Assert.NotEmpty(evaluation.ReferencePaths);
        Assert.Contains(evaluation.ReferencePaths, path => path.EndsWith("System.Runtime.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_self_project_includes_transitive_package_references()
    {
        // csproj 只直接引 Microsoft.CodeAnalysis.CSharp(.Workspaces)，Common 是传递依赖。
        MsBuildEvaluation evaluation = MsBuildEvaluator.Evaluate(SelfProjectPath());

        Assert.Contains(
            evaluation.ReferencePaths,
            path => path.Contains("microsoft.codeanalysis.common", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_self_project_includes_compile_items_injected_by_directory_build_props()
    {
        // 仓库根的 Global.cs 由 Directory.Build.props 注入，csproj 里完全没有它的影子。
        MsBuildEvaluation evaluation = MsBuildEvaluator.Evaluate(SelfProjectPath());

        Assert.Contains(evaluation.CompileItems, path => Path.GetFileName(path) == "Global.cs");
        Assert.Contains(evaluation.CompileItems, path => Path.GetFileName(path) == "MsBuildEvaluator.cs");
    }

    [Fact]
    public void Evaluate_self_project_exposes_define_constants()
    {
        // DefineConstants 只有 MSBuild 评估知道，csproj 里看不到（NET10_0 / TRACE 等都是 SDK 加的）。
        MsBuildEvaluation evaluation = MsBuildEvaluator.Evaluate(SelfProjectPath());

        string defineConstants = evaluation.GetProperty("DefineConstants");
        Assert.Contains("NET10_0", defineConstants);
        Assert.Contains("TRACE", defineConstants);
    }

    [Fact]
    public void Evaluate_uses_cache_within_same_input()
    {
        string project = SelfProjectPath();
        MsBuildEvaluation first = MsBuildEvaluator.Evaluate(project, refresh: true);
        MsBuildEvaluation second = MsBuildEvaluator.Evaluate(project, refresh: false);

        Assert.Same(first, second);
    }

    [Fact]
    public void Evaluate_invalidates_cache_when_project_file_is_touched()
    {
        string project = SelfProjectPath();
        DateTime originalTimestamp = File.GetLastWriteTimeUtc(project);

        MsBuildEvaluation first = MsBuildEvaluator.Evaluate(project, refresh: true);
        try
        {
            // 把项目文件的时间戳推到未来：缓存必须判为过期并重新评估
            File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddSeconds(5));
            MsBuildEvaluation second = MsBuildEvaluator.Evaluate(project, refresh: false);

            Assert.NotSame(first, second);
        }
        finally
        {
            File.SetLastWriteTimeUtc(project, originalTimestamp);
            MsBuildEvaluator.Evaluate(project, refresh: true);
        }
    }

    [Fact]
    public void Evaluate_missing_project_throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Missing.csproj");

        Assert.Throws<FileNotFoundException>(() => MsBuildEvaluator.Evaluate(missing));
    }

    [Fact]
    public void Evaluate_broken_project_file_throws_with_context()
    {
        // 故意留一个坏项目文件（测试结束后不主动删除，交给系统清理临时目录）
        string directory = Path.Combine(Path.GetTempPath(), "zms-mcp-csharp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "Broken.csproj");
        File.WriteAllText(project, "<Project>this is not a valid msbuild project</Project>");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MsBuildEvaluator.Evaluate(project));

        Assert.Contains("Broken.csproj", exception.Message);
    }
}
