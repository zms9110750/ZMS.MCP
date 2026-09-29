namespace ZMS.MCP.Structured.Formats;

/// <summary>
/// 一份结构化文档。json / yaml / toml / ini 都归一到 json 树来定位、比较、改动；xml 走 XPath。
/// 写回是整份重新序列化，所以注释与排版不保留。
/// 注意 json 的 null 在这个模型里就是 C# 的 null 引用，所以 _json 为 null 是正常状态（空文档或文档就是 null）。
/// </summary>
internal sealed partial class Document
{
    private readonly string _format;
    private readonly JsonNode? _json;
    private readonly XDocument? _xml;

    private Document(string format, JsonNode? json)
    {
        _format = format;
        _json = json;
    }

    private Document(string format, XDocument xml)
    {
        _format = format;
        _xml = xml;
    }

    /// <summary>这份文档的格式名（json / xml / yaml / toml / ini）。</summary>
    public string Format
    {
        get
        {
            return _format;
        }
    }

    /// <summary>按格式把文本解析成一份文档；空文本算空文档。</summary>
    public static Document Parse(string format, string text)
    {
        if (text.Trim().Length == 0)
        {
            return format == "xml"
                ? new Document(format, new XDocument())
                : new Document(format, (JsonNode?)null);
        }

        switch (format)
        {
            case "json":
                return new Document(format, JsonNode.Parse(text) ?? throw new InvalidDataException("json 里没有内容。"));

            case "yaml":
                return new Document(format, FromYaml(text));

            case "toml":
                return new Document(format, FromToml(text));

            case "ini":
                return new Document(format, FromIni(text));

            case "xml":
                return new Document(format, XDocument.Parse(text));

            default:
                throw new NotSupportedException($"不支持的格式：{format}");
        }
    }

    /// <summary>yaml 文本 → json 树。</summary>
    private static JsonNode? FromYaml(string source)
    {
        RefuseAnchors(source);
        object? model = new DeserializerBuilder().Build().Deserialize<object>(source);
        return FromValue(model);

        static JsonNode? FromValue(object? value)
        {
            switch (value)
            {
                case null:
                    return null;

                case IDictionary<object, object> map:
                {
                    JsonObject result = new();
                    foreach (KeyValuePair<object, object> pair in map)
                    {
                        result[pair.Key?.ToString() ?? ""] = FromValue(pair.Value);
                    }

                    return result;
                }

                case IEnumerable<object> list:
                {
                    JsonArray result = new();
                    foreach (object item in list)
                    {
                        result.Add(FromValue(item));
                    }

                    return result;
                }

                case string text:
                    return JsonValue.Create(text);

                case bool flag:
                    return JsonValue.Create(flag);

                case int number:
                    return JsonValue.Create(number);

                case long number:
                    return JsonValue.Create(number);

                case double number:
                    return JsonValue.Create(number);

                case DateTime stamp:
                    return JsonValue.Create(stamp.ToString("O", CultureInfo.InvariantCulture));

                default:
                    return JsonValue.Create(value.ToString());
            }
        }
    }

    /// <summary>
    /// 带锚点 / 别名 / merge 的 yaml 一律拒绝。这套模型只认"键值树"，反序列化会把 <c>&lt;&lt;: *b</c> 当成普通键，
    /// 写回就把 merge 语义写死了 —— 宁可拒绝，也不静默改坏别人的文件。
    /// </summary>
    private static void RefuseAnchors(string source)
    {
        Parser parser = new(new StringReader(source));
        while (parser.MoveNext())
        {
            if (parser.Current is AnchorAlias)
            {
                throw new InvalidDataException(
                    "这份 yaml 用了别名（*name）或 merge（<<:），这个工具会把它们写坏：先手工展开再来改。");
            }

            if (parser.Current is NodeEvent node && !node.Anchor.IsEmpty)
            {
                throw new InvalidDataException(
                    "这份 yaml 用了锚点（&name），这个工具会把它们写坏：先手工展开再来改。");
            }
        }
    }

