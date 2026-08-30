using System.IO.Compression;
using System.Text;

namespace zms9110750.ZMS_MCP.Cli.Tools;

/// <summary>
/// 压缩包操作工具。支持 ZIP / .nupkg 格式。
/// 可列目录、查看文件内容、解压到指定目录。
/// </summary>
[McpServerToolType]
public static partial class ArchiveTools
{
    [McpServerTool(
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "操作压缩包文件（ZIP / .nupkg）。\n" +
        "- 路径为空：列出包内文件树。\n" +
        "- 路径指向一个文件：显示该文件的内容。\n" +
        "- 指定了解压路径：将包（或包内指定路径）解压到目标目录。")]
    public static async Task<string> Archive(
        [Description("Compressed archive file path, e.g. .zip or .nupkg")] string archivePath,
        [Description("Path inside archive. Empty=tree; non-empty=view/extract that entry")] string path = "",
        [Description("Extract destination. When set, performs extraction")] string extractPath = "")
    {
        try
        {
            if (!File.Exists(archivePath))
            {
                return $"文件不存在: {archivePath}";
            }

            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            // 模式 3：解压操作
            if (!string.IsNullOrEmpty(extractPath))
            {
                return await ExtractAsync(archive, archivePath, path, extractPath);
            }

            // 模式 2：查看包内文件内容
            if (!string.IsNullOrEmpty(path))
            {
                return await ReadFileContentAsync(archive, path);
            }

            // 模式 1：列出包内目录树
            return ListArchiveTree(archive, archivePath);
        }
        catch (InvalidDataException)
        {
            return $"无法识别压缩包格式：{archivePath}。仅支持 ZIP 格式（含 .nupkg）。";
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }

    /// <summary>列出压缩包内所有文件/目录。</summary>
    private static string ListArchiveTree(ZipArchive archive, string archivePath)
    {
        var entries = archive.Entries
            .Select(e => new {
                e.FullName,
                e.Length,
                IsDir = e.FullName.EndsWith('/')
            })
            .OrderBy(e => e.FullName)
            .ToArray();

        if (entries.Length == 0)
        {
            return "压缩包为空。";
        }

        var totalSize = entries.Sum(e => e.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"# {Path.GetFileName(archivePath)}  —  {entries.Length} 项，{FormatSize(totalSize)}");
        sb.AppendLine();

        // 按目录分组，构建树
        var roots = new SortedSet<string>();
        foreach (var entry in entries)
        {
            var parts = entry.FullName.TrimEnd('/').Split('/');
            var path = "";
            for (int i = 0; i < parts.Length; i++)
            {
                var parent = path;
                path = path.Length > 0 ? $"{path}/{parts[i]}" : parts[i];
                if (roots.Add(path) && !entry.IsDir && i == parts.Length - 1)
                {
                    // 这是个文件，附加大小信息
                }
            }
        }

        // 直接用缩进列表展现
        foreach (var entry in entries)
        {
            var indent = entry.FullName.Count(c => c == '/');
            var prefix = new string(' ', indent * 2);
            var name = Path.GetFileName(entry.FullName.AsSpan().TrimEnd('/')).ToString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (entry.IsDir)
            {
                sb.AppendLine($"{prefix}{name}/");
            }
            else
            {
                sb.AppendLine($"{prefix}{name}  _{FormatSize(entry.Length)}_");
            }
        }

        return sb.ToString();
    }

    /// <summary>读取压缩包内指定文件的内容。</summary>
    private static async Task<string> ReadFileContentAsync(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path.Replace('\\', '/'));
        if (entry == null)
        {
            // 检查是否是目录
            var dirPrefix = path.TrimEnd('/') + "/";
            if (archive.Entries.Any(e => e.FullName.StartsWith(dirPrefix)))
            {
                return $"[目录] 包含 {archive.Entries.Count(e => e.FullName.StartsWith(dirPrefix))} 个文件。";
            }

            // 模糊匹配
            var candidates = archive.Entries
                .Where(e => e.FullName.Contains(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FullName)
                .Take(10)
                .ToArray();

            if (candidates.Length == 0)
            {
                return $"包内未找到路径: {path}";
            }

            return $"未找到精确匹配。相近路径：\n" + string.Join("\n", candidates.Select(c => $"  - {c}"));
        }

        using var reader = new StreamReader(entry.Open());
        var content = await reader.ReadToEndAsync();

        // 检测是否为二进制文件，如果是则只返回大小信息
        if (IsBinary(entry.Name))
        {
            return $"[二进制文件] {entry.Name}  ({FormatSize(entry.Length)})";
        }

        // 限制返回大小
        const int maxLength = 100_000;
        if (content.Length > maxLength)
        {
            content = content[..maxLength] + $"\n...（仅显示前 {maxLength} 字符，共 {content.Length} 字符）";
        }

        return content;
    }

    /// <summary>解压操作。</summary>
    private static async Task<string> ExtractAsync(ZipArchive archive, string archivePath, string? path, string extractPath)
    {
        if (string.IsNullOrWhiteSpace(extractPath))
        {
            return "解压路径不能为空。";
        }

        // 创建目标目录
        var targetDir = Path.GetFullPath(extractPath);
        Directory.CreateDirectory(targetDir);

        if (string.IsNullOrEmpty(path))
        {
            // 解压全部
            var count = 0;
            foreach (var entry in archive.Entries)
            {
                var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                // 防止路径穿越
                if (!destPath.StartsWith(targetDir, StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                    count++;
                }
            }
            return $"已解压 {count} 个文件到 {targetDir}。";
        }
        else
        {
            // 解压指定路径
            var normalized = path.Replace('\\', '/');
            var entries = archive.Entries
                .Where(e => e.FullName.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (entries.Length == 0)
            {
                return $"包内未找到 '{path}'。";
            }

            var count = 0;
            foreach (var entry in entries)
            {
                var relativePath = entry.FullName.StartsWith(normalized + "/")
                    ? entry.FullName[(normalized.Length + 1)..]
                    : entry.FullName;
                var destPath = Path.GetFullPath(Path.Combine(targetDir, relativePath));
                if (!destPath.StartsWith(targetDir, StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                    count++;
                }
            }
            return $"已解压 {count} 个文件到 {targetDir}。";
        }
    }

    /// <summary>通过文件扩展名判断是否为二进制。</summary>
    private static bool IsBinary(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch {
            ".dll" or ".exe" or ".pdb" or ".png" or ".jpg" or ".jpeg"
            or ".gif" or ".bmp" or ".ico" or ".pdf" or ".zip"
            or ".nupkg" or ".snupkg" or ".so" or ".dylib" => true,
            _ => false
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_000_000)
        {
            return $"{bytes / 1_000_000.0:F1} MB";
        }

        if (bytes >= 1_000)
        {
            return $"{bytes / 1_000.0:F1} KB";
        }

        return $"{bytes} B";
    }
}
