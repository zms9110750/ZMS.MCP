using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Csharp.Draft;

namespace ZMS.MCP.Csharp.Tools;

/// <summary>
/// 拟定层：追踪（拿 cookie）、拟定新增/修改/删除符号、列出拟定、解决冲突、确认（预演或带 cookie 落盘）。
/// 拟定状态存在 sqlite（MCP 自己的目录），MCP 重启不丢；agent 重启靠「列出拟定」拿 cookit。
/// 编辑**必须先追踪**：stage_draft / confirm_draft 都要 track_project 发出来的追踪 cookie。
/// </summary>
[McpServerToolType]
public static class DraftTools
{
    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Track a project, or clear its tracking - one tool, two uses, and the only source of the tracking cookie. " +
        "Without cookie: the first call saves every symbol's hash and returns a tracking cookie; later calls keep that baseline and only report. " +
        "That cookie has two uses: pass it to stage_draft / confirm_draft to write drafts, or pass it back here to clear the tracking. " +
        "The report lists the pending draft edits and the untracked changes (symbols whose hash no longer matches the saved one). " +
        "With cookie: verifies it, then deletes this project's tracking record and every pending draft edit - the only way to throw drafts away; " +
        "a still-pending write-ahead journal is rolled forward first, never silently dropped. Editing drafts requires an active tracking session.")]
    public static string TrackProject(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("The tracking cookie from a previous call; empty = start or resume tracking")] string cookie = "")
    {
        return string.IsNullOrWhiteSpace(cookie)
            ? TrackingService.Track(csprojPath)
            : TrackingService.Untrack(csprojPath, cookie);
    }

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Stage an add/modify/delete of a member inside a draft (drafts accumulate: repeated calls stack on the same draft). " +
        "cookie = the tracking cookie returned by track_project (the same cookie clears the tracking); no csprojPath is needed. " +
        "typePath = fully qualified name down to the top-level type, and may go past multiple dots for nested types; " +
        "memberName includes the parameter list for methods. content = null deletes the member. " +
        "The content must pass a C# syntax check, otherwise staging fails and nothing is recorded. " +
        "Staging a symbol that has an unresolved conflict also resolves it: that symbol's saved hash becomes the current one.")]
    public static string StageDraft(
        [Description("Tracking cookie from track_project")] string cookie,
        [Description("Fully qualified type path, e.g. 'My.Ns.Outer' or 'My.Ns.Outer.Inner'")] string typePath,
        [Description("Member name (with parameter list for methods); empty stages a brand new type")] string memberName,
        [Description("New member source; null deletes the member")] string? content)
    {
        return DraftService.Stage(cookie, typePath, memberName, content);
    }

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Rename a symbol: records one rename entry in the draft, covering the declaration plus every reference " +
        "inside this compilation (strings, comments, non-C# files and other projects are out of scope). " +
        "cookie = the tracking cookie returned by track_project (the same cookie clears the tracking); no csprojPath is needed. " +
        "memberPath = a symbol path exactly as the symbol tool reports it, e.g. 'My.Ns.Type.Member(int)' or 'My.Ns.Type'. " +
        "newName = the new name, without a parameter list. " +
        "It never writes to disk by itself: the edit sites are computed at apply time and the write still goes " +
        "through confirm_draft, exactly like every other draft entry.")]
    public static string RenameSymbol(
        [Description("Tracking cookie from track_project")] string cookie,
        [Description("Symbol path, e.g. 'My.Ns.Type.Member(int)' or 'My.Ns.Type'")] string memberPath,
        [Description("New name, without a parameter list")] string newName)
    {
        return DraftService.Rename(cookie, memberPath, newName);
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "List the pending draft of a project, including its cookit - this is how an agent that restarted " +
        "picks the draft back up.")]
    public static string ListDraft(
        [Description("csproj path, or a unique project name")] string csprojPath)
    {
        return DraftService.List(csprojPath);
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Confirm a draft. cookie = the tracking cookie returned by track_project; no csprojPath is needed. " +
        "Without applyCookie: rebuilds the symbol tree, locates each symbol's file, runs the pre-check " +
        "(file busy / unresolved conflicts / file bytes) and, only when there is no unresolved conflict, returns an in-memory applyCookie plus the files it would touch. " +
        "With that applyCookie: re-checks that every symbol still lives in the same file and that the file bytes are unchanged, " +
        "then writes (journal -> atomic write -> clear journal/draft/permits -> dotnet format -> recompute the tracking baseline).")]
    public static string ConfirmDraft(
        [Description("Tracking cookie from track_project")] string cookie,
        [Description("applyCookie from the pre-check; empty = pre-check only")] string applyCookie)
    {
        return DraftService.Confirm(cookie, applyCookie);
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Resolve one symbol's conflict using the selectCookie that the symbol tool returned while that symbol had an unresolved conflict. " +
        "choice = keep (keep this symbol's draft) or drop (remove this symbol's draft); there is no snapshot choice any more. " +
        "Both choices update that symbol's saved hash to the current one, so the conflict disappears. Any applyCookie is invalidated afterwards.")]
    public static string SelectDraft(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("Symbol path, e.g. 'My.Ns.Type.Member(int)'")] string memberPath,
        [Description("The selectCookie returned by the symbol tool")] string selectCookie,
        [Description("keep (keep the draft) / drop (remove the draft)")] string choice)
    {
        return DraftService.Select(csprojPath, memberPath, selectCookie, choice);
    }
}
