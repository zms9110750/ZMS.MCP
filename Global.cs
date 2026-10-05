// 所有 MCP 服务项目共享的基础设施：
// 1) MCP 相关的 global using；
// 2) 通用入口——扫描本程序集注册全部工具，日志走 stderr（stdout 留给协议）；
// 3) 工作空间边界——统一校验每次工具调用涉及的路径参数。
global using System.ComponentModel;
global using System.IO;
global using System.Text.Json;
global using System.Text.Json.Nodes;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
global using ModelContextProtocol.Protocol;
global using ModelContextProtocol.Server;

namespace ZMS.MCP;

/// <summary>
/// MCP stdio 服务的通用入口。各 MCP 项目只需：
/// <code>await McpStdioServer.RunAsync(args);</code>
/// </summary>
internal static class McpStdioServer
{
    /// <summary>
    /// 构建并运行 MCP stdio 服务。
    /// </summary>
    /// <param name="args">命令行参数，透传给 <see cref="Host.CreateApplicationBuilder(string[])"/>。</param>
    /// <param name="configure">可选：追加自定义配置，如请求/消息过滤器。</param>
    public static async Task RunAsync(string[] args, Action<IMcpServerBuilder>? configure = null)
    {
        await Build(args, configure).RunAsync();
    }

    /// <summary>
    /// 构建 Host（未启动）。默认已注册：stdio 传输 + 扫描本程序集里的全部 MCP 工具 + 工作空间边界检查。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="configure">可选：追加自定义配置，如请求/消息过滤器。</param>
    public static IHost Build(string[] args, Action<IMcpServerBuilder>? configure = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // MCP 走 stdout，日志一律写 stderr，避免污染协议流
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        IMcpServerBuilder mcp = builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            // 所有工具调用都先过这一道：路径参数必须在工作空间边界内。
            // 它拦在**工具体之前**（next 才是工具），所以越界时工具一个字节都不会碰。
            .WithRequestFilters(filters => filters.AddCallToolFilter(next =>
                async (context, cancellationToken) =>
                {
                    string? refusal = ToolScope.Refuse(context);
                    if (refusal != null)
                    {
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = refusal }],
                            IsError = true,
                        };
                    }

                    CallToolResult result;
                    try
                    {
                        result = await next(context, cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        // 工具抛出来的异常在这里收口：调用方看到的永远是 "Error: ..." + isError=true，
                        // 而不是一条裸异常（裸异常会让 MCP 只回一句 “An error occurred invoking 'x'.”）。
                        // 有了这道口，工具方法里就不必再自己 try/catch 包一层了。
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = FailurePrefix + exception.Message }],
                            IsError = true,
                        };
                    }

                    // 工具把失败写成 "Error: ..." 文本（那是个约定），回来时把它翻成协议层的信号：
                    // 否则调用方只能靠读文本猜"到底成没成"，而 isError 一直说"成功"。
                    if (result.IsError != true && Failed(result))
                    {
                        result.IsError = true;
                    }

                    return result;
                }));

        // 把"边界在哪"写进 server instructions：调用方一上来就知道自己只能碰哪儿
        builder.Services.Configure<McpServerOptions>(options =>
        {
            options.ServerInstructions =
                $"Workspace: {WorkspaceGuard.Describe()}. "
                + "Every path argument must resolve inside that workspace (relative paths are resolved against it). "
                + "A call whose path points outside is refused before the tool runs; the refusal names the path you gave, "
                + $"the workspace, and how to widen it (set the {WorkspaceGuard.VariableName} environment variable).";
        });

        configure?.Invoke(mcp);

        return builder.Build();
    }

    /// <summary>
    /// "这次调用失败了"的文本前缀。工具失败时返回的第一段文本以它开头
    /// （<c>ToolGuard</c> 捕获异常、工作空间边界拒绝，两处都这么写）。
    /// </summary>
    internal const string FailurePrefix = "Error: ";

    /// <summary>
    /// 这次调用的结果是不是"失败" —— 只看**第一段**文本，正常结果里出现 "Error: " 字样不该被误判。
    /// </summary>
    private static bool Failed(CallToolResult result)
    {
        foreach (ContentBlock block in result.Content ?? [])
        {
            if (block is TextContentBlock text)
            {
                return text.Text.StartsWith(FailurePrefix, StringComparison.Ordinal);
            }
        }

        return false;
    }
}

