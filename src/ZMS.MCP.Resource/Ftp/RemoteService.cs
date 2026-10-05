using System.Text.RegularExpressions;
using FluentFTP;
using ZMS.MCP.Core.Credentials;
using ZMS.MCP.Resource.Local;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Ftp;

/// <summary>
/// 会话上的操作：读、列、传。
///
/// 远端**只做查看与搬运** —— 删除一律拒绝（远端没有回收站，删了不可恢复），
/// 写入也不假手（要往远端放东西就用 <c>copy</c> 上传）。
/// </summary>
public static class RemoteService
{
    /// <summary>读一个远端文件（内容渲染与本地共用同一套规矩）。</summary>
    public static async Task<string> ReadAsync(
        SessionAddress address,
        int skipline,
        int offset,
        int length,
        int takeline,
        string? encoding,
        string? regex)
    {
        FtpSession open = FtpSessions.Require(address.Session);
        byte[] bytes;
        try
        {
            bytes = await open.Client.DownloadBytes(address.InnerPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ArgumentException($"远端读不了它：{address.InnerPath} —— {exception.Message}");
        }

        DateTimeOffset modified = await ModifiedAsync(open, address.InnerPath).ConfigureAwait(false);
        return ReadService.Render(
            $"{open.Display} {address.InnerPath}",
            bytes,
            modified,
            skipline,
            offset,
            length,
            takeline,
            encoding,
            regex);
    }

    /// <summary>列一个远端目录（远端的 <c>list</c>）。</summary>
    public static async Task<string> ListAsync(
        SessionAddress address,
        int depth,
        int limit,
        string? type,
        string? meta,
        string? regex)
    {
        if (depth < 0)
        {
            throw new ArgumentException("depth 不能是负数（0 = 只当前文件夹）。");
        }

        string[] wanted = ListService.ParseMeta(meta);
        string kind = (type ?? "").Trim().ToLowerInvariant();
        if (kind.Length > 0 && kind is not ("file" or "dir"))
        {
            throw new ArgumentException($"type 只认 file / dir，给的是：{type}");
        }

        Regex? filter = null;
        if (!string.IsNullOrWhiteSpace(regex))
        {
            try
            {
                filter = new Regex(regex!);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"正则写错了：{exception.Message}");
            }
        }

        FtpSession open = FtpSessions.Require(address.Session);

        // 先看看它到底是个文件还是目录
        bool isDirectory;
        try
        {
            isDirectory = await open.Client.DirectoryExists(address.InnerPath).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ArgumentException($"远端看不了它：{address.InnerPath} —— {exception.Message}");
        }

        if (!isDirectory)
        {
            StringBuilder one = new();
            one.AppendLine($"# {open.Display} {address.InnerPath}");
            one.AppendLine("- 类型：文件（远端）");

            long size = 0;
            try
            {
                size = await open.Client.GetFileSize(address.InnerPath).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 拿不到就当 0
            }

            DateTimeOffset modified = await ModifiedAsync(open, address.InnerPath).ConfigureAwait(false);
            one.AppendLine($"- 大小：{size} 字节");
            one.AppendLine($"- 修改时间：{modified:O}");
            one.AppendLine("- （远端读取不发凭据 —— 远端上的改动本来也不接受凭据。）");
            return one.ToString();
        }

        StringBuilder body = new();
        bool truncated;
        try
        {
            truncated = await WalkAsync(open, address.InnerPath, 0, depth, limit, kind, wanted, filter, body).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ArgumentException($"远端列不了它：{address.InnerPath} —— {exception.Message}");
        }

        StringBuilder output = new();
        output.AppendLine($"# {open.Display} {address.InnerPath}");
        output.AppendLine("- 类型：目录（远端）");
        output.AppendLine();

        if (!truncated && output.Length + body.Length <= ReadService.DeliveryLimit)
        {
            output.Append(body);
            output.AppendLine();
            output.AppendLine("- （远端读取不发凭据。）");
            return output.ToString();
        }

        output.Append(body);
        output.AppendLine();
        output.AppendLine("- **被截断了 → 不给凭据**（凭据只在完全穷举时才给）");
        return output.ToString();
    }

