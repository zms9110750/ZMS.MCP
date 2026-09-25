using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>一条 XML 文档注释条目。</summary>
public sealed class DocEntry
{
    /// <summary>完整 member 名，如 <c>M:Ns.Type.Foo(System.Int32)</c>。</summary>
    public string MemberName { get; }

    /// <summary>种类字母：T / P / F / M / E（<c>!</c> 开头的条目在读入时已丢弃）。</summary>
    public char Kind { get; }

    /// <summary>去掉 <c>X:</c> 前缀后的完全限定名（含参数段）。</summary>
    public string FullName { get; }

    /// <summary>匹配用前缀：类型是 <c>T:Ns.Type</c>，成员是 <c>M:Ns.Type.Foo</c>（不含参数段）。</summary>
    public string Prefix { get; }

    /// <summary>参数段（不含括号），如 <c>System.Int32,System.String</c>；无参数段时为空串。</summary>
    public string Parameters { get; }

    /// <summary><c>summary</c> 节点的纯文本。</summary>
    public string Summary { get; }

    /// <summary>原始 <c>member</c> 片段（给 D 用）。</summary>
    public string Xml { get; }

    public DocEntry(string memberName, char kind, string fullName, string prefix, string parameters, string summary, string xml)
    {
        MemberName = memberName;
        Kind = kind;
        FullName = fullName;
        Prefix = prefix;
        Parameters = parameters;
        Summary = summary;
        Xml = xml;
    }
}

/// <summary>定位到的包内文档源。</summary>
public sealed record DocSource(string PackageName, string Version, string TargetFramework, IReadOnlyList<string> XmlPaths);

