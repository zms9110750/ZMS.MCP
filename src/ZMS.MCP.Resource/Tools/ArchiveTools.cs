using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Resource.Archive;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Tools;

/// <summary>压缩包：追踪起来，再落盘；查看不需要追踪。</summary>
[McpServerToolType]
public static class ArchiveTools
{
    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Track an archive so that writes to it can be staged instead of rewriting the whole file every time. While " +
        "tracked, copy / move / delete aimed at it do not touch disk - each one is recorded as a staged change and asks " +
        "for the cookie of the entry it touches - and confirm later rewrites the archive once. Reading and listing never " +
        "need tracking; they always read the archive as it is on disk. The path may not exist yet: that means 'create it', " +
        "and then format is required (zip / 7z / tgz / ztsd). Passing back the tracking cookie stops tracking and throws " +
        "away the staged changes. An archive on FTP cannot be tracked.")]
    public static string TrackArchive(
        [Description("Path of the archive. Absolute (may include a drive letter) unless target is given.")] string path,
        [Description("Storage target: empty = local file system. An archive on FTP cannot be tracked.")] string? target = null,
        [Description("Format to create with: zip / 7z / tgz / ztsd. Ignored when the archive already exists; required when it does not.")] string? format = null,
        [Description("Tracking cookie. Passing it stops tracking and discards the staged changes.")] string? cookie = null)
    {
        Address address = Address.Parse(target, path);
        if (address is not LocalAddress local)
        {
            throw new ArgumentException("只追踪本地的压缩包 —— FTP 上的压缩包追踪不了。");
        }

        string archivePath = Path.GetFullPath(local.InnerPath);

        // 传回 cookie = 解除追踪，把拟定一并清掉
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            TrackedArchive tracked = ArchiveService.Require(cookie!.Trim());
            int removed = DraftStore.Untrack(tracked);
            return $"# 已解除追踪\n- 包：{tracked.ArchivePath}\n- 清掉了 {removed} 条拟定。\n";
        }

        bool exists = File.Exists(archivePath);
        string used;

        if (exists)
        {
            used = (format ?? "").Trim().Length > 0
                ? ArchiveService.IsSupported(format!)
                    ? format!.Trim().ToLowerInvariant()
                    : throw new ArgumentException($"认不出这个格式：{format}（支持 {ArchiveService.FormatList}）。")
                : FormatFromExtension(archivePath);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                throw new ArgumentException(
                    $"这个包还不存在，所以要说清楚用什么格式建：{ArchiveService.FormatList}。");
            }

            if (!ArchiveService.IsSupported(format!))
            {
                throw new ArgumentException($"认不出这个格式：{format}（支持 {ArchiveService.FormatList}）。");
            }

            used = format!.Trim().ToLowerInvariant();
        }

        long size = exists ? new FileInfo(archivePath).Length : 0;
        long time = exists ? File.GetLastWriteTimeUtc(archivePath).Ticks : 0;
        string tracking = DraftStore.Track(archivePath, used, size, time);
        IReadOnlyList<ArchiveDraft> drafts = DraftStore.DraftsOf(archivePath);

        StringBuilder builder = new();
        builder.AppendLine("# 已开始追踪");
        builder.AppendLine($"- 包：{archivePath}");
        builder.AppendLine($"- 格式：{used}" + (exists ? "（已存在）" : "（还不存在，落盘时创建）"));
        builder.AppendLine($"- 追踪 cookie：`{tracking}`");
        builder.AppendLine($"- 现在攒了 {drafts.Count} 条拟定。");
        builder.AppendLine("- 之后 `copy` / `move` / `delete` 打到这个包上只会攒一条，`confirm` 才落盘。");
        return builder.ToString();
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Settle a tracked archive. With only the tracking cookie this is a dry run: nothing on disk is touched, and the " +
        "reply lays out how many changes were staged (added / deleted), their total size, what the archive will contain " +
        "once written, how it falls against the size thresholds, and an apply cookie. Passing that apply cookie as well " +
        "re-reads the archive and compares it against the baseline first - if the file changed while changes were staged, " +
        "nothing is written. A total over 1 GB is refused outright; over 50 MB asks the host first.")]
    public static string Confirm(
        [Description("Tracking cookie, from track_archive.")] string cookie,
        [Description("Empty = dry run (no disk is touched). Pass the apply cookie from the dry run to actually write.")] string? applyCookie = null)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentException("cookie 必填 —— 它是 `track_archive` 给的那个。");
        }

        return string.IsNullOrWhiteSpace(applyCookie)
            ? ArchiveService.Preview(cookie.Trim())
            : ArchiveService.Apply(cookie.Trim(), applyCookie!);
    }

    /// <summary>按扩展名猜格式。</summary>
    private static string FormatFromExtension(string archivePath)
    {
        string name = Path.GetFileName(archivePath).ToLowerInvariant();
        return true switch
        {
            _ when name.EndsWith(".7z", StringComparison.Ordinal) => "7z",
            _ when name.EndsWith(".tgz", StringComparison.Ordinal) || name.EndsWith(".tar.gz", StringComparison.Ordinal) => "tgz",
            _ when name.EndsWith(".zst", StringComparison.Ordinal) || name.EndsWith(".ztsd", StringComparison.Ordinal) => "ztsd",
            _ => "zip",
        };
    }
}
