using System.Text;

namespace ZMS.MCP.Csharp.Project;

/// <summary>
/// 「编辑项目 / 编辑元数据」：内容先过 XML 语法检查与最低 csproj 语法检查，合法才写进去。
/// 写回用文件**原编码**（见「写文件通则」）。
/// </summary>
public static class ProjectEditor
{
    /// <summary>
    /// 编辑 csproj：**两段式**。
    /// 第一段（<paramref name="content"/> 空）= 只读，给原文 + 内容指纹；
    /// 第二段（带 <paramref name="content"/> 和 <paramref name="cookie"/>）= 先重读并比对指纹，一致才写。
    ///
    /// 为什么要两段：写出去的内容是"基于读到的现状"构造的 —— 期间被别人改过，
    /// 我们就会拿一份自己没见过的现状去覆盖，事后也不知道该还原成什么。
    /// </summary>
    public static string EditMetadata(string csprojPath, string content = "", string cookie = "")
    {
        string fullPath = ProjectViewer.ResolveProjectFile(csprojPath);
        string before = FileWriter.ReadAllText(fullPath, out DetectedEncoding encoding);
        string current = Fingerprint(before);

        StringBuilder builder = new();
        builder.AppendLine("# 编辑元数据");
        builder.AppendLine($"- 项目：{fullPath}");
        builder.AppendLine($"- 编码：{encoding.Encoding.WebName}（来自 {Describe(encoding.Source)}）");

        if (content.Length == 0)
        {
            // 第一段：只读。给原文 + 内容指纹，调用方拿到的就是"改权限"。
            builder.AppendLine($"- cookie：`{current}`");
            builder.AppendLine();
            builder.AppendLine(before.TrimEnd());
            builder.AppendLine();
            builder.AppendLine(
                "要改它：把**完整的新内容**连同这个 cookie 一起传回来。"
                + "写之前会再读一次并对指纹，对不上就拒绝 —— 不会拿一份你没见过的现状去覆盖。");
            return builder.ToString();
        }

        if (cookie.Length == 0)
        {
            throw new InvalidOperationException(
                "写入必须带上读的时候拿到的 cookie —— 没有它就没法确认你改的是哪一版。先不带 content 调一次拿 cookie。");
        }

        if (!string.Equals(cookie, current, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"cookie 对不上了：你读的是 `{cookie}`，现在文件是 `{current}` —— 这期间它被改过。"
                + "重新读一次、基于最新内容再改。");
        }

        ProjectViewer.EnsureValidProjectXml(content);

        if (before == content)
        {
            builder.AppendLine("- 内容没有变化，未写入。");
            return builder.ToString();
        }

        FileWriter.WriteAtomic(fullPath, content, encoding);
        builder.AppendLine("- 已写入（XML 语法检查 + 根元素 Project 检查通过）。");
        return builder.ToString();
    }

    /// <summary>
    /// 文件内容指纹（换行归一后的 SHA256 前 16 位小写十六进制）。
    /// 幂等、不存状态：同一份内容永远算出同一串，所以它既能当"我读过这一版"的凭据，
    /// 也能在写之前确认"还是那一版"。
    /// </summary>
    private static string Fingerprint(string text)
    {
        byte[] digest = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
        return Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }

    private static string Describe(EncodingSource source)
    {
        return source switch
        {
            EncodingSource.EditorConfig => ".editorconfig 的 charset",
            EncodingSource.ByteOrderMark => "BOM",
            EncodingSource.StrictUtf8 => "无 BOM 且是合法 UTF-8",
            _ => "新建文件默认（UTF-8 无 BOM）",
        };
    }
}
