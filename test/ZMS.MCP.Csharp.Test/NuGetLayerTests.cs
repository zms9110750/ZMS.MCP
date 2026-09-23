using Xunit;
using ZMS.MCP.Csharp.NuGet;
using ZMS.MCP.Csharp.Tools;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// nuget 层：版本比较与区间、本地缓存读取、漏洞索引匹配、依赖图解析、包请求解析。
/// 不做任何在线调用（在线部分只走人工验证）。
/// </summary>
public sealed class NuGetLayerTests
{
    private static string NewTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zms-mcp-nuget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    // ───────── 版本比较 ─────────

    [Fact]
    public void ComparableVersion_treats_prerelease_as_lower_than_release()
    {
        ComparableVersion release = ComparableVersion.Parse("1.0.0");
        ComparableVersion prerelease = ComparableVersion.Parse("1.0.0-beta");

        Assert.True(release.CompareTo(prerelease) > 0);
        Assert.True(prerelease.IsPrerelease);
        Assert.False(release.IsPrerelease);
    }

    [Fact]
    public void ComparableVersion_compares_numeric_segments_not_text()
    {
        // 字符串比较会得到 1.10 < 1.9，这里必须是 1.10 > 1.9
        Assert.True(ComparableVersion.Parse("1.10.0").CompareTo(ComparableVersion.Parse("1.9.0")) > 0);
        Assert.True(ComparableVersion.Parse("2.0.0").CompareTo(ComparableVersion.Parse("1.9.9")) > 0);
        Assert.Equal(0, ComparableVersion.Parse("1.0").CompareTo(ComparableVersion.Parse("1.0.0")));
    }

    [Fact]
    public void ComparableVersion_compares_prerelease_labels()
    {
        Assert.True(ComparableVersion.Parse("1.0.0-beta.2").CompareTo(ComparableVersion.Parse("1.0.0-beta.1")) > 0);
        Assert.True(ComparableVersion.Parse("1.0.0-beta").CompareTo(ComparableVersion.Parse("1.0.0-alpha")) > 0);
        // 数字标签小于字符串标签（SemVer 规则）
        Assert.True(ComparableVersion.Parse("1.0.0-1").CompareTo(ComparableVersion.Parse("1.0.0-alpha")) < 0);
    }

    // ───────── 版本区间 ─────────

    [Fact]
    public void VersionRange_parses_interval_forms()
    {
        VersionRange range = VersionRange.Parse("[1.0.0,2.0.0)");

        Assert.True(range.Contains(ComparableVersion.Parse("1.0.0")));
        Assert.True(range.Contains(ComparableVersion.Parse("1.9.9")));
        Assert.False(range.Contains(ComparableVersion.Parse("2.0.0")));
        Assert.False(range.Contains(ComparableVersion.Parse("0.9.9")));
    }

    [Fact]
    public void VersionRange_exact_and_bare_forms()
    {
        VersionRange exact = VersionRange.Parse("[1.0.0]");
        Assert.True(exact.Contains(ComparableVersion.Parse("1.0.0")));
        Assert.False(exact.Contains(ComparableVersion.Parse("1.0.1")));

        VersionRange minimum = VersionRange.Parse("1.0.0");
        Assert.True(minimum.Contains(ComparableVersion.Parse("1.0.0")));
        Assert.True(minimum.Contains(ComparableVersion.Parse("9.0.0")));
        Assert.False(minimum.Contains(ComparableVersion.Parse("0.9.0")));
    }

    [Fact]
    public void VersionRange_prerelease_requires_the_wildcard_or_an_explicit_label()
    {
        Assert.False(VersionRange.Parse("").Contains(ComparableVersion.Parse("1.0.0-beta")));
        Assert.True(VersionRange.Parse("*").Contains(ComparableVersion.Parse("1.0.0-beta")));
        // 区间两端都是具体版本时，预发布也能落进去
        Assert.True(VersionRange.Parse("[1.0.0-alpha,1.0.0]").Contains(ComparableVersion.Parse("1.0.0-beta")));
    }

