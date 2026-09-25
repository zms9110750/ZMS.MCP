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
    private static VulnerabilityIndexData Index(params (string Package, string[] Ranges)[] entries)
    {
        Dictionary<string, IReadOnlyList<string>> affected = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string package, string[] ranges) in entries)
        {
            affected[package] = ranges;
        }

        return new VulnerabilityIndexData(affected, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Available: true);
    }

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

    [Fact]
    public void ComparableVersion_hash_agrees_with_equality()
    {
        // Equals 是按比较结果判等的，散列必须跟着一致，否则放进 HashSet/Distinct 会自相矛盾
        Assert.Equal(ComparableVersion.Parse("1.0").GetHashCode(), ComparableVersion.Parse("1.0.0").GetHashCode());
        Assert.Equal(ComparableVersion.Parse("1.0.0").GetHashCode(), ComparableVersion.Parse("1.0.0+meta").GetHashCode());
        Assert.NotEqual(ComparableVersion.Parse("1.0.0").GetHashCode(), ComparableVersion.Parse("1.0.1").GetHashCode());
        Assert.Single(new HashSet<ComparableVersion> { ComparableVersion.Parse("1.0"), ComparableVersion.Parse("1.0.0") });
    }

    [Fact]
    public void ComparableVersion_rejects_versions_that_are_not_numbers()
    {
        Assert.False(ComparableVersion.TryParse("lib", out _));
        Assert.False(ComparableVersion.TryParse("", out _));
        Assert.Throws<ArgumentException>(() => ComparableVersion.Parse("1.notanumber"));
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

    // ───────── verRange：范围解析 + 所有符合的版本（list_package_versions） ─────────

    [Fact]
    public void VersionRangeFilter_star_is_all_release_versions_only()
    {
        VersionRangeFilter star = VersionRangeFilter.Parse("*");

        Assert.False(star.IncludePrerelease);
        Assert.True(star.Contains(ComparableVersion.Parse("13.0.3")));
        Assert.True(star.Contains(ComparableVersion.Parse("0.0.1")));
        // 预览版不在「所有正式版」里
        Assert.False(star.Contains(ComparableVersion.Parse("13.1.0-beta.1")));
        // 等价于 [0.0.0,9999.9999.9999]
        Assert.Contains("9999.9999.9999", star.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionRangeFilter_empty_lists_release_versions_only()
    {
        VersionRangeFilter empty = VersionRangeFilter.Parse("");

        Assert.False(empty.IncludePrerelease);
        Assert.True(empty.Contains(ComparableVersion.Parse("1.0.0")));
        Assert.False(empty.Contains(ComparableVersion.Parse("1.0.0-beta")));
    }

    [Fact]
    public void VersionRangeFilter_star_dash_star_includes_prerelease()
    {
        VersionRangeFilter all = VersionRangeFilter.Parse("*-*");

        Assert.True(all.IncludePrerelease);
        Assert.True(all.Contains(ComparableVersion.Parse("13.1.0-beta.1")));
        Assert.True(all.Contains(ComparableVersion.Parse("13.0.3")));
    }

    [Fact]
    public void VersionRangeFilter_parses_a_range_and_keeps_every_matching_version()
    {
        VersionRangeFilter range = VersionRangeFilter.Parse("[13.0,14.0)");

        Assert.True(range.Contains(ComparableVersion.Parse("13.0.0")));
        Assert.True(range.Contains(ComparableVersion.Parse("13.9.9")));
        Assert.False(range.Contains(ComparableVersion.Parse("14.0.0")));
        Assert.False(range.Contains(ComparableVersion.Parse("12.9.9")));
        // 范围两端没写预览版 → 预览版不列
        Assert.False(range.Contains(ComparableVersion.Parse("13.1.0-beta")));
    }

    [Fact]
    public void VersionRangeFilter_honours_explicit_prerelease_bounds()
    {
        VersionRangeFilter range = VersionRangeFilter.Parse("[1.0.0-alpha,1.0.0]");

        Assert.True(range.IncludePrerelease);
        Assert.True(range.Contains(ComparableVersion.Parse("1.0.0-beta")));
        Assert.True(range.Contains(ComparableVersion.Parse("1.0.0")));
    }

    [Fact]
    public void VersionRangeFilter_narrows_a_floating_range_to_its_prefix()
    {
        // NuGet 的 Satisfies 只看边界，1.3.0 也会落在 1.2.* —— 前缀必须自己收紧
        VersionRangeFilter floating = VersionRangeFilter.Parse("1.2.*");

        Assert.True(floating.Contains(ComparableVersion.Parse("1.2.0")));
        Assert.True(floating.Contains(ComparableVersion.Parse("1.2.9")));
        Assert.False(floating.Contains(ComparableVersion.Parse("1.3.0")));
        Assert.False(floating.Contains(ComparableVersion.Parse("13.0.0")));
    }

    [Fact]
    public void VersionRangeFilter_rejects_malformed_ranges()
    {
        Assert.Throws<ArgumentException>(() => VersionRangeFilter.Parse("[1.0.0,2.0.0"));
    }

    // ───────── 移除包：切图算出会一并消失的传递包 ─────────

    [Fact]
    public void ComputeDisappearingPackages_cuts_the_removed_root_and_compares_with_the_old_graph()
    {
        List<PackageNode> known =
        [
            new("A", "1.0.0"),
            new("B", "1.0.0"),
            new("C", "1.0.0"),
            new("D", "1.0.0"),
        ];
        Dictionary<string, string[]> dependencies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = ["B"],
            ["B"] = ["C"],
        };

        IReadOnlyList<string> disappeared = PackageManager.ComputeDisappearingPackages(
            [new PackageNode("D", "1.0.0")],
            known,
            ["A"],
            (id, version) => ReadFakeDependencies(dependencies, id));

        // A 是被直接移除的（不列进「依赖传递包」），B、C 不再被需要 → 一并消失；D 保住
        Assert.Equal(["B", "C"], disappeared);
    }

    [Fact]
    public void ComputeDisappearingPackages_keeps_a_package_that_another_root_still_needs()
    {
        List<PackageNode> known =
        [
            new("A", "1.0.0"),
            new("X", "1.0.0"),
            new("Shared", "1.0.0"),
        ];
        Dictionary<string, string[]> dependencies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = ["Shared"],
            ["X"] = ["Shared"],
        };

        IReadOnlyList<string> disappeared = PackageManager.ComputeDisappearingPackages(
            [new PackageNode("X", "1.0.0")],
            known,
            ["A"],
            (id, version) => ReadFakeDependencies(dependencies, id));

        // Shared 还被 X 需要 → 不消失
        Assert.Empty(disappeared);
    }

    private static IReadOnlyList<(string Id, string Range)> ReadFakeDependencies(
        IReadOnlyDictionary<string, string[]> dependencies,
        string id)
    {
        if (!dependencies.TryGetValue(id, out string[]? deps))
        {
            return [];
        }

        List<(string Id, string Range)> result = [];
        foreach (string dep in deps)
        {
            result.Add((dep, ""));
        }

        return result;
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
        VulnerabilityIndexData index = Index(("Vulnerable.Package", ["[1.0.0, 1.5.0)", "[2.0.0, 2.1.0]"]));

        Assert.True(VulnerabilityIndex.IsVulnerable(index, "vulnerable.package", ComparableVersion.Parse("1.2.0")));
        Assert.True(VulnerabilityIndex.IsVulnerable(index, "Vulnerable.Package", ComparableVersion.Parse("2.1.0")));
        Assert.False(VulnerabilityIndex.IsVulnerable(index, "Vulnerable.Package", ComparableVersion.Parse("1.5.0")));
        Assert.False(VulnerabilityIndex.IsVulnerable(index, "Other.Package", ComparableVersion.Parse("1.2.0")));
    }

    [Fact]
    public void VulnerabilityIndex_picks_the_latest_version_that_is_not_vulnerable()
    {
        VulnerabilityIndexData index = Index(("Vulnerable.Package", ["[1.0.0, 2.0.0)"]));

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

    [Fact]
    public void VulnerabilityIndex_treats_unreadable_entries_as_risky_not_safe()
    {
        // 索引里有这个包、但区间读不懂：宁可不自动升级，也不能判成"安全"
        VulnerabilityIndexData index = new(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mystery.Package" },
            Available: true);

        Assert.True(VulnerabilityIndex.IsVulnerable(index, "Mystery.Package", ComparableVersion.Parse("1.0.0")));
        Assert.Null(VulnerabilityIndex.PickLatestSafe(index, "Mystery.Package", [ComparableVersion.Parse("1.0.0")]));
        // 没提到的包仍然按"没有已知漏洞"处理
        Assert.False(VulnerabilityIndex.IsVulnerable(index, "Clean.Package", ComparableVersion.Parse("1.0.0")));
    }

    [Fact]
    public void VulnerabilityIndex_treats_a_broken_range_as_risky()
    {
        // 区间文本坏掉（解析会抛）时，同样不能当成"安全"
        VulnerabilityIndexData index = Index(("Broken.Package", ["[1.0.0,2.0.0"]));

        Assert.True(VulnerabilityIndex.IsVulnerable(index, "Broken.Package", ComparableVersion.Parse("9.9.9")));
    }

    // ───────── 依赖图 ─────────

    [Fact]
    public void PackageGraph_parses_package_paths_from_the_cache_root()
    {
        (string Id, string Version)? parsed = PackageGraph.ParsePackagePath(
            @"C:\nuget-cache\microsoft.codeanalysis.csharp\5.9.0\lib\net10.0\Microsoft.CodeAnalysis.CSharp.dll",
            @"C:\nuget-cache");

        Assert.Equal("microsoft.codeanalysis.csharp", parsed?.Id);
        Assert.Equal("5.9.0", parsed?.Version);
    }

    [Fact]
    public void PackageGraph_ignores_paths_outside_the_cache_and_too_short_ones()
    {
        Assert.Null(PackageGraph.ParsePackagePath(@"C:\dotnet\shared\System.dll", @"C:\nuget-cache"));
        // 第二段不是版本号 → 不是包路径
        Assert.Null(PackageGraph.ParsePackagePath(@"C:\nuget-cache\onlypackage\lib\x.dll", @"C:\nuget-cache"));
        Assert.Null(PackageGraph.ParsePackagePath(@"C:\nuget-cache\onlypackage\1.0.0", @"C:\nuget-cache"));
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

    // ───────── 漏洞索引的在线形状（真实 index.json / 分片） ─────────

    [Fact]
    public void ReadShardUrls_reads_the_real_index_shape()
    {
        // 真实的 vulnerabilities/index.json 是**数组**，地址在 @id 里 ——
        // 曾经按"对象 + vulnerabilities 数组 + 字符串"解析，于是永远拿不到分片（索引恒为不可用）
        const string indexJson = """
            [
              { "@name": "base", "@id": "https://api.nuget.org/v3/vulnerabilities/vulnerability.base.json", "@updated": "2023-12-13T00:48:47.5745986Z" },
              { "@name": "update", "@id": "https://api.nuget.org/v3/vulnerabilities/vulnerability.update.json", "comment": "periodic" }
            ]
            """;

        Assert.Equal(
            new[]
            {
                "https://api.nuget.org/v3/vulnerabilities/vulnerability.base.json",
                "https://api.nuget.org/v3/vulnerabilities/vulnerability.update.json",
            },
            VulnerabilityIndex.ReadShardUrls(indexJson));
        // 形状不对时不抛异常，只是取不到地址
        Assert.Empty(VulnerabilityIndex.ReadShardUrls("""{ "vulnerabilities": [] }"""));
    }

    [Fact]
    public void Merge_reads_shards_and_records_unparsable_versions()
    {
        const string shard = """
            {
              "microsoft.data.odata": [
                { "url": "https://github.com/advisories/GHSA-mv2r-q4g5-j8q5", "severity": 2, "versions": "(, 5.8.4)" }
              ],
              "some.package": [
                { "url": "https://github.com/advisories/GHSA-xxxx", "severity": 1, "versions": "not-a-range" }
              ],
              "no.versions": [ { "url": "https://github.com/advisories/GHSA-yyyy", "severity": 0 } ]
            }
            """;
        Dictionary<string, List<string>> merged = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> unparsed = new(StringComparer.OrdinalIgnoreCase);

        VulnerabilityIndex.Merge(merged, unparsed, shard);

        Assert.Equal(new[] { "(, 5.8.4)" }, merged["microsoft.data.odata"]);
        // 读不出区间的包按"未知风险"处理，不能静默当成安全
        Assert.Contains("some.package", unparsed);
        Assert.Contains("no.versions", unparsed);
    }

    [Fact]
    public void IsVulnerable_handles_a_range_without_lower_bound()
    {
        // NuGet 索引里常见 (, 5.8.4)：全是"无下界 + 不含上界"
        VulnerabilityIndexData data = Index(("microsoft.data.odata", ["(, 5.8.4)"]));

        Assert.True(VulnerabilityIndex.IsVulnerable(data, "microsoft.data.odata", ComparableVersion.Parse("4.0.0")));
        Assert.True(VulnerabilityIndex.IsVulnerable(data, "Microsoft.Data.Odata", ComparableVersion.Parse("5.8.3")));
        Assert.False(VulnerabilityIndex.IsVulnerable(data, "microsoft.data.odata", ComparableVersion.Parse("5.8.4")));
        Assert.False(VulnerabilityIndex.IsVulnerable(data, "microsoft.data.odata", ComparableVersion.Parse("7.0.0")));
    }

    // ───────── 落盘后的漏洞审计（NU1901–NU1904） ─────────

    [Fact]
    public void ExtractAuditWarnings_picks_only_the_nu190x_lines()
    {
        const string output = """
            正在确定要还原的项目…
            C:\x\Demo.csproj : warning NU1903: 包 "Newtonsoft.Json" 13.0.1 具有已知的 高 严重性漏洞
            C:\x\Demo.csproj : warning NU1901: 包 "A" 1.0.0 具有已知的 低 严重性漏洞
            C:\x\Demo.csproj : warning NU1903: 包 "Newtonsoft.Json" 13.0.1 具有已知的 高 严重性漏洞
            已还原 C:\x\Demo.csproj (用时 200 毫秒)。
            """;

        IReadOnlyList<string> warnings = PackageManager.ExtractAuditWarnings(output);

        // 只摘 NU1901–NU1904，且同样的行去重
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, line => line.Contains("NU1903", StringComparison.Ordinal));
        Assert.Contains(warnings, line => line.Contains("NU1901", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, line => line.Contains("已还原", StringComparison.Ordinal));
    }

    // ───────── 包请求解析与版本选择 ─────────

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
        PackageManager.VersionChoice choice = PackageManager.ResolveVersion(
            "microsoft.codeanalysis.csharp",
            "5.9.0",
            false,
            VulnerabilityIndexData.Empty);

        Assert.Equal("5.9.0", choice.Version);
        Assert.False(choice.Vulnerable);
    }

    [Fact]
    public void ResolveVersion_skips_vulnerable_cached_versions()
    {
        // 这个包在本地缓存里；把它所有版本标成受影响，就会退回「最高版本」并**标成有漏洞**（而不是默默当安全）
        IReadOnlyList<ComparableVersion> cached = NuGetCache.Versions("microsoft.codeanalysis.csharp");
        Assert.NotEmpty(cached);
        VulnerabilityIndexData index = Index(("microsoft.codeanalysis.csharp", ["[0.0.0,)"]));

        PackageManager.VersionChoice choice = PackageManager.ResolveVersion("microsoft.codeanalysis.csharp", "", false, index);

        Assert.Equal(cached[0].Original, choice.Version);
        Assert.True(choice.Vulnerable);
        Assert.False(choice.IndexUnavailable);
    }

    [Fact]
    public void ResolveVersion_prefers_non_vulnerable_when_one_exists()
    {
        IReadOnlyList<ComparableVersion> cached = NuGetCache.Versions("microsoft.codeanalysis.csharp");
        Assert.True(cached.Count >= 2);
        // 把最高的那个版本标成漏洞，应挑第二高的
        VulnerabilityIndexData index = Index(("microsoft.codeanalysis.csharp", [$"[{cached[0].Original}]"]));

        PackageManager.VersionChoice choice = PackageManager.ResolveVersion("microsoft.codeanalysis.csharp", "", false, index);

        Assert.NotEqual(cached[0].Original, choice.Version);
        Assert.False(choice.Vulnerable);
    }

    [Fact]
    public void ResolveVersion_reports_an_unavailable_index_instead_of_pretending_it_is_safe()
    {
        // 索引拿不到时不能自动升级（PickLatestSafe 直接返回 null），而且要显式说明"没核对过"
        PackageManager.VersionChoice choice = PackageManager.ResolveVersion(
            "microsoft.codeanalysis.csharp",
            "",
            false,
            VulnerabilityIndexData.Unavailable);

        Assert.NotEmpty(choice.Version);
        Assert.True(choice.IndexUnavailable);
        Assert.False(choice.Vulnerable);
        Assert.Null(VulnerabilityIndex.PickLatestSafe(
            VulnerabilityIndexData.Unavailable,
            "microsoft.codeanalysis.csharp",
            NuGetCache.Versions("microsoft.codeanalysis.csharp")));
    }
}