    /// <summary>
    /// 跨存储搬东西：FTP → 本地是下载，本地 → FTP 是上传。
    ///
    /// **执行前先做目录验证**：查清哪几个文件夹会合并、哪几个文件会被覆盖 ——
    /// 两者都要**它自己**的 cookie，没给就拒绝。
    /// 目标在**远端**时拿不到凭据（远端的读取不发 cookie），所以那种「需要凭据的工作」直接拒绝。
    /// </summary>
    public static async Task<string> CopyAsync(Address from, Address to, IReadOnlyList<string>? toCookie)
    {
        SessionAddress? sessionFrom = from as SessionAddress;
        SessionAddress? sessionTo = to as SessionAddress;
        FtpSession? source = sessionFrom == null ? null : FtpSessions.Require(sessionFrom.Session);
        FtpSession? destination = sessionTo == null ? null : FtpSessions.Require(sessionTo.Session);

        string toPath = to is LocalAddress local ? Path.GetFullPath(local.InnerPath) : to.InnerPath;

        (List<string> sourceFiles, List<string> sourceFolders, bool sourceIsFolder) =
            await EntriesAsync(from.InnerPath, source).ConfigureAwait(false);
        (List<string> targetFiles, List<string> targetFolders, bool _) =
            await EntriesAsync(toPath, destination).ConfigureAwait(false);

        List<string> merged = [.. sourceFolders.Intersect(targetFolders, StringComparer.OrdinalIgnoreCase)];
        List<string> overwritten = [.. sourceFiles.Intersect(targetFiles, StringComparer.OrdinalIgnoreCase)];

        if (merged.Count > 0 || overwritten.Count > 0)
        {
            // 目标在远端：远端读取不发 cookie，拿不到这些对象的凭据 —— 这一类工作不能做
            if (destination != null)
            {
                return "# 没执行：目标在远端、已经有东西\n"
                    + $"- 目标（远端）：{toPath}\n"
                    + $"- 会合并的文件夹：{merged.Count} 个；会被覆盖的文件：{overwritten.Count} 个\n"
                    + "- 远端读取不发 cookie，拿不到它们自己的凭据 —— 这一类工作不能做，请用户手动处理。\n";
            }

            return Refuse(toPath, merged, overwritten, toCookie);
        }

        try
        {
            await TransferAsync(from.InnerPath, toPath, source, destination, sourceIsFolder).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ArgumentException($"搬运没成：{exception.Message}");
        }

        return $"# 已复制\n- 源：{(from is SessionAddress sourceSession ? $"{source!.Display} {sourceSession.InnerPath}" : from.InnerPath)}\n"
            + $"- 目标：{(to is SessionAddress targetSession ? $"{destination!.Display} {targetSession.InnerPath}" : toPath)}\n";
    }

