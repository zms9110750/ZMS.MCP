using System.Security.Cryptography;
using System.Text;

namespace ZMS.MCP.Csharp.Project;

/// <summary>文件编码的判定来源（决定写回时用什么编码）。</summary>
public enum EncodingSource
{
    /// <summary>新建文件：UTF-8 无 BOM。</summary>
    NewFile,

    /// <summary>来自最近的 <c>.editorconfig</c> 的 <c>charset</c>。</summary>
    EditorConfig,

    /// <summary>字节序标记。</summary>
    ByteOrderMark,

    /// <summary>无 BOM 但能按 UTF-8 严格解出来。</summary>
    StrictUtf8,
}

/// <summary>判定出来的编码。</summary>
public sealed record DetectedEncoding(Encoding Encoding, EncodingSource Source);

/// <summary>落盘前的可写性预检查结论。</summary>
public enum WriteProbe
{
    /// <summary>现在能写。</summary>
    Writable,

    /// <summary>被别的进程占用（保守判断：拿不到独占句柄就算）。</summary>
    Busy,

    /// <summary>文件带只读属性。</summary>
    ReadOnly,

    /// <summary>没有写权限。</summary>
    Denied,

    /// <summary>新建文件所需的目录不可写。</summary>
    DirectoryNotWritable,

    /// <summary>新建文件所需的目录还不存在（落盘时会建出来 —— 能写，但要让 agent 知道）。</summary>
    DirectoryWillBeCreated,
}

/// <summary>
/// 写文件通则（见需求文档）：原编码写回、新建用 UTF-8 无 BOM、单文件写临时文件后原子替换、
/// 落盘前可校验基线 hash。
/// </summary>
public static class FileWriter
{
    private const string Utf8BomName = "utf-8-bom";

    /// <summary>
    /// 按文档的判定顺序定编码：<c>.editorconfig</c> 的 <c>charset</c> → BOM → UTF-8 严格校验。
    /// 三者都不成立（无 BOM 且不是合法 UTF-8）时抛异常：**不猜**，拒绝写这个文件。
    /// </summary>
    public static DetectedEncoding DetectEncoding(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new DetectedEncoding(new UTF8Encoding(false), EncodingSource.NewFile);
        }

        byte[] bytes = File.ReadAllBytes(filePath);

        Encoding? fromEditorConfig = ReadEditorConfigCharset(filePath);
        if (fromEditorConfig != null)
        {
            return new DetectedEncoding(fromEditorConfig, EncodingSource.EditorConfig);
        }

        Encoding? fromBom = DetectBom(bytes);
        if (fromBom != null)
        {
            return new DetectedEncoding(fromBom, EncodingSource.ByteOrderMark);
        }

        if (IsStrictUtf8(bytes))
        {
            return new DetectedEncoding(new UTF8Encoding(false), EncodingSource.StrictUtf8);
        }

