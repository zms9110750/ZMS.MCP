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

    /// <summary>
    /// 单文件落盘：写同目录临时文件再原子替换（同卷 Move 带覆盖）。
    /// 目标已存在时用**它自己的编码**写回。
    /// </summary>
    public static void WriteAtomic(string filePath, string content, DetectedEncoding? encoding = null)
    {
        DetectedEncoding target = encoding ?? (File.Exists(filePath) ? DetectEncoding(filePath) : NewFileEncoding());
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = filePath + ".zms-tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, content, target.Encoding);
        File.Move(temporary, filePath, overwrite: true);
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