    /// <summary>toml 文本 → json 树。</summary>
    private static JsonNode? FromToml(string source)
    {
        TomlTable? model = TomlSerializer.Deserialize<TomlTable>(source);
        if (model == null)
        {
            return null;
        }

        return FromValue(model);

        static JsonNode? FromValue(object? value)
        {
            switch (value)
            {
                case null:
                    return null;

                case TomlTable table:
                {
                    JsonObject result = new();
                    foreach (KeyValuePair<string, object> pair in table)
                    {
                        result[pair.Key] = FromValue(pair.Value);
                    }

                    return result;
                }

                case TomlTableArray tables:
                {
                    JsonArray result = new();
                    foreach (TomlTable item in tables)
                    {
                        result.Add(FromValue(item));
                    }

                    return result;
                }

                case TomlArray array:
                {
                    JsonArray result = new();
                    foreach (object? item in array)
                    {
                        result.Add(FromValue(item));
                    }

                    return result;
                }

                case string text:
                    return JsonValue.Create(text);

                case bool flag:
                    return JsonValue.Create(flag);

                case long number:
                    return JsonValue.Create(number);

                case double number:
                    return JsonValue.Create(number);

                case TomlDateTime stamp:
                    return ValueOf(stamp);

                default:
                    return JsonValue.Create(value.ToString());
            }
        }

        static JsonNode ValueOf(TomlDateTime stamp)
        {
            // toml 的日期/时间有四种，json 树里对应 DateOnly / TimeOnly / DateTime ——
            // 写回的时候才认得出"这个不是字符串，不加引号"。
            switch (stamp.Kind)
            {
                case TomlDateTimeKind.LocalDate:
                    return JsonValue.Create(DateOnly.FromDateTime(stamp.DateTime.DateTime))!;

                case TomlDateTimeKind.LocalTime:
                    return JsonValue.Create(TimeOnly.FromDateTime(stamp.DateTime.DateTime))!;

                case TomlDateTimeKind.LocalDateTime:
                    return JsonValue.Create(stamp.DateTime.DateTime);

                default:
                    return JsonValue.Create(stamp.DateTime);
            }
        }
    }

    /// <summary>ini 文本 → json 树：section 当对象，键一律当字符串，注释与空行丢掉。</summary>
    private static JsonNode FromIni(string source)
    {
        JsonObject root = new();
        JsonObject current = root;
        foreach (string raw in source.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                JsonObject section = new();
                root[line[1..^1].Trim()] = section;
                current = section;
                continue;
            }

            int cut = line.IndexOf('=');
            if (cut <= 0)
            {
                continue;
            }

            current[line[..cut].Trim()] = JsonValue.Create(line[(cut + 1)..].Trim());
        }

