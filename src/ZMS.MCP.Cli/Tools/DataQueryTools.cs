using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.XPath;

namespace zms9110750.ZMS_MCP.Cli.Tools;

/// <summary>
/// 数据查询工具：以字符串、文件或 URL 为来源，用路径表达式查询 JSON 或 XML 数据。
/// JSON 路径支持点号分隔和数组索引（如 $.store.book[0].title）。
/// XML 路径支持 XPath。
/// </summary>
[McpServerToolType]
public static partial class DataQueryTools
{
    [McpServerTool(
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true)]
    [Description(
        "查询 JSON 或 XML 数据。支持四种输入来源：\n" +
        "1. JSON 字符串（以 { 或 [ 开头）\n" +
        "2. XML 字符串（以 < 开头）\n" +
        "3. 本地文件路径（将自动检测 JSON 或 XML）\n" +
        "4. URL（将下载后自动检测 JSON 或 XML）\n" +
        "JSON 路径示例：$.store.book[0].title、$..author（简易递归下降）\n" +
        "XML 路径使用标准 XPath。")]
    public static async Task<string> QueryData(
        [Description("数据来源：JSON 字符串、XML 字符串、文件路径或 URL")] string source,
        [Description("路径表达式。JSON 用 $.prop[idx].prop 格式；XML 用 XPath")] string path,
        [Description("结果截断长度。超过此长度时只展属性结构（名+类型）而非完整值。默认 500，最大 10000")] int? maxLength)
    {
        try
        {
            // 1. 确定数据内容
            string content;
            var trimmed = source.Trim();

            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                // JSON 字符串
                content = source;
            }
            else if (trimmed.StartsWith('<'))
            {
                // XML 字符串
                content = source;
            }
            else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // URL：下载内容
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var response = await client.GetAsync(trimmed);
                if (!response.IsSuccessStatusCode)
                {
                    return $"HTTP {(int)response.StatusCode}: 下载失败";
                }

                content = await response.Content.ReadAsStringAsync();
            }
            else if (File.Exists(trimmed))
            {
                // 本地文件
                content = await File.ReadAllTextAsync(trimmed);
            }
            else
            {
                return $"无法识别输入来源。请提供 JSON/XML 字符串、有效文件路径或 HTTP(S) URL。";
            }

            var trimmedContent = content.Trim();

            // 2. 检测格式并查询
            string result;
            if (trimmedContent.StartsWith('{') || trimmedContent.StartsWith('['))
            {
                result = QueryJson(content, path);
            }
            else if (trimmedContent.StartsWith('<'))
            {
                result = QueryXml(content, path);
            }
            else
            {
                return "无法识别数据格式：内容既不是 JSON（{/[）也不是 XML（<）。";
            }

