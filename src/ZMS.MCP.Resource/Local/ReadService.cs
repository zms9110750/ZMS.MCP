using ZMS.MCP.Core.Credentials;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Local;

/// <summary>
/// 读一个本地文本文件。
///
/// - **不带范围** → 完整读：内容（至多 <see cref="DeliveryLimit"/> 字符）+ **凭据**；
/// - **带范围**（<c>skipline</c>/<c>offset</c>/<c>length</c>/<c>takeline</c>/<c>regex</c>）→ 只有内容，**不给凭据**；
/// - 交付不下整份（超上限）→ 截断并写明"范围内还有多少""全文共多少"，**不给凭据**。
/// </summary>
public static class ReadService
{
    /// <summary>单次交付上限（字符）。</summary>
    public const int DeliveryLimit = 16000;

    /// <summary>带范围读时，没给 <c>length</c> 的默认展示量（字符）。</summary>
    public const int DefaultLength = 5000;

    /// <summary>正则命中数的分级门槛。</summary>
    public const int ManyMatches = 20;
    public const int FewMatches = 5;

    /// <summary>读一个本地文件并渲染成 markdown。</summary>
    public static string Run(
        LocalAddress address,
        int skipline,
        int offset,
        int length,
        int takeline,
        string? encoding,
        string? regex)
    {
        string path = address.InnerPath;
        if (Directory.Exists(path))
        {
            throw new ArgumentException($"{path} 是目录，不是文件。");
        }

        if (!File.Exists(path))
        {
            throw new ArgumentException($"文件不存在：{path}");
        }

        return Render(
            path,
            File.ReadAllBytes(path),
            File.GetLastWriteTimeUtc(path),
            skipline,
            offset,
            length,
            takeline,
            encoding,
            regex);
    }

    /// <summary>把一份内容渲染成 markdown —— 本地与远端共用同一套规矩。</summary>
    /// <param name="displayPath">展示用的路径（远端就是那个远端路径）。</param>
    /// <param name="bytes">内容。</param>
    /// <param name="modified">它的修改时间（凭据要）。</param>
    public static string Render(
        string displayPath,
        byte[] bytes,
        DateTimeOffset modified,
        int skipline,
        int offset,
        int length,
        int takeline,
        string? encoding,
        string? regex)
    {
        (string text, Encoding used) = EncodingRules.Decode(bytes, encoding);

        bool ranged = skipline > 0 || offset > 0 || length > 0 || takeline > 0 || !string.IsNullOrWhiteSpace(regex);

        if (ranged)
        {
            return RenderRanged(displayPath, text, used, skipline, offset, length, takeline, regex);
        }

        // 完整读：交付得下才给凭据
        bool complete = text.Length <= DeliveryLimit;
        StringBuilder builder = new();
        builder.AppendLine($"# {displayPath}");
        builder.AppendLine($"- 编码：{EncodingRules.Describe(used)}");
        builder.AppendLine($"- 字符数：{text.Length}");

        if (complete)
        {
            builder.AppendLine($"- 凭据：`{Cookie.Of(displayPath, bytes.LongLength, modified, text, used.WebName)}`");
        }
        else
        {
            builder.AppendLine($"- 全文共 {text.Length} 字符，这里只给前 {DeliveryLimit} 字符。");
            builder.AppendLine("- **没交付完整份 → 没有凭据**：不能改，也不能删。");
        }

        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(complete ? text : text[..DeliveryLimit]);
        builder.AppendLine("```");

        return builder.ToString();
    }

    private static string RenderRanged(
        string path,
        string text,
        Encoding used,
        int skipline,
        int offset,
        int length,
        int takeline,
        string? regex)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# {path}（带范围读）");
        builder.AppendLine($"- 编码：{EncodingRules.Describe(used)}");
        builder.AppendLine($"- 全文 {text.Length} 字符");
        builder.AppendLine("- **只读了一段 → 不给凭据**（不算看过全貌）");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(regex))
        {
            builder.AppendLine(RenderMatches(text, regex!));
            return builder.ToString();
        }

        string[] lines = text.Split('\n');
        int fromLine = Math.Clamp(skipline, 0, lines.Length);
        int toLine = takeline > 0 ? Math.Min(lines.Length, fromLine + takeline) : lines.Length;
        string slice = string.Join('\n', lines[fromLine..toLine]);

        int fromChar = Math.Clamp(offset, 0, slice.Length);
        int take = length > 0 ? length : DefaultLength;
        string shown = slice.Length - fromChar > take ? slice.Substring(fromChar, take) : slice[fromChar..];

        int omitted = slice.Length - fromChar - shown.Length;
        builder.AppendLine("```");
        builder.AppendLine(shown);
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine($"- 范围内还有 {omitted} 字符未给出（范围共 {slice.Length - fromChar} 字符）");

        return builder.ToString();
    }

    /// <summary>正则命中按数量分级给上下文。</summary>
    private static string RenderMatches(string text, string pattern)
    {
        System.Text.RegularExpressions.Regex re;
        try
        {
            re = new System.Text.RegularExpressions.Regex(pattern);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"正则写错了：{exception.Message}");
        }

        string[] lines = text.Split('\n');
        List<int> hits = [];
        for (int index = 0; index < lines.Length; index++)
        {
            if (re.IsMatch(lines[index]))
            {
                hits.Add(index);
            }
        }

        if (hits.Count == 0)
        {
            return $"没命中：{pattern}";
        }

        int around = hits.Count > ManyMatches ? 0 : hits.Count > FewMatches ? 1 : 3;
        StringBuilder builder = new();
        builder.AppendLine($"命中 {hits.Count} 处" + (around == 0 ? "（只给行号）" : $"（每处上下 {around} 行）"));

        foreach (int hit in hits)
        {
            if (around == 0)
            {
                builder.AppendLine($"- {hit + 1}: {lines[hit].TrimEnd()}");
                continue;
            }

            builder.AppendLine($"- {hit + 1}:");
            int from = Math.Max(0, hit - around);
            int to = Math.Min(lines.Length - 1, hit + around);
            for (int index = from; index <= to; index++)
            {
                builder.AppendLine($"    {index + 1}: {lines[index].TrimEnd()}");
            }
        }

        return builder.ToString();
    }
}
