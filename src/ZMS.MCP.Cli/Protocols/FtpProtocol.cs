namespace zms9110750.ZMS_MCP.Cli.Protocols;

/// <summary>
/// ftp 协议处理器：访问远程 FTP 服务器（典型场景：手机开 FTP，电脑互传文件）。
///
/// 职责（待实现）：
/// 1. action 映射到 FTP 命令（方法词汇表与 TODO.md ftp 对齐，后端=FTP 会话）：
///    - GET → RETR 下载文件内容（fragment 给本地下载路径，否则返回内容，>5k 截断）。
///    - POST → STOR 上传 / MKD 创建文件夹。
///    - MOVE → RNFR+RNTO 重命名/移动（目标在 body 或 head Destination）。
///    - DELETE → DELE（文件）/ RMD（目录）。
/// 2. userinfo = user:pass（默认匿名 anonymous@）；host 必填；port 缺省 21。
/// 3. 二进制文件：GET 时按 Content-Type/扩展名识别，返回大小或下载到本地
///    （fragment 给本地下载路径，走 http 同款限流池：容量 1M，恢复 1M/分钟，最大储存 10 分钟）。
/// 4. 实现依赖 FluentFTP 包（已引）：AsyncFtpClient，连接复用（单例会话），
///    支持 FTPS 可选。
/// 5. 破坏性操作（POST 覆盖已存在文件 / MOVE 覆盖 / DELETE）走 cookie 两阶段确认（见「破坏」）。
/// 6. 错误：连接失败/登录失败/文件不存在转友好中文消息。
/// </summary>
public sealed class FtpProtocol : IProtocolHandler
{
    /// <inheritdoc />
    public string Scheme => "ftp";

    /// <inheritdoc />
    public Task<string> HandleAsync(UriRequestContext ctx, CancellationToken ct = default)
    {
        // TODO: 实现 FTP 访问逻辑（见类注释），依赖 FluentFTP。
        throw new NotImplementedException("FtpProtocol 尚未实现。");
    }
}
