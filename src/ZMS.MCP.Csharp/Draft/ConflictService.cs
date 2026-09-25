using ZMS.MCP.Csharp.Project;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Draft;

/// <summary>一个未解决冲突的性质。</summary>
public enum ConflictKind
{
    /// <summary>追踪之后被外部改动。</summary>
    Changed,

    /// <summary>追踪之后新出现（外部新增；拟定里没有它也算冲突）。</summary>
    Added,

    /// <summary>追踪之后消失。</summary>
    Removed,
}

/// <summary>一个未解决的冲突：追踪基线里的 hash 与当前源码里的 hash 对不上。</summary>
public sealed record SymbolConflict(string SymbolKey, ConflictKind Kind, bool HasDraft)
{
    /// <summary>给 agent 看的一行说明。</summary>
    public string Describe()
    {
        string suffix = HasDraft ? "（拟定里有它）" : "";
        switch (Kind)
        {
            case ConflictKind.Added:
                return $"{SymbolKey} —— 追踪之后新出现{suffix}";
            case ConflictKind.Removed:
                return $"{SymbolKey} —— 追踪之后消失{suffix}";
            default:
                return $"{SymbolKey} —— 追踪之后被外部改动{suffix}";
        }
    }
}

/// <summary>
/// 冲突模型（见审查意见第 7 条）：
/// 追踪初始化时保存**所有符号的 hash**；落盘检测时比对「当前符号 hash」与「已保存 hash」，
/// **不对齐的符号就是未解决的冲突**（新增的、没有拟定的符号也算冲突）。
/// 解决 = 把该符号的 hash 更新为**现在的 hash**（见 <see cref="AcceptCurrent"/>）；没有未解决冲突才发落盘 cookie。
/// </summary>
public static class ConflictService
{
    /// <summary>基线里 diff 出来的所有差异都是冲突（改动的 / 新增的 / 消失的）。</summary>
    public static IReadOnlyList<SymbolConflict> Unresolved(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> current,
        IReadOnlyList<DraftEdit> edits)
    {
        HashSet<string> drafted = new(
            edits.Select(edit => edit.SymbolKey).Where(key => key.Length > 0),
            StringComparer.Ordinal);
        BaselineDiff diff = SymbolBaseline.Compare(baseline, current);

        List<SymbolConflict> conflicts = [];
        foreach (string key in diff.Changed)
        {
            conflicts.Add(new SymbolConflict(key, ConflictKind.Changed, drafted.Contains(key)));
        }

        foreach (string key in diff.Added)
        {
            conflicts.Add(new SymbolConflict(key, ConflictKind.Added, drafted.Contains(key)));
        }

        foreach (string key in diff.Removed)
        {
            conflicts.Add(new SymbolConflict(key, ConflictKind.Removed, drafted.Contains(key)));
        }

        return conflicts;
    }

    /// <summary>这一个符号是不是未解决冲突（对齐即不算；基线没有而现状有也算）。</summary>
    public static bool IsUnresolved(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> current,
        string symbolKey)
    {
        if (!baseline.TryGetValue(symbolKey, out string? saved))
        {
            return current.ContainsKey(symbolKey);
        }

        return !current.TryGetValue(symbolKey, out string? now) || !string.Equals(saved, now, StringComparison.Ordinal);
    }

    /// <summary>
    /// 解决冲突：把这个符号（连同它所属的类型链）的 hash 更新为**现在的 hash**；
    /// 其它还没解决的冲突原样保留（否则解决一个就把别的冲突静默吞掉了）。
    /// </summary>
    /// <param name="projectPath">项目文件。</param>
    /// <param name="symbolKey">被解决的符号键。</param>
    /// <param name="current">现状基线；为空时自己重算（调用方已经算过就传进来，省一次编译）。</param>
    /// <param name="databasePath">拟定库路径；空 = MCP 自己的目录（测试可注入临时库）。</param>
    public static void AcceptCurrent(
        string projectPath,
        string symbolKey,
        IReadOnlyDictionary<string, string>? current = null,
        string? databasePath = null)
    {
        DraftStore store = new(databasePath);
        TrackingRecord? tracking = store.GetTracking(projectPath);
        if (tracking == null)
        {
            return;
        }

        IReadOnlyDictionary<string, string> now = current ?? SymbolBaseline.Capture(LoadedProject.Load(projectPath).Compilation);

        // 以**基线**为底重建：只把被解决的符号（及其类型链）换成"现在的 hash"，其余一律保留旧 hash。
        // 这一步很关键 —— 若以「现状」为底，「基线里没有、现状里新增」的符号（Added 冲突）
        // 会被顺手静默接受，于是每解决一个符号就吞掉一批外部新增，预检再也报不出来。
        Dictionary<string, string> rebuilt = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in tracking.Baseline)
        {
            if (IsRelated(pair.Key, symbolKey))
            {
                if (now.TryGetValue(pair.Key, out string? value))
                {
                    rebuilt[pair.Key] = value;
                }

                continue;
            }

            rebuilt[pair.Key] = pair.Value;
        }

        // 被解决的符号本身若是"新增"（基线里没有它），显式按现状写进去 —— 它就是这次要接受的那个。
        if (!rebuilt.ContainsKey(symbolKey) && now.TryGetValue(symbolKey, out string? accepted))
        {
            rebuilt[symbolKey] = accepted;
        }

        store.SaveTracking(projectPath, tracking.TrackingCookie, rebuilt);
    }

    /// <summary>同一个符号，或者它是该符号的**父类型链**（成员与它所属的类型/外层类型视为一体）。</summary>
    internal static bool IsRelated(string key, string symbolKey)
    {
        return key.Equals(symbolKey, StringComparison.Ordinal)
            || symbolKey.StartsWith(key + ".", StringComparison.Ordinal);
    }
}
