using System.Globalization;
using ZMS.MCP.Resource.Credentials;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Local;

/// <summary>
/// 列目录、或取一个文件的元数据。
///
/// - 指到**目录**：列树。**完全穷举**（没被 <c>depth</c>/<c>limit</c> 截断，输出也没超
///   <see cref="ReadService.DeliveryLimit"/> 字符）才给**目录树凭据**；穷举不完就没有。
/// - 指到**文件**：只给元数据 —— **这里就能拿到它的凭据**（覆盖它、删它都要用）。
/// </summary>
public static class ListService
{
    /// <summary>每层条目的硬上限。</summary>
    public const int MaxLimit = 200;

    /// <summary>认得的元数据属性名。</summary>
    private static readonly string[] KnownMeta = ["created", "modified", "size", "count"];

    /// <summary><c>limit = 0</c> 时按层递减：当前层 200、递归 1 层 100、2 层 50、3 层及以后 20。</summary>
    public static int LimitForLevel(int level, int limit)
    {
        if (limit > 0)
        {
            return Math.Min(limit, MaxLimit);
        }

        return level switch
        {
            0 => 200,
            1 => 100,
            2 => 50,
            _ => 20,
        };
    }

    /// <summary>列出来并渲染成 markdown。</summary>
    public static string Run(LocalAddress address, int depth, int limit, string? type, string? meta, string? regex)
    {
        if (depth < 0)
        {
            throw new ArgumentException("depth 不能是负数（0 = 只当前文件夹）。");
        }

        string[] wanted = ParseMeta(meta);
        string kind = (type ?? "").Trim().ToLowerInvariant();
        if (kind.Length > 0 && kind is not ("file" or "dir"))
        {
            throw new ArgumentException($"type 只认 file / dir，给的是：{type}");
        }

        System.Text.RegularExpressions.Regex? filter = null;
        if (!string.IsNullOrWhiteSpace(regex))
        {
            try
            {
                filter = new System.Text.RegularExpressions.Regex(regex!);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"正则写错了：{exception.Message}");
            }
        }

        string path = address.InnerPath;
        if (File.Exists(path))
        {
            return RenderFile(path, wanted);
        }

        if (!Directory.Exists(path))
        {
            throw new ArgumentException($"不存在：{path}");
        }

        StringBuilder body = new();
        bool complete = !Walk(path, 0, depth, limit, kind, wanted, filter, body);

        StringBuilder output = new();
        output.AppendLine($"# {path}");
        output.AppendLine("- 类型：目录");
        output.AppendLine();

        if (complete && output.Length + body.Length <= ReadService.DeliveryLimit)
        {
            output.Append(body);
            output.AppendLine();
            output.AppendLine($"- 目录树凭据：`{Cookie.Of(path, DirectorySize(path), Directory.GetLastWriteTimeUtc(path))}`");
            output.AppendLine($"- 条目总数：{Count(path)}");
            output.AppendLine($"- 总大小：{DirectorySize(path)} 字节");
            return output.ToString();
        }

        output.Append(body);
        output.AppendLine();
        output.AppendLine("- **被截断了 → 不给目录树凭据**（凭据只在完全穷举时才给）");
        return output.ToString();
    }

    /// <summary>递归列。返回"有没有被截断"。</summary>
    private static bool Walk(
        string directory,
        int level,
        int depth,
        int limit,
        string kind,
        string[] wanted,
        System.Text.RegularExpressions.Regex? filter,
        StringBuilder body)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException)
        {
            body.AppendLine($"{Indent(level)}（读不了这个目录）");
            return true;
        }

        Array.Sort(entries, StringComparer.OrdinalIgnoreCase);

        int allowed = LimitForLevel(level, limit);
        bool truncated = entries.Length > allowed;

        int shown = 0;
        foreach (string entry in entries)
        {
            bool isDirectory = Directory.Exists(entry);
            if (kind == "file" && isDirectory)
            {
                continue;
            }

            if (kind == "dir" && !isDirectory)
            {
                continue;
            }

            if (filter != null && !filter.IsMatch(Path.GetFileName(entry)))
            {
                continue;
            }

            if (shown >= allowed)
            {
                truncated = true;
                break;
            }

            shown++;
            body.AppendLine($"{Indent(level)}{Path.GetFileName(entry)}{(isDirectory ? "/" : "")}{Meta(entry, isDirectory, wanted)}");

            if (isDirectory && level < depth)
            {
                if (Walk(entry, level + 1, depth, limit, kind, wanted, filter, body))
                {
                    truncated = true;
                }
            }
            else if (isDirectory)
            {
                // 还有更深的一层没展开 —— 这就是截断
                truncated = true;
            }
        }

        return truncated;
    }

    private static string RenderFile(string path, string[] wanted)
    {
        FileInfo info = new(path);
        StringBuilder builder = new();
        builder.AppendLine($"# {path}");
        builder.AppendLine("- 类型：文件");
        builder.AppendLine($"- 大小：{info.Length} 字节");
        builder.AppendLine($"- 修改时间：{info.LastWriteTimeUtc:O}");
        builder.AppendLine($"- 创建时间：{info.CreationTimeUtc:O}");
        builder.AppendLine($"- 凭据：`{Cookie.Of(path, info.Length, info.LastWriteTimeUtc)}`");
        builder.AppendLine("（只看了元数据，没有读内容 —— 覆盖它、删它，有这个凭据就够。）");

        foreach (string attribute in wanted)
        {
            if (attribute == "size")
            {
                continue;
            }

            if (attribute == "modified")
            {
                continue;
            }

            if (attribute == "created")
            {
                continue;
            }

            if (attribute == "count")
            {
                builder.AppendLine("- 条目数：（文件没有这一项）");
            }
        }

        return builder.ToString();
    }

    private static string Meta(string path, bool isDirectory, string[] wanted)
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
                    parts.Add(isDirectory ? "" : new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture));
                    break;
                case "modified":
                    parts.Add(File.GetLastWriteTimeUtc(path).ToString("O", CultureInfo.InvariantCulture));
                    break;
                case "created":
                    parts.Add(File.GetCreationTimeUtc(path).ToString("O", CultureInfo.InvariantCulture));
                    break;
                case "count":
                    parts.Add(isDirectory ? Directory.GetFileSystemEntries(path).Length.ToString(CultureInfo.InvariantCulture) : "");
                    break;
            }
        }

        parts.RemoveAll(part => part.Length == 0);
        return parts.Count == 0 ? "" : "  [" + string.Join(", ", parts) + "]";
    }

    /// <summary>解析 <c>meta</c>：认不出的属性名报错。</summary>
    public static string[] ParseMeta(string? meta)
    {
        if (string.IsNullOrWhiteSpace(meta))
        {
            return [];
        }

        string[] parts = meta!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (!KnownMeta.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"认不出这个属性名：{part}（只认 {string.Join(" / ", KnownMeta)}）。");
            }
        }

        return [.. parts.Select(part => part.ToLowerInvariant())];
    }

    private static string Indent(int level)
    {
        return new string(' ', level * 2);
    }

    private static int Count(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Count();
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
