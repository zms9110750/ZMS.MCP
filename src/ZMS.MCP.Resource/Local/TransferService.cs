using ZMS.MCP.Resource.Credentials;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Local;

/// <summary>
/// 移动与复制。
///
/// 目标**没东西**时直接做；目标**已经有东西**时（合并文件夹、覆盖文件）先做一遍**目录验证**：
/// 把"会合并的目录"和"会冲突的文件"逐个列出来，各要**它自己**的凭据 —— 拿到才动手。
/// 凭据是一批（<c>toCookie</c>），**顺序无关**：每个对象拿自己的凭据去比，对得上就算数。
/// 冲突太多就不列凭据，让用户来处理 —— 这个工具给不了那个授权。
/// </summary>
public static class TransferService
{
    /// <summary>冲突的文件超过它，就带上"建议用户手动移动"。</summary>
    public const int WarnAbove = 15;

    /// <summary>冲突的文件超过它，就不再逐个列凭据。</summary>
    public const int RefuseAbove = 50;

    /// <summary>把源挪到目标，源不再留着。</summary>
    public static string Move(LocalAddress from, string toPath, string? cookie, IReadOnlyList<string>? toCookie)
    {
        return Run(from.InnerPath, toPath, cookie, toCookie, move: true);
    }

    /// <summary>把源复制到目标，源留着。跨存储搬东西就用它（FTP 下载 / 上传）。</summary>
    public static string Copy(LocalAddress from, string toPath, IReadOnlyList<string>? toCookie)
    {
        return Run(from.InnerPath, toPath, cookie: null, toCookie, move: false);
    }