            // 3. 截断处理
            var limit = Math.Clamp(maxLength ?? 500, 1, 10000);
            if (result.Length > limit)
            {
                return TruncateToStructure(result, trimmedContent);
            }
            return result;
        }
        catch (JsonException ex)
        {
            return $"JSON 查询错误: {ex.Message}";
        }
        catch (XmlException ex)
        {
            return $"XML 查询错误: {ex.Message}";
        }
        catch (XPathException ex)
        {
            return $"XPath 错误: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }

    // ============================================================
    // JSON 查询
    // ============================================================

    /// <summary>
    /// 用简易 JSON 路径表达式查询 JsonNode。
    /// 支持：$.prop、$.prop.sub、$.arr[0]、$.arr[0].prop、$..name（递归搜索所有匹配 "name" 的节点）。
    /// </summary>
    private static string QueryJson(string json, string path)
    {
        var node = JsonNode.Parse(json)
            ?? throw new JsonException("JSON 解析为空");

        var trimmedPath = path.Trim();

        // 递归搜索：$..name（在正常路径解析之前处理）
        if (trimmedPath.StartsWith("$.."))
        {
            var searchName = trimmedPath[3..];
            if (string.IsNullOrEmpty(searchName))
            {
                return "Missing property name after '$..'.";
            }

            var results = new List<string>();
            SearchRecursive(node, searchName, results);
            return results.Count > 0
                ? string.Join("\n---\n", results)
                : $"路径 '{path}' 未找到匹配的值。";
        }

        // 去掉开头的 $. 或 $
        var expr = trimmedPath.StartsWith("$.") ? trimmedPath[2..] :
                   trimmedPath.StartsWith('$') ? trimmedPath[1..] :
                   trimmedPath;

        if (string.IsNullOrEmpty(expr))
        {
            return JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true });
        }

        // 递归搜索已移至上方处理，此处直接进入路径解析
        // 按点号分段解析路径
        var segments = ParsePathSegments(expr);
        JsonNode? current = node;

        foreach (var seg in segments)
        {
            if (current == null)
            {
                return $"路径 '{path}' 在中途遇到 null。";
            }

            if (seg.IsIndex)
            {
                // 数组索引 [n]
                if (current is JsonArray arr)
                {
                    current = seg.Index < arr.Count ? arr[seg.Index] : null;
                }
                else
                {
                    return $"无法对非数组类型使用索引 [{seg.Index}]。";
                }
            }
            else
            {
                // 属性访问
                if (current is JsonObject obj)
                {
                    current = obj.TryGetPropertyValue(seg.Name, out var val) ? val : null;
                }
                else if (current is JsonArray array)
                {
                    // 在数组每项中查找属性
                    var matches = array.Select(item => {
                        if (item is JsonObject o && o.TryGetPropertyValue(seg.Name, out var v))
                        {
                            return v;
                        }

                        return null;
                    }).Where(v => v != null).ToArray();

                    if (matches.Length == 0)
                    {
                        return $"在数组元素中未找到属性 '{seg.Name}'。";
                    }

                    if (matches.Length == 1)
                    {
                        current = matches[0];
                    }
                    else
                    {
                        // 返回所有匹配
                        var sb = new StringBuilder();
                        sb.AppendLine($"找到 {matches.Length} 个匹配：");
                        foreach (var m in matches)
                        {
                            sb.AppendLine(JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
                        }

                        return sb.ToString();
                    }
                }
                else
                {
                    return $"路径 '{path}'：'{seg.Name}' 不是对象或数组。";
                }
            }
        }

        if (current == null)
        {
            return $"路径 '{path}' 未找到匹配的值。";
        }

        // 格式化输出
        return JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>路径段：属性名或数组索引。</summary>
    private readonly record struct PathSegment(string Name, int Index, bool IsIndex);

    /// <summary>解析路径表达式为段列表。支持 foo.bar[0].baz 格式。</summary>
    private static List<PathSegment> ParsePathSegments(string expr)
    {
        var segments = new List<PathSegment>();
        int i = 0;
        while (i < expr.Length)
        {
            if (expr[i] == '.')
            {
                i++; // 跳过点号
                continue;
            }
            if (expr[i] == '[')
            {
                // 数组索引 [n]
                int close = expr.IndexOf(']', i);
                if (close < 0)
                {
                    throw new FormatException($"路径格式错误：缺少 ]");
                }

                var indexStr = expr[(i + 1)..close];
                if (!int.TryParse(indexStr, out var idx))
                {
                    throw new FormatException($"路径格式错误：无效索引 '{indexStr}'");
                }

                segments.Add(new PathSegment("", idx, true));
                i = close + 1;
            }
            else
            {
                // 属性名
                int end = i;
                while (end < expr.Length && expr[end] != '.' && expr[end] != '[')
                {
                    end++;
                }

                segments.Add(new PathSegment(expr[i..end], 0, false));
                i = end;
            }
        }
        return segments;
    }

    /// <summary>递归搜索所有名称为 searchName 的属性。</summary>
    private static void SearchRecursive(JsonNode? node, string searchName, List<string> results)
    {
        if (node == null)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Key == searchName)
                {
                    results.Add(JsonSerializer.Serialize(kv.Value, new JsonSerializerOptions { WriteIndented = true }));
                }
                SearchRecursive(kv.Value, searchName, results);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                SearchRecursive(item, searchName, results);
            }
        }
    }

    // ============================================================
    // XML 查询
    // ============================================================

    /// <summary>用标准 XPath 查询 XML。</summary>
    private static string QueryXml(string xml, string xpath)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var nav = doc.CreateNavigator()
            ?? throw new XmlException("无法创建 XPath 导航器。");

        // 尝试作为单节点查询
        var single = nav.SelectSingleNode(xpath);
        if (single != null)
        {
            // 如果是属性或文本节点，直接返回值
            if (single.NodeType is XPathNodeType.Attribute or XPathNodeType.Text)
            {
                return single.Value;
            }

            // 元素节点，返回其 XML
            return single.OuterXml;
        }

        // 尝试作为多节点查询
        var nodes = nav.Select(xpath);
        if (nodes == null || nodes.Count == 0)
        {
            return $"XPath '{xpath}' 未找到匹配。";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"找到 {nodes.Count} 个匹配：");
        foreach (XPathNavigator node in nodes)
        {
            if (node.NodeType is XPathNodeType.Attribute or XPathNodeType.Text)
            {
                sb.AppendLine(node.Value);
            }
            else
            {
                sb.AppendLine(node.OuterXml);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 当结果超过截断长度时，将 JSON 属性结构压缩为精简视图：
    /// 只显示键名和值的类型，不展开完整值。
    /// </summary>
    private static string TruncateToStructure(string result, string rawContent)
    {
        try
        {
            var node = JsonNode.Parse(rawContent);
            if (node == null)
            {
                return result[..500] + "\n…（截断）";
            }

            var sb = new StringBuilder();
            sb.AppendLine("（结果过长，仅展示属性结构）");
            BuildStructurePreview(node, sb, 0);
            return sb.ToString();
        }
        catch
        {
            return result[..500] + "\n…（截断）";
        }
    }

    /// <summary>递归构建属性结构预览。</summary>
    private static void BuildStructurePreview(JsonNode? node, StringBuilder sb, int indent)
    {
        if (node == null)
        {
            sb.AppendLine("null");
            return;
        }

        var prefix = new string(' ', indent * 2);

        if (node is JsonObject obj)
        {
            if (indent == 0)
            {
                sb.AppendLine("{");
            }

            foreach (var kv in obj)
            {
                var typeLabel = GetTypeLabel(kv.Value);
                sb.Append(prefix).Append("  ").Append(kv.Key).Append(": ").AppendLine(typeLabel);

                if (kv.Value is JsonObject subObj)
                {
                    BuildStructurePreview(subObj, sb, indent + 1);
                }
                else if (kv.Value is JsonArray arr && arr.Count > 0 && arr[0] is JsonObject)
                {
                    sb.Append(prefix).Append("    [").AppendLine();
                    BuildStructurePreview(arr[0], sb, indent + 2);
                    sb.Append(prefix).Append("      ").AppendLine("...");
                    sb.Append(prefix).Append("    ]").AppendLine();
                }
            }
            if (indent == 0)
            {
                sb.AppendLine("}");
            }
        }
        else if (node is JsonArray arr)
        {
            sb.AppendLine("[");
            if (arr.Count > 0 && arr[0] != null)
            {
                BuildStructurePreview(arr[0], sb, indent + 1);
                if (arr.Count > 1)
                {
                    sb.Append(prefix).Append("  ... (").Append(arr.Count).Append(" 项)").AppendLine();
                }
            }
            sb.Append(prefix).Append("]").AppendLine();
        }
    }

    /// <summary>
    /// 获取 JsonNode 的类型标签。
    /// - 纯量（string/number/bool/null）→ "value"
    /// - 对象 → "object"
    /// - 数组 → "array[...]"（含元素类型提示）
    /// </summary>
    private static string GetTypeLabel(JsonNode? node)
    {
        if (node == null)
        {
            return "null";
        }

        return node switch {
            JsonObject => "object",
            JsonArray arr => arr.Count > 0 && arr[0] != null
                ? $"array[{GetTypeLabel(arr[0])}]"
                : "array",
            JsonValue val => val.GetValueKind() switch {
                JsonValueKind.String => "value",
                JsonValueKind.Number => "value",
                JsonValueKind.True or JsonValueKind.False => "value",
                JsonValueKind.Null => "null",
                _ => "value"
            },
            _ => "value"
        };
    }
}
