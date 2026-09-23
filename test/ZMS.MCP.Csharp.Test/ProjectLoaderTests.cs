using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 项目加载（评估接线与降级）的测试。
/// 评估类用例要求本仓库已还原过；降级用例用没还原过的临时项目。
/// </summary>
public sealed class ProjectLoaderTests
{
    private static string RepositoryRoot()
    {
        string? fromMetadata = typeof(ProjectLoaderTests).Assembly
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

    [Fact]
    public void Load_real_project_uses_msbuild_evaluation()
    {
        LoadedProject project = LoadedProject.Load(SelfProjectPath());

        Assert.Equal(LoadMode.Evaluated, project.Mode);
        Assert.Empty(project.FallbackReason);
        // 评估模式不该给使用者任何"简化模式"提示
        Assert.Empty(project.ModeNotice());
    }

    [Fact]
    public void Load_real_project_resolves_framework_and_package_types()
    {
        LoadedProject project = LoadedProject.Load(SelfProjectPath());

        // 框架程序集与包的传递依赖都要能被解析出来，否则就是引用集没接对
        Assert.NotNull(project.Compilation.GetTypeByMetadataName("System.String"));
        Assert.NotNull(project.Compilation.GetTypeByMetadataName("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree"));
        Assert.NotNull(project.Compilation.GetTypeByMetadataName("Microsoft.CodeAnalysis.SyntaxNode"));
    }

    [Fact]
    public void Load_real_project_has_no_missing_type_errors()
    {
        LoadedProject project = LoadedProject.Load(SelfProjectPath());

        // CS0246 = 找不到类型：引用集接对时不该出现（这正是评估模式的意义）
        string[] missing = project.Compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == "CS0246")
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Load_real_project_picks_up_define_constants_from_evaluation()
    {
        LoadedProject project = LoadedProject.Load(SelfProjectPath());

        // NET10_0 / TRACE 等都不是 csproj 写的，只有 MSBuild 评估才知道
        CSharpParseOptions parseOptions = Assert.IsType<CSharpParseOptions>(
            project.Compilation.SyntaxTrees.First().Options);

        Assert.Contains("NET10_0", parseOptions.PreprocessorSymbolNames);
        Assert.Contains("TRACE", parseOptions.PreprocessorSymbolNames);
    }

    [Fact]
    public void Load_real_project_compiles_the_injected_global_file()
    {
        LoadedProject project = LoadedProject.Load(SelfProjectPath());

        // Global.cs 由 Directory.Build.props 注入，评估模式必须把它算进来
        Assert.Contains(
            project.Compilation.SyntaxTrees,
            tree => Path.GetFileName(tree.FilePath) == "Global.cs");
    }

    [Fact]
    public void Load_project_without_restore_falls_back()
    {
        // 没有 obj/project.assets.json 的项目：MSBuild 评估会失败，必须降级而不是抛异常
        string directory = Path.Combine(Path.GetTempPath(), "zms-mcp-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "NotRestored.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Sample.cs"), "public class Sample { }");

        LoadedProject loaded = LoadedProject.Load(project);

        Assert.Equal(LoadMode.Fallback, loaded.Mode);
        Assert.NotEmpty(loaded.FallbackReason);
        // 降级必须给出可读提示（不能静默降级，否则使用者会把假错误当真错误）
        Assert.Contains("简化模式", loaded.ModeNotice());
        // 降级时仍然要能读到源文件（扫目录兜底）
        Assert.Contains(loaded.Info.SourceFiles, file => Path.GetFileName(file) == "Sample.cs");
        Assert.Contains(loaded.Compilation.SyntaxTrees, tree => Path.GetFileName(tree.FilePath) == "Sample.cs");
    }

    [Fact]
    public void Load_missing_project_throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Missing.csproj");

        Assert.Throws<FileNotFoundException>(() => LoadedProject.Load(missing));
    }
}
