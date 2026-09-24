using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace ZMS.MCP.Csharp.Draft;

/// <summary>基线差异：变化 / 新出现 / 消失的符号。</summary>
public sealed record BaselineDiff(
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed)
{
    public bool IsEmpty => Changed.Count == 0 && Added.Count == 0 && Removed.Count == 0;
}

/// <summary>
/// 追踪基线：**符号级语义快照** —— 「符号完全限定名 → 声明文本 hash」。
/// 不含文件路径，所以**移动文件 / 文件改名不算变化**（见 `docs/Csharp-拟定流程v3.md` 第五节）。
/// </summary>
public static class SymbolBaseline
{
    /// <summary>
    /// 符号身份的显示格式（含泛型元数与参数类型）—— 全项目只用这一份，别到处手拼字符串。
    /// </summary>
    private static readonly SymbolDisplayFormat IdentityFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>拍一份基线：遍历程序集里本项目声明的符号（类型 + 成员）。</summary>
    public static Dictionary<string, string> Capture(Compilation compilation)
    {
        Dictionary<string, string> baseline = new(StringComparer.Ordinal);
        CollectNamespace(compilation.Assembly.GlobalNamespace, baseline);
        return baseline;
    }

    /// <summary>比两份基线：谁变了、谁新来、谁走了。</summary>
    public static BaselineDiff Compare(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> current)
    {
        List<string> changed = [];
        List<string> removed = [];
        foreach (KeyValuePair<string, string> pair in baseline)
        {
            if (!current.TryGetValue(pair.Key, out string? hash))
            {
                removed.Add(pair.Key);
                continue;
            }

            if (!string.Equals(hash, pair.Value, StringComparison.Ordinal))
            {
                changed.Add(pair.Key);
            }
        }

        List<string> added = [.. current.Keys.Where(key => !baseline.ContainsKey(key))];
        changed.Sort(StringComparer.Ordinal);
        added.Sort(StringComparer.Ordinal);
        removed.Sort(StringComparer.Ordinal);
        return new BaselineDiff(changed, added, removed);
    }

    /// <summary>符号身份（完全限定名，含参数类型与泛型元数）。</summary>
    public static string Key(ISymbol symbol)
    {
        return symbol.ToDisplayString(IdentityFormat);
    }

    /// <summary>存进 sqlite 的 JSON。</summary>
    public static string Serialize(IReadOnlyDictionary<string, string> baseline)
    {
        return JsonSerializer.Serialize(baseline);
    }

    /// <summary>从 sqlite 读回；坏数据当空基线（宁可报"一片未追踪更改"，也不要抛）。</summary>
    public static Dictionary<string, string> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                is { } parsed
                ? new Dictionary<string, string>(parsed, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void CollectNamespace(INamespaceSymbol @namespace, Dictionary<string, string> baseline)
    {
        foreach (INamespaceSymbol child in @namespace.GetNamespaceMembers())
        {
            CollectNamespace(child, baseline);
        }

        foreach (INamedTypeSymbol type in @namespace.GetTypeMembers())
        {
            CollectType(type, baseline);
        }
    }

    private static void CollectType(INamedTypeSymbol type, Dictionary<string, string> baseline)
    {
        if (type.IsImplicitlyDeclared)
        {
            return;
        }

        baseline[Key(type)] = DeclaredTextHash(type);

        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            CollectType(nested, baseline);
        }

        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || IsAccessor(member))
            {
                continue;
            }

            baseline[Key(member)] = DeclaredTextHash(member);
        }
    }

    /// <summary>文本 → hash（快照比对用；与基线 hash 同一套"换行归一"规则）。</summary>
    internal static string HashText(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));
    }

    /// <summary>符号的声明文本（不是 hash）—— 拟定快照 / 三方展示要用。</summary>
    internal static string DeclaredText(ISymbol symbol)
    {
        List<string> parts = [];
        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
        {
            parts.Add(reference.GetSyntax().ToString());
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n---\n", parts);
    }

    /// <summary>
    /// 声明文本 hash：把一个符号的**全部声明**（分部类会有多份）取文本、换行归一后按序拼起来再哈希。
    /// 归一换行是为了不让 CRLF/LF 变化误报成"被改"。
    /// </summary>
    private static string DeclaredTextHash(ISymbol symbol)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(DeclaredText(symbol)))));
    }

    private static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n");
    }

    private static bool IsAccessor(ISymbol member)
    {
        return member is IMethodSymbol method && method.MethodKind is
            MethodKind.PropertyGet or MethodKind.PropertySet or
            MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise;
    }
}
