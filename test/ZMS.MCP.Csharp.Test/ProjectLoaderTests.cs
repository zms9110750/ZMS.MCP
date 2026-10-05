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

    [Fact(Skip = "停用中，理由见下面的 Obsolete；要恢复请先按那条注释说的改掉它")]
    [Obsolete("靠真跑 dotnet msbuild 才成立：用别人的命令测自己的稳定性，冷启动几秒且与要验的语义无关")]
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
