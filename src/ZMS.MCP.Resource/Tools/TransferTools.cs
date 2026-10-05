using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Resource.Archive;
using ZMS.MCP.Resource.Ftp;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Tools;

/// <summary>移动与复制：目标干净就直接做；目标已有内容先做目录验证、要凭据。</summary>
[McpServerToolType]
public static class TransferTools
{
    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Move a file or a directory to another place. When the target is empty this just happens. When the target " +
        "already holds something - a folder that would be merged into, files that would be replaced - nothing happens " +
        "yet: the reply is a directory check listing every folder that would be merged and every file that would be " +
        "replaced, each with its cookie, and you come back with those cookies in toCookie. Without them nothing is ever " +
        "merged or overwritten silently. When the conflicts are far too many to review, the check only reports the " +
        "count and tells you the user has to handle it - this tool cannot grant that permission.")]
    public static async Task<string> Move(
        [Description("Source path. Absolute (may include a drive letter) unless target is given.")] string path,
        [Description("Destination path. Absolute (may include a drive letter) unless toTarget is given.")] string toPath,
        [Description("Source storage target: empty = local file system; 'ftp:<session>' = FTP session; anything else is an archive path.")] string? target = null,
        [Description("Destination storage target: empty = local file system; 'ftp:<session>' = FTP session; anything else is an archive path.")] string? toTarget = null,
        [Description("Cookie for the source, required when the move crosses volumes or storages.")] string? cookie = null,
        [Description("Cookies for the destination side (folders to be merged, files to be replaced). Order does not matter; each is compared against the object it must vouch for.")] string[]? toCookie = null)
    {
        Address source = Resolve.Address(target, path);
        Address destination = Resolve.Address(toTarget, toPath);

        if (source is LocalAddress from && destination is LocalAddress into)
        {
            return TransferService.Move(from, into.InnerPath, cookie, toCookie);
        }

        if (source is ArchiveAddress || destination is ArchiveAddress)
        {
            throw new ArgumentException(
                "压缩包里的移动请拆成两步：`copy` 到包里 + `delete` 掉原来那个（两者都走拟定）。");
        }

        // 涉及远端：只有「本地 → 远端」能做（删的是本地源），见 RemoteService.MoveAsync
        return await RemoteService.MoveAsync(source, destination, cookie, toCookie).ConfigureAwait(false);
    }

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Copy a file or a directory to another place, leaving the source alone. This is also how things move between " +
        "storages. When the target is empty the copy just happens; when the target already holds something - folders " +
        "that would be merged into, files that would be replaced - nothing happens yet: the reply is a directory check " +
        "listing each folder and each file with its cookie, and you come back with those cookies in toCookie. Too many " +
        "conflicts to review are not listed at all, only counted, with a note that the user has to handle it.")]
    public static async Task<string> Copy(
        [Description("Source path. Absolute (may include a drive letter) unless target is given.")] string path,
        [Description("Destination path. Absolute (may include a drive letter) unless toTarget is given.")] string toPath,
        [Description("Source storage target: empty = local file system; 'ftp:<session>' = FTP session; anything else is an archive path.")] string? target = null,
        [Description("Destination storage target: empty = local file system; 'ftp:<session>' = FTP session; anything else is an archive path.")] string? toTarget = null,
        [Description("Cookies for the destination side (folders to be merged, files to be replaced). Order does not matter; each is compared against the object it must vouch for.")] string[]? toCookie = null)
    {
        Address source = Resolve.Address(target, path);
        Address destination = Resolve.Address(toTarget, toPath);

        if (source is LocalAddress && destination is LocalAddress)
        {
            return TransferService.Copy((LocalAddress)source, ((LocalAddress)destination).InnerPath, toCookie);
        }

        // 往被追踪的压缩包里放东西：不落盘，攒一条拟定；要**那个条目自己**的凭据
        if (destination is ArchiveAddress into && source is LocalAddress from)
        {
            TrackedArchive tracked = DraftStore.FindByPath(into.ArchivePath)
                ?? throw new ArgumentException(
                    $"这个压缩包没在追踪：{into.ArchivePath} —— 先 `track_archive` 再往里放东西。");

            byte[] content = File.ReadAllBytes(Path.GetFullPath(from.InnerPath));
            return ArchiveService.Stage(
                into.ArchivePath,
                "add",
                into.InnerPath,
                content,
                toCookie is { Length: > 0 } ? toCookie[0] : null);
        }

        // 跨存储：FTP → 本地是下载，本地 → FTP 是上传，FTP → FTP 走流
        return await RemoteService.CopyAsync(source, destination, toCookie).ConfigureAwait(false);
    }
}
