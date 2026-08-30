namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// cmd 协议处理器：执行白名单命令（agent 不直接调命令行，一切命令被过滤）。
///
/// uri 语义（命令树走 path，参数走 body，固定 POST）：
///   cmd://git/status              → git status
///   cmd://git/log                 → git log（body 可加 --help、--grep="?" 等）
///   cmd://dotnet/new/console      → dotnet new console（多级命令树 = path 多段）
///   cmd:///C:/tools/custom.exe/run → 程序绝对路径（不在 PATH 时，host 空 + path 首段是 exe）
///
/// 职责（待实现）：
/// 1. host = 程序名（PATH 查找）；path = 命令树（每段一个纯单词子命令）；
///    head.path = 程序绝对路径（不在 PATH 时）；body = 所有参数（含 flag/值/作用目标）。
/// 2. action 固定 POST（HTTP 标准的"执行动作"语义；命令只有一个作用，无需 GET/PUT 区分）。
/// 3. 命令树放 path 的理由：树形结构可解析、uri 唯一确定命令、作用固定；
///    带符号的 flag（-h/--help/?/#/%/空格）全放 body，避免 URI 保留符号转义。
/// 4. 白名单（服务器配置 allowed_commands，精确到子命令）：
///    - 不在白名单 → 拒绝（"命令未授权"）。
///    - readonly 命令（git status/diff/log、dotnet build/test）→ 直接执行。
///    - destructive 命令（git reset/commit、dotnet clean 等）→ 先 dry-run 或列影响
///      + 返回 cookie → 带 cookie 再执行（必须知道自己在做什么）。
/// 5. 安全：进程隔离（超时、输出截断、工作目录限制、环境变量白名单）；
///    命令注入防护（参数数组传递，不拼 shell 字符串）。
/// 6. 输出：退出码 + stdout/stderr（截断）+ 超时/错误友好中文。
/// </summary>
public sealed class CmdProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "cmd";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现白名单命令执行（见类注释）。
        throw new NotImplementedException("CmdProtocol 尚未实现。");
    }
}
