using System.Text.RegularExpressions;
using ZMS.MCP.Core.Credentials;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Local;

/// <summary>
/// 写本地文本文件：<c>write</c> 只管**新建**与**覆写**，<c>replace</c> 只管**改已有内容里的某几处**。
///
/// **改到已经有内容的东西，就要带它的凭据**；没有凭据只出**影响清单**、不执行，而且**不算失败**
/// （那是两段式的第一段）。凭据对不上说明这期间它被改过 —— 也停下，把现状摆回来。
/// 写回用**文件原本的编码**；新建文件用 UTF-8 无 BOM。
/// </summary>
public static class WriteService
{
    /// <summary>新建或覆写整份内容。</summary>
    public static string Write(LocalAddress address, string content, string? encoding, string? cookie)
    {
        string path = address.InnerPath;

        // 目标不存在 = 新建：不要凭据
        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
            {
                throw new ArgumentException($"{path} 是目录，写不进去。");
            }

            byte[] fresh = EncodingRules.Encode(content, new UTF8Encoding(false));
            File.WriteAllBytes(path, fresh);

            return Header("已写入（新建）", path)
                + $"- 大小：{fresh.LongLength} 字节\n"
                + "- 编码：utf-8（无 BOM）\n"
                + $"- 下一个凭据：`{Cookie.Of(path, fresh.LongLength, File.GetLastWriteTimeUtc(path), content, "utf-8")}`\n";
        }

        byte[] bytes = File.ReadAllBytes(path);
        (string text, Encoding used) = EncodingRules.Decode(bytes, encoding);
        DateTimeOffset modified = File.GetLastWriteTimeUtc(path);
        string current = Cookie.Of(path, bytes.LongLength, modified, text, used.WebName);

        if (string.IsNullOrWhiteSpace(cookie))
        {
            return Header("会发生什么（未执行）", path)
                + $"- 目标已有内容：{bytes.LongLength} 字节，改于 {modified:O}\n"
                + "- 这次会把整个文件覆盖掉。\n"
                + $"- 要执行，先读它拿凭据（现在是 `{current}`）。\n"
                + "- （这不算失败：缺凭据是两段式的第一段。）\n";
        }

        if (!Cookie.Matches(cookie, current))
        {
            return Header("没执行：凭据对不上", path)
                + $"- 你给的：`{cookie.Trim()}`\n"
                + $"- 现在算出来：`{current}`\n"
                + $"- 现状：{bytes.LongLength} 字节，改于 {modified:O} —— 这期间它被改过。\n"
                + "- 重新读一次，基于最新内容再来。\n";
        }

        Encoding writeWith = encoding == null ? used : Encoding.GetEncoding(encoding.Trim());
        byte[] written = EncodingRules.Encode(content, writeWith);
        File.WriteAllBytes(path, written);

        return Header("已写入", path)
            + $"- 大小：{written.LongLength} 字节\n"
            + $"- 编码：{EncodingRules.Describe(writeWith)}\n"
            + $"- 下一个凭据：`{Cookie.Of(path, written.LongLength, File.GetLastWriteTimeUtc(path), content, writeWith.WebName)}`\n";
    }

    /// <summary>改已有内容里的某几处。默认按字面找；<paramref name="regex"/> 为真时把 <paramref name="pattern"/> 当正则。</summary>
    public static string Replace(
        LocalAddress address,
        string pattern,
        string replacement,
        bool regex,
        string? encoding,
        string? cookie)
    {
        string path = address.InnerPath;
        if (!File.Exists(path))
        {
            throw new ArgumentException($"文件不存在：{path}（replace 只改已有内容，不新建）");
        }

        byte[] bytes = File.ReadAllBytes(path);
        (string text, Encoding used) = EncodingRules.Decode(bytes, encoding);
        DateTimeOffset modified = File.GetLastWriteTimeUtc(path);
        string current = Cookie.Of(path, bytes.LongLength, modified, text, used.WebName);

        if (string.IsNullOrWhiteSpace(cookie))
        {
            return Header("会发生什么（未执行）", path)
                + $"- 目标：{bytes.LongLength} 字节，改于 {modified:O}\n"
                + $"- 这次要改的是匹配 `{pattern}` 的那一处。\n"
                + $"- 要执行，先读它拿凭据（现在是 `{current}`）。\n"
                + "- （这不算失败：缺凭据是两段式的第一段。）\n";
        }

        if (!Cookie.Matches(cookie, current))
        {
            return Header("没执行：凭据对不上", path)
                + $"- 你给的：`{cookie.Trim()}`\n"
                + $"- 现在算出来：`{current}`\n"
                + $"- 现状：{bytes.LongLength} 字节，改于 {modified:O} —— 这期间它被改过。\n"
                + "- 重新读一次，基于最新内容再来。\n";
        }

        string updated;
        int hits;
        if (regex)
        {
            Regex compiled;
            try
            {
                compiled = new Regex(pattern);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"正则写错了：{exception.Message}");
            }

            hits = compiled.Matches(text).Count;
            if (hits == 0)
            {
                throw new ArgumentException($"没命中：{pattern}（不自动创建）");
            }

            if (hits > 1)
            {
                return Header("没执行：命中多处", path)
                    + $"- 命中 {hits} 处。收紧匹配（或用更精确的正则），再来一次。\n";
            }

            updated = compiled.Replace(text, replacement);
        }
        else
        {
            if (pattern.Length == 0)
            {
                throw new ArgumentException("pattern 不能是空串。");
            }

            hits = Count(text, pattern);
            if (hits == 0)
            {
                throw new ArgumentException($"没命中：{pattern}（不自动创建）");
            }

            if (hits > 1)
            {
                return Header("没执行：命中多处", path)
                    + $"- 命中 {hits} 处。收紧匹配（或打开 regex 用更精确的写法），再来一次。\n";
            }

            int at = text.IndexOf(pattern, StringComparison.Ordinal);
            updated = string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + pattern.Length));
        }

        byte[] written = EncodingRules.Encode(updated, used);
        File.WriteAllBytes(path, written);

        return Header("已替换", path)
            + $"- 大小：{written.LongLength} 字节\n"
            + $"- 编码：{EncodingRules.Describe(used)}\n"
            + $"- 下一个凭据：`{Cookie.Of(path, written.LongLength, File.GetLastWriteTimeUtc(path), updated, used.WebName)}`\n";
    }

    private static int Count(string text, string pattern)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(pattern, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += pattern.Length;
        }

        return count;
    }

    private static string Header(string title, string path)
    {
        return $"# {title}\n- {path}\n";
    }
}
