using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.ZStandard;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using SharpCompress.Writers.Tar;
using SharpCompress.Writers.Zip;
using ZMS.MCP.Core.Credentials;

namespace ZMS.MCP.Resource.Archive;

/// <summary>
/// 压缩包：**查看永远直读磁盘上现在那个包**（不需要追踪）；写要先 `track_archive` 起来，
/// 之后的 <c>copy</c> / <c>move</c> / <c>delete</c> 只**攒一条拟定**，
/// 等 `confirm` 才把整包重写一遍（先写临时文件，再原子替换）—— 因为改一个条目就要重写整包，
/// 攒起来一次做才划算。
///
/// 读法分两路（SharpCompress 0.50 的实际口径）：<c>.7z</c> 走 <see cref="ArchiveFactory"/>，
/// 其余（zip、tar.gz）走 <see cref="ReaderFactory"/>。
/// </summary>
public static class ArchiveService
{
    /// <summary>合计超过它就不给打。</summary>
    public const long RefuseAbove = 1024L * 1024 * 1024;

    /// <summary>合计超过它就先问宿主。</summary>
    public const long ConfirmAbove = 50L * 1024 * 1024;

    private static readonly string[] Formats = ["zip", "7z", "tgz", "ztsd"];

    /// <summary>认不认这个格式。</summary>
    public static bool IsSupported(string format)
    {
        return Formats.Contains(format.Trim().ToLowerInvariant());
    }

    /// <summary>支持的格式清单（报错时用）。</summary>
    public static string FormatList => string.Join(" / ", Formats);

    /// <summary>包内条目。</summary>
    /// <param name="Path">包内路径。</param>
    /// <param name="Size">大小（字节）。7z 读出来可能恒为 0，那就只作展示用。</param>
    /// <param name="Modified">修改时间。</param>
    /// <param name="Cookie">它自己的凭据。</param>
    public sealed record ArchiveItem(string Path, long Size, DateTimeOffset Modified, string Cookie);

