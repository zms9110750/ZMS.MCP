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
        "Confirm a draft: lists the changes grouped by add/delete and the diagnostics delta " +
        "(paired by code + message + file, line numbers only for display), and prints the cookit. " +
        "Passing a matching cookit WRITES the changes to disk; then the changed files are formatted with " +
        "dotnet format --include, and no git operation is performed at all. " +
        "Files changed externally during the draft are reported as conflicts and refused.")]
    public static string ConfirmDraft(
        [Description("csproj path, or a unique project name")] string csprojPath,
        [Description("The cookit from stage/list; empty = dry run")] string cookit,
        [Description("Set false to only preview even when the cookit matches")] bool apply = true)
    {
        return ToolGuard.Run(() => DraftService.Confirm(csprojPath, cookit, apply));
    }
}
