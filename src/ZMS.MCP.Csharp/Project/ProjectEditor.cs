using System.Text;

namespace ZMS.MCP.Csharp.Project;

/// <summary>
/// 「编辑项目 / 编辑元数据」：内容先过 XML 语法检查与最低 csproj 语法检查，合法才写进去。
/// 写回用文件**原编码**（见「写文件通则」）。
/// </summary>
public static class ProjectEditor
{
    public static string EditMetadata(string csprojPath, string content, bool dryRun = false)
    {
        string fullPath = ProjectViewer.ResolveProjectFile(csprojPath);
        ProjectViewer.EnsureValidProjectXml(content);

        string before = FileWriter.ReadAllText(fullPath, out DetectedEncoding encoding);
        StringBuilder builder = new();
        builder.AppendLine("# 编辑元数据");
        builder.AppendLine($"- 项目：{fullPath}");
        builder.AppendLine($"- 编码：{encoding.Encoding.WebName}（来自 {Describe(encoding.Source)}）");

        if (before == content)
        {
            builder.AppendLine("- 内容没有变化，未写入。");
            return builder.ToString();
        }

        if (dryRun)
        {
            builder.AppendLine("- **预演，未写入**。");
            builder.AppendLine();
            builder.AppendLine("```xml");
            builder.AppendLine(content.TrimEnd());
            builder.AppendLine("```");
            return builder.ToString();
        }

        FileWriter.WriteAtomic(fullPath, content, encoding);
        builder.AppendLine("- 已写入（XML 语法检查 + 根元素 Project 检查通过）。");
        return builder.ToString();
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