/// <summary>
/// 从**本地 NuGet 缓存**读 XML 文档注释（不做在线查找）。
/// 包内位置：<c>&lt;cache&gt;/&lt;包&gt;/&lt;版本&gt;/lib/&lt;tfm&gt;/*.xml</c>。
/// </summary>
public static class NuGetXmlDocumentation
{
    /// <summary>本地包缓存根：优先 <c>NUGET_PACKAGES</c>，否则 <c>~/.nuget/packages</c>。</summary>
    public static string CacheRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".nuget", "packages");
    }

    /// <summary>版本缺省取最高；TFM 缺省取最优（net10.0 优先）。</summary>
    public static DocSource Locate(string packageName, string version, string targetFramework)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new ArgumentException("packageName 不能为空。");
        }

        string packageDirectory = Path.Combine(CacheRoot(), packageName.Trim().ToLowerInvariant());
        if (!Directory.Exists(packageDirectory))
        {
            throw new DirectoryNotFoundException($"包不在本地缓存：{packageName}（找的是 {packageDirectory}）");
        }

        string resolvedVersion = string.IsNullOrWhiteSpace(version) ? PickLatestVersion(packageDirectory) : version.Trim();
        string versionDirectory = Path.Combine(packageDirectory, resolvedVersion);
        if (!Directory.Exists(versionDirectory))
        {
            throw new DirectoryNotFoundException($"版本不在本地缓存：{packageName} {resolvedVersion}");
        }

        string libDirectory = Path.Combine(versionDirectory, "lib");
        if (!Directory.Exists(libDirectory))
        {
            throw new DirectoryNotFoundException($"包（{packageName} {resolvedVersion}）没有 lib 目录，取不到 XML 文档。");
        }

        string resolvedTarget = string.IsNullOrWhiteSpace(targetFramework)
            ? PickBestTargetFramework(libDirectory)
            : targetFramework.Trim();
        string targetDirectory = Path.Combine(libDirectory, resolvedTarget);
        if (!Directory.Exists(targetDirectory))
        {
            throw new DirectoryNotFoundException($"包里没有该 TFM：{packageName} {resolvedVersion} / {resolvedTarget}");
        }

        string[] xmlFiles = Directory.GetFiles(targetDirectory, "*.xml");
        if (xmlFiles.Length == 0)
        {
            throw new FileNotFoundException($"该 TFM 下没有 XML 文档文件：{targetDirectory}");
        }

        Array.Sort(xmlFiles, PathComparison.Comparer);
        return new DocSource(packageName.Trim(), resolvedVersion, resolvedTarget, xmlFiles);
    }

    /// <summary>读一个 XML 文档文件；<c>!</c> 开头的条目（编译器无法解析的成员）丢弃。</summary>
    public static IReadOnlyList<DocEntry> Read(string xmlPath)
    {
        XDocument document = XDocument.Load(xmlPath);
        List<DocEntry> entries = [];
        foreach (XElement member in document.Descendants().Where(element => element.Name.LocalName == "member"))
        {
            string? name = member.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3 || name[1] != ':')
            {
                continue;
            }

            char kind = name[0];
            if (kind == '!')
            {
                continue;
            }

            string fullName = name[2..];
            string prefix = fullName;
            string parameters = "";
            int bracket = fullName.IndexOf('(');
            if (bracket >= 0 && fullName.EndsWith(')'))
            {
                prefix = fullName[..bracket];
                parameters = fullName[(bracket + 1)..^1];
            }

            entries.Add(new DocEntry(name, kind, fullName, prefix, parameters, ReadSummary(member), member.ToString()));
        }

        return entries;
    }

    /// <summary>读多个 XML 文档文件并合并（同一包内可能有多个程序集）。</summary>
    public static IReadOnlyList<DocEntry> ReadAll(IEnumerable<string> xmlPaths)
    {
        List<DocEntry> entries = [];
        foreach (string path in xmlPaths)
        {
            entries.AddRange(Read(path));
        }

        return entries;
    }

    private static string ReadSummary(XElement member)
    {
        XElement? summary = member.Elements().FirstOrDefault(element => element.Name.LocalName == "summary");
        if (summary == null)
        {
            return "";
        }

        StringBuilder builder = new();
        foreach (string line in summary.Value.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(trimmed);
        }

        return builder.ToString();
    }

    private static string PickLatestVersion(string packageDirectory)
    {
        string[] versions = Directory.GetDirectories(packageDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderByDescending(name => name, VersionTextComparer.Instance)
            .ToArray();

        if (versions.Length == 0)
        {
            throw new DirectoryNotFoundException($"缓存里没有可用版本：{packageDirectory}");
        }

        return versions[0];
    }

    private static string PickBestTargetFramework(string libDirectory)
    {
        string[] frameworks = Directory.GetDirectories(libDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

        if (frameworks.Length == 0)
        {
            throw new DirectoryNotFoundException($"lib 目录是空的：{libDirectory}");
        }

        return frameworks
            .OrderByDescending(TargetFrameworkScore)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>
    /// 给 TFM 打分分档：新式 netX.Y &gt; netcoreappX.Y &gt; .NET Framework(net48) &gt; netstandard。
    /// 注意 net48 里的 "48" 是 4.8，不能当成版本 48 去和 net10.0 比大小。
    /// 平台后缀（<c>net8.0-windows</c>）不参与分档，只看前面的框架版本。
    /// （internal 是为了让分档规则能被单测直接验证。）
    /// </summary>
    internal static int TargetFrameworkScore(string framework)
    {
        Match modern = Regex.Match(framework, @"^net(\d+)\.(\d+)(?:-[a-zA-Z0-9.]+)?$", RegexOptions.IgnoreCase);
        if (modern.Success)
        {
            return 20_000 + int.Parse(modern.Groups[1].Value) * 100 + int.Parse(modern.Groups[2].Value);
        }

        Match core = Regex.Match(framework, @"^netcoreapp(\d+)\.(\d+)(?:-[a-zA-Z0-9.]+)?$", RegexOptions.IgnoreCase);
        if (core.Success)
        {
            return 15_000 + int.Parse(core.Groups[1].Value) * 100 + int.Parse(core.Groups[2].Value);
        }

        // .NET Framework：net48 / net472 / net40（不写点，首位是主版本，逐位是次版本与修正号）
        Match legacy = Regex.Match(framework, @"^net(\d)(\d)?(\d)?$", RegexOptions.IgnoreCase);
        if (legacy.Success)
        {
            int score = 5_000 + int.Parse(legacy.Groups[1].Value) * 100;
            if (legacy.Groups[2].Success)
            {
                score += int.Parse(legacy.Groups[2].Value) * 10;
            }

            if (legacy.Groups[3].Success)
            {
                score += int.Parse(legacy.Groups[3].Value);
            }

            return score;
        }

        Match standard = Regex.Match(framework, @"^netstandard(\d+)\.(\d+)(?:-[a-zA-Z0-9.]+)?$", RegexOptions.IgnoreCase);
        if (standard.Success)
        {
            return 1_000 + int.Parse(standard.Groups[1].Value) * 100 + int.Parse(standard.Groups[2].Value);
        }

        return -1;
    }

    /// <summary>版本号比较器（internal 是为了让排序规则能被单测直接验证）。</summary>
    internal static IComparer<string> VersionComparer => VersionTextComparer.Instance;

    /// <summary>按点分段的数值比较版本号；数值相同时正式版排在预发布版之前。</summary>
    private sealed class VersionTextComparer : IComparer<string>
    {
        public static readonly VersionTextComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            int[] a = Parse(left);
            int[] b = Parse(right);
            int length = Math.Max(a.Length, b.Length);
            for (int index = 0; index < length; index++)
            {
                int x = index < a.Length ? a[index] : 0;
                int y = index < b.Length ? b[index] : 0;
                int comparison = x.CompareTo(y);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            // 数值一样：1.0.0 要排在 1.0.0-beta 之前（调用方按降序取第一个，所以正式版要返回更"大"）
            bool leftPrerelease = IsPrerelease(left);
            bool rightPrerelease = IsPrerelease(right);
            if (leftPrerelease != rightPrerelease)
            {
                return leftPrerelease ? -1 : 1;
            }

            return string.CompareOrdinal(left, right);
        }

        private static bool IsPrerelease(string? value)
        {
            return (value ?? "").IndexOfAny(['-', '+']) >= 0;
        }

        private static int[] Parse(string? value)
        {
            return (value ?? "")
                .Split('-', '+')[0]
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, out int number) ? number : 0)
                .ToArray();
        }
    }
}