    /// <summary>读包内一个条目（直读磁盘上的包）。没有就返回 <c>null</c>。</summary>
    public static byte[]? ReadEntry(string archivePath, string innerPath)
    {
        EnsureArchive(archivePath);
        string wanted = Normalize(innerPath);

        try
        {
            if (IsSevenZip(archivePath))
            {
                using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
                IArchiveEntry? entry = archive.Entries.FirstOrDefault(candidate =>
                    !candidate.IsDirectory && string.Equals(Normalize(candidate.Key), wanted, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    return null;
                }

                using Stream source = entry.OpenEntryStream();
                using MemoryStream sink = new();
                source.CopyTo(sink);
                return sink.ToArray();
            }

            using IReader reader = ReaderFactory.OpenReader(archivePath);
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory
                    || !string.Equals(Normalize(reader.Entry.Key), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using Stream source = reader.OpenEntryStream();
                using MemoryStream sink = new();
                source.CopyTo(sink);
                return sink.ToArray();
            }

            return null;
        }
        catch (Exception exception)
        {
            throw new ArgumentException($"包里读不出这个条目：{innerPath} —— {exception.Message}");
        }
    }

    /// <summary>列包内条目（直读磁盘上的包）；每条附它**自己**的凭据。</summary>
    public static IReadOnlyList<ArchiveItem> ListEntries(string archivePath)
    {
        EnsureArchive(archivePath);
        List<ArchiveItem> items = [];

        try
        {
            if (IsSevenZip(archivePath))
            {
                using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
                foreach (IArchiveEntry entry in archive.Entries)
                {
                    if (!entry.IsDirectory)
                    {
                        items.Add(Make(entry.Key, entry.Size, entry.LastModifiedTime));
                    }
                }
            }
            else
            {
                using IReader reader = ReaderFactory.OpenReader(archivePath);
                while (reader.MoveToNextEntry())
                {
                    if (!reader.Entry.IsDirectory)
                    {
                        items.Add(Make(reader.Entry.Key, reader.Entry.Size, reader.Entry.LastModifiedTime));
                    }
                }
            }
        }
        catch (Exception exception)
        {
            throw new ArgumentException($"打不开这个压缩包：{archivePath} —— {exception.Message}");
        }

        items.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        return items;
    }

    /// <summary>把一条拟定攒起来。返回这句人话。</summary>
    public static string Stage(string archivePath, string kind, string innerPath, byte[]? content, string? cookie)
    {
        string inner = Normalize(innerPath);
        if (inner.Length == 0)
        {
            throw new ArgumentException("包内路径不能是空的。");
        }

        ArchiveItem? existing = File.Exists(archivePath)
            ? ListEntries(archivePath).FirstOrDefault(item =>
                string.Equals(item.Path, inner, StringComparison.OrdinalIgnoreCase))
            : null;

        // 动到包里**已经有**的东西 → 要它自己的凭据
        if (existing != null)
        {
            if (string.IsNullOrWhiteSpace(cookie))
            {
                return $"# 拟定需要凭据（未记下）\n- 包：{archivePath}\n- 条目：{inner}\n"
                    + $"- 它现在的凭据是 `{existing.Cookie}` —— 用 `list` 打进包里也能拿到。\n"
                    + "- （这不算失败：缺凭据是两段式的第一段。）\n";
            }

            if (!Cookie.Matches(cookie, existing.Cookie))
            {
                return $"# 拟定没记下：凭据对不上\n- 条目：{inner}\n"
                    + $"- 你给的：`{cookie.Trim()}`\n"
                    + $"- 现在算出来：`{existing.Cookie}` —— 这期间包被改过。\n";
            }
        }

        DraftStore.Stage(archivePath, kind, inner, content, cookie ?? "");
        int total = DraftStore.DraftsOf(archivePath).Count;

        return $"# 拟定已更新\n- 包：{archivePath}\n"
            + $"- 这一条：{(kind == "add" ? "增加" : "删除")} `{inner}`"
            + (content == null ? "" : $"（{content.LongLength} 字节）") + "\n"
            + $"- 现在共 {total} 条拟定。\n";
    }

    /// <summary>落盘预检：不碰磁盘，把"落盘后会变成什么样"摆出来。</summary>
    public static string Preview(string cookie)
    {
        TrackedArchive tracked = Require(cookie);
        IReadOnlyList<ArchiveDraft> drafts = DraftStore.DraftsOf(tracked.ArchivePath);
        IReadOnlyList<ArchiveItem> existing = File.Exists(tracked.ArchivePath)
            ? ListEntries(tracked.ArchivePath)
            : [];

        List<string> adds = [.. drafts.Where(draft => draft.IsAdd).Select(draft => draft.InnerPath)];
        List<string> deletes = [.. drafts.Where(draft => !draft.IsAdd).Select(draft => draft.InnerPath)];
        long added = drafts.Where(draft => draft.IsAdd).Sum(draft => draft.Content?.LongLength ?? 0);

        List<string> after = [.. existing
            .Select(item => item.Path)
            .Where(path => !deletes.Contains(path, StringComparer.OrdinalIgnoreCase))];
        after.AddRange(adds.Where(add => !after.Contains(add, StringComparer.OrdinalIgnoreCase)));
        after.Sort(StringComparer.OrdinalIgnoreCase);

        StringBuilder builder = new();
        builder.AppendLine($"# 落盘预检（{tracked.ArchivePath}）");
        builder.AppendLine($"- 格式：{tracked.Format}");
        builder.AppendLine($"- 攒了 {drafts.Count} 条：增加 {adds.Count} 条，删除 {deletes.Count} 条");
        builder.AppendLine($"- 合计大小：{added} 字节（落盘后要占这么多）");
        builder.AppendLine($"- 现在包里：{existing.Count} 条；落盘后：{after.Count} 条");
        builder.AppendLine();
        builder.AppendLine("落盘后包里会变成：");
        builder.AppendLine("```");

        foreach (string path in after)
        {
            builder.AppendLine($"  {path}{(adds.Contains(path, StringComparer.OrdinalIgnoreCase) ? "  （新）" : "")}");
        }

        builder.AppendLine("```");
        builder.AppendLine();

        if (added > RefuseAbove)
        {
            builder.AppendLine($"- **合计超过 1 GB，不给打**（现在是 {added} 字节）。");
        }
        else if (added > ConfirmAbove)
        {
            builder.AppendLine($"- 合计超过 50 MB（{added} 字节）：**先向宿主确认**再落盘。");
        }
        else
        {
            builder.AppendLine("- 分档：正常（合计不到 50 MB）。");
        }

        builder.AppendLine();
        builder.AppendLine($"- 落盘 cookie：`{ApplyCookieFor(cookie)}`");
        builder.AppendLine("- 确认无误就把这个 cookie 填进 `applyCookie` 再来一次。");
        return builder.ToString();
    }

    /// <summary>落盘：重读比对 → 在内存里重放 → 攒出新包 → 原子替换。</summary>
    public static string Apply(string cookie, string applyCookie)
    {
        TrackedArchive tracked = Require(cookie);

        if (!File.Exists(tracked.ArchivePath))
        {
            // 追踪时它还不存在 = 要建一个新的：那就没有"被改过"可言
            if (tracked.BaselineSize != 0 || tracked.BaselineTime != 0)
            {
                throw new ArgumentException($"压缩包不存在了：{tracked.ArchivePath}");
            }
        }
        else
        {
            // 拟定期间包被人动过 → 拒绝落盘
            FileInfo current = new(tracked.ArchivePath);
            if (current.Length != tracked.BaselineSize || current.LastWriteTimeUtc.Ticks != tracked.BaselineTime)
            {
                throw new ArgumentException(
                    "拟定期间这个压缩包被改过（大小或修改时间对不上），拒绝落盘。"
                    + "先 `track_archive` 重新起一次，再重放那些改动。");
            }
        }

        IReadOnlyList<ArchiveDraft> drafts = DraftStore.DraftsOf(tracked.ArchivePath);
        if (drafts.Count == 0)
        {
            throw new ArgumentException("这个包上没有攒任何拟定，没东西可落盘。");
        }

        string expected = ApplyCookieFor(cookie);
        if (!Cookie.Matches(applyCookie, expected))
        {
            return $"# 没落盘：落盘 cookie 对不上\n- 你给的：`{applyCookie.Trim()}`\n"
                + $"- 现在算出来：`{expected}` —— 拟定或包变了。重新预检一次再来。\n";
        }

        long added = drafts.Where(draft => draft.IsAdd).Sum(draft => draft.Content?.LongLength ?? 0);
        if (added > RefuseAbove)
        {
            throw new ArgumentException($"合计超过 1 GB（{added} 字节），不给打 —— 请用户手动处理。");
        }

        Dictionary<string, byte[]> keep = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(tracked.ArchivePath))
        {
            foreach (ArchiveItem item in ListEntries(tracked.ArchivePath))
            {
                byte[]? body = ReadEntry(tracked.ArchivePath, item.Path);
                if (body != null)
                {
                    keep[item.Path] = body;
                }
            }
        }

        foreach (ArchiveDraft draft in drafts)
        {
            if (draft.IsAdd)
            {
                keep[draft.InnerPath] = draft.Content ?? [];
            }
            else
            {
                keep.Remove(draft.InnerPath);
            }
        }

        string temporary = tracked.ArchivePath + ".zms-building";
        Write(temporary, tracked.Format, keep);

        File.Move(temporary, tracked.ArchivePath, overwrite: true);

        DraftStore.ClearDrafts(tracked.ArchivePath);
        FileInfo done = new(tracked.ArchivePath);
        DraftStore.Rebaseline(tracked, done.Length, done.LastWriteTimeUtc.Ticks);

        return $"# 已落盘\n- 包：{tracked.ArchivePath}\n- 大小：{done.Length} 字节\n"
            + $"- 里面 {keep.Count} 条；拟定已清空（追踪还在，cookie 不变）。\n";
    }

