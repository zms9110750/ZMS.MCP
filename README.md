# ZMS.MCP

Model Context Protocol (MCP) stdio server — 提供可组合的工具集，通过标准输入/输出与 AI 客户端通信。

## 构建

```bash
dotnet build
```

## 发布

```bash
dotnet publish -c Release -o publish
```

## 使用

MCP Server 通过 stdio 协议工作，通常由支持 MCP 的 AI 客户端（如 Claude Desktop、VS Code 等）自动启动。

在客户端配置中添加：

```json
{
  "mcpServers": {
    "zms-mcp-cli": {
      "command": "path\\to\\zms9110750.ZMS.MCP.Cli.exe",
      "args": []
    }
  }
}
```

## 项目

| 项目 | 说明 | 工具清单 |
|------|------|----------|
| `src/ZMS.MCP.Csharp` | 基于 **Roslyn** 的 C# 工作区工具：解决方案 / 项目 / 符号 / NuGet / 拟定事务 | 见 [`src/ZMS.MCP.Csharp/README.md`](src/ZMS.MCP.Csharp/README.md) |
| `src/ZMS.MCP.Cli` | 通用工具集（URI 协议层建设中，见 `docs/`） | 见 `src/ZMS.MCP.Cli` |
| `test/ZMS.MCP.Test`、`test/ZMS.MCP.Csharp.Test` | 单元测试 | — |

## 文档

需求文档在 `docs/`，实现以文档为基线：

- `docs/Csharp.md` — C# 工作区工具（扫盘、查看/编辑解决方案与项目、NuGet、列出符号、拟定）
- `docs/Resource.md` — 网络与文件资源（http / ftp / 本地文件与目录 / 压缩包）
- `docs/Structured.md` — 结构化文档（json / xml / yaml / toml / ini）
- `docs/Workflow.md` — GitHub Actions 工作流生成

## 工程约定

- `Directory.Build.props` — 全仓库通用：项目元数据、程序集名（`zms9110750.<项目名>`）、`InternalsVisibleTo`，以及所有项目都需要的 **MCP 包**（`ModelContextProtocol`）。
  没有模板开关，没有条件包引用，也没有编译前格式化。
- `Global.cs` — 全仓库共享：MCP 相关的 `global using` + 通用入口 `McpStdioServer`。
  每个 MCP 项目的 `Program.cs` 只需要：

  ```csharp
  using ZMS.MCP;

  await McpStdioServer.RunAsync(args);
  ```

  `McpStdioServer` 负责注册 stdio 传输、扫描本程序集注册全部工具，并把日志固定到 stderr（stdout 留给协议）。
  项目自己的启动准备（如 ZMS.MCP.Csharp 的写前日志前滚）写在各自的 `Program.cs` 里。

## 项目结构

```
ZMS.MCP/
├── Directory.Build.props   # 全仓库通用：元数据 + MCP 包
├── Global.cs               # 全仓库共享：MCP global using + 通用入口
├── docs/                   # 需求基线（Csharp / Resource / Structured / Workflow）
├── src/
│   ├── ZMS.MCP.Cli/        # 通用工具集（协议层尚未实现，见 docs）
│   └── ZMS.MCP.Csharp/     # Roslyn 驱动的 C# 工作区工具
└── test/
    ├── ZMS.MCP.Test/       # 单元测试
    └── ZMS.MCP.Csharp.Test/# C# 工作区工具的单元测试
```