    /// <summary>缺凭据时的清单：会合并的文件夹、会覆盖的文件，各附它自己的 cookie。</summary>
    private static string Refuse(string toPath, List<string> merged, List<string> overwritten, IReadOnlyList<string>? toCookie)
    {
        StringBuilder report = new();
        report.AppendLine("# 目标已有内容（未执行）");
        report.AppendLine($"- 目标：{toPath}");
        report.AppendLine();

        if (overwritten.Count > TransferService.RefuseAbove)
        {
            report.AppendLine($"有 {overwritten.Count} 个文件路径冲突。"
                + "如果要覆盖，请让用户来处理 —— 你无法从这个工具得到授权。");
            return report.ToString();
        }

        if (merged.Count > 0)
        {
            report.AppendLine($"## 会合并的文件夹（{merged.Count} 个）");
            foreach (string relative in merged.Take(TransferService.WarnAbove))
            {
                string full = Path.Combine(toPath, relative.Replace('/', Path.DirectorySeparatorChar));
                report.AppendLine($"- {full}");
                report.AppendLine($"  cookie: `{TargetCookie(full)}`");
            }

            report.AppendLine();
        }

        if (overwritten.Count > 0)
        {
            report.AppendLine($"## 会被覆盖的文件（{overwritten.Count} 个）");
            foreach (string relative in overwritten.Take(TransferService.WarnAbove))
            {
                string full = Path.Combine(toPath, relative.Replace('/', Path.DirectorySeparatorChar));
                report.AppendLine($"- {full}");
                report.AppendLine($"  cookie: `{TargetCookie(full)}`");
            }

            report.AppendLine();
        }

        List<string> missing = [.. merged.Concat(overwritten).Where(relative =>
        {
            string full = Path.Combine(toPath, relative.Replace('/', Path.DirectorySeparatorChar));
            string current = TargetCookie(full);
            return toCookie is null || !toCookie.Any(candidate => Cookie.Matches(candidate, current));
        })];

        report.AppendLine($"- 还缺 {missing.Count} 个凭据" + (missing.Count > 0 ? $"（例如 {missing[0]}）" : ""));
        report.AppendLine();
        report.AppendLine(overwritten.Count > TransferService.WarnAbove
            ? $"冲突太多（{overwritten.Count} 个），建议由用户手动处理。"
            : "请确认意图；如果确认，就用上面这些 cookie 再来一次（`toCookie` 顺序无关）。");
        return report.ToString();
    }

    /// <summary>目标那一侧、某个已有对象的凭据（只有本地算得出）。</summary>
    private static string TargetCookie(string fullPath)
    {
        return Directory.Exists(fullPath)
            ? Cookie.Of(fullPath, Size(fullPath), Directory.GetLastWriteTimeUtc(fullPath))
            : Cookie.Of(fullPath, new FileInfo(fullPath).Length, File.GetLastWriteTimeUtc(fullPath));
    }

    private static long Size(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", System.IO.SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    /// <summary>把一侧的东西列成相对路径（文件、文件夹各一份），并说明它本身是不是文件夹。</summary>
    private static async Task<(List<string> Files, List<string> Folders, bool IsFolder)> EntriesAsync(
        string path,
        FtpSession? session)
    {
        List<string> files = [];
        List<string> folders = [];

        if (session == null)
        {
            if (File.Exists(path))
            {
                files.Add(Path.GetFileName(path));
                return (files, folders, false);
            }

            if (!Directory.Exists(path))
            {
                return (files, folders, false);
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", System.IO.SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(path, entry).Replace('\\', '/');
                if (Directory.Exists(entry))
                {
                    folders.Add(relative);
                }
                else
                {
                    files.Add(relative);
                }
            }

            return (files, folders, true);
        }

        try
        {
            if (await session.Client.FileExists(path).ConfigureAwait(false))
            {
                files.Add(path[(path.LastIndexOf('/') + 1)..]);
                return (files, folders, false);
            }

            foreach (FtpListItem item in await session.Client.GetListing(path, FtpListOption.Recursive).ConfigureAwait(false))
            {
                string relative = item.FullName.StartsWith(path, StringComparison.OrdinalIgnoreCase)
                    ? item.FullName[path.Length..].TrimStart('/')
                    : item.Name;

                if (relative.Length == 0)
                {
                    continue;
                }

                if (item.Type == FtpObjectType.Directory)
                {
                    folders.Add(relative);
                }
                else
                {
                    files.Add(relative);
                }
            }
        }
        catch (Exception)
        {
            // 目标不存在（或看不了）就当它是空的
        }

        return (files, folders, true);
    }

    /// <summary>逐项搬（文件夹会被递归）。</summary>
    private static async Task TransferAsync(
        string fromPath,
        string toPath,
        FtpSession? source,
        FtpSession? destination,
        bool isFolder)
    {
        if (!isFolder)
        {
            if (source != null && destination != null)
            {
                await using Stream stream = await source.Client.OpenRead(fromPath).ConfigureAwait(false);
                await destination.Client.UploadStream(stream, toPath, FtpRemoteExists.Overwrite).ConfigureAwait(false);
            }
            else if (source != null)
            {
                await source.Client.DownloadFile(toPath, fromPath, FtpLocalExists.Overwrite).ConfigureAwait(false);
            }
            else
            {
                await destination!.Client.UploadFile(fromPath, toPath, FtpRemoteExists.Overwrite).ConfigureAwait(false);
            }

            return;
        }

        if (destination != null)
        {
            await destination.Client.CreateDirectory(toPath).ConfigureAwait(false);
        }
        else
        {
            Directory.CreateDirectory(toPath);
        }

        if (source == null)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(fromPath))
            {
                bool childIsFolder = Directory.Exists(entry);
                await TransferAsync(
                    entry,
                    Combine(toPath, Path.GetFileName(entry), destination != null),
                    null,
                    destination,
                    childIsFolder).ConfigureAwait(false);
            }

            return;
        }