    /// <summary>按 cookie 找回追踪；找不到就报错。</summary>
    public static TrackedArchive Require(string cookie)
    {
        return DraftStore.Find(cookie)
            ?? throw new ArgumentException($"没有这个追踪 cookie：{cookie}（先用 `track_archive` 追踪一个包）。");
    }

    /// <summary>当前该用的落盘 cookie（预检给的就是它；落盘时再算一次比对）。</summary>
    public static string ApplyCookieFor(string cookie)
    {
        TrackedArchive tracked = Require(cookie);
        return ApplyCookie(tracked, DraftStore.DraftsOf(tracked.ArchivePath));
    }

    private static ArchiveItem Make(string? key, long size, DateTime? modified)
    {
        string path = Normalize(key);
        DateTimeOffset when = modified ?? DateTimeOffset.UnixEpoch;
        return new ArchiveItem(path, size, when, Cookie.Of(path, size, when));
    }

    private static string ApplyCookie(TrackedArchive tracked, IReadOnlyList<ArchiveDraft> drafts)
    {
        StringBuilder builder = new();
        builder.Append(tracked.ArchivePath).Append('\n');
        builder.Append(tracked.BaselineSize).Append('\n');
        builder.Append(tracked.BaselineTime).Append('\n');

        foreach (ArchiveDraft draft in drafts)
        {
            builder.Append(draft.Id).Append('\n');
            builder.Append(draft.Kind).Append('\n');
            builder.Append(draft.InnerPath).Append('\n');
            builder.Append(draft.Content == null ? "" : Cookie.Fingerprint(Convert.ToBase64String(draft.Content))).Append('\n');
        }

        return Cookie.Fingerprint(builder.ToString());
    }

