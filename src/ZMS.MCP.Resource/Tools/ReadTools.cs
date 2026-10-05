using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Resource.Archive;
using ZMS.MCP.Resource.Ftp;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Tools;

/// <summary>读取：按需取一段，并且在看完整份时换一个凭据。</summary>
[McpServerToolType]
public static class ReadTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Read text from an object. Without any range argument this is a complete read: it returns the content plus a " +
        "cookie (proof that you saw this version), which every later write to the same object must carry. With a range " +
        "argument (skipline / offset / length / takeline / regex) it returns only that slice and mints no cookie. " +
        "The whole thing is delivered only when it fits in the per-call limit; otherwise the reply is truncated and " +
        "says how much was left out and how long the whole thing is - and no cookie is minted, so that object cannot be " +
        "written or deleted. Decoding is strict: it refuses rather than silently replacing undecodable bytes.")]
    public static async Task<string> Read(
        [Description("Path of the object. Absolute (may include a drive letter) unless target is given.")] string path,
        [Description("Storage target: empty = local file system; 'ftp:<session>' = FTP session; anything else is an archive path.")] string? target = null,
        [Description("Skip this many lines first (applied before offset). 0 = from the first line.")] int skipline = 0,
        [Description("Skip this many characters (applied after skipline). 0 = from the start.")] int offset = 0,
        [Description("Show at most this many characters. 0 = 'no range given'; a ranged read then shows 5000 by default.")] int length = 0,
        [Description("Show at most this many lines. 0 = no limit.")] int takeline = 0,
        [Description("Encoding name, e.g. 'gbk' or 'utf-8'. Empty = BOM, then strict UTF-8, else refuse.")] string? encoding = null,
        [Description("Regular expression searched in the content. Context is graded by match count: over 20 matches give line numbers only, over 5 give one line around each, 5 or fewer give three.")] string? regex = null)
    {
        Address address = Resolve.Address(target, path);

        if (address is LocalAddress local)
        {
            return ReadService.Run(local, skipline, offset, length, takeline, encoding, regex);
        }

        if (address is SessionAddress session)
        {
            return await RemoteService.ReadAsync(session, skipline, offset, length, takeline, encoding, regex)
                .ConfigureAwait(false);
        }

        // 压缩包：**永远直读磁盘上现在那个包**，跟有没有在追踪无关
        if (address is ArchiveAddress archive)
        {
            ArchiveService.ArchiveItem? item = ArchiveService.ListEntries(archive.ArchivePath)
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Path,
                    archive.InnerPath.Replace('\\', '/').Trim('/'),
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"包里没有这个条目：{archive.InnerPath}");

            byte[]? body = ArchiveService.ReadEntry(archive.ArchivePath, archive.InnerPath)
                ?? throw new ArgumentException($"包里读不出这个条目：{archive.InnerPath}");

            return ReadService.Render(
                $"{archive.ArchivePath}!{archive.InnerPath}",
                body,
                item.Modified,
                skipline,
                offset,
                length,
                takeline,
                encoding,
                regex);
        }

        throw new ArgumentException("压缩包还没接上。");
    }
}