    private static string Run(string from, string toPath, string? cookie, IReadOnlyList<string>? toCookie, bool move)
    {
        string to = Path.GetFullPath(toPath);
        if (string.Equals(Path.GetFullPath(from), to, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("源和目标是一个路径。");
        }

        Verification plan = Plan(from, to);

        if (plan.IsEmpty)
        {
            Perform(from, to, move);
            return Header(move ? "已移动" : "已复制")
                + $"- 源：{from}\n"
                + $"- 目标：{to}\n";
        }

        // 跨卷（或跨存储）的移动 = 拷过去 + 删源，要先确认真看过源
        if (move && cookie != null && !Cookie.Matches(cookie, FromCookie(from)))
        {
            return $"# 没执行：源那份凭据对不上\n- {from}\n"
                + $"- 现在算出来：`{FromCookie(from)}` —— 这期间它被改过。\n";
        }

        string? missing = Missing(plan, toCookie);
        if (missing != null)
        {
            return Report(plan, from, to, move, missing);
        }

        Perform(from, to, move);
        return Header(move ? "已移动" : "已复制")
            + $"- 源：{from}\n"
            + $"- 目标：{to}\n"
            + (plan.MergedDirectories.Count > 0 ? $"- 合并的目录：{plan.MergedDirectories.Count} 个\n" : "")
            + (plan.ConflictingFiles.Count > 0 ? $"- 覆盖的文件：{plan.ConflictingFiles.Count} 个\n" : "");
    }

    /// <summary>目录验证：目标里哪几个目录会合并、哪几个文件会被替换。</summary>
    private static Verification Plan(string from, string to)
    {
        List<string> merged = [];
        List<string> conflicting = [];

        if (File.Exists(from))
        {
            if (Directory.Exists(to))
            {
                throw new ArgumentException($"目标是目录：{to}（源是文件）");
            }

            if (File.Exists(to))
            {
                conflicting.Add(to);
            }

            return new(merged, conflicting);
        }

        if (!Directory.Exists(from))
        {
            throw new ArgumentException($"不存在：{from}");
        }

        if (File.Exists(to))
        {
            throw new ArgumentException($"源是目录、目标是文件：{to}");
        }

        if (!Directory.Exists(to))
        {
            return new(merged, conflicting);
        }

        merged.Add(to);
        Collect(from, to, merged, conflicting);
        return new(merged, conflicting);
    }

    private static void Collect(string from, string to, List<string> merged, List<string> conflicting)
    {
        foreach (string entry in Directory.GetFileSystemEntries(from))
        {
            string target = Path.Combine(to, Path.GetFileName(entry));

            if (File.Exists(target))
            {
                // 撞上文件：不管源这边是文件还是目录，都是"会被替换掉"
                conflicting.Add(target);
                continue;
            }

            if (Directory.Exists(entry) && Directory.Exists(target))
            {
                merged.Add(target);
                Collect(entry, target, merged, conflicting);
            }
        }
    }

    /// <summary>缺哪个对象的凭据；都齐了返回 <c>null</c>。</summary>
    private static string? Missing(Verification plan, IReadOnlyList<string>? toCookie)
    {
        if (toCookie is null || toCookie.Count == 0)
        {
            return plan.MergedDirectories.Count > 0 ? plan.MergedDirectories[0] : plan.ConflictingFiles[0];
        }

        foreach (string directory in plan.MergedDirectories)
        {
            if (!Accepts(toCookie, Cookie.Of(directory, Size(directory), Directory.GetLastWriteTimeUtc(directory))))
            {
                return directory;
            }
        }

        foreach (string file in plan.ConflictingFiles)
        {
            if (!Accepts(toCookie, Cookie.Of(file, new FileInfo(file).Length, File.GetLastWriteTimeUtc(file))))
            {
                return file;
            }
        }

        return null;
    }

    private static bool Accepts(IReadOnlyList<string> cookies, string current)
    {
        return cookies.Any(candidate => Cookie.Matches(candidate, current));
    }

    private static string Report(Verification plan, string from, string to, bool move, string missing)
    {
        StringBuilder builder = new();
        builder.AppendLine("# 目标已有内容（未执行）");
        builder.AppendLine($"- 源：{from}");
        builder.AppendLine($"- 目标：{to}");
        builder.AppendLine();

        if (plan.ConflictingFiles.Count > RefuseAbove)
        {
            builder.AppendLine($"有 {plan.ConflictingFiles.Count} 个文件路径冲突。"
                + "如果要覆盖，请让用户来处理 —— 你无法从这个工具得到授权。");
            return builder.ToString();
        }

        if (plan.MergedDirectories.Count > 0)
        {
            builder.AppendLine($"## 会合并的目录（{plan.MergedDirectories.Count} 个）");
            foreach (string directory in plan.MergedDirectories.Take(WarnAbove))
            {
                builder.AppendLine($"- {directory}");
                builder.AppendLine($"  cookie: `{Cookie.Of(directory, Size(directory), Directory.GetLastWriteTimeUtc(directory))}`");
            }

            builder.AppendLine();
        }

        if (plan.ConflictingFiles.Count > 0)
        {
            builder.AppendLine($"## 会被替换的文件（{plan.ConflictingFiles.Count} 个）");
            foreach (string file in plan.ConflictingFiles.Take(WarnAbove))
            {
                builder.AppendLine($"- {file}");
                builder.AppendLine($"  cookie: `{Cookie.Of(file, new FileInfo(file).Length, File.GetLastWriteTimeUtc(file))}`");
            }

            builder.AppendLine();
        }

        builder.AppendLine($"- 还缺这个对象的凭据：{missing}");
        builder.AppendLine();

        if (plan.ConflictingFiles.Count > WarnAbove)
        {
            builder.AppendLine($"冲突太多（{plan.ConflictingFiles.Count} 个），建议由用户手动{(move ? "移动" : "复制")}。");
        }
        else
        {
            builder.AppendLine("请确认意图；如果确认，就用上面这些 cookie 再来一次（`toCookie` 顺序无关）。");
        }

        return builder.ToString();
    }

    /// <summary>目标不存在时的整块搬运，或目标已存在时的逐项落定。</summary>
    private static void Perform(string from, string to, bool move)
    {
        if (File.Exists(from))
        {
            File.Copy(from, to, overwrite: true);
            if (move)
            {
                File.Delete(from);
            }

            return;
        }

        Directory.CreateDirectory(to);
        foreach (string entry in Directory.GetFileSystemEntries(from))
        {
            Perform(entry, Path.Combine(to, Path.GetFileName(entry)), move);
        }

        if (move)
        {
            Directory.Delete(from, recursive: true);
        }
    }

    private static string FromCookie(string path)
    {
        return Directory.Exists(path)
            ? Cookie.Of(path, Size(path), Directory.GetLastWriteTimeUtc(path))
            : Cookie.Of(path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
    }

    private static long Size(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", System.IO.SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private static string Header(string title)
    {
        return $"# {title}\n";
    }

    private sealed record Verification(List<string> MergedDirectories, List<string> ConflictingFiles)
    {
        /// <summary>目标那边什么都没有 —— 不需要任何凭据。</summary>
        public bool IsEmpty => MergedDirectories.Count == 0 && ConflictingFiles.Count == 0;
    }
}