        foreach (FtpListItem item in await source.Client.GetListing(fromPath).ConfigureAwait(false))
        {
            await TransferAsync(
                item.FullName,
                Combine(toPath, item.Name, destination != null),
                source,
                destination,
                item.Type == FtpObjectType.Directory).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 往远端写一份（<c>write</c> 打到会话上）。
    ///
    /// **只做「目标还不存在」这一种**：远端重叠写需要凭据，而**远端读取不发 cookie**，
    /// 所以「需要凭据的工作」在远端一律拒绝，交给用户。
    /// </summary>
    public static async Task<string> WriteAsync(SessionAddress address, string content, string? encoding)
    {
        FtpSession open = FtpSessions.Require(address.Session);

        if (await open.Client.FileExists(address.InnerPath).ConfigureAwait(false))
        {
            return "# 没执行：远端已经有这个文件\n"
                + $"- {open.Display} {address.InnerPath}\n"
                + "- 远端重叠写属于「需要凭据的工作」，而远端读取不发 cookie —— 拿不到它的凭据，做不了，请用户手动处理。\n";
        }

        Encoding used = encoding == null ? new UTF8Encoding(false) : Encoding.GetEncoding(encoding.Trim());
        byte[] bytes = used.GetBytes(content);

        using MemoryStream payload = new(bytes);
        await open.Client.UploadStream(payload, address.InnerPath, FtpRemoteExists.Overwrite).ConfigureAwait(false);

        return $"# 已写入（远端）\n- {open.Display} {address.InnerPath}\n"
            + $"- 大小：{bytes.LongLength} 字节，编码：{used.WebName}\n"
            + "- 远端不发凭据，所以没有「下一个凭据」。\n";
    }

    /// <summary>
    /// 移动一份（<c>move</c> 打到会话上）。
    ///
    /// 移动 = 「搬过去 + 删掉源」。**远端删除是拒绝的**（没有回收站），所以只有
    /// **本地 → 远端**这一路能做（删的是本地源，走回收站），而且目标远端必须还空着。
    /// </summary>
    public static async Task<string> MoveAsync(
        Address from,
        Address to,
        string? cookie,
        IReadOnlyList<string>? toCookie)
    {
        if (from is not LocalAddress local)
        {
            throw new ArgumentException(
                "远端的移动做不了：move 等于「搬过去 + 删掉源」，而远端删除是拒绝的（远端没有回收站）。"
                + "请用 `copy` 搬，源那边由用户手动删。");
        }

        if (to is not SessionAddress sessionTo)
        {
            throw new ArgumentException("这一路没有实现。");
        }

        FtpSession open = FtpSessions.Require(sessionTo.Session);
        if (await open.Client.FileExists(sessionTo.InnerPath).ConfigureAwait(false)
            || await open.Client.DirectoryExists(sessionTo.InnerPath).ConfigureAwait(false))
        {
            return "# 没执行：远端已经有东西\n"
                + $"- {open.Display} {sessionTo.InnerPath}\n"
                + "- 覆盖 / 合并属于「需要凭据的工作」，而远端读取不发 cookie —— 拿不到它的凭据，做不了，请用户手动处理。\n";
        }

        if (string.IsNullOrWhiteSpace(cookie))
        {
            return "# 会发生什么（未执行）\n"
                + $"- 源：{local.InnerPath}\n"
                + $"- 目标（远端）：{open.Display} {sessionTo.InnerPath}\n"
                + "- 跨存储的移动 = 搬上去 + **删掉本地源**（删源走回收站），删源要**源那份凭据**。\n"
                + $"- 源现在的凭据：`{TargetCookie(Path.GetFullPath(local.InnerPath))}`\n"
                + "- （这不算失败：缺凭据是两段式的第一段。）\n";
        }

        string moved = await CopyAsync(local, sessionTo, toCookie).ConfigureAwait(false);
        if (!moved.StartsWith("# 已复制", StringComparison.Ordinal))
        {
            return moved;
        }

        string removed = DeleteService.Run(local, cookie);
        if (!removed.Contains("已删除", StringComparison.Ordinal))
        {
            return $"# 只搬了一半\n- 已经复制到远端：{sessionTo.InnerPath}\n"
                + $"- 但删本地源没成：\n{removed}\n";
        }

        return $"# 已移动\n- 源（已删入回收站）：{local.InnerPath}\n"
            + $"- 目标（远端）：{open.Display} {sessionTo.InnerPath}\n";
    }

    private static string Combine(string parent, string name, bool remote)
    {
        return remote ? parent.TrimEnd('/') + "/" + name : Path.Combine(parent, name);
    }

    private static async Task<bool> WalkAsync(
        FtpSession open,
        string path,
        int level,
        int depth,
        int limit,
        string kind,
        string[] wanted,
        Regex? filter,
        StringBuilder body)
    {
        FtpListItem[] entries = await open.Client.GetListing(path).ConfigureAwait(false);
        Array.Sort(entries, (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        int allowed = ListService.LimitForLevel(level, limit);
        bool truncated = entries.Length > allowed;
        int shown = 0;

        foreach (FtpListItem entry in entries)
        {
            bool isDirectory = entry.Type == FtpObjectType.Directory;
            if (kind == "file" && isDirectory)
            {
                continue;
            }

            if (kind == "dir" && !isDirectory)
            {
                continue;
            }

            if (filter != null && !filter.IsMatch(entry.Name))
            {
                continue;
            }

            if (shown >= allowed)
            {
                truncated = true;
                break;
            }

            shown++;
            body.AppendLine($"{new string(' ', level * 2)}{entry.Name}{(isDirectory ? "/" : "")}{Describe(entry, wanted)}");

            if (isDirectory && level < depth)
            {
                if (await WalkAsync(open, entry.FullName, level + 1, depth, limit, kind, wanted, filter, body).ConfigureAwait(false))
                {
                    truncated = true;
                }
            }
            else if (isDirectory)
            {
                truncated = true;
            }
        }

        return truncated;
    }

    private static string Describe(FtpListItem entry, string[] wanted)
    {
        if (wanted.Length == 0)
        {
            return "";
        }

        List<string> parts = [];
        foreach (string attribute in wanted)
        {
            switch (attribute)
            {
                case "size":
                    parts.Add(entry.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "modified":
                    parts.Add(entry.Modified.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "created":
                    parts.Add("（远端没有这一项）");
                    break;
                case "count":
                    parts.Add("（远端不数这一项）");
                    break;
            }
        }

        return parts.Count == 0 ? "" : "  [" + string.Join(", ", parts) + "]";
    }

    private static async Task<DateTimeOffset> ModifiedAsync(FtpSession open, string path)
    {
        try
        {
            return await open.Client.GetModifiedTime(path).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }
}
