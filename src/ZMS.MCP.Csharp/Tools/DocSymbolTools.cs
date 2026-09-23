using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Tools;

/// <summary>
/// 文档注释符号工具：只从**本地 NuGet 缓存**读 XML 文档注释（不做在线查找）。
/// </summary>
[McpServerToolType]
public static class DocSymbolTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "List XML doc-comment symbols of a NuGet package from the LOCAL package cache only (no online lookup, no wild docs). " +
        "path = dot-separated object path; precision decides the default kind set: not a type -> T (namespace prefix), " +
        "exact type -> PFME, single member -> D (raw XML fragment), overloads -> M. " +
        "type = explicit letters from NTPFMED (N namespace, T type, P property, F field, M method, E event, D raw fragment). " +
        "argsList = comma separated parameter types; 'ref'/'out' are written with a trailing '@' in XML and are matched without it. " +
        "tar = target framework folder under lib/; empty = best available. ver empty = highest cached version.")]
    public static string ListDocSymbols(
        [Description("Package id, e.g. 'Newtonsoft.Json'")] string packName,
        [Description("Package version; empty = highest cached version")] string ver = "",
        [Description("TFM folder under lib/, e.g. 'net8.0'; empty = best available")] string tar = "",
        [Description("Object path (dot separated), e.g. 'Newtonsoft.Json.Linq.JObject' or 'Newtonsoft.Json'. Empty = list all types")] string path = "",
        [Description("Parameter types for overload disambiguation, e.g. 'string,int' or '(System.Int32,System.String)'")] string argsList = "",
        [Description("Explicit kind letters (NTPFMED); empty = inferred from path precision")] string type = "")
    {
        return ToolGuard.Run(() =>
        {
            DocSource source = NuGetXmlDocumentation.Locate(packName, ver, tar);
            IReadOnlyList<DocEntry> entries = NuGetXmlDocumentation.ReadAll(source.XmlPaths);
            IReadOnlyList<string> arguments = ParseArguments(argsList);

            bool listAllTypes = string.IsNullOrWhiteSpace(path);
            DocQueryResult result = listAllTypes
                ? AllTypes(entries, type.Trim())
                : DocSymbolQuery.Query(entries, path, arguments, type.Trim());

            return Render(source, result, entries.Count);
        });
    }

    /// <summary>渲染查询结果（抽出来是为了让"唯一成员给原始片段"这条分支可单测）。</summary>
    internal static string Render(DocSource source, DocQueryResult result, int totalEntries = 0)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# {source.PackageName} {source.Version} ({source.TargetFramework})");
        builder.AppendLine($"- 文档文件: {string.Join("; ", source.XmlPaths)}");
        string totalPart = totalEntries > 0 ? $"条目总数: {totalEntries} | " : "";
        builder.AppendLine(
            $"- {totalPart}命中: {result.Entries.Count} | 生效 type: `{result.EffectiveKinds}`" +
            (result.KindsWereInferred ? "（按精度推断）" : "（显式指定）"));
        if (result.Note.Length > 0)
        {
            builder.AppendLine($"- 提示: {result.Note}");
        }

        builder.AppendLine();
        if (result.Entries.Count == 0)
        {
            builder.AppendLine("_(没有命中的文档条目)_");
            return builder.ToString();
        }

        foreach (DocEntry entry in result.Entries)
        {
            if (result.EffectiveKinds.Contains('D') && result.Entries.Count == 1)
            {
                // D：原始 XML 片段
                builder.AppendLine("```xml");
                builder.AppendLine(entry.Xml);
                builder.AppendLine("```");
                continue;
            }

            string summary = entry.Summary.Length == 0 ? "" : $" — {entry.Summary}";
            builder.AppendLine($"- `{entry.MemberName}`{summary}");
        }

        return builder.ToString();
    }

    /// <summary>没有给 path 时：列出包里（有文档注释的）所有类型。</summary>
    private static DocQueryResult AllTypes(IReadOnlyList<DocEntry> entries, string explicitKinds)
    {
        string kinds = explicitKinds.Length > 0 ? explicitKinds : "T";
        List<DocEntry> types = kinds.Contains('T')
            ? entries.Where(entry => entry.Kind == 'T').ToList()
            : [];

        return new DocQueryResult("", kinds, explicitKinds.Length == 0, types, "");
    }

    /// <summary>参数列表：容忍 <c>(System.Int32,System.String)</c> 与 <c>string,int</c> 两种写法。</summary>
    private static IReadOnlyList<string> ParseArguments(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            trimmed = trimmed[1..^1];
        }

        if (trimmed.Length == 0)
        {
            return [];
        }

        return trimmed
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