/// <summary>
/// 工作空间边界：**这次会话允许碰的目录**。
///
/// 它**不是**安全边界 —— MCP 的 roots 协议也不被强制（服务端是独立进程，用启动者的权限跑），
/// 而且宿主自己就有命令行，真想越狱谁也拦不住。它要解决的是另一件事：
/// **让调用方知道自己碰的是哪儿**。越界时明确拒绝、并说清"你给的是哪、边界在哪、怎么改"，
/// 而不是静默地按一个没人核对过的路径去读去写。
///
/// 边界来源（按优先级）：环境变量 <c>ZMS_MCP_WORKSPACE</c>（多个目录用 <see cref="Path.PathSeparator"/> 分隔）
/// → 进程当前目录（这是宿主启动服务时给的）。两个都取不到时视为"不限制"。
/// </summary>
internal static class WorkspaceGuard
{
    /// <summary>环境变量名：显式指定工作区（多个目录用 <see cref="Path.PathSeparator"/> 分隔）。</summary>
    public const string VariableName = "ZMS_MCP_WORKSPACE";

    /// <summary>边界目录（已规范化为绝对路径；空表 = 不限制）。</summary>
    public static IReadOnlyList<string> Roots { get; } = ResolveRoots();

    /// <summary>边界的人话描述（错误消息与工具输出共用）。</summary>
    public static string Describe()
    {
        return Describe(Roots);
    }

    /// <summary>这组边界的人话描述。</summary>
    public static string Describe(IReadOnlyList<string> roots)
    {
        return roots.Count == 0 ? "（未设置边界，不限制）" : string.Join("、", roots);
    }

    /// <summary>
    /// 这个路径在进程的边界内吗。不在时 <paramref name="message"/> 是一句能照着改的话
    /// （说清"你给的是哪、边界在哪、想越界该怎么办"）。
    /// </summary>
    public static bool IsInside(string path, out string full, out string message)
    {
        return IsInside(path, Roots, out full, out message);
    }

    /// <summary>这个路径在这组边界内吗（测试用这个重载，免得依赖进程启动时的环境）。</summary>
    public static bool IsInside(string path, IReadOnlyList<string> roots, out string full, out string message)
    {
        full = "";
        message = "";

        if (roots.Count == 0)
        {
            return true;
        }

        try
        {
            full = RealPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            message = $"Error: 这个路径解析不了：{path}（{exception.Message}）";
            return false;
        }

        foreach (string root in roots)
        {
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        message = $"Error: 路径不在本次会话的工作区里：{path}{Environment.NewLine}"
            + $"  工作区：{Describe(roots)}{Environment.NewLine}"
            + $"  要动它的话：让宿主把这个目录加进去（环境变量 {VariableName}，多个目录用 '{Path.PathSeparator}' 分隔），"
            + "或者明确告诉我你确认要越界 —— 我不替你猜。";
        return false;
    }

    private static IReadOnlyList<string> ResolveRoots()
    {
        string configured = Environment.GetEnvironmentVariable(VariableName) ?? "";
        IEnumerable<string> candidates = configured.Length > 0
            ? configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [Environment.CurrentDirectory];

        List<string> roots = [];
        foreach (string candidate in candidates)
        {
            try
            {
                string full = Path.GetFullPath(candidate);
                if (Directory.Exists(full))
                {
                    roots.Add(full.TrimEnd(Path.DirectorySeparatorChar));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 配置本身写坏了就当没这一条，不让它把服务拖起来就崩
            }
        }

        return roots;
    }

    /// <summary>
    /// 尽力取"真实路径"：解析符号链接 / junction，免得边界比较被人从链接那头绕过去。
    /// 路径还不存在时退回 <see cref="Path.GetFullPath(string)"/>（它已经把 <c>..</c> 消解掉了）。
    /// </summary>
    private static string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        FileSystemInfo? info = Directory.Exists(full)
            ? new DirectoryInfo(full)
            : File.Exists(full)
                ? new FileInfo(full)
                : null;
        FileSystemInfo? target = info?.ResolveLinkTarget(returnFinalTarget: true);
        return Path.GetFullPath(target?.FullName ?? full);
    }
}

/// <summary>
/// 一次工具调用的作用域检查：把它涉及的**路径参数**逐个解析、逐个比对工作空间边界。
/// 越界就返回一句能照着改的拒绝话术，否则返回 null（放行）。
///
/// 只认"看起来像文件系统路径"的值：<c>symbols</c> 的 <c>path='Ns.Type.Member'</c> 那种
/// 符号路径没有分隔符也没有盘符，不会被当成文件路径。
/// </summary>
internal static class ToolScope
{
    /// <summary>参数名里出现这些词就当成"可能是路径"。</summary>
    private static readonly string[] PathLikeWords = ["path", "file", "dir", "folder", "slnx", "csproj", "target"];

