using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Resource.Archive;
using ZMS.MCP.Resource.Ftp;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Tools;

/// <summary>列清单：知道里面有什么，并在完整列过时换一个目录树凭据。</summary>
[McpServerToolType]
public static class ListTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "List what is inside a directory, or take a single file's metadata. A directory listing comes with the entries, " +
        "the total count and the total size; when the whole tree is delivered (not cut by depth/limit and within the " +
        "per-call limit) it also mints a tree cookie - that is what deleting a directory or merging folders requires. " +
        "A truncated listing mints nothing. Pointing at a file instead returns only its metadata, and that already mints " +
        "its cookie, because overwriting or deleting a file needs its size and path, not its content.")]
    public static Task<string> List(
        [Description("Path of the directory or file. Absolute (may include a drive letter) unless target is given.")] string path,
        [Description("Storage target: empty = local file system; 'ftp:<session>' = FTP session; anything else is an archive path.")] string? target = null,
        [Description("Recursion depth: 0 = only the current folder. Default 0.")] int depth = 0,
        [Description("Maximum entries per level (files and directories together). 0 = leveled defaults (200 / 100 / 50 / 20). Hard cap 200.")] int limit = 0,
        [Description("Entry kinds to show: 'file' or 'dir'. Empty = both.")] string? type = null,
        [Description("Comma separated attribute names to attach to each entry: created / modified / size / count. Empty = none.")] string? meta = null,
        [Description("Regular expression used to filter entries by name.")] string? regex = null)
    {
        return ToolGuard.RunAsync(async () =>
        {
            Address address = Resolve.Address(target, path);

            if (address is LocalAddress local)
            {
                return ListService.Run(local, depth, limit, type, meta, regex);
            }

            if (address is SessionAddress session)
            {
                return await RemoteService.ListAsync(session, depth, limit, type, meta, regex).ConfigureAwait(false);
            }

            // 压缩包：**永远直读磁盘上现在那个包**
            if (address is ArchiveAddress archive)
            {
                IReadOnlyList<ArchiveService.ArchiveItem> items = ArchiveService.ListEntries(archive.ArchivePath);
                StringBuilder builder = new();
                builder.AppendLine($"# {archive.ArchivePath}（压缩包）");
                builder.AppendLine($"- 路径：{archive.InnerPath}");
                builder.AppendLine($"- 里面 {items.Count} 条");
                builder.AppendLine();

                if (items.Count == 0)
                {
                    builder.AppendLine("（这个包里没有条目）");
                    return builder.ToString();
                }

                foreach (ArchiveService.ArchiveItem item in items)
                {
                    builder.AppendLine($"- {item.Path}  [{item.Size} 字节, {item.Modified:O}]");
                    builder.AppendLine($"  cookie: `{item.Cookie}`");
                }

                builder.AppendLine();
                builder.AppendLine("- 要动里面哪一条，就带**它自己**的 cookie（`copy` / `delete` 走拟定）。");
                return builder.ToString();
            }

            throw new ArgumentException("压缩包还没接上。");
        });
    }
}
