namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// sqlite 协议处理器：访问本地 SQLite 数据库，避免开命令行。
///
/// 职责（待实现）：
/// 1. path = 本地 .db/.sqlite 文件路径。
/// 2. fragment 定位：
///    - `#sql:SELECT ...` → 执行查询（只读），返回结果表。
///    - `#sql:UPDATE/DELETE/INSERT ...` → 写操作（必须 cookie 确认 + 行数预估）。
///    - `#表名` → 查看表结构（列、类型、索引，来自 sqlite_master/PRAGMA）。
///    - 空 → 列出所有表（sqlite_master）。
/// 3. 安全：
///    - SELECT 只读直接执行；输出限流（max 行/列，防全表爆输出）。
///    - 写操作（UPDATE/DELETE/INSERT）必须带 sqlite@ 断言 + 先预估影响行数
///      （EXPLAIN 或 COUNT 预查）+ cookie 两阶段确认。
///    - 参数化查询防注入（body/query 里的值走参数绑定，不拼 SQL 字符串）。
/// 4. query：max（最大返回行数，默认 100）、maxCol（最大列数）。
/// 5. 实现依赖 Microsoft.Data.Sqlite（轻量，纯托管）。
/// 6. 输出：Markdown 表格（列名 + 类型 + 行值）。
/// </summary>
public sealed class SqliteProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "sqlite";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现 SQLite 访问（见类注释），依赖 Microsoft.Data.Sqlite。
        throw new NotImplementedException("SqliteProtocol 尚未实现。");
    }
}