        throw new InvalidOperationException(
            $"无法判定编码，拒绝写入：{filePath}（没有 .editorconfig charset、没有 BOM，也不是合法 UTF-8 字节序列）");
    }

    /// <summary>读文件内容（用判定出来的编码）。</summary>
    public static string ReadAllText(string filePath, out DetectedEncoding encoding)
    {
        encoding = DetectEncoding(filePath);
        return File.ReadAllText(filePath, encoding.Encoding);
    }

    /// <summary>内容 hash，用于落盘前的基线校验。</summary>
    public static string ComputeHash(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return "";
        }

        byte[] bytes = File.ReadAllBytes(filePath);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>新建文件：UTF-8 无 BOM。</summary>
    public static DetectedEncoding NewFileEncoding()
    {
        return new DetectedEncoding(new UTF8Encoding(false), EncodingSource.NewFile);
    }

    /// <summary>替换被占用时的退避间隔（毫秒）：200 / 1000 / 3000，之后放弃并报"被占用"。</summary>
    internal static readonly int[] DefaultRetryDelays = [200, 1000, 3000];

    /// <summary>
    /// 单文件落盘：写同目录临时文件再原子替换（同卷 Move 带覆盖）。
    /// 目标已存在时用**它自己的编码**写回；
    /// **内容没变就不写**（省掉一次元数据事务，也不搅动文件时间戳）；
    /// 替换被别的进程占用时按 <see cref="DefaultRetryDelays"/> 退避重试。
    /// </summary>
    /// <returns>真的写了返回 true；内容相同被跳过返回 false。</returns>
    public static bool WriteAtomic(string filePath, string content, DetectedEncoding? encoding = null)
    {
        return WriteAtomic(filePath, content, encoding, DefaultRetryDelays);
    }

    /// <summary>
    /// 落盘主体。探测编码、写临时文件、原子替换**任一步**都可能因文件被占用而失败，
    /// 所以整段一起退避重试；重试间隔可注入，便于单测不必真等 4 秒。
    /// </summary>
    internal static bool WriteAtomic(
        string filePath,
        string content,
        DetectedEncoding? encoding,
        IReadOnlyList<int> retryDelays)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return WriteOnce(filePath, content, encoding);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt < retryDelays.Count)
                {
                    Thread.Sleep(retryDelays[attempt]);
                    continue;
                }

                CleanUpTemporaries(filePath);
                throw new InvalidOperationException(
                    $"写入失败（已重试 {retryDelays.Count + 1} 次）：{filePath} —— "
                    + $"常见原因是文件正被别的进程占用（编辑器 / 杀软 / 索引服务）；原始错误：{exception.Message}",
                    exception);
            }
        }
    }

    /// <summary>写一次：内容没变直接跳过，否则写临时文件再原子替换。失败时清掉自己的临时文件。</summary>
    private static bool WriteOnce(string filePath, string content, DetectedEncoding? encoding)
    {
        DetectedEncoding target = encoding ?? (File.Exists(filePath) ? DetectEncoding(filePath) : NewFileEncoding());

        // 内容没变就不写：省掉一次"建临时文件 + 改名 + 删旧文件"的元数据事务，
        // 也让"没改动"的文件时间戳保持原样
        if (File.Exists(filePath) &&
            string.Equals(File.ReadAllText(filePath, target.Encoding), content, StringComparison.Ordinal))
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = filePath + ".zms-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content, target.Encoding);
            File.Move(temporary, filePath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>清掉这个文件可能残留的临时文件（反复失败或崩溃留下的）。</summary>
    private static void CleanUpTemporaries(string filePath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";
        string pattern = Path.GetFileName(filePath) + ".zms-tmp-*";
        try
        {
            foreach (string stale in Directory.GetFiles(directory, pattern))
            {
                TryDelete(stale);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 目录都读不了就算了
        }
    }

    /// <summary>
    /// 预检查：这个文件现在能不能写。
    /// **保守判断** —— 拿不到独占句柄就算"被占用"（对方允许删除时其实能替换，所以可能误报），
    /// 但不会漏报"写不进去"。
    /// </summary>
    public static WriteProbe Probe(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                // 目录不存在：落盘时会 CreateDirectory 建出来 —— 能写，但要如实报出去（提示级，不是错误）
                return WriteProbe.DirectoryWillBeCreated;
            }

            return IsDirectoryWritable(directory) ? WriteProbe.Writable : WriteProbe.DirectoryNotWritable;
        }

        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReadOnly) != 0)
            {
                return WriteProbe.ReadOnly;
            }

            using FileStream probe = new(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return WriteProbe.Writable;
        }
        catch (UnauthorizedAccessException)
        {
            return WriteProbe.Denied;
        }
        catch (IOException)
        {
            return WriteProbe.Busy;
        }
    }

    /// <summary>目录能不能写：建一个空探测文件再删掉。</summary>
    private static bool IsDirectoryWritable(string directory)
    {
        string probe = Path.Combine(directory, ".zms-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // 能建出来就说明目录可写
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 临时文件也删不掉就算了，别把真正的失败盖掉
        }
    }

    /// <summary>
    /// 最近的 <c>.editorconfig</c> 里的 <c>charset</c>。
    /// 说明：这里不按 section（<c>[*.cs]</c>）做精细匹配，只看最近一份里有没有 <c>charset</c> 键 ——
    /// 够用且不会因为 section 规则不同而误判成"没有配置"。
    /// </summary>
    private static Encoding? ReadEditorConfigCharset(string filePath)
    {
        string? configPath = FindNearestUpwards(Path.GetDirectoryName(filePath) ?? ".", ".editorconfig");
        if (configPath == null)
        {
            return null;
        }

        foreach (string rawLine in File.ReadAllLines(configPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('['))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (!line[..separator].Trim().Equals("charset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = line[(separator + 1)..].Trim().ToLowerInvariant();
            if (value == Utf8BomName)
            {
                return new UTF8Encoding(true);
            }

            if (value is "utf-8" or "utf8")
            {
                return new UTF8Encoding(false);
            }

            if (value is "utf-16le" or "utf-16be" or "latin1")
            {
                return value switch
                {
                    "utf-16le" => new UnicodeEncoding(false, true),
                    "utf-16be" => new UnicodeEncoding(true, true),
                    _ => Encoding.Latin1,
                };
            }
        }

        return null;
    }

    private static Encoding? DetectBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new UnicodeEncoding(false, true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new UnicodeEncoding(true, true);
        }

        return null;
    }

    private static bool IsStrictUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>从某目录向上找最近的一个同名文件，到盘根为止。</summary>
    internal static string? FindNearestUpwards(string startDirectory, string fileName)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(startDirectory));
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
