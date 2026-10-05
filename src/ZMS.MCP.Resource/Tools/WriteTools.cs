using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Resource.Ftp;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Tools;

/// <summary>写入：新建与覆写，以及在已有内容里改某几处 —— 两者都先验凭据。</summary>
[McpServerToolType]
public static class WriteTools
{
    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Write a whole file: create it when nothing is there, or overwrite it when something is. " +
        "Creating needs no cookie; overwriting requires the cookie you got by reading that content in full, and a cookie " +
        "that no longer matches means someone changed it in between - then nothing is written and the current state is " +
        "returned instead. Without a cookie nothing is written at all: the reply is the list of effects (size, " +
        "modification time, that the whole file would be replaced) and that is not an error. Written back in the file's " +
        "own encoding; a new file is UTF-8 without BOM.")]
    public static Task<string> Write(
        [Description("Path of the file. Absolute (may include a drive letter) unless target is given.")] string path,
        [Description("The whole content to write.")] string content,
        [Description("Storage target: empty = local file system; 'ftp:<session>' = FTP session; anything else is an archive path.")] string? target = null,
        [Description("Encoding to write with. Empty = the encoding the file was read with; a new file is UTF-8 without BOM.")] string? encoding = null,
        [Description("Cookie from reading this content in full. Required when the target already holds content.")] string? cookie = null)
    {
        return ToolGuard.RunAsync(async () =>
        {
            Address address = Resolve.Address(target, path);

            if (address is LocalAddress local)
            {
                return WriteService.Write(local, content, encoding, cookie);
            }

            // 远端：只有「目标还不存在」能做 —— 覆盖需要凭据，而远端读取不发 cookie
            if (address is SessionAddress session)
            {
                return await RemoteService.WriteAsync(session, content, encoding).ConfigureAwait(false);
            }

            throw new ArgumentException("压缩包内的改动不走 write：往包里放东西用 `copy`（目标填追踪 cookie），删条目用 `delete`。");
        });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Replace parts of existing content. By default the pattern is matched literally; with regex = true it is a " +
        "regular expression and the replacement may refer to captured groups ($1, ${name}). The cookie is required - it " +
        "is what proves you read this version - and a cookie that no longer matches leaves the file untouched. " +
        "Exactly one place must match: none is an error and several are refused, so nothing is ever changed by halves. " +
        "Written back in the file's own encoding.")]
    public static string Replace(
        [Description("Path of the file. Absolute (may include a drive letter) unless target is given.")] string path,
        [Description("Text to match; a regular expression when regex = true.")] string pattern,
        [Description("What to put in its place; may refer to captured groups when regex = true, e.g. '$1'.")] string replacement,
        [Description("Cookie from reading this content in full.")] string cookie,
        [Description("Storage target: empty = local file system; 'ftp:<session>' = FTP session; anything else is an archive path.")] string? target = null,
        [Description("Treat pattern as a regular expression. Default false = literal match.")] bool regex = false,
        [Description("Encoding to write with. Empty = the encoding the file was read with.")] string? encoding = null)
    {
        return ToolGuard.Run(() =>
        {
            Address address = Address.Parse(target, path);
            if (address is not LocalAddress local)
            {
                throw new ArgumentException("目前只支持本地对象；FTP 与压缩包还没接上。");
            }

            return WriteService.Replace(local, pattern, replacement, regex, encoding, cookie);
        });
    }
}