    /// <summary>FTP 会话句柄的前缀：这种值指向远端，不做本地边界检查。</summary>
    private const string SessionPrefix = "ftp:";

    /// <summary>检查这次调用；返回 null = 放行，否则是给调用方看的拒绝话术。</summary>
    public static string? Refuse(RequestContext<CallToolRequestParams> context)
    {
        IDictionary<string, JsonElement>? arguments = context.Params?.Arguments;
        if (arguments == null)
        {
            return null;
        }

        // `target` / `toTarget` 指的是**别的存储/容器**（FTP 会话、压缩包、正在被追踪的压缩包）。
        // 只要它不是空的，配套的 `path` / `toPath` 就是**那个容器里的路径**，不是本地文件系统上的路径 ——
        // 本地边界管不着它。只有 target 为空（本地）时，path 才是本地路径。
        bool insideSomething = Text(arguments, "target").Length > 0;
        bool insideSomethingElse = Text(arguments, "toTarget").Length > 0;

        foreach (KeyValuePair<string, JsonElement> pair in arguments)
        {
            if (!PathLikeWords.Any(word => pair.Key.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (pair.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string text = (pair.Value.GetString() ?? "").Trim();
            if (IsSession(text))
            {
                continue;   // 会话句柄本身不是本地路径
            }

            if (!LooksLikePath(text))
            {
                continue;
            }

            if ((insideSomething && pair.Key.Equals("path", StringComparison.OrdinalIgnoreCase))
                || (insideSomethingElse && pair.Key.Equals("toPath", StringComparison.OrdinalIgnoreCase)))
            {
                continue;   // 容器内的路径（包内 / 远端），本地边界管不着
            }

            if (!WorkspaceGuard.IsInside(text, out _, out string message))
            {
                return message;
            }
        }

        return null;
    }

    /// <summary>取某个参数的文字值；没给、或不是字符串，就返回空串。</summary>
    private static string Text(IDictionary<string, JsonElement> arguments, string name)
    {
        foreach (KeyValuePair<string, JsonElement> pair in arguments)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                && pair.Value.ValueKind == JsonValueKind.String)
            {
                return (pair.Value.GetString() ?? "").Trim();
            }
        }

        return "";
    }

    /// <summary>这个值是不是 FTP 会话句柄。</summary>
    private static bool IsSession(string text)
    {
        return text.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>值看起来像不像文件系统路径：有分隔符，或者有盘符。</summary>
    private static bool LooksLikePath(string text)
    {
        return text.Contains('\\', StringComparison.Ordinal)
            || text.Contains('/', StringComparison.Ordinal)
            || (text.Length >= 2 && text[1] == ':');
    }
}
