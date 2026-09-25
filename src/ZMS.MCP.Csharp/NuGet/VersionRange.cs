using System.Globalization;

namespace ZMS.MCP.Csharp.NuGet;

/// <summary>
/// 按 SemVer 2 / NuGet 的规则比较版本号：数字段逐段比，**有预发布标签的小于没有标签的**。
/// </summary>
public sealed class ComparableVersion : IComparable<ComparableVersion>, IEquatable<ComparableVersion>
{
    public string Original { get; }

    public int[] Numbers { get; }

    /// <summary>预发布标签（<c>-beta.1</c> → <c>["beta","1"]</c>）；正式版为空数组。</summary>
    public string[] ReleaseLabels { get; }

    public bool IsPrerelease => ReleaseLabels.Length > 0;

    private ComparableVersion(string original, int[] numbers, string[] releaseLabels)
    {
        Original = original;
        Numbers = numbers;
        ReleaseLabels = releaseLabels;
    }

    public static ComparableVersion Parse(string text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("版本号不能为空。");
        }

        string core = trimmed;
        int plus = core.IndexOf('+');
        if (plus >= 0)
        {
            core = core[..plus];
        }

        string[] releaseLabels = [];
        int dash = core.IndexOf('-');
        if (dash >= 0)
        {
            releaseLabels = core[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries);
            core = core[..dash];
        }