/// <summary>一次文档注释符号查询的结果。</summary>
public sealed class DocQueryResult
{
    public string Path { get; }

    /// <summary>实际生效的种类字母（推断出来的或调用方显式给的）。</summary>
    public string EffectiveKinds { get; }

    /// <summary>种类字母是否是推断出来的（调用方没显式给）。</summary>
    public bool KindsWereInferred { get; }

    public IReadOnlyList<DocEntry> Entries { get; }

    /// <summary>需要提醒调用方的话（例如命名空间只能靠手传前缀 + 类型名反推）。</summary>
    public string Note { get; }

    public DocQueryResult(string path, string effectiveKinds, bool kindsWereInferred, IReadOnlyList<DocEntry> entries, string note)
    {
        Path = path;
        EffectiveKinds = effectiveKinds;
        KindsWereInferred = kindsWereInferred;
        Entries = entries;
        Note = note;
    }
}

/// <summary>
/// 按需求文档的"精度决定缺省 type"规则查询 XML 文档条目：
/// 不是具体类型 → T；命中具体类型 → PFME；命中方法但有重载 → M；命中唯一成员 → D。
/// </summary>
public static class DocSymbolQuery
{
    private static readonly Dictionary<string, string> KeywordAliases = new(StringComparer.Ordinal)
    {
        ["bool"] = "System.Boolean",
        ["byte"] = "System.Byte",
        ["sbyte"] = "System.SByte",
        ["char"] = "System.Char",
        ["decimal"] = "System.Decimal",
        ["double"] = "System.Double",
        ["float"] = "System.Single",
        ["int"] = "System.Int32",
        ["uint"] = "System.UInt32",
        ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64",
        ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16",
        ["object"] = "System.Object",
        ["string"] = "System.String",
        ["void"] = "System.Void",
    };

    public static DocQueryResult Query(
        IReadOnlyList<DocEntry> entries,
        string path,
        IReadOnlyList<string> argumentTypes,
        string explicitKinds)
    {
        string trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("path 不能为空（要给出对象路径）。");
        }

        bool explicitGiven = explicitKinds.Length > 0;

        // A) 精确命中某个类型 → 缺省 PFME（类型本体要显式带 T）
        DocEntry? exactType = FindType(entries, trimmed);
        if (exactType != null)
        {
            string kinds = explicitGiven ? explicitKinds : "PFME";
            List<DocEntry> matched = entries
                .Where(entry => IsMemberOf(entry, exactType.FullName, entries))
                .Where(entry => AcceptsKind(kinds, entry.Kind))
                .Where(entry => MatchesArguments(entry, argumentTypes))
                .ToList();

            if (kinds.Contains('T'))
            {
                matched.Insert(0, exactType);
            }

            return new DocQueryResult(trimmed, kinds, !explicitGiven, matched, "");
        }

