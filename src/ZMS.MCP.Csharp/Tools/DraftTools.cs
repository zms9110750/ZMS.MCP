using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Csharp.Draft;

namespace ZMS.MCP.Csharp.Tools;

/// <summary>
/// 拟定层：拟定新增/修改/删除符号、列出拟定、确认（预演或带 cookit 落盘）。
/// 拟定状态存在 sqlite（MCP 自己的目录），MCP 重启不丢；agent 重启靠「列出拟定」拿 cookit。
/// </summary>
[McpServerToolType]
public static class DraftTools
{
    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Track a project, or clear its tracking — one tool, two uses. " +
        "Without cookie: the first call records a symbol-level snapshot and returns a tracking cookie; later calls keep that snapshot and only report. " +
        "The report lists the pending draft edits and the untracked changes (symbols changed on disk outside this tool). " +
        "With cookie: verifies it, then deletes this project's tracking snapshot and every pending draft edit — the only way to throw drafts away; " +
        "a still-pending write-ahead journal is rolled forward first, never silently dropped. Editing drafts requires an active tracking session.")]
    public static string TrackProject(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("The tracking cookie from a previous call; empty = start or resume tracking")] string cookie = "")
    {
        return ToolGuard.Run(() => string.IsNullOrWhiteSpace(cookie)
            ? TrackingService.Track(csprojPath)
            : TrackingService.Untrack(csprojPath, cookie));
    }

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Stage an add/modify/delete of a member inside a draft (drafts accumulate: repeated calls stack on the same draft). " +
        "typePath = fully qualified name down to the top-level type, and may go past multiple dots for nested types; " +
        "memberName includes the parameter list for methods. content = null deletes the member. " +
        "The content must pass a C# syntax check, otherwise staging fails and nothing is recorded.")]
    public static string StageDraft(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("Fully qualified type path, e.g. 'My.Ns.Outer' or 'My.Ns.Outer.Inner'")] string typePath,
        [Description("Member name (with parameter list for methods); empty stages a brand new type")] string memberName,
        [Description("New member source; null deletes the member")] string? content)
    {
        return ToolGuard.Run(() => DraftService.Stage(csprojPath, typePath, memberName, content));
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "List the pending draft of a project, including its cookit — this is how an agent that restarted " +
        "picks the draft back up.")]
    public static string ListDraft(
        [Description("csproj path, or a unique project name")] string csprojPath)
    {
        return ToolGuard.Run(() => DraftService.List(csprojPath));
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Confirm a draft. Without applyCookie: rebuilds the symbol tree, locates each symbol's file, runs the pre-check " +
        "(file busy / symbol conflict / file bytes) and, when clean, returns an in-memory applyCookie plus the files it would touch. " +
        "With that applyCookie: re-checks that every symbol still lives in the same file and that the file bytes are unchanged, " +
        "then writes (journal -> atomic write -> clear journal/draft/permits -> dotnet format -> recompute tracking baseline). No git operation is performed.")]
    public static string ConfirmDraft(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("applyCookie from the pre-check; empty = pre-check only")] string applyCookie,
        [Description("Set false to only preview even when the cookie matches")] bool apply = true)
    {
        return ToolGuard.Run(() => DraftService.Confirm(csprojPath, applyCookie, apply));
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Resolve one symbol's conflict using the selectCookie that get_member returned while that symbol had a conflict. " +
        "choice = draft (keep the drafted content) / snapshot (restore the pre-edit content) / disk (take the current on-disk content) / " +
        "drop (remove this symbol from the draft). Any applyCookie is invalidated afterwards.")]
    public static string SelectDraft(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("Symbol path, e.g. 'My.Ns.Type.Member(int)'")] string memberPath,
        [Description("The selectCookie returned by get_member")] string selectCookie,
        [Description("draft / snapshot / disk / drop")] string choice)
    {
        return ToolGuard.Run(() => DraftService.Select(csprojPath, memberPath, selectCookie, choice));
    }
}