        // 数字段必须是纯数字：否则像 "lib" 这种目录名会被悄悄当成版本 0，
        // 让「这不是包路径」的判断失效（TryParse 要能如实返回 false）。
        string[] parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => !part.All(char.IsAsciiDigit)))
        {
            throw new ArgumentException($"版本号格式不对：{trimmed}");
        }

        int[] numbers = parts
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : throw new ArgumentException($"版本号数字段超出范围：{trimmed}"))
            .ToArray();

        return new ComparableVersion(trimmed, numbers, releaseLabels);
    }

    public static bool TryParse(string text, out ComparableVersion? version)
    {
        try
        {
            version = Parse(text);
            return true;
        }
        catch (ArgumentException)
        {
            version = null;
            return false;
        }
    }

    public int CompareTo(ComparableVersion? other)
    {
        if (other == null)
        {
            return 1;
        }

        int length = Math.Max(Numbers.Length, other.Numbers.Length);
        for (int index = 0; index < length; index++)
        {
            int left = index < Numbers.Length ? Numbers[index] : 0;
            int right = index < other.Numbers.Length ? other.Numbers[index] : 0;
            int comparison = left.CompareTo(right);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        // 数字段相同：正式版（无标签）大于任何预发布版
        if (ReleaseLabels.Length == 0 && other.ReleaseLabels.Length == 0)
        {
            return 0;
        }

        if (ReleaseLabels.Length == 0)
        {
            return 1;
        }

        if (other.ReleaseLabels.Length == 0)
        {
            return -1;
        }

        int labelLength = Math.Max(ReleaseLabels.Length, other.ReleaseLabels.Length);
        for (int index = 0; index < labelLength; index++)
        {
            if (index >= ReleaseLabels.Length)
            {
                return -1;
            }

            if (index >= other.ReleaseLabels.Length)
            {
                return 1;
            }

            string left = ReleaseLabels[index];
            string right = other.ReleaseLabels[index];
            bool leftNumeric = int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out int leftNumber);
            bool rightNumeric = int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rightNumber);
            int comparison = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left, right),
            };

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    public bool Equals(ComparableVersion? other)
    {
        return other != null && CompareTo(other) == 0;
    }

    public override bool Equals(object? obj)
    {
        return obj is ComparableVersion other && Equals(other);
    }

    public override int GetHashCode()
    {
        // Equals 是按比较结果判等的（1.0 == 1.0.0，1.0.0+meta == 1.0.0），
        // 所以散列也必须基于规范化后的数字段与标签，不能直接用原始字符串。
        int significant = Numbers.Length;
        while (significant > 0 && Numbers[significant - 1] == 0)
        {
            significant--;
        }

        HashCode hash = new();
        hash.Add(significant);
        for (int index = 0; index < significant; index++)
        {
            hash.Add(Numbers[index]);
        }

        hash.Add(ReleaseLabels.Length);
        foreach (string label in ReleaseLabels)
        {
            // 与 CompareTo 保持一致：数字标签按数值参与比较（1.0.0-01 == 1.0.0-1），
            // 所以散列也必须按数值算，否则"相等但哈希不同"、放进 HashSet/Dictionary 会失效。
            if (int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            {
                hash.Add(numeric);
            }
            else
            {
                hash.Add(label);
            }
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return Original;
    }
}

/// <summary>
/// NuGet 版本区间：支持 <c>[1.0,2.0)</c>、<c>(1.0,)</c>、<c>[1.0]</c>、<c>1.0</c>（= 最小含）、
/// 空串（= 任意）、<c>*</c>（= 任意，含预览）。
/// </summary>
public sealed class VersionRange
{
    public ComparableVersion? Minimum { get; }

    public bool MinimumInclusive { get; }

    public ComparableVersion? Maximum { get; }

    public bool MaximumInclusive { get; }

    /// <summary>是否为"任意版本"（含预览）。</summary>
    public bool AllowsPrerelease { get; }

    private VersionRange(
        ComparableVersion? minimum,
        bool minimumInclusive,
        ComparableVersion? maximum,
        bool maximumInclusive,
        bool allowsPrerelease)
    {
        Minimum = minimum;
        MinimumInclusive = minimumInclusive;
        Maximum = maximum;
        MaximumInclusive = maximumInclusive;
        AllowsPrerelease = allowsPrerelease;
    }

    public static VersionRange Any(bool allowPrerelease)
    {
        return new VersionRange(null, true, null, true, allowPrerelease);
    }

    /// <summary>解析区间文本；空串与 <c>*</c> 都表示任意。</summary>
    public static VersionRange Parse(string text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == "*")
        {
            return Any(allowPrerelease: trimmed == "*");
        }

        bool isInterval = trimmed.StartsWith('[') || trimmed.StartsWith('(');
        if (!isInterval)
        {
            // 裸版本 = 最小含
            return new VersionRange(ComparableVersion.Parse(trimmed), true, null, true, false);
        }

        if (!trimmed.EndsWith(']') && !trimmed.EndsWith(')'))
        {
            throw new ArgumentException($"版本区间格式不对（缺右括号）：{trimmed}");
        }

        bool minimumInclusive = trimmed[0] == '[';
        bool maximumInclusive = trimmed[^1] == ']';
        string body = trimmed[1..^1];
        int comma = body.IndexOf(',');
        if (comma < 0)
        {
            // [1.0] = 精确
            ComparableVersion exact = ComparableVersion.Parse(body);
            return new VersionRange(exact, true, exact, true, false);
        }

        string minimumText = body[..comma].Trim();
        string maximumText = body[(comma + 1)..].Trim();
        ComparableVersion? minimum = minimumText.Length == 0 ? null : ComparableVersion.Parse(minimumText);
        ComparableVersion? maximum = maximumText.Length == 0 ? null : ComparableVersion.Parse(maximumText);
        return new VersionRange(minimum, minimumInclusive, maximum, maximumInclusive, false);
    }

    public bool Contains(ComparableVersion version)
    {
        // 区间两端自己写了预发布版本时，就该按区间本身判断；
        // 否则 [1.0.0-alpha,1.0.0] 这种显式区间会把预发布永远挡在外面。
        if (!AllowsPrerelease && version.IsPrerelease && !BoundsMentionPrerelease())
        {
            return false;
        }

        if (Minimum != null)
        {
            int comparison = version.CompareTo(Minimum);
            if (comparison < 0 || (comparison == 0 && !MinimumInclusive))
            {
                return false;
            }
        }

        if (Maximum != null)
        {
            int comparison = version.CompareTo(Maximum);
            if (comparison > 0 || (comparison == 0 && !MaximumInclusive))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>区间两端是否写明了预发布版本（有的话就不再无条件挡掉预发布）。</summary>
    private bool BoundsMentionPrerelease()
    {
        return (Minimum?.IsPrerelease ?? false) || (Maximum?.IsPrerelease ?? false);
    }

    public override string ToString()
    {
        if (Minimum == null && Maximum == null)
        {
            return AllowsPrerelease ? "*" : "";
        }

        if (Minimum != null && Maximum != null && Minimum.Equals(Maximum) && MinimumInclusive && MaximumInclusive)
        {
            return $"[{Minimum}]";
        }

        return $"{(MinimumInclusive ? '[' : '(')}{Minimum?.Original}{(Maximum == null ? "" : "," + Maximum.Original)}{(MaximumInclusive ? ']' : ')')}";
    }
}
