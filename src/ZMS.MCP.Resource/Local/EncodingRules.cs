namespace ZMS.MCP.Resource.Local;

/// <summary>
/// 文本的编码判定与写回口径。
///
/// 读：给了 <c>encoding</c> 就按它解（解不出来报错）；没给就**BOM → 严格试 UTF-8 → 拒绝**。
/// 写回：用读进来时的那一份编码（含 BOM 状态）；新建文件用 UTF-8 无 BOM。
/// **一律严格解码** —— 解不出来宁可报错，也不静默替换成 <c>U+FFFD</c>。
/// </summary>
public static class EncodingRules
{
    /// <summary>解一份字节，返回文本与它用的编码。</summary>
    /// <exception cref="ArgumentException">编码名认不出，或这份字节按该编码解不出来。</exception>
    public static (string Text, Encoding Encoding) Decode(byte[] bytes, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            Encoding named;
            try
            {
                named = Encoding.GetEncoding(name.Trim());
            }
            catch (ArgumentException)
            {
                throw new ArgumentException($"认不出这个编码：{name}（试试 utf-8 / gbk / utf-16）。");
            }

            return (DecodeStrict(bytes, 0, bytes.Length, named), named);
        }

        // BOM 优先
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            UTF8Encoding utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
            return (DecodeStrict(bytes, 3, bytes.Length - 3, new UTF8Encoding(false)), utf8Bom);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (DecodeStrict(bytes, 2, bytes.Length - 2, Encoding.Unicode), Encoding.Unicode);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (DecodeStrict(bytes, 2, bytes.Length - 2, Encoding.BigEndianUnicode), Encoding.BigEndianUnicode);
        }

        try
        {
            return (DecodeStrict(bytes, 0, bytes.Length, new UTF8Encoding(false)), new UTF8Encoding(false));
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(
                "这不是 UTF-8（没有 BOM，严格 UTF-8 也解不出来）。用 encoding 显式指定，或先把它转成 UTF-8。");
        }
    }

    /// <summary>把文本按给定编码编成字节（写回用）。</summary>
    public static byte[] Encode(string text, Encoding encoding)
    {
        return encoding.GetBytes(text);
    }

    /// <summary>编码的人话名字（输出里用）。</summary>
    public static string Describe(Encoding encoding)
    {
        return encoding.CodePage switch
        {
            65001 => encoding.GetPreamble().Length > 0 ? "utf-8（带 BOM）" : "utf-8",
            1200 => "utf-16 LE",
            1201 => "utf-16 BE",
            _ => encoding.WebName,
        };
    }

    private static string DecodeStrict(byte[] bytes, int index, int count, Encoding encoding)
    {
        Decoder decoder = encoding.GetDecoder();
        decoder.Fallback = DecoderFallback.ExceptionFallback;
        char[] buffer = new char[decoder.GetCharCount(bytes, index, count, flush: true)];

        try
        {
            decoder.GetChars(bytes, index, count, buffer, 0, flush: true);
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException("这份内容按该编码解不出来（换一个 encoding，或先把它转成 UTF-8）。");
        }

        return new string(buffer);
    }
}
