using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 加载选项的解析与降级判定的测试（审查要求的守卫：这些回退方向错了会静默产生假阴性/假错误）。
/// </summary>
public sealed class LoadOptionTests
{
    [Theory]
    [InlineData("enable", NullableContextOptions.Enable)]
    [InlineData("ENABLE", NullableContextOptions.Enable)]
    [InlineData("disable", NullableContextOptions.Disable)]
    [InlineData("warnings", NullableContextOptions.Warnings)]
    [InlineData("annotations", NullableContextOptions.Annotations)]
    [InlineData("", NullableContextOptions.Disable)]
    [InlineData("nonsense", NullableContextOptions.Disable)]
    public void ParseNullable_maps_msbuild_values(string value, NullableContextOptions expected)
    {
        // 关键：未设置（空串）必须是 Disable —— MSBuild 的语义如此，写成 Enable 会把诊断方向搞反
        Assert.Equal(expected, LoadedProject.ParseNullable(value));
    }

    [Theory]
    [InlineData("", LanguageVersion.Default)]
    [InlineData("nonsense", LanguageVersion.Default)]
    [InlineData("default", LanguageVersion.Default)]
    [InlineData("preview", LanguageVersion.Preview)]
    [InlineData("latest", LanguageVersion.Latest)]
    [InlineData("latestMajor", LanguageVersion.LatestMajor)]
    [InlineData("12.0", LanguageVersion.CSharp12)]
    public void ParseLanguageVersion_falls_back_to_default(string value, LanguageVersion expected)
    {
        // 关键：缺失时回退 Default 而不是 Preview（Preview 会接受实验语法 → 假阴性）
        Assert.Equal(expected, LoadedProject.ParseLanguageVersion(value));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException), true)]
    [InlineData(typeof(TimeoutException), true)]
    [InlineData(typeof(IOException), true)]
    [InlineData(typeof(UnauthorizedAccessException), true)]
    // dotnet 不在 PATH 时 Process.Start 抛的就是它：必须降级，否则没装 SDK 的机器直接报错
    [InlineData(typeof(Win32Exception), true)]
    [InlineData(typeof(OutOfMemoryException), false)]
    [InlineData(typeof(OperationCanceledException), false)]
    [InlineData(typeof(ArgumentNullException), false)]
    public void IsFallbackWorthy_only_accepts_evaluation_failures(Type exceptionType, bool expected)
    {
        Exception exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(expected, LoadedProject.IsFallbackWorthy(exception));
    }

    [Obsolete("靠「降级」才成立：降级提示本身是 MSBuild 失败的产物；用别人的命令测自己的稳定性，不去测它")]
    public void ModeNotice_is_short_even_when_reason_is_long()
    {
        // 降级原因来自 MSBuild 输出（可能上千字符、多行）：提示必须是单行短摘要
        string directory = Path.Combine(Path.GetTempPath(), "zms-mcp-notice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "NotRestored.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        LoadedProject loaded = LoadedProject.Load(project);
        string notice = loaded.ModeNotice();

        Assert.Equal(LoadMode.Fallback, loaded.Mode);
        Assert.Contains("简化模式", notice);
        Assert.DoesNotContain('\n', notice);
        Assert.True(notice.Length < 500, $"提示过长（{notice.Length} 字符）：{notice}");
    }
}
