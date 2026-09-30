namespace ZMS.MCP.Structured.Tools;

/// <summary>
/// 结构化文档的读写。写操作统一走 cookie —— cookie 就是文档结构的指纹，读一次拿一个，写的时候带回来。
/// 编码：给了 encoding 就按它解和写；没给就看 BOM，再看能不能**严格**解成 UTF-8，解不出来一律拒绝 ——
/// 绝不"硬按 UTF-8 读、再按 UTF-8 写回"，那会把 GBK 之类的中文整片毁掉。
/// </summary>
[McpServerToolType]
public static partial class StructuredTools
{
    /// <summary>进程起来时注册一次代码页注册器，这样 gbk / gb18030 这类编码名才认。</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Read a whole structured document, or one location inside it. " +
        "point = location expression; empty = the whole document. " +
        "json uses JsonPath, xml uses XPath, yaml and toml use dot paths, ini uses section.key. " +
        "depth limits how deep the tree is expanded (0 = unlimited); whatever is hidden that way is reported as a character count. " +
        "length caps the characters shown (default 500, max 5000). " +
        "encoding names the text encoding (utf-8, utf-16, gb18030, ...); empty = detect from a BOM, then by strictly trying UTF-8, and refuse anything else. " +
        "Reading the whole document also returns a cookie that edit_structured needs; reading one location does not.")]
    public static string ReadStructured(
        [Description("File path")] string path,
        [Description("Location expression; empty = the whole document")] string point = "",
        [Description("Expansion depth; 0 = unlimited")] int depth = 0,
        [Description("Max characters to show; default 500, max 5000")] int length = 500,
        [Description("Explicit format (json/xml/yaml/toml/ini); empty = infer from the extension")] string format = "",
        [Description("Explicit encoding name (utf-8 / utf-16 / gb18030 ...); empty = detect")] string encoding = "")
    {
        try
        {
            return Read(path, point, depth, length, format, encoding);
        }
        catch (Exception exception)
        {
            return McpStdioServer.FailurePrefix + exception.Message;
        }
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Change one location in a structured document, insert, delete, or replace the whole document. " +
        "cookie = the one read_structured returned for this file; the file must not have changed since. " +
        "point = location expression; empty = the whole document, and then value is the whole new document. " +
        "value = the new value; null means delete. " +
        "The switches say what this call is allowed to do; if the needed one is off, nothing is written and the matches are reported instead: " +
        "update = rewrite a match, remove = delete when value is null, insert = create when nothing matched, multi = act on every match. " +
        "encoding works like in read_structured, and the file is written back in the encoding it was read with.")]
    public static string EditStructured(
        [Description("Cookie from read_structured")] string cookie,
        [Description("File path")] string path,
        [Description("Location expression; empty = the whole document")] string point = "",
        [Description("New value; null deletes. With an empty point it is the whole new document")] string? value = null,
        [Description("Explicit format (json/xml/yaml/toml/ini); empty = infer from the extension")] string format = "",
        [Description("Explicit encoding name (utf-8 / utf-16 / gb18030 ...); empty = detect")] string encoding = "",
        [Description("Allow creating when the point matches nothing")] bool insert = false,
        [Description("Allow deleting when value is null")] bool remove = false,
        [Description("Allow rewriting when the point matches")] bool update = false,
        [Description("Allow acting on every match instead of refusing when several match")] bool multi = false)
    {
        try
        {
            return Edit(cookie, path, point, value, format, encoding, insert, remove, update, multi);
        }
        catch (Exception exception)
        {
            return McpStdioServer.FailurePrefix + exception.Message;
        }
    }

    private static string Read(string path, string point, int depth, int length, string format, string encoding)
    {
        string full = Path.GetFullPath(path);
        string resolved = FormatOf(full, format);
        LoadedFile loaded = Load(full, resolved, encoding);
        Document document = loaded.Document;

        if (point.Length > 0)
        {
            IReadOnlyList<object> hits = document.Find(point);
            if (hits.Count == 0)
            {
                throw new InvalidOperationException($"没命中：{point}（不自动创建）");
            }

            if (hits.Count == 1)
            {
                return document.Show(hits[0], length);
            }

            StringBuilder builder = new();
            builder.AppendLine($"# {full}（{resolved}）— `{point}`");
            builder.AppendLine($"- 命中 {hits.Count} 处（读只列出来，不带 cookie）");
            int index = 1;
            foreach (object hit in hits)
            {
                string oneLine = document.Show(hit, 200).Replace(Environment.NewLine, " ");
                builder.AppendLine($"- {index}. {oneLine}");
                index++;
            }

            return builder.ToString();
        }

        return $"# {full}（{resolved}）{Environment.NewLine}"
            + $"- 结构：{document.Shape()}{Environment.NewLine}"
            + $"- 编码：{loaded.Describe()}{Environment.NewLine}"
            + $"- cookie：`{CookieOf(document)}`{Environment.NewLine}{Environment.NewLine}"
            + document.ShowBody(length, depth);
    }

    /// <summary>编辑的公用流程：校 cookie（结构指纹）→ 按开关做动作 → 落盘。</summary>
    private static string Edit(
        string cookie,
        string path,
        string point,
        string? value,
        string format,
        string encoding,
        bool insert,
        bool remove,
        bool update,
        bool multi)
    {
        string full = Path.GetFullPath(path);
        LoadedFile loaded = Load(full, format, encoding);
        Document document = loaded.Document;
        if (!string.Equals(CookieOf(document), cookie, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("文件和你读的时候不一样了（或者 cookie 不对）：重新 read_structured 再改。");
        }

        string action;
        if (point.Length == 0)
        {
            if (value == null)
            {
                throw new InvalidOperationException("point 为空 + value 为空 = 清空整份文档，不做。");
            }

            if (!update)
            {
                return $"# 没有写入{Environment.NewLine}"
                    + $"- 定位：`（整份文档）`{Environment.NewLine}"
                    + $"- 原因：update 没开{Environment.NewLine}"
                    + $"{Environment.NewLine}{document.ShowBody(800, 0)}";
            }

            document = Document.Parse(document.Format, value);
            action = "整份替换";
        }
        else
        {
            IReadOnlyList<object> hits = document.Find(point);
            if (hits.Count == 0)
            {
                if (value == null)
                {
                    throw new InvalidOperationException($"没命中：{point}，也没有值可插。");
                }

                if (!insert)
                {
                    return Refuse(document, point, hits, "没命中，且 insert 没开");
                }

                document.Insert(point, value);
                action = $"插入 {point}";
            }
            else if (hits.Count > 1 && !multi)
            {
                throw new InvalidOperationException($"命中 {hits.Count} 处，multi 没开：要么收紧表达式，要么打开 multi。");
            }
            else if (value == null)
            {
                if (!remove)
                {
                    return Refuse(document, point, hits, "value 为空要删，但 remove 没开");
                }

                foreach (object hit in hits)
                {
                    document.Remove(hit);
                }

                action = $"删除 {point}（{hits.Count} 处）";
            }
            else
            {
                if (!update)
                {
                    return Refuse(document, point, hits, "命中了要改，但 update 没开");
                }

                List<object> targets = [];
                foreach (object hit in hits)
                {
                    if (!document.SameAs(hit, value))
                    {
                        targets.Add(hit);
                    }
                }

                if (targets.Count == 0)
                {
                    return "值没有变化，未写入。";
                }

                foreach (object target in targets)
                {
                    document.Replace(target, value);
                }

                action = $"改写 {point}（{targets.Count} 处）";
            }
        }

        Save(full, document.Serialize(), loaded);
        return $"# 已落盘{Environment.NewLine}"
            + $"- 文件：{full}{Environment.NewLine}"
            + $"- 动作：{action}{Environment.NewLine}"
            + $"- 结构：{document.Shape()}{Environment.NewLine}"
            + $"- 编码：{loaded.Describe()}{Environment.NewLine}"
            + $"- 现在的 cookie：`{CookieOf(document)}`";
    }

    /// <summary>读盘结果：文档 + 它按什么编码读进来的（写回要用同一份）。</summary>
    private sealed record LoadedFile(Document Document, Encoding Encoding, bool Bom)
    {
        public string Describe()
        {
            return Bom ? Encoding.WebName + "（带 BOM）" : Encoding.WebName;
        }
    }

    /// <summary>按格式和编码把文件读成一份文档。</summary>
    private static LoadedFile Load(string full, string format, string encoding)
    {
        if (Directory.Exists(full))
        {
            throw new InvalidOperationException($"{full} 是目录，不是文件。");
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"文件不存在：{full}");
        }

        byte[] bytes = File.ReadAllBytes(full);
        (Encoding codec, bool bom, int offset) = Detect(full, bytes, encoding);
        string text = codec.GetString(bytes, offset, bytes.Length - offset);

        return new LoadedFile(Document.Parse(FormatOf(full, format), text), codec, bom);
    }

    /// <summary>
    /// 认编码。给了 encoding 就按它解（解不出来报错，不糊弄）；没给就看 BOM，再看能不能**严格**解成 UTF-8，
    /// 解不出来一律拒绝。
    /// </summary>
    private static (Encoding Encoding, bool Bom, int Offset) Detect(string full, byte[] bytes, string encoding)
    {
        (Encoding? fromBom, bool bom, int offset) = BomOf(bytes);
        string given = encoding.Trim();

        if (given.Length > 0)
        {
            Encoding wanted = Named(given);

            try
            {
                _ = wanted.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException($"{full} 按 {given} 解不出来（编码名给错了？）。");
            }

            return (Align(wanted, bom), bom, offset);
        }

        if (fromBom != null)
        {
            return (fromBom, bom, offset);
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException(
                $"{full} 不是 UTF-8（没有 BOM，严格 UTF-8 也解不出来）。用 encoding 显式指定，或先把它转成 UTF-8。");
        }

        return (new UTF8Encoding(false), false, 0);
    }

    /// <summary>看开头有没有 BOM；有就顺手给出对应的编码（已经带 BOM 标记）。</summary>
    private static (Encoding? FromBom, bool Bom, int Offset) BomOf(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(true), true, 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(false, true), true, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(true, true), true, 2);
        }

        return (null, false, 0);
    }

    /// <summary>按名字取一个"解不出来就报错"的编码。</summary>
    private static Encoding Named(string name)
    {
        try
        {
            return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"认不出这个编码名：{name}（试试 utf-8 / utf-16 / gb18030 / gbk）。");
        }
    }

    /// <summary>把编码的 BOM 行为对齐到"原文件本来有没有 BOM"。</summary>
    private static Encoding Align(Encoding codec, bool bom)
    {
        if (codec is UTF8Encoding)
        {
            return new UTF8Encoding(bom);
        }

        if (codec is UnicodeEncoding)
        {
            return new UnicodeEncoding(codec.CodePage == 1201, bom);
        }

        return codec;
    }

    /// <summary>结构指纹就是 cookie：同样的结构永远算出同一串，不存任何状态。</summary>
    private static string CookieOf(Document document)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(document.Structure()));
        return Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }

    /// <summary>没有写入时的回报：把命中到的内容摆出来，让调用方自己决定怎么做。</summary>
    private static string Refuse(Document document, string point, IReadOnlyList<object> hits, string reason)
    {
        StringBuilder builder = new();
        builder.AppendLine("# 没有写入");
        builder.AppendLine($"- 定位：`{point}`");
        builder.AppendLine($"- 原因：{reason}");
        builder.AppendLine();
        if (hits.Count == 0)
        {
            builder.AppendLine("（没有命中任何位置）");
            return builder.ToString();
        }

        builder.AppendLine($"- 命中 {hits.Count} 处：");
        foreach (object hit in hits)
        {
            string oneLine = document.Show(hit, 200).Replace(Environment.NewLine, " ");
            builder.AppendLine($"  - {oneLine}");
        }

        return builder.ToString();
    }

    /// <summary>按显式参数或扩展名定格式。</summary>
    private static string FormatOf(string path, string format)
    {
        string given = format.Trim().ToLowerInvariant();
        if (given.Length > 0)
        {
            return given;
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".json" => "json",
            ".xml" => "xml",
            ".yaml" or ".yml" => "yaml",
            ".toml" => "toml",
            ".ini" => "ini",
            _ => throw new InvalidOperationException(
                $"看不懂扩展名 {extension}，请用 format 显式指定（json / xml / yaml / toml / ini）。"),
        };
    }

    /// <summary>落盘：按读进来时的编码写回（BOM 状态也照原样），原子替换。</summary>
    private static void Save(string full, string text, LoadedFile loaded)
    {
        string temporary = full + ".zms-tmp";
        File.WriteAllText(temporary, text, loaded.Encoding);
        File.Move(temporary, full, true);
    }
}