    private static void Write(string target, string format, Dictionary<string, byte[]> entries)
    {
        string name = format.Trim().ToLowerInvariant();
        if (name == "ztsd")
        {
            WriteTarZstd(target, entries);
            return;
        }

        using FileStream output = File.Create(target);
        using IWriter writer = name switch
        {
            "7z" => WriterFactory.OpenWriter(output, ArchiveType.SevenZip, new SevenZipWriterOptions(CompressionType.LZMA)),
            "tgz" => WriterFactory.OpenWriter(output, ArchiveType.Tar, new TarWriterOptions(CompressionType.GZip)),
            _ => WriterFactory.OpenWriter(output, ArchiveType.Zip, new ZipWriterOptions(CompressionType.Deflate)),
        };

        foreach ((string entryName, byte[] body) in entries)
        {
            using MemoryStream source = new(body);
            writer.Write(entryName, source, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// 写 tar + zstandard（<c>.tar.zst</c>）。
    ///
    /// SharpCompress 的 <see cref="TarWriterOptions"/> 不带 ZStandard，所以这里**自己包一层**：
    /// 让框架的 tar 写进 zstd 压缩流，压缩流再落到文件。
    /// （这里必须写全名：<c>TarWriter</c> 与 SharpCompress 的同名类型撞车。）
    /// </summary>
    private static void WriteTarZstd(string target, Dictionary<string, byte[]> entries)
    {
        using FileStream output = File.Create(target);
        using Stream compressed = new CompressionStream(output, 9);
        using System.Formats.Tar.TarWriter tar = new(
            compressed,
            System.Formats.Tar.TarEntryFormat.Pax,
            leaveOpen: true);

        foreach ((string entryName, byte[] body) in entries)
        {
            System.Formats.Tar.PaxTarEntry entry = new(
                System.Formats.Tar.TarEntryType.RegularFile,
                entryName)
            {
                DataStream = new MemoryStream(body),
            };

            tar.WriteEntry(entry);
        }
    }

    private static void EnsureArchive(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            throw new ArgumentException($"压缩包不存在：{archivePath}");
        }
    }

    private static bool IsSevenZip(string archivePath)
    {
        return archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? innerPath)
    {
        return (innerPath ?? "").Replace('\\', '/').Trim('/');
    }
}
