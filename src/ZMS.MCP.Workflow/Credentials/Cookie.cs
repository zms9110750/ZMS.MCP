using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ZMS.MCP.Workflow.Credentials;

/// <summary>
/// 凭据（<c>cookie</c>）：一次读取换来的短标识，改到已有内容时要带回来。
///
/// 与 Resource 那份同口径（SHA256 前 <see cref="Length"/> 位小写十六进制、同样大小写不敏感地比），
/// 但**只有元数据段**：路径 + 大小 + 修改时间。工作流改的是整份文件、不是文本里的某几处，
/// 所以不需要内容段 —— 只要"这个文件还是我上次看到的那一份"。
///
/// 文件**不存在**也是一种可表达的状态：那时凭据写的是"无"，新建就凭它。
/// 修改时间进了段里：哪怕内容一字没动，只要被人改过（甚至只是另存一次），就对不上。
/// </summary>
public static class Cookie
{
    /// <summary>指纹长度（十六进制位数）。</summary>
    public const int Length = 16;

    /// <summary>文件不存在时用的那一格。</summary>
    public const string Missing = "none";

    /// <summary>按「路径 + 大小 + 修改时间」算一个凭据；文件不存在时算的是"无"。</summary>
    public static string Of(string path)
    {
        FileInfo file = new(path);
        return file.Exists
            ? Fingerprint(
                $"{NormalizePath(path)}\n{file.Length.ToString(CultureInfo.InvariantCulture)}\n{file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}")
            : Fingerprint($"{NormalizePath(path)}\n{Missing}");
    }

    /// <summary>两个凭据对不对得上（去空白、大小写不敏感）。</summary>
    public static bool Matches(string? expected, string? actual)
    {
        string left = (expected ?? "").Trim();
        string right = (actual ?? "").Trim();

        return left.Length != 0
            && right.Length != 0
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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
}