    [Fact]
    public void VersionRange_rejects_malformed_intervals()
    {
        Assert.Throws<ArgumentException>(() => VersionRange.Parse("[1.0.0,2.0.0"));
    }

    // ───────── 本地缓存 ─────────

    [Fact]
    public void NuGetCache_lists_versions_and_reads_metadata()
    {
        // 本机缓存里一定有这个包（本项目自己就引用它）
        IReadOnlyList<ComparableVersion> versions = NuGetCache.Versions("microsoft.codeanalysis.csharp");

        Assert.NotEmpty(versions);
        // 降序
        Assert.True(versions[0].CompareTo(versions[^1]) >= 0);

        PackageMetadata? metadata = NuGetCache.ReadMetadata("microsoft.codeanalysis.csharp", versions[0].Original);

        Assert.NotNull(metadata);
        Assert.Contains("Microsoft.CodeAnalysis.CSharp", metadata!.Id, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<package", metadata.NuspecXml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NuGetCache_search_matches_by_keyword()
    {
        IReadOnlyList<string> found = NuGetCache.SearchPackages("codeanalysis.csharp");

        Assert.Contains("microsoft.codeanalysis.csharp", found);
        Assert.Empty(NuGetCache.SearchPackages("this-package-does-not-exist-xyz"));
    }

    [Fact]
    public void NuGetCache_reads_package_metadata_or_null()
    {
        Assert.Null(NuGetCache.ReadMetadata("microsoft.codeanalysis.csharp", "0.0.0-nope"));
    }

    // ───────── 漏洞索引 ─────────

    [Fact]
    public void VulnerabilityIndex_matches_affected_version_ranges()
    {
        Dictionary<string, IReadOnlyList<string>> index = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Vulnerable.Package"] = ["[1.0.0, 1.5.0)", "[2.0.0, 2.1.0]"],
        };

        Assert.True(VulnerabilityIndex.IsVulnerable(index, "vulnerable.package", ComparableVersion.Parse("1.2.0")));
        Assert.True(VulnerabilityIndex.IsVulnerable(index, "Vulnerable.Package", ComparableVersion.Parse("2.1.0")));
        Assert.False(VulnerabilityIndex.IsVulnerable(index, "Vulnerable.Package", ComparableVersion.Parse("1.5.0")));
        Assert.False(VulnerabilityIndex.IsVulnerable(index, "Other.Package", ComparableVersion.Parse("1.2.0")));
    }

    [Fact]
    public void VulnerabilityIndex_picks_the_latest_version_that_is_not_vulnerable()
    {
        Dictionary<string, IReadOnlyList<string>> index = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Vulnerable.Package"] = ["[1.0.0, 2.0.0)"],
        };

        ComparableVersion? picked = VulnerabilityIndex.PickLatestSafe(
            index,
            "Vulnerable.Package",
            [ComparableVersion.Parse("1.0.0"), ComparableVersion.Parse("1.9.9"), ComparableVersion.Parse("2.0.0")]);

        Assert.Equal("2.0.0", picked?.Original);

        // 全部落在受影响区间里 → 没有安全版本
        Assert.Null(VulnerabilityIndex.PickLatestSafe(
            index,
            "Vulnerable.Package",
            [ComparableVersion.Parse("1.0.0"), ComparableVersion.Parse("1.9.9")]));
    }

    // ───────── 依赖图 ─────────

    [Fact]
    public void PackageGraph_parses_package_paths_from_the_cache_root()
    {
        (string Id, string Version)? parsed = PackageGraph.ParsePackagePath(
            @"X:\dotnet\nuget-packages\microsoft.codeanalysis.csharp\5.9.0\lib\net10.0\Microsoft.CodeAnalysis.CSharp.dll",
            @"X:\dotnet\nuget-packages");

        Assert.Equal("microsoft.codeanalysis.csharp", parsed?.Id);
        Assert.Equal("5.9.0", parsed?.Version);
    }

