namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// structured 协议处理器：读写结构化文档（json/xml/yaml/ini/toml/csharp 统一）。
///
/// 职责（待实现）：
/// 1. 格式判定：userinfo 断言（json@/xml@/yaml@/ini@/toml@/csharp@）优先，
///    否则按 path 扩展名推断（.json/.xml/.yaml/.yml/.ini/.toml/.cs）。
/// 2. 只访问本地文件（远程结构化文档先走 http 下载到本地再读）。
/// 3. action（数据格式 json/xml/yaml/ini/toml）：
///    - GET：读取并解析文档；fragment 给定位路径则返回定位结果，否则整个文档。
///    - PUT：整体覆盖写（body=完整新文档）。
///    - PATCH：局部改（fragment 定位 + body=新值，替换该节点）。
///    - POST：插入/追加（fragment 定位 + body=新节点）。
///    - DELETE：删节点（fragment 定位；必须给 fragment，防误删整个文档）。
///    写操作（PUT/PATCH/POST/DELETE）必须带格式断言 + fragment，否则拒绝。
/// 4. fragment 定位语法按格式：
///    JSON=JSONPath（$.a.b[0]）；XML=XPath（/root/item[1]）；
///    YAML/TOML=点路径（a.b.c）；INI=section.key。
/// 5. query 全选填，默认 depth=3、max=100KB：
///    - depth：最大展开深度；max：最大输出字符。
///    - 超过上限不静默截断，返回"（结果过长，总深度 {N}，展开深度 {K}；
///      要更多请用 ?depth=5&max=500KB 重试）"。
/// 6. 解析器：JSON=System.Text.Json；XML=System.Xml；
///    YAML=YamlDotNet（已引包）；TOML=Tomlyn（已引包）；INI=手写/轻量解析。
/// 7. 输出：格式化文档/定位结果，Markdown 代码块包裹。
///
/// ── csharp@ 代码切片（不用 Roslyn，文本级符号索引）──
/// 核心动机：AI 改一个功能不该读整个 100KB 文件。像 VS 顶栏一样，
/// 按 命名空间→类→成员 切成成员级块，只读/只改指定成员。
/// 最细粒度到【方法/属性/字段】，不到 if（控制流无独立签名行，无法稳定切分）。
///
/// 符号索引（用 Roslyn，不用文本级扫描）：
///   已原型验证：文本级行扫描能处理 90% 边界（类中类/namespace 嵌套/局部函数/单行表达式体/
///   主构造器/多行泛型约束/注释字符串花括号/#if），但【多行复杂表达式体】是硬伤——
///   lambda + switch 表达式 + 三元 + 空合并（如 `int Age => _field ??= InitAge(a => a switch {...})`）
///   会被误判成多个"属性/方法"（switch 的 `1 => xxx` 被当成属性）。继续加规则会变成半个 Roslyn。
///   结论：直接用 Roslyn（Microsoft.CodeAnalysis.CSharp），语法树节点带精确行号，
///   多行表达式体等一切边界 100% 准确。
///   - CSharpSyntaxTree.ParseText → 根节点 DescendantNodes 遍历
///   - MethodDeclaration/PropertyDeclaration/FieldDeclaration/ClassDeclaration/... 
///   - 每个节点 GetLocation().GetLineSpan() 给精确行列区间
///   - 得符号表：{name, kind, startLine, endLine}，PROPFIND 列成 命名空间→类→成员 树。
///
/// 已知边界（Roslyn 全部精确处理）：
///   - 类中类、namespace 嵌套、局部函数（归入外层方法，不单列）。
///   - 多行表达式体（lambda/switch/三元/??/?:）→ 精确整段行号。
///   - 字段/属性一行、表达式体 =>、主构造器、多行泛型约束、#if 分支 → 全精确。
///   - 唯一注意：Roslyn 加载 ~100MB 包较慢，PROPFIND 快速列树可先用轻量文本扫描
///     （已验证明细在 script/csharp-symbol-index/），GET/PATCH 精确切片用 Roslyn。
///
/// 三层降级链（定位失败逐级降）：
///   1. 符号路径（#Foo.Bar）→ Roslyn 精确到成员文本块。
///   2. 行号区间（?startLine&endLine）→ 用行看/改（复用 file 的 line/offset/max）。
///   3. 文本匹配（body 给 old/new）→ 精确 → 空白对齐 → 最相似行提示（仿 irmia_devkit_mcp：
///     返回"最接近的行 #N: xxx，建议复制重试"，绝不自动替换）。
///
/// action（csharp@）：
///   - GET：读指定成员切片（fragment 符号路径；缺省=整个文件符号树预览）。
///   - PROPFIND：列符号树（命名空间→类→成员 + 行号）。
///   - PATCH：改指定成员（fragment=符号路径或行号区间；body=新成员体/新片段）。
///   - DELETE：删指定成员。
///   - PUT：整体覆盖文件（body=完整新内容）。
///   写操作返回【改动行号报告】（可审计）：
///     ## 修改结果
///     - 目标: Foo.Bar (Program.cs)
///     - 改动行: 120-145（替换 26 行 → 30 行）
///     - 原文: 第 120 行: `public void Bar() { ...`
///     - 新文: 第 120 行: `public void Bar() { ...`
/// </summary>
public sealed class StructuredProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "structured";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现结构化文档读写（见类注释），YAML/TOML 依赖已引包。
        // csharp@ 符号索引见 CSharpSymbolIndex（原型验证中）。
        throw new NotImplementedException("StructuredProtocol 尚未实现。");
    }
}