        return root;
    }

    /// <summary>把调用方给的 value 文本按本文档的格式解析成节点。</summary>
    private JsonNode? ParseValue(string value)
    {
        switch (_format)
        {
            case "json":
                return JsonNode.Parse(value);

            case "yaml":
                return FromYaml(value);

            case "toml":
                return FromToml("value = " + value)?["value"]?.DeepClone()
                    ?? throw new InvalidDataException($"这段不是合法的 toml：{value}");

            case "ini":
                return JsonValue.Create(value.Trim());

            default:
                throw new NotSupportedException($"不支持的格式：{_format}");
        }
    }

    /// <summary>这个命中的节点换成这段新内容之后，内容是不是没变（用来跳过空写）。</summary>
    public bool SameAs(object node, string value)
    {
        if (node is not JsonNode current)
        {
            return false;
        }

        return string.Equals(DescribeNode(current), DescribeNode(ParseValue(value)), StringComparison.Ordinal);
    }

    /// <summary>规范结构文本：忽略排版与对象内的键顺序，用来算 cookie。</summary>
    public string Structure()
    {
        StringBuilder builder = new();
        if (_xml != null)
        {
            DescribeXml(builder, _xml.Root);
            return builder.ToString();
        }

        return DescribeNode(_json);
    }

    /// <summary>单个节点的规范文本：忽略键顺序，只留内容与层级。</summary>
    private static string DescribeNode(JsonNode? node)
    {
        StringBuilder builder = new();
        Describe(builder, node);
        return builder.ToString();

        static void Describe(StringBuilder target, JsonNode? item)
        {
            if (item == null)
            {
                target.Append("null");
                return;
            }

            if (item is JsonObject holder)
            {
                target.Append('{');
                foreach (KeyValuePair<string, JsonNode?> pair in holder.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    target.Append(pair.Key).Append(':');
                    Describe(target, pair.Value);
                    target.Append(',');
                }

                target.Append('}');
                return;
            }

            if (item is JsonArray list)
            {
                target.Append('[');
                foreach (JsonNode? element in list)
                {
                    Describe(target, element);
                    target.Append(',');
                }

                target.Append(']');
                return;
            }

            target.Append(item.GetValueKind()).Append('=').Append(item.ToJsonString());
        }
    }

    private static void DescribeXml(StringBuilder target, XElement? element)
    {
        if (element == null)
        {
            return;
        }

        target.Append('<').Append(element.Name.LocalName);
        foreach (XAttribute attribute in element.Attributes().OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal))
        {
            target.Append(' ').Append(attribute.Name.LocalName).Append('=').Append(attribute.Value.Trim());
        }

        target.Append('>');
        foreach (XNode node in element.Nodes())
        {
            if (node is XElement child)
            {
                DescribeXml(target, child);
                continue;
            }

            if (node is XText text && text.Value.Trim().Length > 0)
            {
                target.Append(text.Value.Trim());
            }
        }

        target.Append("</").Append(element.Name.LocalName).Append('>');
    }

    /// <summary>一眼能看的结构摘要；空文档单独说。</summary>
    public string Shape()
    {
        if (_xml != null)
        {
            return _xml.Root == null ? "空文档" : $"xml | 最大深度 {XmlDepth(_xml.Root)}";
        }

        if (_json == null)
        {
            return "空文档";
        }

        return $"{KindOf(_json)} | 最大深度 {DepthOf(_json)}";

        static string KindOf(JsonNode node)
        {
            return node switch
            {
                JsonObject => "object",
                JsonArray => "array",
                _ => node.GetValueKind().ToString().ToLowerInvariant(),
            };
        }

        static int DepthOf(JsonNode node)
        {
            if (node is JsonObject holder)
            {
                int deepest = 0;
                foreach (KeyValuePair<string, JsonNode?> pair in holder)
                {
                    if (pair.Value != null)
                    {
                        deepest = Math.Max(deepest, DepthOf(pair.Value));
                    }
                }

                return deepest + 1;
            }

            if (node is JsonArray list)
            {
                int deepest = 0;
                foreach (JsonNode? item in list)
                {
                    if (item != null)
                    {
                        deepest = Math.Max(deepest, DepthOf(item));
                    }
                }

                return deepest + 1;
            }

            return 1;
        }

        static int XmlDepth(XElement? element)
        {
            if (element == null)
            {
                return 0;
            }

            int deepest = 0;
            foreach (XElement child in element.Elements())
            {
                deepest = Math.Max(deepest, XmlDepth(child));
            }

            return deepest + 1;
        }
    }

    /// <summary>整份文档的展示文本：按原格式序列化，depth 限制展开层数（0 = 不限）。</summary>
    public string ShowBody(int length, int depth)
    {
        if (_xml != null)
        {
            if (_xml.Root == null)
            {
                return Cut("（空文件）", length);
            }

            StringBuilder projected = new();
            ProjectXml(projected, _xml.Root, depth, 0, "");
            return Cut(projected.ToString(), length);
        }

        if (_json == null)
        {
            return Cut("（空文件）", length);
        }

        StringBuilder builder = new();
        Project(builder, _json, depth, 0, "");
        return Cut(builder.ToString(), length);
    }

    /// <summary>按深度把 json 树投影成文本，超出深度的层级只报还有多少字符。</summary>
    private static void Project(StringBuilder builder, JsonNode? node, int depth, int level, string indent)
    {
        if (node == null)
        {
            builder.Append("null");
            return;
        }

        if (depth > 0 && level >= depth && node is JsonObject or JsonArray)
        {
            builder.Append($"…（此处还有 {node.ToJsonString().Length} 字符未展开）");
            return;
        }

        if (node is JsonObject holder)
        {
            builder.Append('{');
            if (holder.Count > 0)
            {
                builder.AppendLine();
                string inner = indent + "  ";
                int written = 0;
                foreach (KeyValuePair<string, JsonNode?> pair in holder)
                {
                    builder.Append(inner).Append('"').Append(pair.Key).Append("\": ");
                    Project(builder, pair.Value, depth, level + 1, inner);
                    written++;
                    builder.AppendLine(written < holder.Count ? "," : "");
                }

                builder.Append(indent);
            }

            builder.Append('}');
            return;
        }

        if (node is JsonArray list)
        {
            builder.Append('[');
            if (list.Count > 0)
            {
                builder.AppendLine();
                string inner = indent + "  ";
                for (int index = 0; index < list.Count; index++)
                {
                    builder.Append(inner);
                    Project(builder, list[index], depth, level + 1, inner);
                    builder.AppendLine(index < list.Count - 1 ? "," : "");
                }

                builder.Append(indent);
            }

            builder.Append(']');
            return;
        }

        builder.Append(node.ToJsonString());
    }

    /// <summary>按深度把 xml 投影成文本。</summary>
    private static void ProjectXml(StringBuilder builder, XElement element, int depth, int level, string indent)
    {
        if (depth > 0 && level >= depth && element.HasElements)
        {
            builder.Append($"…（此处还有 {element.ToString().Length} 字符未展开）");
            return;
        }

        builder.Append(indent).Append('<').Append(element.Name.LocalName);
        foreach (XAttribute attribute in element.Attributes())
        {
            builder.Append(' ').Append(attribute.Name.LocalName).Append("=\"").Append(attribute.Value).Append('"');
        }

        if (!element.HasElements && element.Value.Length == 0)
        {
            builder.Append(" />");
            return;
        }

        if (!element.HasElements)
        {
            builder.Append('>').Append(element.Value).Append("</").Append(element.Name.LocalName).Append('>');
            return;
        }

        builder.AppendLine(">");
        string inner = indent + "  ";
        foreach (XElement child in element.Elements())
        {
            ProjectXml(builder, child, depth, level + 1, inner);
            builder.AppendLine();
        }

        builder.Append(indent).Append("</").Append(element.Name.LocalName).Append('>');
    }

    /// <summary>
    /// 写回 xml：带上原来的 <c>&lt;?xml ?&gt;</c> 声明（原来没有声明就不加）。
    /// 声明自己拼，不用 <c>Save(TextWriter)</c> —— 那条路会按 StringWriter 的编码把声明改写成 utf-16。
    /// </summary>
    private static string WriteXml(XDocument xml)
    {
        StringBuilder builder = new();
        if (xml.Declaration != null)
        {
            builder.Append("<?xml version=\"").Append(xml.Declaration.Version ?? "1.0").Append('"');
            if (xml.Declaration.Encoding != null)
            {
                builder.Append(" encoding=\"").Append(xml.Declaration.Encoding).Append('"');
            }

            if (xml.Declaration.Standalone != null)
            {
                builder.Append(" standalone=\"").Append(xml.Declaration.Standalone).Append('"');
            }

            builder.Append("?>").AppendLine();
        }

        // 用整个文档（root 前面的注释也要写），不是只有 root
        builder.Append(xml.ToString());

        return builder.ToString();
    }

    /// <summary>把当前内容序列化回文本（按原格式）。</summary>
    public string Serialize()
    {
        if (_xml != null)
        {
            return _xml.Root == null ? "" : WriteXml(_xml);
        }

        if (_json == null)
        {
            return "";
        }

        switch (_format)
        {
            case "json":
                return _json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            case "yaml":
                return WriteYaml(_json);

            case "toml":
                return WriteToml(_json);

            case "ini":
                return WriteIni(_json);

            default:
                throw new NotSupportedException($"不支持的格式：{_format}");
        }
    }

    /// <summary>json 树 → yaml 文本。</summary>
    private static string WriteYaml(JsonNode root)
    {
        return new SerializerBuilder().Build().Serialize(ToValue(root));

        static object? ToValue(JsonNode? node)
        {
            switch (node)
            {
                case null:
                    return null;

                case JsonObject holder:
                {
                    Dictionary<object, object?> result = new();
                    foreach (KeyValuePair<string, JsonNode?> pair in holder)
                    {
                        result[pair.Key] = ToValue(pair.Value);
                    }

                    return result;
                }

                case JsonArray list:
                {
                    List<object?> result = [];
                    foreach (JsonNode? element in list)
                    {
                        result.Add(ToValue(element));
                    }

                    return result;
                }

                default:
                    return ScalarOf(node);
            }
        }

        static object? ScalarOf(JsonNode node)
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue(out string? text))
                {
                    return text;
                }

                if (value.TryGetValue(out bool flag))
                {
                    return flag;
                }

                if (value.TryGetValue(out long number))
                {
                    return number;
                }

                if (value.TryGetValue(out double real))
                {
                    return real;
                }
            }

            return node.ToJsonString();
        }
    }

    /// <summary>
    /// json 树 → toml 文本。这里不用 <c>TomlSerializer</c>：它会给"只有子表的表"多写一行空表头
    /// （先 <c>[tool]</c> 再 <c>[tool.black]</c>，而后者本身就隐含了 tool）。
    /// 自己写还顺手管住了日期：<c>DateOnly</c> / <c>TimeOnly</c> / <c>DateTime</c> 不加引号。
    /// </summary>
    private static string WriteToml(JsonNode root)
    {
        if (root is not JsonObject top)
        {
            throw new InvalidDataException("toml 的顶层得是表。");
        }

        StringBuilder builder = new();
        WriteTableBody(builder, top, "");
        return builder.ToString();
    }

    /// <summary>把一个表写出去：先写它自己的值键，再递归它的子表 / 表数组。</summary>
    private static void WriteTableBody(StringBuilder builder, JsonObject table, string path)
    {
        foreach (KeyValuePair<string, JsonNode?> pair in table)
        {
            if (IsTableLike(pair.Value))
            {
                continue;
            }

            builder.Append(KeyOf(pair.Key)).Append(" = ").Append(LiteralOf(pair.Value)).AppendLine();
        }

        foreach (KeyValuePair<string, JsonNode?> pair in table)
        {
            string child = path.Length == 0 ? KeyOf(pair.Key) : path + "." + KeyOf(pair.Key);
            if (pair.Value is JsonObject sub)
            {
                WriteSubTable(builder, sub, child);
                continue;
            }

            if (pair.Value is JsonArray rows && rows.Count > 0 && rows.All(row => row is JsonObject))
            {
                WriteTableArray(builder, rows, child);
            }
        }
    }

    /// <summary>这个东西该当成"表"写（对象，或全是对象的数组），而不是当成一个值。</summary>
    private static bool IsTableLike(JsonNode? node)
    {
        if (node is JsonObject)
        {
            return true;
        }

        return node is JsonArray rows && rows.Count > 0 && rows.All(row => row is JsonObject);
    }

    /// <summary>写子表：只有"这一层自己有值键"时才写表头，免得出现空的 <c>[tool]</c>。</summary>
    private static void WriteSubTable(StringBuilder builder, JsonObject table, string path)
    {
        bool hasValues = false;
        foreach (KeyValuePair<string, JsonNode?> pair in table)
        {
            if (!IsTableLike(pair.Value))
            {
                hasValues = true;
                break;
            }
        }

        if (hasValues)
        {
            Blank(builder);
            builder.Append('[').Append(path).Append(']').AppendLine();
        }

        WriteTableBody(builder, table, path);
    }

    /// <summary>写表数组：每一行一个 <c>[[path]]</c>。</summary>
    private static void WriteTableArray(StringBuilder builder, JsonArray rows, string path)
    {
        foreach (JsonNode? row in rows)
        {
            Blank(builder);
            builder.Append("[[").Append(path).Append("]]").AppendLine();
            if (row is JsonObject item)
            {
                WriteTableBody(builder, item, path);
            }
        }
    }

    /// <summary>块与块之间空一行（开头不空）。</summary>
    private static void Blank(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.AppendLine();
            return;
        }

        if (builder.Length > 1 && builder[^2] != '\n')
        {
            builder.AppendLine();
        }
    }

    /// <summary>键的写法：能当裸键就当裸键，否则加引号。</summary>
    private static string KeyOf(string key)
    {
        bool bare = key.Length > 0;
        foreach (char letter in key)
        {
            if (!char.IsAsciiLetterOrDigit(letter) && letter != '_' && letter != '-')
            {
                bare = false;
                break;
            }
        }

        return bare ? key : QuoteOf(key);
    }

    /// <summary>一个值的 toml 字面量。</summary>
    private static string LiteralOf(JsonNode? node)
    {
        if (node is JsonArray list)
        {
            StringBuilder items = new();
            items.Append('[');
            for (int index = 0; index < list.Count; index++)
            {
                if (index > 0)
                {
                    items.Append(", ");
                }

                items.Append(LiteralOf(list[index]));
            }

            items.Append(']');
            return items.ToString();
        }

        if (node is JsonObject inline)
        {
            StringBuilder fields = new();
            fields.Append("{ ");
            int written = 0;
            foreach (KeyValuePair<string, JsonNode?> pair in inline)
            {
                if (written > 0)
                {
                    fields.Append(", ");
                }

                fields.Append(KeyOf(pair.Key)).Append(" = ").Append(LiteralOf(pair.Value));
                written++;
            }

            fields.Append(" }");
            return fields.ToString();
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out DateOnly day))
            {
                return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue(out TimeOnly clock))
            {
                return clock.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue(out DateTime moment))
            {
                string literal = moment.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                return moment.Kind == DateTimeKind.Utc ? literal + "Z" : literal;
            }

            if (value.TryGetValue(out DateTimeOffset offset))
            {
                return offset.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue(out string? text) && text != null)
            {
                return QuoteOf(text);
            }

            if (value.TryGetValue(out bool flag))
            {
                return flag ? "true" : "false";
            }

            if (value.TryGetValue(out long number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue(out double real))
            {
                return real.ToString("R", CultureInfo.InvariantCulture);
            }
        }

        // toml 没有 null；其它认不出的东西原样写成 json（宁可难看，也别丢掉）
        return node?.ToJsonString() ?? "{}";
    }

    /// <summary>toml 基本字符串：转义引号、反斜杠和控制字符。</summary>
    private static string QuoteOf(string text)
    {
        StringBuilder builder = new();
        builder.Append('"');
        foreach (char letter in text)
        {
            switch (letter)
            {
                case '"':
                    builder.Append("\\\"");
                    break;

                case '\\':
                    builder.Append("\\\\");
                    break;

                case '\n':
                    builder.Append("\\n");
                    break;

                case '\r':
                    builder.Append("\\r");
                    break;

                case '\t':
                    builder.Append("\\t");
                    break;

                default:
                    if (char.IsControl(letter))
                    {
                        builder.Append("\\u").Append(((int)letter).ToString("X4", CultureInfo.InvariantCulture));
                        break;
                    }

                    builder.Append(letter);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>json 树 → ini 文本：先写顶层标量，再逐个 section。</summary>
    private static string WriteIni(JsonNode root)
    {
        if (root is not JsonObject holder)
        {
            throw new InvalidDataException("ini 的顶层得是对象。");
        }

        StringBuilder builder = new();
        foreach (KeyValuePair<string, JsonNode?> pair in holder)
        {
            if (pair.Value is not JsonObject)
            {
                builder.AppendLine($"{pair.Key}={TextOf(pair.Value)}");
            }
        }

        foreach (KeyValuePair<string, JsonNode?> pair in holder)
        {
            if (pair.Value is not JsonObject section)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine($"[{pair.Key}]");
            foreach (KeyValuePair<string, JsonNode?> entry in section)
            {
                builder.AppendLine($"{entry.Key}={TextOf(entry.Value)}");
            }
        }

        return builder.ToString();

        static string TextOf(JsonNode? node)
        {
            if (node is JsonValue value && value.TryGetValue(out string? text))
            {
                return text;
            }

            return node?.ToJsonString() ?? "";
        }
    }

    /// <summary>按定位表达式找出全部命中：json 线走点路径，xml 线走 XPath。</summary>
    public IReadOnlyList<object> Find(string point)
    {
        if (_xml != null)
        {
            object? evaluated = _xml.XPathEvaluate(point);
            List<object> found = [];
            if (evaluated is IEnumerable<object> many)
            {
                foreach (object item in many)
                {
                    found.Add(item);
                }
            }
            else if (evaluated != null)
            {
                found.Add(evaluated);
            }

            return found;
        }

        return _json == null ? [] : FindJson(_json, point);
    }

    /// <summary>json 点路径：$.a.b[0] 与 $..name（递归下降）。</summary>
    private static IReadOnlyList<object> FindJson(JsonNode root, string point)
    {
        string trimmed = point.Trim();
        if (!trimmed.StartsWith('$'))
        {
            throw new InvalidOperationException($"json 的定位要从 $ 开始：{point}");
        }

        List<JsonNode> current = [root];
        int index = 1;
        while (index < trimmed.Length)
        {
            bool deep = false;
            string step;
            if (trimmed[index] == '.')
            {
                if (index + 1 < trimmed.Length && trimmed[index + 1] == '.')
                {
                    deep = true;
                    index += 2;
                }
                else
                {
                    index++;
                }

                int start = index;
                while (index < trimmed.Length && trimmed[index] != '.' && trimmed[index] != '[')
                {
                    index++;
                }

                step = trimmed[start..index];
            }
            else if (trimmed[index] == '[')
            {
                int end = trimmed.IndexOf(']', index);
                if (end < 0)
                {
                    throw new InvalidOperationException($"方括号没闭合：{point}");
                }

                step = trimmed[index..(end + 1)];
                index = end + 1;
            }
            else
            {
                throw new InvalidOperationException($"看不懂的定位：{point}");
            }

            List<JsonNode> next = [];
            foreach (JsonNode node in current)
            {
                if (deep)
                {
                    Gather(node, step, next);
                }
                else
                {
                    JsonNode? child = Step(node, step);
                    if (child != null)
                    {
                        next.Add(child);
                    }
                }
            }

            current = next;
        }

        List<object> found = [];
        foreach (JsonNode node in current)
        {
            found.Add(node);
        }

        return found;

        static JsonNode? Step(JsonNode node, string selector)
        {
            if (selector.StartsWith('['))
            {
                int slot = int.TryParse(selector[1..^1], out int parsed) ? parsed : -1;
                return node is JsonArray list && slot >= 0 && slot < list.Count ? list[slot] : null;
            }

            return node is JsonObject holder && holder.TryGetPropertyValue(selector, out JsonNode? child) ? child : null;
        }

        static void Gather(JsonNode node, string selector, List<JsonNode> found)
        {
            JsonNode? self = Step(node, selector);
            if (self != null)
            {
                found.Add(self);
            }

            if (node is JsonObject holder)
            {
                foreach (KeyValuePair<string, JsonNode?> pair in holder)
                {
                    if (pair.Value != null)
                    {
                        Gather(pair.Value, selector, found);
                    }
                }
            }
            else if (node is JsonArray list)
            {
                foreach (JsonNode? item in list)
                {
                    if (item != null)
                    {
                        Gather(item, selector, found);
                    }
                }
            }
        }
    }

    /// <summary>把命中的东西展示成文本，超过 length 就截断。</summary>
    public string Show(object node, int length)
    {
        string text = node switch
        {
            JsonNode json => json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            XAttribute attribute => attribute.Value,
            XNode xml => xml.ToString(),
            _ => node.ToString() ?? "",
        };

        return Cut(text, length);
    }

    /// <summary>把命中的节点换成新内容（新内容按本文档的格式解析）。</summary>
    public void Replace(object node, string value)
    {
        switch (node)
        {
            case JsonNode json:
                json.ReplaceWith(ParseValue(value));
                return;

            case XAttribute attribute:
                attribute.Value = value;
                return;

            case XText text:
                text.Value = value;
                return;

            case XElement element:
                element.ReplaceWith(XElement.Parse(value));
                return;

            default:
                throw new InvalidOperationException("这个位置改不了。");
        }
    }

    /// <summary>在 point 指向的父容器里插入新内容。</summary>
    public void Insert(string point, string value)
    {
        (string parentPath, string last) = SplitLast(point);

        if (_xml != null)
        {
            XElement parent = _xml.XPathSelectElement(parentPath)
                ?? throw new InvalidOperationException($"没命中：{parentPath}");
            parent.Add(XElement.Parse(value));
            return;
        }

        IReadOnlyList<object> containers = Find(parentPath);
        if (containers.Count != 1 || containers[0] is not JsonNode container)
        {
            throw new InvalidOperationException($"要先定位父容器，但 {parentPath} 命中了 {containers.Count} 处。");
        }

        if (last.StartsWith('['))
        {
            if (container is not JsonArray list)
            {
                throw new InvalidOperationException("这个位置不是数组，插不了索引。");
            }

            int slot = int.TryParse(last[1..^1], out int parsed) ? parsed : -1;
            if (slot < 0 || slot > list.Count)
            {
                throw new InvalidOperationException($"索引越界：{last}（数组有 {list.Count} 个元素）。");
            }

            list.Insert(slot, ParseValue(value));
            return;
        }

        if (container is not JsonObject holder)
        {
            throw new InvalidOperationException("这个位置不是对象，插不了键。");
        }

        if (holder.ContainsKey(last))
        {
            throw new InvalidOperationException($"键已存在：{last}（已存在就是改，用 update）。");
        }

        holder[last] = ParseValue(value);
    }

    /// <summary>把定位表达式拆成父路径 + 最后一段。</summary>
    private static (string Parent, string Last) SplitLast(string point)
    {
        int cut = Math.Max(Math.Max(point.LastIndexOf('.'), point.LastIndexOf('[')), point.LastIndexOf('/'));
        if (cut <= 0)
        {
            throw new InvalidOperationException($"插不进去：{point}");
        }

        return (point[..cut], point[cut..].TrimStart('.', '/'));
    }

    /// <summary>把命中的节点删掉。</summary>
    public void Remove(object node)
    {
        switch (node)
        {
            case JsonNode json:
                RemoveJson(json);
                return;

            case XAttribute attribute:
                attribute.Remove();
                return;

            case XNode xml:
                xml.Remove();
                return;

            default:
                throw new InvalidOperationException("这个位置删不了。");
        }
    }

    /// <summary>从它的父对象 / 数组里把节点摘掉。</summary>
    private static void RemoveJson(JsonNode node)
    {
        if (node.Parent is JsonObject holder && node.GetPropertyName() is string name)
        {
            holder.Remove(name);
            return;
        }

        if (node.Parent is JsonArray list)
        {
            list.Remove(node);
            return;
        }

        throw new InvalidOperationException("命中的是根节点，删不了。");
    }

    /// <summary>按上限截断文本，超了就在末尾说明省掉多少。</summary>
    private static string Cut(string text, int length)
    {
        int cap = length <= 0 ? 0 : Math.Min(length, 5000);
        if (cap == 0 || text.Length <= cap)
        {
            return text;
        }

        return text[..cap] + $"{Environment.NewLine}（已省略 {text.Length - cap} 字符）";
    }
}