    [Fact]
    public void PackageGraph_ignores_paths_outside_the_cache_and_too_short_ones()
    {
        Assert.Null(PackageGraph.ParsePackagePath(@"C:\Program Files\dotnet\shared\System.dll", @"X:\dotnet\nuget-packages"));
        Assert.Null(PackageGraph.ParsePackagePath(@"X:\dotnet\nuget-packages\onlypackage\lib\x.dll", @"X:\dotnet\nuget-packages"));
    }

    [Fact]
    public void PackageGraph_reads_declared_packages_and_project_references()
    {
        string root = NewTempDirectory();
        string project = Path.Combine(root, "Demo.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="Polly" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../Other/Other.csproj" />
              </ItemGroup>
            </Project>
            """);

        IReadOnlyList<string> packages = PackageGraph.ReadDeclaredPackages(project);
        IReadOnlyList<string> references = PackageGraph.ReadProjectReferences(project);

        Assert.Equal(["Newtonsoft.Json", "Polly"], packages);
        Assert.Equal(["../Other/Other.csproj"], references);
    }

    [Fact]
    public void PackageGraph_returns_empty_for_broken_xml_instead_of_throwing()
    {
        string root = NewTempDirectory();
        string project = Path.Combine(root, "Broken.csproj");
        File.WriteAllText(project, "<Project><ItemGroup>");

        Assert.Empty(PackageGraph.ReadDeclaredPackages(project));
        Assert.Empty(PackageGraph.ReadProjectReferences(project));
    }

    // ───────── 包请求解析 ─────────

    [Fact]
    public void ParseRequests_splits_name_and_version()
    {
        List<PackageRequest> requests = WorkspaceTools.ParseRequests(["Newtonsoft.Json@13.0.3", "Polly", "  ", "A@1.0.0@2.0.0"]);

        Assert.Equal(3, requests.Count);
        Assert.Equal(new PackageRequest("Newtonsoft.Json", "13.0.3"), requests[0]);
        Assert.Equal(new PackageRequest("Polly", ""), requests[1]);
        // 版本里带 @ 时按最后一个 @ 切（版本本身不会带 @，但这样更稳）
        Assert.Equal(new PackageRequest("A@1.0.0", "2.0.0"), requests[2]);
    }

    [Fact]
    public void ResolveVersion_uses_the_requested_version_when_given()
    {
        string version = PackageManager.ResolveVersion("microsoft.codeanalysis.csharp", "5.9.0", false, new Dictionary<string, IReadOnlyList<string>>());

        Assert.Equal("5.9.0", version);
    }

    [Fact]
    public void ResolveVersion_skips_vulnerable_cached_versions()
    {
        // 这个包在本地缓存里；把「它自己」全部版本标成受影响，就会退回最后一个候选
        IReadOnlyList<ComparableVersion> cached = NuGetCache.Versions("microsoft.codeanalysis.csharp");
        Assert.NotEmpty(cached);
        Dictionary<string, IReadOnlyList<string>> index = new(StringComparer.OrdinalIgnoreCase)
        {
            ["microsoft.codeanalysis.csharp"] = ["[0.0.0,)"],
        };

        string version = PackageManager.ResolveVersion("microsoft.codeanalysis.csharp", "", false, index);

        // 全都漏洞时退化为「最高版本」而不是抛异常
        Assert.Equal(cached[0].Original, version);
    }

    [Fact]
    public void ResolveVersion_prefers_non_vulnerable_when_one_exists()
    {
        IReadOnlyList<ComparableVersion> cached = NuGetCache.Versions("microsoft.codeanalysis.csharp");
        Assert.True(cached.Count >= 2);
        // 把最高的那个版本标成漏洞，应挑第二高的
        Dictionary<string, IReadOnlyList<string>> index = new(StringComparer.OrdinalIgnoreCase)
        {
            ["microsoft.codeanalysis.csharp"] = [$"[{cached[0].Original}]"],
        };

        string version = PackageManager.ResolveVersion("microsoft.codeanalysis.csharp", "", false, index);

        Assert.NotEqual(cached[0].Original, version);
    }
}
