namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// webdav 协议处理器：操作本地一切资源（TODO.md 的 webdav 协议）。
/// 用 WebDAV 方法名作为 action 词汇表，后端映射到 .NET 文件系统/解析器 API。
///
/// 职责（待实现）：
/// 1. host 环境变量根：nuget / cache / user / temp，表示这些文件夹的根；
///    path = 绝对路径，或有 host 时的相对路径（host 为 cache 时路径为 key）。
/// 2. userinfo = 类型断言：file@ / dir@ / archive@ / json@ / xml@ / yaml@ / toml@ / ini@ / csharp@。
///    有断言时地址必须为该格式，否则报错；基于断言有不一样的查询字符串和片段。
///    无断言时，除了 get 和 head 都报错；根据目标自动视作 file 或 dir。
/// 3. file@（文件）：GET 读文本（query skipline/takeline/offset/length/encoding；fragment=正则搜索，
///    结果按数量分级返回行+行号/上下1行/上下3行）；HEAD 元数据；POST/PUT 新建/覆写（POST 目标必须不存在）；
///    PATCH 替换部分（body 对齐 edit_file/multi_edit：old_string/new_string 或 edits 数组，删除=替换为空）；
///    POST 追加文本行；MOVE/COPY 移动复制（头 Destination/cookie）；DELETE 删→回收站。
/// 4. dir@（目录）：GET 列目录（query depth 缺省3/limit 缺省20/type 缺省 file&dir/meta；
///    fragment=搜索结果过滤）；HEAD 元数据；POST+dir@ 建目录（目标处无资源，免 cookie）；
///    DELETE 删→回收站。
/// 5. archive@（压缩包）：GET 读/列包内（fragment=容器内路径）；POST 创建压缩包
///    （path=包路径，body=内容清单+格式，源总量>10M 需所有源 cookie，覆写已有包需包 cookie）；
///    PUT/DELETE 包内写删（fragment=容器内路径，头 cookie）。
/// 6. json@ / xml@ / yaml@ / toml@ / ini@（结构化文档）：GET 读（query depth/length；
///    fragment=该语言查询表达式 JsonPath/XPath/其他）；PUT/PATCH/POST/DELETE 结构化写
///    （fragment=定位路径必带，头 cookie）。
/// 7. csharp@（C# 代码）：path=项目文件地址；GET 代码切片（fragment=完全限定符号名，Roslyn 项目解析）；
///    任何写操作都要 cookie（仅针对 Roslyn 解析出来的对象/类型字符串）；
///    写操作后返回改动文件地址和行号（防语法错误无法解析）。
/// 8. cookie 两阶段确认（破坏性操作：删除 / 移动目的有文件需覆盖 / 移动文件夹到已有文件夹合并 /
///    替换文件部分）：无 cookie 时转为 GET 并返回"本次操作被拒绝…携带cookie:xxx…展示get结果"；
///    带 cookie 重试且清单未变才执行；成功后返回下一个 cookie 用于链式修改。cookie = 16 位 GUID。
/// 9. 输出 Markdown：文件内容/目录树/属性表/搜索结果/结构化文档/操作结果。
/// </summary>
public sealed class WebDavProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "webdav";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现本地文件/目录/压缩容器/结构化文档/代码操作（见类注释）。
        throw new NotImplementedException("WebDavProtocol 尚未实现。");
    }
}
