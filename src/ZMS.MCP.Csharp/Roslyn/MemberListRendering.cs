using Microsoft.CodeAnalysis;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>
/// 成员列表的**分组显示**（需求 3.7）：同一个名字的重载超过 <see cref="OverloadGroupThreshold"/> 个时，
/// 不再重复方法名，改成"分组头 + 每个重载只给参数列表"，例如：
/// <code>
/// - `Demo.Bloat.Many` 有 20 个重载：
///   - `(int)` — Bloat.cs:209
///   - `(int, int)` — Bloat.cs:210
/// </code>
/// </summary>
public static class MemberListRendering
{
    /// <summary>同一名字的重载数**超过**它就改分组显示。</summary>
    public const int OverloadGroupThreshold = 10;

    /// <summary>按名字切分成连续分组（输入应先按名字排序）。</summary>
    /// <typeparam name="T">列表元素类型。</typeparam>
    /// <param name="items">已排序的元素。</param>
    /// <param name="selector">取出元素对应的符号（用它的 <c>Name</c> 分组）。</param>
    public static IReadOnlyList<IReadOnlyList<T>> GroupByName<T>(IReadOnlyList<T> items, Func<T, ISymbol> selector)
    {
        List<IReadOnlyList<T>> groups = [];
        int index = 0;
        while (index < items.Count)
        {
            string name = selector(items[index]).Name;
            int end = index;
            while (end < items.Count && string.Equals(selector(items[end]).Name, name, StringComparison.Ordinal))
            {
                end++;
            }

            groups.Add([.. items.Skip(index).Take(end - index)]);
            index = end;
        }

        return groups;
    }

    /// <summary>这一组是否该用"分组显示"（只对方法、且重载数超过阈值）。</summary>
    public static bool IsOverloadedGroup<T>(IReadOnlyList<T> group, Func<T, ISymbol> selector)
    {
        return group.Count > OverloadGroupThreshold && selector(group[0]) is IMethodSymbol;
    }

    /// <summary>
    /// 重载歧义报错里的候选列表：最多列 <paramref name="limit"/> 个，其余只报数量 ——
    /// 否则 50 个重载会把报错本身淹掉（需求 3.7 的同一条精神）。
    /// </summary>
    public static string DescribeOverloads(IReadOnlyList<ISymbol> members, int limit = 20)
    {
        List<string> lines =
        [
            .. members
                .Take(limit)
                .Select(member => "  - " + member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)),
        ];
        if (members.Count > limit)
        {
            lines.Add($"  …（还有 {members.Count - limit} 个重载；带上参数列表即可精确定位）");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>重载一行的参数列表：<c>(int, int)</c>。</summary>
    public static string ParameterList(ISymbol method)
    {
        if (method is not IMethodSymbol symbol)
        {
            return "";
        }

        return "(" + string.Join(
            ", ",
            symbol.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))) + ")";
    }
}