        // B) 类型全名 + 成员名 → 唯一成员给 D（原始片段），重载给 M
        (DocEntry TypeEntry, string MemberName)? split = SplitMemberPath(entries, trimmed);
        if (split != null)
        {
            List<DocEntry> matched = MatchMembers(entries, split.Value.TypeEntry, split.Value.MemberName, argumentTypes);
            if (matched.Count > 0)
            {
                string inferred = matched.Count == 1 ? "D" : "M";
                string kinds = explicitGiven ? explicitKinds : inferred;
                if (explicitGiven)
                {
                    matched = matched.Where(entry => AcceptsKind(kinds, entry.Kind)).ToList();
                }

                string note = matched.Count > 1 && !explicitGiven
                    ? "命中多个重载（缺省按 M 处理）；用 argsList 可以精确到某一个。"
                    : "";
                return new DocQueryResult(trimmed, kinds, !explicitGiven, matched, note);
            }
        }

        // C) 当成命名空间前缀 → 缺省 T（N 也走这里：XML 里没有 N: 条目）
        string namespaceKinds = explicitGiven ? explicitKinds : "T";
        if (namespaceKinds.Contains('T') || namespaceKinds.Contains('N'))
        {
            string prefix = trimmed + ".";
            List<DocEntry> types = entries
                .Where(entry => entry.Kind == 'T' && entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            if (types.Count > 0)
            {
                return new DocQueryResult(
                    trimmed,
                    namespaceKinds,
                    !explicitGiven,
                    types,
                    "XML 里没有 N: 条目，命名空间只能靠调用方手传前缀 + 类型全名反推（没有文档注释的类型会漏）。");
            }
        }

        throw new InvalidOperationException($"文档里找不到 '{trimmed}'。");
    }

    /// <summary>
    /// 种类过滤。<c>D</c> 不是一种条目种类（XML 里没有 <c>D:</c> 条目），而是"输出原始片段"的开关，
    /// 所以它不参与过滤；只给 D 时按"全部种类"处理。
    /// </summary>
    private static bool AcceptsKind(string kinds, char kind)
    {
        string letters = kinds.Replace("D", "");
        return letters.Length == 0 || letters.Contains(kind);
    }

    /// <summary>
    /// 找类型条目：**精确相等优先**，找不到再容忍泛型元数后缀。
    /// <c>Tuple</c> 与 <c>Tuple`1</c> 这样的同名泛型/非泛型类型能同时存在，精确的那个必须先命中。
    /// </summary>
    private static DocEntry? FindType(IReadOnlyList<DocEntry> entries, string name)
    {
        DocEntry? exact = entries.FirstOrDefault(
            entry => entry.Kind == 'T' && string.Equals(entry.FullName, name, StringComparison.Ordinal));
        return exact ?? entries.FirstOrDefault(
            entry => entry.Kind == 'T' && NameMatchesIgnoringArity(name, entry.FullName));
    }

    /// <summary>
    /// 名字相等判定，容忍泛型元数后缀：XML 里泛型类型是 <c>T:Ns.Type`1</c>、泛型方法是
    /// <c>M:Ns.Type.Foo``1</c>，而调用方一般只写 <c>Ns.Type</c> / <c>Ns.Type.Foo</c>。
    /// </summary>
    internal static bool NameMatchesIgnoringArity(string query, string actual)
    {
        if (string.Equals(query, actual, StringComparison.Ordinal))
        {
            return true;
        }

        return actual.Length > query.Length
            && actual.StartsWith(query, StringComparison.Ordinal)
            && IsAritySuffix(actual[query.Length..]);
    }

    /// <summary>形如 <c>`1</c>（类型）或 <c>``2</c>（方法）的元数后缀。</summary>
    private static bool IsAritySuffix(string value)
    {
        int index = 0;
        while (index < value.Length && value[index] == '`')
        {
            index++;
        }

        return index > 0 && index < value.Length && value[index..].All(char.IsDigit);
    }

