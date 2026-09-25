using NuGetRange = NuGet.Versioning.VersionRange;
using NuGetVersion = NuGet.Versioning.NuGetVersion;

namespace ZMS.MCP.Csharp.NuGet;

/// <summary>
/// <c>list_package_versions</c> 的 <c>verRange</c>：**解析走 NuGet 官方 API**（<see cref="NuGet.Versioning.VersionRange"/>），
/// 语义固定为「范围 + 是否包含预览版」——它永远是范围，符合的都列出来，绝不挑一个版本。
///
/// 约定（见需求）：
/// <list type="bullet">
/// <item>空串 = 只列正式版（范围 <c>[0.0.0,)</c>，排除预览版）；</item>
/// <item><c>*</c> = 所有正式版，等价于范围 <c>[0.0.0,9999.9999.9999]</c>；</item>
/// <item><c>*-*</c> = 包含预览版；</item>
/// <item>其余一律交给 NuGet 解析（如 <c>[13.0,14.0)</c>、<c>1.2.*</c>、<c>13.0</c>）；显式把预览版写进范围两端时也算「包含预览版」。</item>
/// </list>
/// </summary>
public sealed class VersionRangeFilter
{
    /// <summary>需求里 <c>*</c> 的等价范围上界（够大即可；正式版的实际上界）。</summary>
    private const string ReleaseCeiling = "9999.9999.9999";

    private readonly int[] _floatingPrefix;

    private VersionRangeFilter(NuGetRange range, bool includePrerelease, string description, int[] floatingPrefix)
    {
        Range = range;
        IncludePrerelease = includePrerelease;
        Description = description;
        _floatingPrefix = floatingPrefix;
    }

    /// <summary>NuGet 解析出来的范围（保留给调用方/测试查看）。</summary>
    public NuGetRange Range { get; }

    /// <summary>是否把预览版也列出来。</summary>
    public bool IncludePrerelease { get; }

    /// <summary>给 agent 看的一句话（英文）。</summary>
    public string Description { get; }

    /// <summary>解析一段范围文本；语法错误抛 <see cref="ArgumentException"/>。</summary>
    public static VersionRangeFilter Parse(string text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == "*")
        {
            // 空串与 `*` 等价（审查意见）：都是「所有正式版」= [0.0.0,9999.9999.9999]；
            // 预览版由 IncludePrerelease 挡掉。
            return new VersionRangeFilter(
                NuGetRange.Parse($"[0.0.0,{ReleaseCeiling}]"),
                includePrerelease: false,
                $"all release versions ([0.0.0,{ReleaseCeiling}])",
                []);
        }

        NuGetRange parsed;
        try
        {
            parsed = NuGetRange.Parse(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new ArgumentException($"'{trimmed}' is not a NuGet version range ({exception.Message})");
        }

        // 范围两端写明了预览版（含 NuGet 把 `*-*` 解析成下界 0.0.0-0）→ 连预览版一起列
        bool includePrerelease = parsed.MinVersion?.IsPrerelease == true || parsed.MaxVersion?.IsPrerelease == true;
        if (trimmed.EndsWith("-*", StringComparison.Ordinal))
        {
            includePrerelease = true;
        }

        return new VersionRangeFilter(parsed, includePrerelease, Describe(trimmed, includePrerelease), FloatingPrefix(trimmed));
    }

    /// <summary>版本是否要列出来：落在范围里，并且（预览版要在「包含预览版」时才留）。</summary>
    public bool Contains(ComparableVersion version)
    {
        if (!IncludePrerelease && version.IsPrerelease)
        {
            return false;
        }

        return MatchesRange(version);
    }

    /// <summary>只判「范围 + 浮点前缀」，不判预览版开关（调用方据此统计「有多少预览版被挡在外面」）。</summary>
    public bool MatchesRange(ComparableVersion version)
    {
        NuGetVersion? parsed = TryToNuGetVersion(version);
        if (parsed == null)
        {
            return false;
        }

        return Range.Satisfies(parsed) && MatchesFloatingPrefix(version);
    }

    private bool MatchesFloatingPrefix(ComparableVersion version)
    {
        if (_floatingPrefix.Length == 0)
        {
            return true;
        }

        for (int index = 0; index < _floatingPrefix.Length; index++)
        {
            int segment = index < version.Numbers.Length ? version.Numbers[index] : 0;
            if (segment != _floatingPrefix[index])
            {
                return false;
            }
        }

        return true;
    }

    private static NuGetVersion? TryToNuGetVersion(ComparableVersion version)
    {
        return NuGetVersion.TryParse(version.Original, out NuGetVersion? parsed) ? parsed : null;
    }

    private static string Describe(string trimmed, bool includePrerelease)
    {
        return includePrerelease ? $"range {trimmed} (prerelease included)" : $"range {trimmed} (release versions only)";
    }

    /// <summary>
    /// 浮点版本（<c>1.2.*</c>）的前缀：NuGet 的 <c>Satisfies</c> 只看边界，会把 <c>1.3.0</c> 也算进 <c>1.2.*</c>，
    /// 所以前缀段必须自己收紧（<c>1.2.*</c> → 只留 <c>1.2.x</c>）。
    /// </summary>
    private static int[] FloatingPrefix(string text)
    {
        if (!text.Contains('*', StringComparison.Ordinal))
        {
            return [];
        }

        string[] segments = text.Split('.');
        int starIndex = Array.FindIndex(segments, segment => segment.Contains('*', StringComparison.Ordinal));
        if (starIndex <= 0)
        {
            // "*" / "*-*" 这类「全版本」没有前缀约束
            return [];
        }

        List<int> prefix = [];
        for (int index = 0; index < starIndex; index++)
        {
            if (!int.TryParse(segments[index], out int value))
            {
                return [];
            }

            prefix.Add(value);
        }

        return [.. prefix];
    }
}
