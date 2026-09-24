using System.Text;
using ZMS.MCP.Csharp.Project;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Draft;

/// <summary>
/// 追踪：开始 / 恢复追踪、取消追踪（见 `docs/Csharp-拟定流程v3.md` 第三节 1 与 6）。
/// 快照是**符号级语义**（<see cref="SymbolBaseline"/>）—— 移动文件 / 改文件名**不算**变化。
/// </summary>
public static class TrackingService
{
    /// <summary>开始或恢复追踪：首次记快照并发追踪 cookie；已追踪则只汇报。</summary>
    public static string Track(string csprojPath, string? databasePath = null)
    {
        string projectPath = ProjectViewer.ResolveProjectFile(csprojPath);
        LoadedProject project = LoadedProject.Load(projectPath);
        Dictionary<string, string> current = SymbolBaseline.Capture(project.Compilation);
        DraftStore store = new(databasePath);
        TrackingRecord? existing = store.GetTracking(projectPath);
        DraftRecord? draft = store.Find(projectPath);

        StringBuilder builder = new();
        if (existing == null)
        {
            TrackingRecord created = store.SaveTracking(projectPath, Guid.NewGuid().ToString("D"), current);
            builder.AppendLine("# 追踪已开始");
            builder.AppendLine($"- 项目：{projectPath}");
            builder.AppendLine($"- 追踪 cookie：`{created.TrackingCookie}`（要清除追踪与拟定，把它传回 track_project 的 cookie 参数）");
            builder.AppendLine($"- 快照：{current.Count} 个符号");
            AppendDraftList(builder, draft);
            builder.AppendLine();
            builder.AppendLine("## 有这些未追踪更改");
            builder.AppendLine("（无 —— 快照就是当前现状）");
            return builder.ToString();
        }

        builder.AppendLine("# 已在追踪（快照保持原样）");
        builder.AppendLine($"- 项目：{projectPath}");
        builder.AppendLine($"- 追踪 cookie：`{existing.TrackingCookie}`");
        AppendDraftList(builder, draft);
        AppendUntrackedChanges(builder, existing, current, draft);
        return builder.ToString();
    }

    /// <summary>取消追踪：清追踪记录 + 全部拟定（有未完成的写前日志时先前滚补齐，不静默丢）。</summary>
    public static string Untrack(string csprojPath, string trackCookie, string? databasePath = null)
    {
        string projectPath = ProjectViewer.ResolveProjectFile(csprojPath);
        DraftStore store = new(databasePath);
        TrackingRecord? existing = store.GetTracking(projectPath);
        if (existing == null)
        {
            return $"# 取消追踪{Environment.NewLine}{projectPath}{Environment.NewLine}该项目没有在追踪。";
        }

        if (!string.Equals(existing.TrackingCookie, (trackCookie ?? "").Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "追踪 cookie 不匹配（这是追踪 cookie，不是落盘许可）。未做任何改动。");
        }

        StringBuilder builder = new();
        builder.AppendLine("# 取消追踪");
        if (store.HasPendingJournal(projectPath))
        {
            // 有未完成的落盘：先按写前日志前滚，再清 —— 不能把前滚依据静默删掉
            string recovery = DraftService.RecoverPendingWrites(databasePath);
            builder.AppendLine("- 检测到未完成的写前日志，已先前滚补齐：");
            builder.AppendLine(recovery.Length == 0 ? "  （无）" : recovery);
        }

        int draftCount = store.Find(projectPath)?.Edits.Count ?? 0;

        // 前滚可能刻意**保留**日志（文件被人手改过 → 不敢覆盖）。既然用户明确要清除这个项目的一切，
        // 就在这里把它们一并删掉并报出条数 —— 否则它们会每次启动重复报告，永远没有出口。
        int discarded = store.HasPendingJournal(projectPath) ? store.ClearJournalsForProject(projectPath) : 0;

        store.ClearTracking(projectPath);
        // 内存里的两张许可表也要清（计划 §三.6）：否则 untrack 之后旧的 applyCookie 还能越过
        // "每次落盘前必须先预检"这道闸（落盘内容仍来自现场计算，但闸门语义被破坏）
        PermitStore.Invalidate(projectPath);
        builder.AppendLine($"- 项目：{projectPath}");
        builder.AppendLine($"- 已删除拟定：{draftCount} 条");
        if (discarded > 0)
        {
            builder.AppendLine($"- 同时丢弃 {discarded} 条未处理的写前日志（前滚时发现文件被手改过，之前一直保留着）");
        }
        builder.AppendLine("- 追踪快照已删除；要再写这个项目，请重新 track_project。");
        return builder.ToString();
    }

    private static void AppendDraftList(StringBuilder builder, DraftRecord? draft)
    {
        builder.AppendLine();
        builder.AppendLine("## 现在已经有这些拟定写");
        if (draft == null || draft.Edits.Count == 0)
        {
            builder.AppendLine("（无）");
            return;
        }

        foreach (DraftEdit edit in draft.Edits)
        {
            builder.AppendLine($"- [{edit.Sequence}] {DraftService.CategoryOf(edit)} {DraftService.EditLabel(edit)}");
        }
    }

    /// <summary>未追踪更改 = 基线与当前编译树的差异，**剔掉拟定已经要改的符号**。</summary>
    private static void AppendUntrackedChanges(
        StringBuilder builder,
        TrackingRecord existing,
        IReadOnlyDictionary<string, string> current,
        DraftRecord? draft)
    {
        builder.AppendLine();
        builder.AppendLine("## 有这些未追踪更改");

        BaselineDiff diff = SymbolBaseline.Compare(existing.Baseline, current);
        HashSet<string> drafted = draft == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                draft.Edits.Select(edit => edit.SymbolKey).Where(key => key.Length > 0),
                StringComparer.Ordinal);

        List<string> changed = [.. diff.Changed.Where(key => !drafted.Contains(key))];
        List<string> added = [.. diff.Added.Where(key => !drafted.Contains(key))];
        List<string> removed = [.. diff.Removed.Where(key => !drafted.Contains(key))];
        if (changed.Count == 0 && added.Count == 0 && removed.Count == 0)
        {
            builder.AppendLine("（无）");
            return;
        }

        foreach (string key in changed)
        {
            builder.AppendLine($"- {key} —— 快照之后被外部改动");
        }

        foreach (string key in added)
        {
            builder.AppendLine($"- {key} —— 快照之后新出现");
        }

        foreach (string key in removed)
        {
            builder.AppendLine($"- {key} —— 快照之后消失");
        }
    }
}
