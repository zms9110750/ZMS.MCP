using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Resource.Archive;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Tools;

/// <summary>删除：走回收站；进不去就停手，交用户处理。</summary>
[McpServerToolType]
public static class DeleteTools
{
    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Delete a file or a directory (with everything inside it). Deletion always goes to the recycle bin; when the " +
        "item cannot go there - too large for the bin, a network or removable drive, a read-only volume - nothing is " +
        "deleted at all and the reply says so, because that is a job for the user, not for an agent. The cookie is " +
        "required and must be the one for what is being deleted (for a directory: the one from enumerating the whole " +
        "tree). A cookie that no longer matches means someone changed it in between - then nothing is deleted.")]
    public static string Delete(
        [Description("Path of the object to delete. Absolute (may include a drive letter) unless target is given.")] string path,
        [Description("Cookie for this object (for a directory: from enumerating the whole tree).")] string cookie,
        [Description("Storage target: empty = local file system; 'ftp:<session>' = FTP session; anything else is an archive path.")] string? target = null)
    {
        Address address = Resolve.Address(target, path);

        if (address is LocalAddress local)
        {
            return DeleteService.Run(local, cookie);
        }

        // 压缩包内的条目：不落盘，攒一条拟定；要**这个条目自己**的凭据
        if (address is ArchiveAddress archive)
        {
            TrackedArchive tracked = DraftStore.FindByPath(archive.ArchivePath)
                ?? throw new ArgumentException(
                    $"这个压缩包没在追踪：{archive.ArchivePath} —— 先 `track_archive` 再删里面的条目。");

            return ArchiveService.Stage(archive.ArchivePath, "delete", archive.InnerPath, null, cookie);
        }

        throw new ArgumentException("远端删除一律不执行（远端没有回收站，删了不可恢复）——请用户手动处理。");
    }
}