    /// <summary>成员前缀匹配，并排除"挂在该类型下嵌套类型"上的成员。</summary>
    private static bool IsMemberOf(DocEntry entry, string typeFullName, IReadOnlyList<DocEntry> entries)
    {
        if (entry.Kind == 'T')
        {
            return false;
        }

        string prefix = typeFullName + ".";
        if (!entry.Prefix.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string rest = entry.Prefix[prefix.Length..];
        int dot = rest.IndexOf('.');
        if (dot < 0)
        {
            return true;
        }

        // 「.」后面还有东西：只有当它不是嵌套类型时才算这个类型的成员。
        // 嵌套类型自己也可能泛型（T:Ns.Outer`1.Inner`1），所以这里也要容忍元数后缀。
        string nestedType = prefix + rest[..dot];
        return !entries.Any(
            candidate => candidate.Kind == 'T' && NameMatchesIgnoringArity(nestedType, candidate.FullName));
    }

    /// <summary>从右往左找"确实是类型"的最长前缀，其余当成员名。</summary>
    private static (DocEntry TypeEntry, string MemberName)? SplitMemberPath(IReadOnlyList<DocEntry> entries, string path)
    {
        int end = path.Length;
        while (true)
        {
            int dot = path.LastIndexOf('.', end - 1);
            if (dot <= 0)
            {
                return null;
            }

            string candidate = path[..dot];
            DocEntry? type = FindType(entries, candidate);
            if (type != null)
            {
                return (type, path[(dot + 1)..]);
            }

            end = dot;
        }
    }

    private static List<DocEntry> MatchMembers(
        IReadOnlyList<DocEntry> entries,
        DocEntry typeEntry,
        string memberName,
        IReadOnlyList<string> argumentTypes)
    {
        string query = typeEntry.FullName + "." + memberName;
        return entries
            .Where(entry => entry.Kind is 'P' or 'F' or 'M' or 'E')
            .Where(entry => NameMatchesIgnoringArity(query, entry.Prefix))
            .Where(entry => MatchesArguments(entry, argumentTypes))
            .ToList();
    }

    /// <summary>参数段匹配；<c>ref</c>/<c>out</c> 在 XML 里带 <c>@</c> 后缀，要忽略掉。</summary>
    private static bool MatchesArguments(DocEntry entry, IReadOnlyList<string> argumentTypes)
    {
        if (argumentTypes.Count == 0)
        {
            return true;
        }

        if (entry.Kind != 'M')
        {
            return false;
        }

        string[] actual = SplitParameters(entry.Parameters);
        if (actual.Length != argumentTypes.Count)
        {
            return false;
        }

        for (int index = 0; index < actual.Length; index++)
        {
            if (!ParameterMatches(argumentTypes[index], actual[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 按**顶层**逗号切分参数段：泛型实参里的逗号（<c>Dictionary{System.String,System.Int32}</c>、
    /// 调用方手写的 <c>Dictionary&lt;System.String,System.Int32&gt;</c>）不是参数分隔符，
    /// 靠 <c>{}</c> / <c>&lt;&gt;</c> / <c>()</c> / <c>[]</c> 的嵌套深度区分。
    /// </summary>
    internal static string[] SplitParameters(string value)
    {
        List<string> parts = [];
        int depth = 0;
        int start = 0;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (IsOpen(current))
            {
                depth++;
            }
            else if (IsClose(current))
            {
                depth--;
            }
            else if (current == ',' && depth == 0)
            {
                parts.Add(value[start..index].Trim());
                start = index + 1;
            }
        }

        parts.Add(value[start..].Trim());
        return parts.Where(part => part.Length > 0).ToArray();
    }

    private static bool ParameterMatches(string expected, string actual)
    {
        string normalized = actual.TrimEnd('@');
        if (string.Equals(expected, normalized, StringComparison.Ordinal))
        {
            return true;
        }

        string shortName = ShortName(normalized);
        if (string.Equals(expected, shortName, StringComparison.Ordinal))
        {
            return true;
        }

        if (KeywordAliases.TryGetValue(expected, out string? expanded))
        {
            return string.Equals(expanded, normalized, StringComparison.Ordinal);
        }

        return string.Equals(expected, shortName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取**顶层**最后一段：泛型实参里的点不算分隔符，
    /// 所以 <c>System.Collections.Generic.List{System.Int32}</c> 的短名是 <c>List{System.Int32}</c>（不是 <c>Int32}</c>）。
    /// </summary>
    private static string ShortName(string value)
    {
        int depth = 0;
        int lastDot = -1;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (IsOpen(current))
            {
                depth++;
            }
            else if (IsClose(current))
            {
                depth--;
            }
            else if (current == '.' && depth == 0)
            {
                lastDot = index;
            }
        }

        return lastDot < 0 ? value : value[(lastDot + 1)..];
    }

    private static bool IsOpen(char value)
    {
        return value is '{' or '<' or '(' or '[';
    }

    private static bool IsClose(char value)
    {
        return value is '}' or '>' or ')' or ']';
    }
}
