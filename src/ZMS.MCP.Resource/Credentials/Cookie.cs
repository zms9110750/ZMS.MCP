namespace ZMS.MCP.Resource.Credentials;

/// <summary>
/// 凭据（<c>cookie</c>）：一次读取换来的短标识，改到已有内容时要带回来。
///
/// 分**两段**：<c>元数据段</c>（路径 + 大小 + 修改时间）与 <c>内容段</c>（内容 + 编码）。
/// 算它**最少要知道路径 + 大小**，所以只看元数据就能拿到凭据（例如 <c>list</c> 指到一个文件时）；
/// **读过全文**时才会多出内容段。
///
/// 两段分开放，是为了让两档凭据**能互相校验**：比的时候元数据段必须一致，
/// **两边都有内容段时**内容段也必须一致 —— 只读过元数据的那一档照样能验过。
/// 修改时间进了元数据段：哪怕内容一字没动，只要被人改过，就对不上。
/// </summary>
public static class Cookie
{
    /// <summary>每一段的长度（十六进制位数）。</summary>
    public const int Length = 16;

    /// <summary>两段之间的分隔符。</summary>
    public const char Separator = '-';

    /// <summary>按「路径 + 大小 + 修改时间（+ 内容与编码）」算一个凭据。</summary>
    /// <param name="content">读过全文才有；没读就是 <c>null</c>，这时凭据只有元数据段。</param>
    public static string Of(string path, long size, DateTimeOffset modified, string? content = null, string? encoding = null)
    {
        string metadata = Fingerprint(
            $"{NormalizePath(path)}\n{size.ToString(CultureInfo.InvariantCulture)}\n{modified.UtcTicks.ToString(CultureInfo.InvariantCulture)}");

        if (content == null)
        {
            return metadata;
        }

        return metadata + Separator + Fingerprint($"{encoding ?? ""}\n{content}");
    }

    /// <summary>两个凭据对不对得上（去空白、大小写不敏感；内容段只有两边都有时才比）。</summary>
    public static bool Matches(string? expected, string? actual)
    {
        (string leftMetadata, string leftContent) = Split(expected);
        (string rightMetadata, string rightContent) = Split(actual);

        if (leftMetadata.Length == 0 || rightMetadata.Length == 0)
        {
            return false;
        }

        if (!string.Equals(leftMetadata, rightMetadata, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (leftContent.Length == 0 || rightContent.Length == 0)
        {
            return true;
        }

        return string.Equals(leftContent, rightContent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>规范化路径：全称、去掉尾部分隔符（根目录如 <c>C:\</c> 保留）。</summary>
    public static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length > 3
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    /// <summary>SHA256 的前 <see cref="Length"/> 位十六进制（小写）。</summary>
    public static string Fingerprint(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..Length].ToLowerInvariant();
    }

    private static (string Metadata, string Content) Split(string? cookie)
    {
        string value = (cookie ?? "").Trim();
        if (value.Length == 0)
        {
            return ("", "");
        }

        int at = value.IndexOf(Separator);
        return at < 0
            ? (value, "")
            : (value[..at], value[(at + 1)..]);
    }
}
